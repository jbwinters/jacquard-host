package org.jacquardlang.host.sdk;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.attribute.BasicFileAttributes;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.locks.LockSupport;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Limits;

/**
 * Prepares programs against one Core installation. A runtime is immutable
 * and thread-safe. The {@code --version} check and prelude discovery run once
 * per Core executable and prelude contents in this JVM, across runtimes and
 * threads.
 *
 * <p>The name shadows {@link java.lang.Runtime} where this class is imported.
 */
public final class Runtime {
    /** Prepared programs live here unless {@link PrepareOptions#cacheDir()} is set. */
    public static final String DEFAULT_CACHE_DIR = ".scratch/jacquard-host-java";
    static final String PROGRAM_SCHEMA = "jacquard-java-program-v2";
    private static final long POLL_MIN = TimeUnit.MICROSECONDS.toNanos(100);
    private static final long POLL_MAX = TimeUnit.MILLISECONDS.toNanos(10);
    /** The most source bytes one preparation reads, separators included. */
    public static final long SOURCE_LIMIT = 16L << 20;
    /** The most source files one preparation takes; a longer list is refused from its size before any element is read. */
    public static final int SOURCE_FILES = 1024;
    private static final List<String> PRELUDE_FILES = List.of("01-prim.jqd", "02-data.jqd", "19-audit.jqd");
    /** Core's raw outputs, kept as verified files of each cache entry, from which every binding is derived. */
    static final String CHECK_OUTPUT = "core/check.out";
    static final String HASH_OUTPUT = "core/hash.out";
    /** The most bytes of {@code program.json} a load reads. */
    private static final int METADATA_LIMIT = 32 << 20;

    private static String preludeOutput(String file) {
        return "core/prelude-" + file + ".out";
    }

    /** One discovery: empty until a run succeeds; a failed run leaves it empty for the next caller. */
    private static final class Flight {
        List<String> outputs;
        boolean running;
    }

    /** Version checks and prelude discoveries, by key, shared by every runtime in this JVM. */
    private static final ConcurrentHashMap<String, Flight> FLIGHTS = new ConcurrentHashMap<>();
    private static final AtomicLong STAGING = new AtomicLong();

    private final String binary;
    private final Path prelude;
    private final Duration deadline;
    private final Limits limits;
    private final AtomicInteger commands = new AtomicInteger();
    /** Test seam: how long each Core command may run. */
    volatile Duration commandTimeout = Core.COMMAND_TIMEOUT;
    /**
     * Test seam: runs on a preparing thread when it starts to wait for another
     * thread's version check or prelude discovery. Null outside tests.
     */
    static volatile Runnable flightWaitHook;
    /** Test seam: runs after each source file is read, with its path. Null outside tests. */
    static volatile java.util.function.Consumer<Path> sourceReadHook;
    /** Test seam: runs when a cache entry has been loaded, just before its program is returned. Null outside tests. */
    static volatile Runnable loadedHook;
    /** Test seam: runs with the vacated staging path once an entry is published. Null outside tests. */
    static volatile java.util.function.Consumer<Path> publishedHook;

    /**
     * Validates options; Core does not run yet.
     *
     * @throws JacquardException a binding failure for a missing prelude or invalid options
     */
    public Runtime(RuntimeOptions options) throws JacquardException {
        String bin = options.binary();
        if (bin == null || bin.isEmpty()) {
            bin = System.getenv("JACQUARD_BIN");
        }
        Path preludePath = options.prelude();
        if (preludePath == null || preludePath.toString().isEmpty()) {
            String fromEnv = System.getenv("JACQUARD_PRELUDE");
            preludePath = fromEnv == null || fromEnv.isEmpty() ? null : Path.of(fromEnv);
        }
        if (preludePath == null) {
            throw JacquardException.binding(
                    "set JACQUARD_PRELUDE or RuntimeOptions.prelude; Core and prelude must match the compatibility pin");
        }
        if (preludePath.toString().length() > Core.PATH_LIMIT || Core.absoluteLength(preludePath) > Core.PATH_LIMIT) {
            throw JacquardException.binding("prelude " + Identities.excerpt(preludePath.toString()) + " is longer than any path");
        }
        if (options.deadline() != null && (options.deadline().isZero() || options.deadline().isNegative())) {
            throw JacquardException.binding("deadline must be positive");
        }
        Limits selected = options.limits() == null ? Limits.HARD : options.limits();
        try {
            selected.validate();
        } catch (ProtocolException error) {
            throw JacquardException.binding("invalid runtime limits: " + error.getMessage());
        }
        this.binary = bin == null || bin.isEmpty() ? "jacquard" : bin;
        this.prelude = preludePath.toAbsolutePath();
        this.deadline = options.deadline();
        this.limits = selected;
    }

    /** The Core executable as configured, before it is resolved. */
    public String binary() {
        return binary;
    }

    /** The absolute prelude directory. */
    public Path prelude() {
        return prelude;
    }

    Duration deadline() {
        return deadline;
    }

    Limits limits() {
        return limits;
    }

    /** Test seam: how many Core commands this runtime has run. */
    int coreCommands() {
        return commands.get();
    }

    private String command(Path executable, Cancel cancel, String... arguments) throws JacquardException {
        commands.incrementAndGet();
        return Core.run(executable, prelude, commandTimeout, cancel, arguments);
    }

    @FunctionalInterface
    private interface Work {
        List<String> run() throws JacquardException;
    }

    /**
     * Runs {@code work} once per key. Concurrent callers wait for one run,
     * each observing its own {@code cancel} and interrupt status (which the
     * wait never clears); the run itself is bounded by the command timeout. A
     * successful result stays cached, and a failed run leaves the next caller
     * to try again.
     */
    private static List<String> once(String key, Cancel cancel, Work work) throws JacquardException {
        Flight flight = FLIGHTS.computeIfAbsent(key, ignored -> new Flight());
        long idle = POLL_MIN;
        boolean waiting = false;
        while (true) {
            synchronized (flight) {
                if (flight.outputs != null) {
                    return flight.outputs;
                }
                if (!flight.running) {
                    flight.running = true;
                    break;
                }
            }
            Runnable hook = flightWaitHook;
            if (!waiting && hook != null) {
                hook.run();
            }
            waiting = true;
            if (stopped(cancel)) {
                throw JacquardException.preparation(Core.ABORTED);
            }
            // Parking leaves the interrupt status set.
            LockSupport.parkNanos(idle);
            idle = Math.min(idle * 2, POLL_MAX);
        }
        try {
            List<String> outputs = work.run();
            synchronized (flight) {
                flight.outputs = outputs;
            }
            return outputs;
        } finally {
            synchronized (flight) {
                flight.running = false;
            }
        }
    }

    /** Runs {@code --version} once per executable digest; accepts output whose last field is the pinned version. */
    private void checkVersion(Path executable, String binaryHash, Cancel cancel) throws JacquardException {
        once("version:" + binaryHash, cancel, () -> {
            String version = command(executable, cancel, "--version");
            String[] fields = version.strip().split("\\s+");
            if (!fields[fields.length - 1].equals(Compat.CORE_VERSION)) {
                throw JacquardException.preparation("expected pinned Core version " + Compat.CORE_VERSION + ", found \""
                        + version.strip() + "\"; the exact release is also required");
            }
            return List.of();
        });
    }

    /** Core's {@code hash} output for each prelude file, which must parse. */
    private List<String> preludeOutputs(Path executable, String key, Cancel cancel) throws JacquardException {
        return once("prelude:" + key, cancel, () -> {
            List<String> outputs = new ArrayList<>();
            for (String name : PRELUDE_FILES) {
                String output = command(executable, cancel, "hash", prelude.resolve(name).toString());
                Identities.hashGroups(output);
                outputs.add(output);
            }
            return List.copyOf(outputs);
        });
    }

    /** The bindings of a program: what {@code program.json} records beside its identity and files. */
    record Bindings(List<Identities.Entry> names, List<Identities.Entry> constructors, Map<String, String> signatures,
            List<List<Identities.Entry>> groups) {}

    /**
     * Derives every binding from Core's raw outputs: {@code check --print-sigs}
     * for the signatures, and {@code hash} of each prelude file and of the
     * source for the names, constructors, and declaration groups.
     */
    static Bindings derive(String check, List<String> preludeHashes, String hash) throws JacquardException {
        Map<String, String> signatures = new TreeMap<>(Json.CODE_POINT_ORDER);
        for (String line : check.split("\n", -1)) {
            String trimmed = line.strip();
            int split = trimmed.indexOf(" : ");
            if (split >= 0) {
                String name = trimmed.substring(0, split);
                if (name.indexOf(' ') < 0 && name.indexOf('\t') < 0) {
                    signatures.put(name, trimmed.substring(split + 3));
                }
            }
        }
        List<List<Identities.Entry>> groups = new ArrayList<>();
        for (String output : preludeHashes) {
            groups.addAll(Identities.hashGroups(output));
        }
        groups.addAll(Identities.hashGroups(hash));
        Identities.checkGroups(groups);
        List<Identities.Entry> names = new ArrayList<>();
        LinkedHashMap<String, Hash> constructors = new LinkedHashMap<>();
        Set<String> seen = new HashSet<>();
        for (List<Identities.Entry> group : groups) {
            for (Identities.Entry entry : group) {
                if (seen.add(entry.name())) {
                    names.add(entry);
                }
                constructors.put(entry.name(), entry.hash());
            }
        }
        List<Identities.Entry> constructorList = new ArrayList<>();
        constructors.forEach((name, identity) -> constructorList.add(new Identities.Entry(name, identity)));
        return new Bindings(names, constructorList, signatures, groups);
    }

    /**
     * {@link #prepare(List, PrepareOptions, Cancel)} without cancellation.
     */
    public Program prepare(List<Path> sources, PrepareOptions options) throws JacquardException {
        return prepare(sources, options, new Cancel());
    }

    /**
     * Checks, hashes, and installs first-party declaration sources
     * ({@code .jac} files, or {@code .jqd} files, in dependency order) into a
     * verified cache keyed by the source bytes, the Core executable's bytes,
     * every prelude file, and the protocol. Each Core command is bounded by a
     * 30-second timeout and bounded output. Cancelling {@code cancel}, or
     * interrupting the calling thread, stops the running command and fails
     * the preparation; the interrupt status stays set.
     *
     * @throws JacquardException a preparation failure
     */
    public Program prepare(List<Path> sources, PrepareOptions options, Cancel cancel) throws JacquardException {
        java.util.Objects.requireNonNull(cancel, "cancel");
        if (stopped(cancel)) {
            throw JacquardException.preparation(Core.ABORTED);
        }
        try {
            Program program = prepareChecked(sources, options, cancel);
            // A stop during the last of the work is reported, not a program.
            if (stopped(cancel)) {
                throw JacquardException.preparation(Core.ABORTED);
            }
            return program;
        } catch (JacquardException error) {
            // An interrupt also fails file reads (ClosedByInterruptException);
            // report the stop, not a misleading read or cache failure.
            if (stopped(cancel) && !error.getMessage().endsWith(Core.ABORTED)) {
                throw JacquardException.preparation(Core.ABORTED);
            }
            throw error;
        }
    }

    /** Whether the preparation was cancelled or its thread interrupted; reads the interrupt status without clearing it. */
    private static boolean stopped(Cancel cancel) {
        return cancel.isCancelled() || Thread.currentThread().isInterrupted();
    }

    private Program prepareChecked(List<Path> sources, PrepareOptions options, Cancel cancel) throws JacquardException {
        JacquardException usage = JacquardException.preparation("pass .jac files or .jqd files in dependency order");
        // The count comes from size() alone, before any element is read.
        int count = sources.size();
        if (count == 0) {
            throw usage;
        }
        if (count > SOURCE_FILES) {
            throw JacquardException.preparation("more than " + SOURCE_FILES + " sources");
        }
        String syntax = null;
        // Each element is read once, so the files read below are the ones checked here.
        List<Path> checked = new ArrayList<>(Math.min(count, SOURCE_FILES));
        for (int i = 0; i < count; i++) {
            if (stopped(cancel)) {
                throw JacquardException.preparation(Core.ABORTED);
            }
            Path source = Core.checkedPath(java.util.Objects.requireNonNull(sources.get(i), "source"), "source");
            checked.add(source);
            String kind = extension(source);
            if (syntax == null) {
                syntax = kind;
            }
            if (!(".jac".equals(kind) || ".jqd".equals(kind)) || !syntax.equals(kind)) {
                throw usage;
            }
        }
        Path configured = Core.checkedPath(options.cacheDir() == null ? Path.of(DEFAULT_CACHE_DIR) : options.cacheDir(), "cache directory")
                .toAbsolutePath();
        Path executable = Core.executable(binary);
        String binaryHash;
        try {
            binaryHash = Core.fileDigest(executable, cancel);
        } catch (IOException error) {
            throw JacquardException.preparation("cannot read " + executable + ": " + error.getMessage());
        }
        checkVersion(executable, binaryHash, cancel);
        String preludeHash;
        try {
            preludeHash = Core.mapDigest(Core.tree(prelude, cancel, Core.Access.ANY));
        } catch (JacquardException error) {
            throw JacquardException.preparation("cannot read prelude " + prelude + ": " + error.getMessage());
        }
        byte[] bytes = readSources(checked, cancel);
        TreeMap<String, String> identity = new TreeMap<>(Json.CODE_POINT_ORDER);
        identity.put("binary", binaryHash);
        identity.put("prelude", preludeHash);
        identity.put("protocol", Compat.PROTOCOL);
        identity.put("schema", PROGRAM_SCHEMA);
        identity.put("source", Core.digest(bytes));
        identity.put("syntax", syntax);
        Path cache;
        try {
            if (Owner.posix()) {
                Files.createDirectories(configured, PosixFilePermissions.asFileAttribute(Owner.DIRECTORY));
            } else {
                Files.createDirectories(configured);
            }
            // A symbolic link to the cache root is followed once, here; the
            // real directory is checked, and nothing inside it is followed.
            cache = configured.toRealPath();
        } catch (IOException error) {
            throw JacquardException.preparation("cannot create " + configured + ": " + error.getMessage());
        }
        Path directory = cache.resolve(Core.mapDigest(identity));
        // Only the cache's owner may write it; nothing inside it can resist
        // someone else who may.
        Owner.check(cache, Core.Access.OWNER_ONLY);
        if (Files.exists(directory)) {
            return load(directory, identity, executable, cancel);
        }
        Path staging = stagingDirectory(cache);
        boolean published = false;
        try {
            Path snapshot = staging.resolve("source" + syntax);
            write(snapshot, bytes, "cannot write the source snapshot");
            String check = command(executable, cancel, "check", snapshot.toString(), "--print-sigs");
            List<String> preludeHashes = preludeOutputs(executable, binaryHash + preludeHash, cancel);
            String hash = command(executable, cancel, "hash", snapshot.toString());
            Bindings bindings = derive(check, preludeHashes, hash);
            Path store = staging.resolve("store");
            command(executable, cancel, "run", snapshot.toString(), "--store", store.toString());
            createDirectory(staging.resolve("core"));
            write(staging.resolve(CHECK_OUTPUT), check.getBytes(StandardCharsets.UTF_8), "cannot write Core's output");
            write(staging.resolve(HASH_OUTPUT), hash.getBytes(StandardCharsets.UTF_8), "cannot write Core's output");
            for (int i = 0; i < PRELUDE_FILES.size(); i++) {
                write(staging.resolve(preludeOutput(PRELUDE_FILES.get(i))), preludeHashes.get(i).getBytes(StandardCharsets.UTF_8),
                        "cannot write Core's output");
            }
            // Core made the store with its own permissions; the entry is owner-only.
            Metadata metadata = new Metadata(identity, bindings.names(), bindings.constructors(), bindings.signatures(), bindings.groups(),
                    Core.tree(staging, cancel, Core.Access.RESTRICT));
            write(staging.resolve("program.json"), metadata.toJson().getBytes(StandardCharsets.UTF_8), "cannot write program.json");
            try {
                Files.move(staging, directory, StandardCopyOption.ATOMIC_MOVE);
                // The staging path is vacated; whatever appears there later is not this call's.
                published = true;
                java.util.function.Consumer<Path> hook = publishedHook;
                if (hook != null) {
                    hook.accept(staging);
                }
            } catch (AtomicMoveNotSupportedException error) {
                throw JacquardException.preparation("cannot publish " + directory + ": " + error.getMessage());
            } catch (IOException error) {
                // A concurrent preparation published the same artifact first.
                if (!Files.exists(directory)) {
                    throw JacquardException.preparation("cannot publish " + directory + ": " + error.getMessage());
                }
            }
        } finally {
            if (!published) {
                removeTree(staging, cancel);
            }
        }
        return load(directory, identity, executable, cancel);
    }

    /**
     * The sources joined by newlines. Each must be a regular file (a symbolic
     * link to one is followed); the total is checked against
     * {@link #SOURCE_LIMIT} from the files' sizes before each is read, and no
     * read goes past it. Between files the cancel and the interrupt, which
     * stays set, are observed.
     */
    private static byte[] readSources(List<Path> sources, Cancel cancel) throws JacquardException {
        ByteArrayOutputStream content = new ByteArrayOutputStream();
        JacquardException over = JacquardException.preparation("sources exceed " + (SOURCE_LIMIT >> 20) + " MiB in total");
        long total = 0;
        for (int i = 0; i < sources.size(); i++) {
            if (stopped(cancel)) {
                throw JacquardException.preparation(Core.ABORTED);
            }
            Path source = sources.get(i);
            BasicFileAttributes attributes;
            try {
                attributes = Files.readAttributes(source, BasicFileAttributes.class);
            } catch (IOException error) {
                throw JacquardException.preparation("cannot read " + source + ": " + error.getMessage());
            }
            if (!attributes.isRegularFile()) {
                throw JacquardException.preparation("cannot read " + source + ": not a regular file");
            }
            total += (i > 0 ? 1 : 0) + attributes.size();
            if (total > SOURCE_LIMIT) {
                throw over;
            }
            if (i > 0) {
                content.write('\n');
            }
            int room = (int) (SOURCE_LIMIT - content.size());
            byte[] read;
            try (InputStream in = Files.newInputStream(source)) {
                read = in.readNBytes(room + 1);
            } catch (IOException error) {
                throw JacquardException.preparation("cannot read " + source + ": " + error.getMessage());
            }
            if (read.length > room) {
                throw over;
            }
            content.write(read, 0, read.length);
            java.util.function.Consumer<Path> hook = sourceReadHook;
            if (hook != null) {
                hook.accept(source);
            }
        }
        return content.toByteArray();
    }

    private static String extension(Path source) {
        Path name = source.getFileName();
        String text = name == null ? "" : name.toString();
        int dot = text.lastIndexOf('.');
        return dot <= 0 ? "" : text.substring(dot);
    }

    private static void createDirectory(Path path) throws JacquardException {
        try {
            if (Owner.posix()) {
                Files.createDirectory(path, PosixFilePermissions.asFileAttribute(Owner.DIRECTORY));
            } else {
                Files.createDirectory(path);
            }
        } catch (IOException error) {
            throw JacquardException.preparation("cannot create " + path + ": " + error.getMessage());
        }
    }

    /** Writes a new owner-only file. */
    private static void write(Path path, byte[] bytes, String context) throws JacquardException {
        try {
            if (Owner.posix()) {
                Files.createFile(path, PosixFilePermissions.asFileAttribute(Owner.FILE));
            }
            Files.write(path, bytes);
        } catch (IOException error) {
            throw JacquardException.preparation(context + ": " + error.getMessage());
        }
    }

    private static Path stagingDirectory(Path cache) throws JacquardException {
        long nanos = System.nanoTime() & 0xffffffffL;
        for (int attempt = 0; attempt < 100; attempt++) {
            Path path = cache.resolve(String.format("prepare-%d-%08x-%d", ProcessHandle.current().pid(), nanos, STAGING.getAndIncrement()));
            try {
                return Owner.posix() ? Files.createDirectory(path, PosixFilePermissions.asFileAttribute(Owner.DIRECTORY))
                        : Files.createDirectory(path);
            } catch (FileAlreadyExistsException error) {
                // Try the next name.
            } catch (IOException error) {
                throw JacquardException.preparation("cannot create " + path + ": " + error.getMessage());
            }
        }
        throw JacquardException.preparation("cannot create a staging directory");
    }

    /** Test seam: runs after the failure cleanup removes each entry, with its path. Null outside tests. */
    static volatile java.util.function.Consumer<Path> cleanupHook;

    /**
     * Removes a staging directory unless it was published (and so no longer
     * exists), in a streamed post-order walk that holds one directory
     * iterator per level. It removes at most {@link Core#TREE_ENTRIES}
     * entries, and ends at the first cancel or interrupt (which it never
     * clears), checked before each entry. What it leaves is an unpublished
     * {@code prepare-*} directory, which is never loaded as a cache entry:
     * entries are named by their identity digest.
     */
    static void removeTree(Path root, Cancel cancel) {
        removeTree(root, cancel, new int[] {Core.treeEntries});
    }

    /** {@link #removeTree(Path, Cancel)}, removing at most {@code budget[0]} entries and charging each removal to it. */
    private static void removeTree(Path root, Cancel cancel, int[] budget) {
        if (!Files.exists(root, java.nio.file.LinkOption.NOFOLLOW_LINKS)) {
            return;
        }
        int[] removed = {0};
        int cap = budget[0];
        try {
            Files.walkFileTree(root, new java.nio.file.SimpleFileVisitor<>() {
                private java.nio.file.FileVisitResult remove(Path path) throws IOException {
                    if (stopped(cancel) || removed[0] >= cap) {
                        return java.nio.file.FileVisitResult.TERMINATE;
                    }
                    Files.deleteIfExists(path);
                    removed[0]++;
                    java.util.function.Consumer<Path> hook = cleanupHook;
                    if (hook != null) {
                        hook.accept(path);
                    }
                    return java.nio.file.FileVisitResult.CONTINUE;
                }

                @Override
                public java.nio.file.FileVisitResult preVisitDirectory(Path directory, BasicFileAttributes attributes) {
                    return stopped(cancel) || removed[0] >= cap ? java.nio.file.FileVisitResult.TERMINATE
                            : java.nio.file.FileVisitResult.CONTINUE;
                }

                @Override
                public java.nio.file.FileVisitResult visitFile(Path file, BasicFileAttributes attributes) throws IOException {
                    return remove(file);
                }

                @Override
                public java.nio.file.FileVisitResult postVisitDirectory(Path directory, IOException error) throws IOException {
                    if (error != null) {
                        throw error;
                    }
                    return remove(directory);
                }
            });
        } catch (IOException | RuntimeException ignored) {
            // Left for the user to remove; it never becomes a cache entry.
        } finally {
            budget[0] -= removed[0];
        }
    }

    /**
     * Loads a cache entry, refusing an accidental or partial change: the
     * entry and everything in it must be owned by this user and writable by
     * no one else (on POSIX), every file but {@code program.json} must match
     * the digests it records, and every binding it records must equal what is
     * derived again from Core's raw outputs, which are among those verified
     * files. A coordinated rewrite of several files is accepted: only the
     * cache's owner can make one, and the owner is trusted.
     */
    private Program load(Path directory, Map<String, String> identity, Path executable, Cancel cancel) throws JacquardException {
        java.util.function.Function<String, JacquardException> fail = detail -> JacquardException.preparation("prepared cache "
                + directory + " is incomplete or changed (" + detail + "); remove that cache entry and prepare again");
        TreeMap<String, String> observed;
        try {
            // Regular files only, bounded, so no read below can block.
            observed = Core.tree(directory, cancel, Core.Access.OWNER_ONLY);
        } catch (JacquardException error) {
            throw fail.apply(error.getMessage());
        }
        if (stopped(cancel)) {
            throw JacquardException.preparation(Core.ABORTED);
        }
        if (!observed.containsKey("program.json")) {
            throw fail.apply("no program.json");
        }
        Metadata metadata = Metadata.fromJson(readVerified(directory, "program.json", null, METADATA_LIMIT, fail));
        if (metadata == null) {
            throw fail.apply("unreadable program.json");
        }
        observed.remove("program.json");
        if (!metadata.identity().equals(identity) || !observed.equals(metadata.files())) {
            throw fail.apply("cached artifact digests do not match");
        }
        if (stopped(cancel)) {
            throw JacquardException.preparation(Core.ABORTED);
        }
        Bindings derived;
        try {
            List<String> preludeHashes = new ArrayList<>();
            for (String file : PRELUDE_FILES) {
                preludeHashes.add(text(readVerified(directory, preludeOutput(file), observed, Core.STDOUT_LIMIT, fail), fail));
            }
            derived = derive(text(readVerified(directory, CHECK_OUTPUT, observed, Core.STDOUT_LIMIT, fail), fail), preludeHashes,
                    text(readVerified(directory, HASH_OUTPUT, observed, Core.STDOUT_LIMIT, fail), fail));
        } catch (JacquardException error) {
            if (error.getMessage().startsWith("prepared cache ")) {
                throw error;
            }
            throw fail.apply("Core's cached output does not parse: " + error.getMessage());
        }
        if (stopped(cancel)) {
            throw JacquardException.preparation(Core.ABORTED);
        }
        Bindings recorded = new Bindings(metadata.names(), metadata.constructors(), metadata.signatures(), metadata.groups());
        if (!derived.equals(recorded)) {
            throw fail.apply("program.json bindings differ from Core's cached output");
        }
        Program program = new Program(this, directory, metadata, executable);
        Runnable hook = loadedHook;
        if (hook != null) {
            hook.run();
        }
        return program;
    }

    /**
     * The bytes of a regular file in a cache entry, at most {@code limit};
     * when {@code digests} is given, they must have the digest it records.
     */
    private static byte[] readVerified(Path directory, String name, Map<String, String> digests, int limit,
            java.util.function.Function<String, JacquardException> fail) throws JacquardException {
        if (digests != null && !digests.containsKey(name)) {
            throw fail.apply("no " + name);
        }
        byte[] bytes;
        try (InputStream in = Files.newInputStream(directory.resolve(name), java.nio.file.LinkOption.NOFOLLOW_LINKS)) {
            bytes = in.readNBytes(limit + 1);
        } catch (IOException error) {
            throw fail.apply(name + ": " + error.getMessage());
        }
        if (bytes.length > limit) {
            throw fail.apply(name + " is too long");
        }
        if (digests != null && !Core.digest(bytes).equals(digests.get(name))) {
            throw fail.apply("cached artifact digests do not match");
        }
        return bytes;
    }

    private static String text(byte[] bytes, java.util.function.Function<String, JacquardException> fail) throws JacquardException {
        try {
            return StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                    .decode(java.nio.ByteBuffer.wrap(bytes))
                    .toString();
        } catch (java.nio.charset.CharacterCodingException error) {
            throw fail.apply("Core's cached output is not UTF-8");
        }
    }

    /** The prepared metadata in {@code program.json}. */
    record Metadata(Map<String, String> identity, List<Identities.Entry> names, List<Identities.Entry> constructors,
            Map<String, String> signatures, List<List<Identities.Entry>> groups, Map<String, String> files) {

        private static Json strings(Map<String, String> map) {
            TreeMap<String, Json> fields = new TreeMap<>(Json.CODE_POINT_ORDER);
            map.forEach((key, value) -> fields.put(key, Json.of(value)));
            return Json.of(fields);
        }

        private static Json pairs(List<Identities.Entry> entries) {
            List<Json> items = new ArrayList<>();
            for (Identities.Entry entry : entries) {
                items.add(Json.of(List.of(Json.of(entry.name()), Json.of(entry.hash().hex()))));
            }
            return Json.of(items);
        }

        String toJson() {
            List<Json> groupList = new ArrayList<>();
            for (List<Identities.Entry> group : groups) {
                List<Json> entries = new ArrayList<>();
                for (Identities.Entry entry : group) {
                    entries.add(Json.of(Json.object("hash", Json.of(entry.hash().hex()), "name", Json.of(entry.name()))));
                }
                groupList.add(Json.of(entries));
            }
            Map<String, Json> object = Json.object(
                    "constructors", pairs(constructors),
                    "files", strings(files),
                    "groups", Json.of(groupList),
                    "identity", strings(identity),
                    "names", pairs(names),
                    "signatures", strings(signatures));
            try {
                return JsonCodec.stringify(Json.of(object), 8);
            } catch (ProtocolException error) {
                throw new IllegalStateException("bounded metadata", error);
            }
        }

        private static Map<String, String> map(Map<String, Json> object, String key) {
            Json value = object.get(key);
            Map<String, Json> fields = value == null ? null : value.asObject();
            if (fields == null) {
                return null;
            }
            TreeMap<String, String> out = new TreeMap<>(Json.CODE_POINT_ORDER);
            for (Map.Entry<String, Json> entry : fields.entrySet()) {
                String text = entry.getValue().asString();
                if (text == null) {
                    return null;
                }
                out.put(entry.getKey(), text);
            }
            return out;
        }

        private static Identities.Entry entry(String name, Json hash) {
            String hex = hash == null ? null : hash.asString();
            if (name == null || hex == null) {
                return null;
            }
            try {
                return new Identities.Entry(name, Hash.parse(hex));
            } catch (ProtocolException error) {
                return null;
            }
        }

        private static List<Identities.Entry> pairs(Map<String, Json> object, String key) {
            Json value = object.get(key);
            List<Json> items = value == null ? null : value.asArray();
            if (items == null) {
                return null;
            }
            List<Identities.Entry> out = new ArrayList<>();
            for (Json item : items) {
                List<Json> pair = item.asArray();
                Identities.Entry entry = pair == null || pair.size() != 2 ? null : entry(pair.get(0).asString(), pair.get(1));
                if (entry == null) {
                    return null;
                }
                out.add(entry);
            }
            return out;
        }

        /** The metadata, or null when it is not what {@link #toJson()} writes. */
        static Metadata fromJson(byte[] text) {
            Map<String, Json> object;
            try {
                object = JsonCodec.decodeObject(text, 8);
            } catch (ProtocolException error) {
                return null;
            }
            Json groupsValue = object.get("groups");
            List<Json> groupItems = groupsValue == null ? null : groupsValue.asArray();
            if (groupItems == null) {
                return null;
            }
            List<List<Identities.Entry>> groups = new ArrayList<>();
            for (Json group : groupItems) {
                List<Json> entries = group.asArray();
                if (entries == null) {
                    return null;
                }
                List<Identities.Entry> parsed = new ArrayList<>();
                for (Json entry : entries) {
                    Map<String, Json> fields = entry.asObject();
                    Identities.Entry value = fields == null || fields.get("name") == null ? null
                            : entry(fields.get("name").asString(), fields.get("hash"));
                    if (value == null) {
                        return null;
                    }
                    parsed.add(value);
                }
                groups.add(List.copyOf(parsed));
            }
            Map<String, String> identity = map(object, "identity");
            List<Identities.Entry> names = pairs(object, "names");
            List<Identities.Entry> constructors = pairs(object, "constructors");
            Map<String, String> signatures = map(object, "signatures");
            Map<String, String> files = map(object, "files");
            if (identity == null || names == null || constructors == null || signatures == null || files == null) {
                return null;
            }
            return new Metadata(identity, names, constructors, signatures, groups, files);
        }
    }

    @Override
    public String toString() {
        return "Runtime[binary=" + binary + ", prelude=" + prelude + "]";
    }
}
