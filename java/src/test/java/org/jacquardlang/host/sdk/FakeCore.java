package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.fail;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.util.List;
import java.util.stream.Stream;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.carrier.Cancel;

/**
 * A fake Core: a shell script that logs its first argument, answers
 * {@code --version}, and runs a case for each other command. Every process
 * a case leaves behind records its pid in {@code pids}; {@link #close()}
 * kills them and removes the directory.
 */
final class FakeCore implements AutoCloseable {
    final Path root;
    final Runtime runtime;

    private FakeCore(Path root, Runtime runtime) {
        this.root = root;
        this.runtime = runtime;
    }

    /**
     * A fake whose {@code --version} prints {@code version} and leaves a
     * sleeper holding its output, and whose other commands run {@code cases}
     * (shell {@code case} arms). {@code $ROOT} names the fake's directory.
     */
    static FakeCore create(Path scratch, String version, String versionCase, String cases) throws Exception {
        Path root = Files.createDirectories(scratch);
        Files.createDirectories(root.resolve("prelude"));
        Files.writeString(root.resolve("prelude/01-prim.jqd"), "prelude\n");
        Files.writeString(root.resolve("rules.jac"), "rules\n");
        String script = "#!/bin/sh\nROOT='" + root + "'\necho \"$1\" >> \"$ROOT/log\"\ncase \"$1\" in\n--version) "
                + (versionCase != null ? versionCase : "echo 'jacquard " + version + "'; sleep 30 & echo $! >> \"$ROOT/pids\"") + " ;;\n"
                + cases + "\nesac\n";
        Path binary = root.resolve("jacquard");
        Files.writeString(binary, script);
        Files.setPosixFilePermissions(binary, PosixFilePermissions.fromString("rwx------"));
        Runtime runtime = new Runtime(RuntimeOptions.defaults().withBinary(binary.toString()).withPrelude(root.resolve("prelude")));
        return new FakeCore(root, runtime);
    }

    /** A fake whose {@code check} fails with {@code boom}. */
    static FakeCore failing(Path scratch, String version) throws Exception {
        return create(scratch, version, null, "check) echo 'boom' >&2; exit 3 ;;");
    }

    /** A fake whose {@code check} and {@code hash} succeed and whose {@code run} fails. */
    static FakeCore hashing(Path scratch) throws Exception {
        String hash = "0".repeat(64);
        return create(scratch, Compat.CORE_VERSION, null, "check) ;;\nhash) echo '0:x " + hash + "' ;;\nrun) exit 4 ;;");
    }

    static final String FIRST = "a".repeat(64);
    static final String SECOND = "b".repeat(64);

    /**
     * A fake that prepares every source as two functions, {@code first} and
     * {@code second}, with identities {@link #FIRST} and {@link #SECOND}, over
     * a one-type prelude.
     */
    static FakeCore programs(Path scratch) throws Exception {
        return create(scratch, Compat.CORE_VERSION, null, "check) echo 'first : () ->{} Int'; echo 'second : () ->{} Int' ;;\n"
                + "hash) case \"$2\" in */prelude/*) echo '0:int " + "1".repeat(64) + "' ;; *) echo '0:first " + FIRST + "'; echo '1:second "
                + SECOND + "' ;; esac ;;\n"
                + "run) mkdir -p \"$4\" && echo objects > \"$4/objects\" ;;");
    }

    PrepareOptions options() {
        return new PrepareOptions(root.resolve("cache"));
    }

    Path source(String name) throws IOException {
        Path source = root.resolve(name);
        if (!Files.exists(source)) {
            Files.writeString(source, name + "\n");
        }
        return source;
    }

    /** Prepares {@code rules.jac}, which must fail. */
    JacquardException prepareFails() throws Exception {
        return prepareFails("rules.jac", new Cancel());
    }

    JacquardException prepareFails(String name, Cancel cancel) throws Exception {
        try {
            runtime.prepare(List.of(source(name)), options(), cancel);
        } catch (JacquardException error) {
            return error;
        }
        fail("the fake Core preparation succeeded");
        return null;
    }

    long calls(String command) throws IOException {
        Path log = root.resolve("log");
        if (!Files.exists(log)) {
            return 0;
        }
        return Files.readAllLines(log, StandardCharsets.UTF_8).stream().filter(command::equals).count();
    }

    boolean stagingLeft() throws IOException {
        Path cache = root.resolve("cache");
        if (!Files.exists(cache)) {
            return false;
        }
        try (Stream<Path> entries = Files.list(cache)) {
            return entries.anyMatch(entry -> entry.getFileName().toString().startsWith("prepare-"));
        }
    }

    /** Waits for {@code name} to hold a complete pid line and returns it. */
    long pid(String name) throws Exception {
        Path file = root.resolve(name);
        long deadline = System.nanoTime() + Duration.ofMinutes(1).toNanos();
        while (true) {
            String text = Files.exists(file) ? Files.readString(file) : "";
            if (text.endsWith("\n") && !text.isBlank()) {
                return Long.parseLong(text.strip().split("\\s+")[0]);
            }
            if (System.nanoTime() > deadline) {
                fail(name + " was never written");
            }
            Thread.sleep(5);
        }
    }

    static boolean alive(long pid) {
        return ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false);
    }

    /** Waits for {@code pid} to exit, failing if it outlives ten seconds; the fakes' children live for five minutes. */
    static void dies(String what, long pid) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
        while (alive(pid)) {
            if (System.nanoTime() > deadline) {
                fail(what + " is still running");
            }
            Thread.sleep(10);
        }
    }

    @Override
    public void close() throws IOException {
        try (Stream<Path> files = Files.list(root)) {
            for (Path file : files.filter(path -> path.getFileName().toString().endsWith("pids") || path.getFileName().toString().endsWith(".pid")).toList()) {
                for (String pid : Files.readString(file).split("\\s+")) {
                    if (!pid.isEmpty()) {
                        ProcessHandle.of(Long.parseLong(pid)).ifPresent(ProcessHandle::destroyForcibly);
                    }
                }
            }
        }
    }
}
