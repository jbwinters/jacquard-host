package org.jacquardlang.host.sdk;

import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.attribute.BasicFileAttributes;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.stream.Stream;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.CarrierException;
import org.jacquardlang.host.carrier.Command;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;

/** Bounded runs of Core's public commands, and digests of prepared files. */
final class Core {
    private Core() {}

    /** Each Core command must finish within this time. */
    static final Duration COMMAND_TIMEOUT = Duration.ofSeconds(30);
    static final int STDOUT_LIMIT = 8 << 20;
    static final int STDERR_LIMIT = 4096;
    /** Why a preparation stopped early. */
    static final String ABORTED = "preparation was cancelled or interrupted";

    /**
     * Runs one Core subcommand with the prelude set, returning its stdout. The
     * command is bounded by {@code timeout}, its output by the limits above,
     * and it stops at once when {@code cancel} is cancelled or the calling
     * thread is interrupted (whose interrupt status stays set).
     */
    static String run(Path binary, Path prelude, Duration timeout, Cancel cancel, String... arguments) throws JacquardException {
        String name = arguments.length == 0 ? "" : arguments[0];
        List<String> command = new ArrayList<>();
        command.add(binary.toString());
        command.addAll(List.of(arguments));
        Command.Output output;
        try {
            output = Command.run(command, Map.of("JACQUARD_PRELUDE", prelude.toString()), timeout, STDOUT_LIMIT, STDERR_LIMIT, cancel);
        } catch (CarrierException error) {
            String detail;
            if (error.isTimeout()) {
                detail = "no exit within " + (timeout.toMillis() % 1000 == 0 ? timeout.toSeconds() + " seconds" : timeout.toMillis() + " ms");
            } else if (error.isAborted()) {
                detail = ABORTED;
            } else {
                detail = error.getMessage();
            }
            throw JacquardException.preparation("Core " + name + " failed: " + detail);
        }
        if (output.exitStatus() != 0) {
            String detail = new String(output.stderr(), StandardCharsets.UTF_8).strip();
            throw JacquardException.preparation("Core " + name + " failed: " + (detail.isEmpty() ? "exit status " + output.exitStatus() : detail));
        }
        if (output.stdoutOverflow()) {
            throw JacquardException.preparation("Core " + name + " output exceeds 8 MiB");
        }
        try {
            return StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(output.stdout()))
                    .toString();
        } catch (CharacterCodingException error) {
            throw JacquardException.preparation("Core " + name + " output is not UTF-8");
        }
    }

    private static MessageDigest sha256() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException error) {
            throw new IllegalStateException("every JDK provides SHA-256", error);
        }
    }

    /** The SHA-256 digest of bytes, in lowercase hexadecimal. */
    static String digest(byte[] data) {
        return HexFormat.of().formatHex(sha256().digest(data));
    }

    /** The most bytes of the Core executable that are fingerprinted. */
    static final long EXECUTABLE_LIMIT = 512L << 20;
    /** Test seam: runs after each chunk {@link #fileDigest} reads. Null outside tests. */
    static volatile Runnable digestChunkHook;

    /**
     * The SHA-256 digest of the Core executable's bytes, read in pieces. A
     * file larger than {@link #EXECUTABLE_LIMIT} is refused from its size
     * before it is read, and the limit is enforced again while it is read.
     * Between pieces it observes {@code cancel} and the caller's interrupt,
     * which it never clears.
     */
    static String fileDigest(Path path, Cancel cancel) throws IOException, JacquardException {
        JacquardException over = JacquardException.preparation("Core executable " + path + " exceeds " + (EXECUTABLE_LIMIT >> 20) + " MiB");
        if (Files.size(path) > EXECUTABLE_LIMIT) {
            throw over;
        }
        MessageDigest digest = sha256();
        byte[] buffer = new byte[1 << 16];
        long read = 0;
        try (InputStream in = Files.newInputStream(path)) {
            while (true) {
                if (cancel.isCancelled() || Thread.currentThread().isInterrupted()) {
                    throw JacquardException.preparation(ABORTED);
                }
                int n = in.read(buffer);
                if (n < 0) {
                    break;
                }
                read += n;
                if (read > EXECUTABLE_LIMIT) {
                    throw over;
                }
                digest.update(buffer, 0, n);
                Runnable hook = digestChunkHook;
                if (hook != null) {
                    hook.run();
                }
            }
        }
        return HexFormat.of().formatHex(digest.digest());
    }

    /** The most bytes {@link #tree} reads under one root. */
    static final long TREE_LIMIT = 256L << 20;
    /** The most entries (files and directories, empty ones included) {@link #tree} visits under one root. */
    static final int TREE_ENTRIES = 100_000;
    /** Test seam: the entry cap {@link #tree} applies. */
    static volatile int treeEntries = TREE_ENTRIES;

    /**
     * Test seam: runs after {@link #tree} digests each file, with its path.
     * Null outside tests.
     */
    static volatile java.util.function.Consumer<Path> treeFileHook;

    /** What {@link #tree} does about who may write what it walks. */
    enum Access {
        /** Nothing: a Core installation's prelude. */
        ANY,
        /** Removes every group and other permission, as each new cache entry's files are made owner-only. */
        RESTRICT,
        /** Refuses the root or any entry that this user does not own or that its group or others may write (POSIX only). */
        OWNER_ONLY
    }

    /**
     * Digests every regular file under {@code root}, keyed by
     * {@code /}-separated relative path, refusing other entry kinds (symbolic
     * links, FIFOs, and devices included, so no read can block on one), more
     * than {@link #TREE_ENTRIES} entries, and more than {@link #TREE_LIMIT}
     * bytes in all, which is checked against each file's size before it is
     * read and again while it is read. The walk streams each directory, so
     * nothing beyond the entry cap is held, and before each entry and each
     * piece of a file it observes {@code cancel} and the caller's interrupt,
     * which it never clears. The result is sorted as it is collected, within
     * the cap. {@code access} says whether entries are also checked for
     * owner-only access, or made owner-only.
     */
    static TreeMap<String, String> tree(Path root, Cancel cancel, Access access) throws JacquardException {
        TreeMap<String, String> files = new TreeMap<>(Json.CODE_POINT_ORDER);
        Owner.check(root, access);
        walk(root, root, files, new long[2], cancel, access);
        return files;
    }

    private static void walk(Path root, Path directory, Map<String, String> files, long[] total, Cancel cancel, Access access)
            throws JacquardException {
        try (java.nio.file.DirectoryStream<Path> entries = Files.newDirectoryStream(directory)) {
            for (Path path : entries) {
                if (cancel.isCancelled() || Thread.currentThread().isInterrupted()) {
                    throw JacquardException.preparation(ABORTED);
                }
                int cap = treeEntries;
                if (++total[1] > cap) {
                    throw JacquardException.preparation("more than " + cap + " entries under " + root);
                }
                List<String> parts = new ArrayList<>();
                for (Path part : root.relativize(path)) {
                    parts.add(part.toString());
                }
                String relative = String.join("/", parts);
                BasicFileAttributes attributes;
                try {
                    attributes = Files.readAttributes(path, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
                } catch (IOException error) {
                    throw JacquardException.preparation("cannot read " + path + ": " + error.getMessage());
                }
                if (attributes.isDirectory()) {
                    Owner.check(path, access);
                    walk(root, path, files, total, cancel, access);
                } else if (attributes.isRegularFile()) {
                    Owner.check(path, access);
                    JacquardException over = JacquardException.preparation("files under " + root + " exceed " + (TREE_LIMIT >> 20) + " MiB");
                    if (attributes.size() > TREE_LIMIT - total[0]) {
                        throw over;
                    }
                    try {
                        files.put(relative, boundedDigest(path, TREE_LIMIT - total[0], total, over, cancel));
                    } catch (IOException error) {
                        throw JacquardException.preparation("cannot read " + path + ": " + error.getMessage());
                    }
                    java.util.function.Consumer<Path> hook = treeFileHook;
                    if (hook != null) {
                        hook.accept(path);
                    }
                } else {
                    throw JacquardException.preparation("unsupported filesystem entry in prepared artifacts: " + relative);
                }
            }
        } catch (IOException | java.nio.file.DirectoryIteratorException error) {
            throw JacquardException.preparation("cannot read " + directory + ": " + error.getMessage());
        }
    }

    /** The digest of at most {@code room} bytes of a file, adding what it read to {@code total[0]}; {@code over} once it is longer. */
    private static String boundedDigest(Path path, long room, long[] total, JacquardException over, Cancel cancel)
            throws IOException, JacquardException {
        MessageDigest digest = sha256();
        byte[] buffer = new byte[1 << 16];
        long read = 0;
        try (InputStream in = Files.newInputStream(path, LinkOption.NOFOLLOW_LINKS)) {
            for (int n; (n = in.read(buffer)) > 0; ) {
                if (cancel.isCancelled() || Thread.currentThread().isInterrupted()) {
                    throw JacquardException.preparation(ABORTED);
                }
                read += n;
                if (read > room) {
                    throw over;
                }
                digest.update(buffer, 0, n);
            }
        }
        total[0] += read;
        return HexFormat.of().formatHex(digest.digest());
    }

    /** Canonical JSON for a string map: keys in code-point order. */
    static String canonical(Map<String, String> map) {
        TreeMap<String, Json> fields = new TreeMap<>(Json.CODE_POINT_ORDER);
        map.forEach((key, value) -> fields.put(key, Json.of(value)));
        try {
            return JsonCodec.stringify(Json.of(fields), 4);
        } catch (ProtocolException error) {
            throw new IllegalStateException("a flat object", error);
        }
    }

    /** The digest of a string map's canonical JSON. */
    static String mapDigest(Map<String, String> map) {
        return digest(canonical(map).getBytes(StandardCharsets.UTF_8));
    }

    /** The longest path, in chars, the SDK passes to the file system (Linux's PATH_MAX). */
    static final int PATH_LIMIT = 4096;

    /**
     * {@code path}, refused when it is longer than any path, before the file
     * system or any message sees it whole.
     */
    static Path checkedPath(Path path, String what) throws JacquardException {
        String text = path.toString();
        // As given first, so a huge path is not copied again; then as the
        // file system will see it, since a relative path grows.
        if (text.length() > PATH_LIMIT || path.toAbsolutePath().toString().length() > PATH_LIMIT) {
            throw JacquardException.preparation(what + " " + Identities.excerpt(text) + " is longer than any path");
        }
        return path;
    }

    /** Finds an executable the way a shell would, then resolves symbolic links. */
    static Path executable(String binary) throws JacquardException {
        // No path is this long (PATH_MAX is 4096 on Linux), so a longer name is refused before it is used.
        if (binary.length() > PATH_LIMIT) {
            throw JacquardException.preparation("cannot find Core executable " + Identities.excerpt(binary)
                    + "; configure JACQUARD_BIN using the setup guide");
        }
        JacquardException missing = JacquardException.preparation(
                "cannot find Core executable " + Identities.excerpt(binary) + "; configure JACQUARD_BIN using the setup guide");
        if (binary.indexOf('\0') >= 0) {
            // No path holds a NUL; the JDK would refuse it unchecked.
            throw missing;
        }
        Path candidate = null;
        try {
            candidate = lookUp(binary);
        } catch (java.nio.file.InvalidPathException error) {
            throw missing;
        }
        if (candidate == null || !isExecutable(candidate)) {
            throw missing;
        }
        try {
            return candidate.toRealPath();
        } catch (IOException error) {
            throw JacquardException.preparation("cannot resolve Core executable " + Identities.excerpt(binary) + ": " + error.getMessage());
        }
    }

    private static Path lookUp(String binary) {
        Path candidate = null;
        if (binary.contains("/") || binary.contains(java.io.File.separator)) {
            candidate = Path.of(binary);
        } else {
            String path = System.getenv("PATH");
            if (path != null) {
                // Like Go's exec.LookPath, relative entries (including empty
                // ones, which mean the current directory) are not searched.
                for (String entry : path.split(java.io.File.pathSeparator, -1)) {
                    Path directory = Path.of(entry);
                    if (directory.isAbsolute() && isExecutable(directory.resolve(binary))) {
                        candidate = directory.resolve(binary);
                        break;
                    }
                }
            }
        }
        return candidate;
    }

    private static boolean isExecutable(Path path) {
        return Files.isRegularFile(path) && Files.isExecutable(path);
    }
}
