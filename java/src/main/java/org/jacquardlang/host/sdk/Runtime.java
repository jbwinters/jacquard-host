package org.jacquardlang.host.sdk;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Comparator;
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
import java.util.stream.Stream;

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
    static final String PROGRAM_SCHEMA = "jacquard-java-program-v1";
    private static final long POLL_MIN = TimeUnit.MICROSECONDS.toNanos(100);
    private static final long POLL_MAX = TimeUnit.MILLISECONDS.toNanos(10);
    private static final List<String> PRELUDE_FILES = List.of("01-prim.jqd", "02-data.jqd", "19-audit.jqd");

    /** One discovery: empty until a run succeeds; a failed run leaves it empty for the next caller. */
    private static final class Flight {
        List<List<Identities.Entry>> groups;
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
        List<List<Identities.Entry>> run() throws JacquardException;
    }

    /**
     * Runs {@code work} once per key. Concurrent callers wait for one run,
     * each observing its own {@code cancel} and interrupt status (which the
     * wait never clears); the run itself is bounded by the command timeout. A
     * successful result stays cached, and a failed run leaves the next caller
     * to try again.
     */
    private static List<List<Identities.Entry>> once(String key, Cancel cancel, Work work) throws JacquardException {
        Flight flight = FLIGHTS.computeIfAbsent(key, ignored -> new Flight());
        long idle = POLL_MIN;
        boolean waiting = false;
        while (true) {
            synchronized (flight) {
                if (flight.groups != null) {
                    return flight.groups;
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
            List<List<Identities.Entry>> groups = work.run();
            synchronized (flight) {
                flight.groups = groups;
            }
            return groups;
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

    private List<List<Identities.Entry>> preludeGroups(Path executable, String key, Cancel cancel) throws JacquardException {
        return once("prelude:" + key, cancel, () -> {
            List<List<Identities.Entry>> groups = new ArrayList<>();
            for (String name : PRELUDE_FILES) {
                groups.addAll(Identities.hashGroups(command(executable, cancel, "hash", prelude.resolve(name).toString())));
            }
            return List.copyOf(groups);
        });
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
            return prepareChecked(sources, options, cancel);
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
        if (sources.isEmpty()) {
            throw usage;
        }
        String syntax = extension(sources.get(0));
        if (!(".jac".equals(syntax) || ".jqd".equals(syntax)) || sources.stream().anyMatch(source -> !syntax.equals(extension(source)))) {
            throw usage;
        }
        Path cache = (options.cacheDir() == null ? Path.of(DEFAULT_CACHE_DIR) : options.cacheDir()).toAbsolutePath();
        Path executable = Core.executable(binary);
        String binaryHash;
        try {
            binaryHash = Core.fileDigest(executable);
        } catch (IOException error) {
            throw JacquardException.preparation("cannot read " + executable + ": " + error.getMessage());
        }
        checkVersion(executable, binaryHash, cancel);
        String preludeHash;
        try {
            preludeHash = Core.mapDigest(Core.tree(prelude));
        } catch (JacquardException error) {
            throw JacquardException.preparation("cannot read prelude " + prelude + ": " + error.getMessage());
        }
        ByteArrayOutputStream content = new ByteArrayOutputStream();
        for (int i = 0; i < sources.size(); i++) {
            if (i > 0) {
                content.write('\n');
            }
            try {
                content.write(Files.readAllBytes(sources.get(i)));
            } catch (IOException error) {
                throw JacquardException.preparation("cannot read " + sources.get(i) + ": " + error.getMessage());
            }
        }
        byte[] bytes = content.toByteArray();
        TreeMap<String, String> identity = new TreeMap<>(Json.CODE_POINT_ORDER);
        identity.put("binary", binaryHash);
        identity.put("prelude", preludeHash);
        identity.put("protocol", Compat.PROTOCOL);
        identity.put("schema", PROGRAM_SCHEMA);
        identity.put("source", Core.digest(bytes));
        identity.put("syntax", syntax);
        Path directory = cache.resolve(Core.mapDigest(identity));
        try {
            Files.createDirectories(cache);
        } catch (IOException error) {
            throw JacquardException.preparation("cannot create " + cache + ": " + error.getMessage());
        }
        if (Files.exists(directory)) {
            return load(directory, identity, executable);
        }
        Path staging = stagingDirectory(cache);
        try {
            Path snapshot = staging.resolve("source" + syntax);
            write(snapshot, bytes, "cannot write the source snapshot");
            String output = command(executable, cancel, "check", snapshot.toString(), "--print-sigs");
            Map<String, String> signatures = new TreeMap<>(Json.CODE_POINT_ORDER);
            for (String line : output.split("\n", -1)) {
                String trimmed = line.strip();
                int split = trimmed.indexOf(" : ");
                if (split >= 0) {
                    String name = trimmed.substring(0, split);
                    if (name.indexOf(' ') < 0 && name.indexOf('\t') < 0) {
                        signatures.put(name, trimmed.substring(split + 3));
                    }
                }
            }
            List<List<Identities.Entry>> groups = new ArrayList<>(preludeGroups(executable, binaryHash + preludeHash, cancel));
            groups.addAll(Identities.hashGroups(command(executable, cancel, "hash", snapshot.toString())));
            Identities.checkGroups(groups);
            Path store = staging.resolve("store");
            command(executable, cancel, "run", snapshot.toString(), "--store", store.toString());
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
            constructors.forEach((name, hash) -> constructorList.add(new Identities.Entry(name, hash)));
            Metadata metadata = new Metadata(identity, names, constructorList, signatures, groups, Core.tree(staging));
            write(staging.resolve("program.json"), metadata.toJson().getBytes(StandardCharsets.UTF_8), "cannot write program.json");
            try {
                Files.move(staging, directory, StandardCopyOption.ATOMIC_MOVE);
            } catch (AtomicMoveNotSupportedException error) {
                throw JacquardException.preparation("cannot publish " + directory + ": " + error.getMessage());
            } catch (IOException error) {
                // A concurrent preparation published the same artifact first.
                if (!Files.exists(directory)) {
                    throw JacquardException.preparation("cannot publish " + directory + ": " + error.getMessage());
                }
            }
        } finally {
            removeTree(staging);
        }
        return load(directory, identity, executable);
    }

    private static String extension(Path source) {
        Path name = source.getFileName();
        String text = name == null ? "" : name.toString();
        int dot = text.lastIndexOf('.');
        return dot <= 0 ? "" : text.substring(dot);
    }

    private static void write(Path path, byte[] bytes, String context) throws JacquardException {
        try {
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
                return Files.createDirectory(path);
            } catch (FileAlreadyExistsException error) {
                // Try the next name.
            } catch (IOException error) {
                throw JacquardException.preparation("cannot create " + path + ": " + error.getMessage());
            }
        }
        throw JacquardException.preparation("cannot create a staging directory");
    }

    /** Removes a staging directory unless it was published (and so no longer exists). */
    private static void removeTree(Path root) {
        if (!Files.exists(root)) {
            return;
        }
        try (Stream<Path> walk = Files.walk(root)) {
            for (Path path : walk.sorted(Comparator.reverseOrder()).toList()) {
                Files.deleteIfExists(path);
            }
        } catch (IOException | RuntimeException ignored) {
            // Left for the user to remove; it never becomes a cache entry.
        }
    }

    private Program load(Path directory, Map<String, String> identity, Path executable) throws JacquardException {
        java.util.function.Function<String, JacquardException> fail = detail -> JacquardException.preparation("prepared cache "
                + directory + " is incomplete or changed (" + detail + "); remove that cache entry and prepare again");
        byte[] text;
        try {
            text = Files.readAllBytes(directory.resolve("program.json"));
        } catch (IOException error) {
            throw fail.apply(String.valueOf(error.getMessage()));
        }
        Metadata metadata = Metadata.fromJson(text);
        if (metadata == null) {
            throw fail.apply("unreadable program.json");
        }
        TreeMap<String, String> observed;
        try {
            observed = Core.tree(directory);
        } catch (JacquardException error) {
            throw fail.apply(error.getMessage());
        }
        observed.remove("program.json");
        if (!metadata.identity().equals(identity) || !observed.equals(metadata.files())) {
            throw fail.apply("cached artifact digests do not match");
        }
        Identities.checkGroups(metadata.groups());
        return new Program(this, directory, metadata, executable);
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
