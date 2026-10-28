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

    /** The SHA-256 digest of a file's bytes, read in pieces. */
    static String fileDigest(Path path) throws IOException {
        MessageDigest digest = sha256();
        byte[] buffer = new byte[1 << 16];
        try (InputStream in = Files.newInputStream(path)) {
            for (int n; (n = in.read(buffer)) > 0; ) {
                digest.update(buffer, 0, n);
            }
        }
        return HexFormat.of().formatHex(digest.digest());
    }

    /**
     * Digests every regular file under {@code root}, keyed by
     * {@code /}-separated relative path, refusing other entry kinds (symbolic
     * links included).
     */
    static TreeMap<String, String> tree(Path root) throws JacquardException {
        TreeMap<String, String> files = new TreeMap<>(Json.CODE_POINT_ORDER);
        walk(root, root, files);
        return files;
    }

    private static void walk(Path root, Path directory, Map<String, String> files) throws JacquardException {
        List<Path> entries;
        try (Stream<Path> list = Files.list(directory)) {
            entries = new ArrayList<>(list.toList());
        } catch (IOException | RuntimeException error) {
            throw JacquardException.preparation("cannot read " + directory + ": " + error.getMessage());
        }
        entries.sort((a, b) -> Json.CODE_POINT_ORDER.compare(a.getFileName().toString(), b.getFileName().toString()));
        for (Path path : entries) {
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
                walk(root, path, files);
            } else if (attributes.isRegularFile()) {
                try {
                    files.put(relative, fileDigest(path));
                } catch (IOException error) {
                    throw JacquardException.preparation("cannot read " + path + ": " + error.getMessage());
                }
            } else {
                throw JacquardException.preparation("unsupported filesystem entry in prepared artifacts: " + relative);
            }
        }
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

    /** Finds an executable the way a shell would, then resolves symbolic links. */
    static Path executable(String binary) throws JacquardException {
        JacquardException missing = JacquardException.preparation(
                "cannot find Core executable " + binary + "; configure JACQUARD_BIN using the setup guide");
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
        if (candidate == null || !isExecutable(candidate)) {
            throw missing;
        }
        try {
            return candidate.toRealPath();
        } catch (IOException error) {
            throw JacquardException.preparation("cannot resolve Core executable " + binary + ": " + error.getMessage());
        }
    }

    private static boolean isExecutable(Path path) {
        return Files.isRegularFile(path) && Files.isExecutable(path);
    }
}
