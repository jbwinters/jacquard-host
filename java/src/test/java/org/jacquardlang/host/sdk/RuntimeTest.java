package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermission;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.stream.Stream;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.protocol.Limits;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.DisabledOnOs;
import org.junit.jupiter.api.condition.EnabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * Runtime options, and preparation against a fake Core: one version check and
 * prelude discovery per key across threads and runtimes, a child holding a
 * command's output failing it and killed, every Core command bounded by its
 * timeout, its output limit, the caller's {@link Cancel}, and its interrupt,
 * bounded regular-file reads, and a verified cache.
 */
class RuntimeTest {
    @TempDir
    Path scratch;

    @Test
    void runtimeOptionsAreValidatedBeforeCoreRuns() throws Exception {
        RuntimeOptions base = RuntimeOptions.defaults().withPrelude(scratch);
        JacquardException deadline = assertThrows(JacquardException.class, () -> new Runtime(base.withDeadline(Duration.ZERO)));
        assertEquals(JacquardException.Kind.BINDING, deadline.kind());
        JacquardException limits = assertThrows(JacquardException.class, () -> new Runtime(base.withLimits(Limits.HARD.with("max_frame_bytes", 0))));
        assertTrue(limits.getMessage().startsWith("invalid runtime limits"), limits.getMessage());
        Runtime runtime = new Runtime(base.withBinary("/no/such/jacquard"));
        for (List<Path> sources : List.of(List.of(Path.of("rules.txt")), List.<Path>of(), List.of(Path.of("a.jac"), Path.of("b.jqd")))) {
            JacquardException error = assertThrows(JacquardException.class, () -> runtime.prepare(sources, PrepareOptions.defaults()));
            assertEquals(JacquardException.Kind.PREPARATION, error.kind(), sources.toString());
        }
        JacquardException missing = assertThrows(JacquardException.class, () -> runtime.prepare(List.of(Path.of("rules.jac")), PrepareOptions.defaults()));
        assertTrue(missing.getMessage().contains("cannot find Core executable /no/such/jacquard"), missing.getMessage());
        assertEquals(0, runtime.coreCommands());
        // A name longer than any path is refused with an excerpt, never used or copied whole.
        Runtime huge = new Runtime(base.withBinary("/" + "x".repeat(1_000_000)));
        String refused = assertThrows(JacquardException.class, () -> huge.prepare(List.of(Path.of("rules.jac")), PrepareOptions.defaults())).getMessage();
        assertTrue(refused.startsWith("cannot find Core executable /" + "x".repeat(63) + "... (1000001 chars)"), refused.substring(0, 100));
        // A shorter missing name is quoted as an excerpt too, and a NUL, which no path holds, is a preparation failure.
        String longer = assertThrows(JacquardException.class, () -> new Runtime(base.withBinary("/no/such/" + "y".repeat(1000)))
                .prepare(List.of(Path.of("rules.jac")), PrepareOptions.defaults())).getMessage();
        assertTrue(longer.length() < 200 && longer.contains("... (1009 chars)"), longer);
        // Paths longer than any path are refused with an excerpt before the file system sees them.
        Path long1 = Path.of("/" + "s".repeat(1_000_000) + ".jac");
        String source = assertThrows(JacquardException.class, () -> runtime.prepare(List.of(long1), PrepareOptions.defaults())).getMessage();
        assertTrue(source.startsWith("source /sss") && source.endsWith("(1000005 chars) is longer than any path"), source.substring(0, 80));
        String cache = assertThrows(JacquardException.class, () -> runtime.prepare(List.of(Path.of("rules.jac")),
                new PrepareOptions(Path.of("/" + "c".repeat(1_000_000))))).getMessage();
        assertTrue(cache.length() < 200 && cache.startsWith("cache directory /ccc"), cache.substring(0, 80));
        JacquardException prelude = assertThrows(JacquardException.class, () -> new Runtime(base.withPrelude(Path.of("/" + "p".repeat(1_000_000)))));
        assertEquals(JacquardException.Kind.BINDING, prelude.kind());
        assertTrue(prelude.getMessage().length() < 200, prelude.getMessage().substring(0, 80));
        for (String nul : List.of("jac\0quard", "/tmp/jac\0quard")) {
            JacquardException error = assertThrows(JacquardException.class, () -> new Runtime(base.withBinary(nul))
                    .prepare(List.of(Path.of("rules.jac")), PrepareOptions.defaults()));
            assertEquals(JacquardException.Kind.PREPARATION, error.kind());
            assertTrue(error.getMessage().startsWith("cannot find Core executable"), error.getMessage());
        }
    }

    @Test
    void emptyRuntimeOptionsMeanUnset() throws Exception {
        Runtime runtime = new Runtime(new RuntimeOptions("", scratch, null, null));
        String bin = System.getenv("JACQUARD_BIN");
        assertEquals(bin == null || bin.isEmpty() ? "jacquard" : bin, runtime.binary());
        assertEquals(scratch.toAbsolutePath(), runtime.prelude());
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void versionIsCheckedOnceAcrossThreadsAndRuntimes() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("once"), Compat.CORE_VERSION)) {
            List<CompletableFuture<JacquardException>> workers = new ArrayList<>();
            for (int i = 0; i < 4; i++) {
                workers.add(CompletableFuture.supplyAsync(() -> {
                    try {
                        return fake.prepareFails();
                    } catch (Exception error) {
                        throw new IllegalStateException(error);
                    }
                }));
            }
            for (CompletableFuture<JacquardException> worker : workers) {
                JacquardException error = worker.get(60, TimeUnit.SECONDS);
                assertEquals(JacquardException.Kind.PREPARATION, error.kind());
                assertEquals("Core check failed: boom", error.getMessage());
            }
            assertEquals(1, fake.calls("--version"));
            assertEquals(4, fake.calls("check"));
            assertFalse(fake.stagingLeft(), "a staging directory was left behind");
            // Another runtime for the same Core shares the check.
            Runtime other = new Runtime(RuntimeOptions.defaults().withBinary(fake.root.resolve("jacquard").toString())
                    .withPrelude(fake.root.resolve("prelude")));
            assertThrows(JacquardException.class, () -> other.prepare(List.of(fake.source("rules.jac")), fake.options()));
            assertEquals(1, fake.calls("--version"));
            assertEquals(1, other.coreCommands());
        }
    }

    /**
     * A Core command whose child keeps its output open is not held by it: on
     * Linux the command's group is killed when it exits, so the child dies
     * and preparation goes on (and here fails at {@code run}).
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void aCoreCommandsChildHoldingItsOutputIsKilledAtTheExit() throws Exception {
        org.junit.jupiter.api.Assumptions.assumeTrue(Files.isExecutable(Path.of("/usr/bin/setsid")) || Files.isExecutable(Path.of("/bin/setsid")));
        String hash = "0".repeat(64);
        try (FakeCore fake = FakeCore.create(scratch.resolve("holder"), Compat.CORE_VERSION, null,
                "check) sleep 300 & echo $! > \"$ROOT/holder.pid\" ;;\nhash) echo '0:x " + hash + "' ;;\nrun) exit 4 ;;")) {
            assertEquals("Core run failed: exit status 4", fake.prepareFails().getMessage());
            FakeCore.dies("the command's child", fake.pid("holder.pid"));
            assertFalse(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aVersionMismatchIsRefusedAndCheckedAgain() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("mismatch"), "9.9.9")) {
            for (int i = 0; i < 2; i++) {
                String message = fake.prepareFails().getMessage();
                assertTrue(message.contains("expected pinned Core version") && message.contains("jacquard 9.9.9"), message);
            }
            assertEquals(2, fake.calls("--version"));
            assertEquals(0, fake.calls("check"));
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void preludeDiscoveryRunsOncePerCoreAndPrelude() throws Exception {
        try (FakeCore fake = FakeCore.hashing(scratch.resolve("prelude"))) {
            for (String source : List.of("rules.jac", "other.jac")) {
                JacquardException error = fake.prepareFails(source, new Cancel());
                assertEquals("Core run failed: exit status 4", error.getMessage());
            }
            // Three prelude files once, then one local hash per source.
            assertEquals(5, fake.calls("hash"));
            assertEquals(1, fake.calls("--version"));
            assertEquals(2, fake.calls("run"));
            assertFalse(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void coreOutputBeyondItsLimitIsRefused() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("overflow"), Compat.CORE_VERSION, null, "check) head -c 9000000 /dev/zero ;;")) {
            assertEquals("Core check output exceeds 8 MiB", fake.prepareFails().getMessage());
        }
    }

    private static final String SLOW_CHECK = "check) sleep 300 & echo $! > \"$ROOT/child.pid\"; wait ;;";

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aCoreCommandThatOutlivesItsTimeoutIsKilledWithItsChild() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("timeout"), Compat.CORE_VERSION, null, SLOW_CHECK)) {
            fake.runtime.commandTimeout = Duration.ofMillis(500);
            assertEquals("Core check failed: no exit within 500 ms", fake.prepareFails().getMessage());
            FakeCore.dies("the timed-out command's child", fake.pid("child.pid"));
            assertFalse(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void cancellationStopsTheRunningCoreCommand() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("cancel"), Compat.CORE_VERSION, null, SLOW_CHECK)) {
            Cancel cancel = new Cancel();
            Thread canceller = Thread.ofPlatform().daemon().start(() -> {
                try {
                    fake.pid("child.pid");
                } catch (Exception error) {
                    return;
                }
                cancel.cancel();
            });
            JacquardException error = fake.prepareFails("rules.jac", cancel);
            assertEquals("Core check failed: preparation was cancelled or interrupted", error.getMessage());
            FakeCore.dies("the cancelled command's child", fake.pid("child.pid"));
            canceller.join();
            // The cleanup observes the stop too, so the unpublished staging directory stays.
            assertTrue(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void interruptionStopsTheRunningCoreCommandAndKeepsTheInterrupt() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("interrupt"), Compat.CORE_VERSION, null, SLOW_CHECK)) {
            Thread caller = Thread.currentThread();
            Thread interrupter = Thread.ofPlatform().daemon().start(() -> {
                try {
                    fake.pid("child.pid");
                } catch (Exception error) {
                    return;
                }
                caller.interrupt();
            });
            JacquardException error = fake.prepareFails("rules.jac", new Cancel());
            boolean interrupted = Thread.interrupted();
            assertTrue(interrupted, "the interrupt status was cleared");
            assertEquals("Core check failed: preparation was cancelled or interrupted", error.getMessage());
            FakeCore.dies("the interrupted command's child", fake.pid("child.pid"));
            interrupter.join();
        }
    }

    /**
     * One preparation runs a slow {@code --version}; another waits for that
     * check. The waiter observes its own cancellation, or its interrupt (which
     * it keeps), while the first still runs, and never runs the command.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aPreparationWaitingForAnotherObservesItsCancelAndInterrupt() throws Exception {
        String slowVersion = "sleep 300 & echo $! > \"$ROOT/version.pid\"; wait; echo 'jacquard " + Compat.CORE_VERSION + "'";
        try (FakeCore fake = FakeCore.create(scratch.resolve("waiter"), Compat.CORE_VERSION, slowVersion, "check) exit 3 ;;")) {
            Cancel first = new Cancel();
            CompletableFuture<JacquardException> owner = CompletableFuture.supplyAsync(() -> {
                try {
                    return fake.prepareFails("rules.jac", first);
                } catch (Exception error) {
                    throw new IllegalStateException(error);
                }
            });
            long sleeper = fake.pid("version.pid");
            // The seam stops each waiter only once it waits, so the wait itself must notice.
            Cancel cancelled = new Cancel();
            try {
                Runtime.flightWaitHook = cancelled::cancel;
                assertEquals("preparation was cancelled or interrupted", fake.prepareFails("rules.jac", cancelled).getMessage());
                Runtime.flightWaitHook = () -> Thread.currentThread().interrupt();
                JacquardException interrupted = fake.prepareFails("rules.jac", new Cancel());
                assertTrue(Thread.interrupted(), "the waiter cleared its interrupt");
                assertEquals("preparation was cancelled or interrupted", interrupted.getMessage());
            } finally {
                Runtime.flightWaitHook = null;
            }
            assertTrue(FakeCore.alive(sleeper), "the first preparation's command stopped");
            assertEquals(1, fake.calls("--version"));
            first.cancel();
            assertEquals("Core --version failed: preparation was cancelled or interrupted", owner.get(60, TimeUnit.SECONDS).getMessage());
            FakeCore.dies("the first preparation's command", sleeper);
        }
    }
    private static void mkfifo(Path path) throws Exception {
        Process mkfifo = new ProcessBuilder("mkfifo", path.toString()).redirectErrorStream(true).start();
        assertTrue(mkfifo.waitFor(30, TimeUnit.SECONDS) && mkfifo.exitValue() == 0, "mkfifo failed");
    }

    /**
     * Prepares on another thread, which must finish within a minute. If it is
     * blocked opening {@code fifo}, a writer is opened to release it and the
     * test fails.
     */
    private static JacquardException preparesWithoutBlocking(FakeCore fake, List<Path> sources, Path fifo) throws Exception {
        CompletableFuture<JacquardException> preparing = CompletableFuture.supplyAsync(() -> {
            try {
                fake.runtime.prepare(sources, fake.options());
                return null;
            } catch (JacquardException error) {
                return error;
            }
        });
        try {
            JacquardException error = preparing.get(60, TimeUnit.SECONDS);
            assertTrue(error != null, "the preparation succeeded");
            return error;
        } catch (java.util.concurrent.TimeoutException blocked) {
            try (var writer = Files.newOutputStream(fifo)) {
                writer.write('\n');
            }
            preparing.get(60, TimeUnit.SECONDS);
            throw new AssertionError("the preparation blocked on " + fifo);
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aSourceThatIsNotARegularFileIsRefusedWithoutBlocking() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("kinds"), Compat.CORE_VERSION)) {
            Path fifo = fake.root.resolve("fifo.jac");
            mkfifo(fifo);
            JacquardException error = preparesWithoutBlocking(fake, List.of(fifo), fifo);
            assertEquals("cannot read " + fifo + ": not a regular file", error.getMessage());
            Path directory = Files.createDirectory(fake.root.resolve("directory.jac"));
            assertEquals("cannot read " + directory + ": not a regular file",
                    assertThrows(JacquardException.class, () -> fake.runtime.prepare(List.of(directory), fake.options())).getMessage());
            assertEquals(0, fake.calls("check"));
            // A symbolic link to a regular file is read.
            Path link = Files.createSymbolicLink(fake.root.resolve("link.jac"), fake.source("rules.jac"));
            assertEquals("Core check failed: boom",
                    assertThrows(JacquardException.class, () -> fake.runtime.prepare(List.of(link), fake.options())).getMessage());
        }
    }

    /**
     * A sparse file of {@code length} bytes that nobody may read, so a
     * refusal that is not "cannot read" was made from its size alone.
     */
    private static Path sparse(Path path, long length) throws Exception {
        try (java.io.RandomAccessFile file = new java.io.RandomAccessFile(path.toFile(), "rw")) {
            file.setLength(length);
        }
        Files.setPosixFilePermissions(path, java.util.Set.of());
        return path;
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void sourcesOverTheCeilingAreRefusedBeforeTheyAreRead() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("ceiling"), Compat.CORE_VERSION)) {
            List<Path> read = new java.util.concurrent.CopyOnWriteArrayList<>();
            Runtime.sourceReadHook = read::add;
            try {
                Path one = sparse(fake.root.resolve("one.jac"), Runtime.SOURCE_LIMIT + 1);
                assertEquals("sources exceed 16 MiB in total",
                        assertThrows(JacquardException.class, () -> fake.runtime.prepare(List.of(one), fake.options())).getMessage());
                Path half = sparse(fake.root.resolve("half.jac"), Runtime.SOURCE_LIMIT / 2);
                Files.setPosixFilePermissions(half, java.util.Set.of(java.nio.file.attribute.PosixFilePermission.OWNER_READ));
                Path other = sparse(fake.root.resolve("other.jac"), Runtime.SOURCE_LIMIT / 2);
                assertEquals("sources exceed 16 MiB in total",
                        assertThrows(JacquardException.class, () -> fake.runtime.prepare(List.of(half, other), fake.options())).getMessage());
                assertEquals(List.of(half), read, "a source past the ceiling was read");
                assertEquals(0, fake.calls("check"));
            } finally {
                Runtime.sourceReadHook = null;
            }
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aCancelOrInterruptIsObservedBetweenSourceFiles() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("between"), Compat.CORE_VERSION)) {
            List<Path> sources = List.of(fake.source("a.jac"), fake.source("b.jac"));
            List<Path> read = new java.util.concurrent.CopyOnWriteArrayList<>();
            Cancel cancel = new Cancel();
            try {
                Runtime.sourceReadHook = path -> {
                    read.add(path);
                    cancel.cancel();
                };
                assertEquals(Core.ABORTED, assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options(), cancel)).getMessage());
                assertEquals(List.of(sources.get(0)), read);
                read.clear();
                Runtime.sourceReadHook = path -> {
                    read.add(path);
                    Thread.currentThread().interrupt();
                };
                assertEquals(Core.ABORTED, assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options())).getMessage());
                assertTrue(Thread.interrupted(), "the interrupt status was cleared");
                assertEquals(List.of(sources.get(0)), read);
            } finally {
                Runtime.sourceReadHook = null;
                Thread.interrupted();
            }
            assertEquals(0, fake.calls("check"));
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void thePreludeWalkReadsOnlyBoundedRegularFiles() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("walk"), Compat.CORE_VERSION)) {
            Path big = sparse(fake.root.resolve("prelude/zz-big.jqd"), Core.TREE_LIMIT + 1);
            String message = fake.prepareFails().getMessage();
            assertTrue(message.startsWith("cannot read prelude") && message.endsWith("exceed 256 MiB"), message);
            Files.delete(big);
            Path fifo = fake.root.resolve("prelude/zz-fifo.jqd");
            mkfifo(fifo);
            message = preparesWithoutBlocking(fake, List.of(fake.source("rules.jac")), fifo).getMessage();
            assertTrue(message.endsWith("unsupported filesystem entry in prepared artifacts: zz-fifo.jqd"), message);
            Files.delete(fifo);
            Files.writeString(fake.root.resolve("prelude/02-more.jqd"), "more\n");
            List<Path> digested = new java.util.concurrent.CopyOnWriteArrayList<>();
            Cancel cancel = new Cancel();
            Core.treeFileHook = path -> {
                digested.add(path);
                cancel.cancel();
            };
            try {
                String stopped = fake.prepareFails("rules.jac", cancel).getMessage();
                assertTrue(stopped.endsWith(Core.ABORTED), stopped);
            } finally {
                Core.treeFileHook = null;
            }
            // The walk streams each directory, so either file may come first.
            assertEquals(1, digested.size(), digested.toString());
            assertEquals(0, fake.calls("check"));
        }
    }
    /** Replaces every occurrence of {@code from}, which must occur, in {@code file}. */
    private static void replace(Path file, String from, String to) throws Exception {
        String text = Files.readString(file);
        assertTrue(text.contains(from), from + " is not in " + file);
        Files.writeString(file, text.replace(from, to));
    }

    /**
     * A warm preparation loads the entry without Core. A change to any
     * binding in {@code program.json} (a name's identity, a group, a
     * signature) is refused because the bindings are derived again from
     * Core's cached output; a change to that output is refused by its digest,
     * and so is a rewrite of the output with its recorded digest when the
     * bindings no longer match it.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aChangedCacheEntryIsRefusedIncludingItsMetadata() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("verified"))) {
            List<Path> sources = List.of(fake.source("rules.jac"));
            Program program = fake.runtime.prepare(sources, fake.options());
            assertEquals(FakeCore.FIRST, program.data().identity("first").hex());
            assertTrue(Files.isRegularFile(program.directory().resolve(Runtime.HASH_OUTPUT)));
            int commands = fake.runtime.coreCommands();
            assertEquals(program.directory(), fake.runtime.prepare(sources, fake.options()).directory());
            assertEquals(commands, fake.runtime.coreCommands(), "a warm preparation ran Core");
            Path metadata = program.directory().resolve("program.json");
            String original = Files.readString(metadata);
            List<List<String>> changes = List.of(
                    List.of("[\"first\",\"" + FakeCore.FIRST + "\"]", "[\"first\",\"" + FakeCore.SECOND + "\"]"),
                    List.of("{\"hash\":\"" + FakeCore.FIRST + "\",\"name\":\"first\"}", "{\"hash\":\"" + FakeCore.SECOND + "\",\"name\":\"first\"}"),
                    List.of("\"first\":\"() ->{} Int\"", "\"first\":\"() ->{} Text\""));
            for (List<String> change : changes) {
                replace(metadata, change.get(0), change.get(1));
                JacquardException error = assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options()),
                        "a changed program.json was accepted: " + change);
                assertTrue(error.getMessage().contains("program.json bindings differ from Core's cached output"), error.getMessage());
                Files.writeString(metadata, original);
            }
            assertEquals(program.directory(), fake.runtime.prepare(sources, fake.options()).directory());
            Path hash = program.directory().resolve(Runtime.HASH_OUTPUT);
            String output = Files.readString(hash);
            replace(hash, "0:first " + FakeCore.FIRST, "0:first " + FakeCore.SECOND);
            String changed = Files.readString(hash);
            JacquardException digest = assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options()));
            assertTrue(digest.getMessage().contains("cached artifact digests do not match"), digest.getMessage());
            replace(metadata, Core.digest(output.getBytes(java.nio.charset.StandardCharsets.UTF_8)),
                    Core.digest(changed.getBytes(java.nio.charset.StandardCharsets.UTF_8)));
            JacquardException rewritten = assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options()));
            assertTrue(rewritten.getMessage().contains("program.json bindings differ"), rewritten.getMessage());
            assertEquals(commands, fake.runtime.coreCommands());
        }
    }

    private static Set<PosixFilePermission> addGroupWrite(Path path) throws Exception {
        Set<PosixFilePermission> original = Files.getPosixFilePermissions(path, java.nio.file.LinkOption.NOFOLLOW_LINKS);
        Set<PosixFilePermission> widened = java.util.EnumSet.copyOf(original);
        widened.add(PosixFilePermission.GROUP_WRITE);
        Files.setPosixFilePermissions(path, widened);
        return original;
    }

    /**
     * A new cache entry is owner-only throughout (Core's store included), and
     * the cache root, the entry, or any file or directory in it that its
     * group may write is refused on load; restoring the permission makes the
     * entry load again.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aGroupWritableCacheEntryIsRefused() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("owner"))) {
            List<Path> sources = List.of(fake.source("rules.jac"));
            Program program = fake.runtime.prepare(sources, fake.options());
            Path entry = program.directory();
            List<Path> targets;
            try (Stream<Path> walk = Files.walk(entry)) {
                targets = walk.toList();
            }
            assertTrue(targets.contains(entry.resolve("store/objects")), targets.toString());
            for (Path path : targets) {
                Set<PosixFilePermission> permissions = Files.getPosixFilePermissions(path, java.nio.file.LinkOption.NOFOLLOW_LINKS);
                assertTrue(permissions.stream().allMatch(permission -> permission.name().startsWith("OWNER")),
                        path + " is not owner-only: " + permissions);
            }
            List<Path> checked = new ArrayList<>(List.of(fake.options().cacheDir().toAbsolutePath()));
            checked.addAll(List.of(entry, entry.resolve("program.json"), entry.resolve(Runtime.HASH_OUTPUT), entry.resolve("core"),
                    entry.resolve("store"), entry.resolve("store/objects")));
            for (Path path : checked) {
                Set<PosixFilePermission> original = addGroupWrite(path);
                JacquardException error = assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options()),
                        "a group-writable " + path + " was accepted");
                assertTrue(error.getMessage().contains(path + " is group- or world-writable"), error.getMessage());
                Files.setPosixFilePermissions(path, original);
            }
            assertEquals(entry, fake.runtime.prepare(sources, fake.options()).directory());
        }
    }

    /**
     * A cache root this user does not own is refused before anything is
     * written to it. The root directory stands in for a foreign owner (root);
     * a foreign-owned entry inside an owned root would need another account,
     * so it is not tested.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aForeignOwnedCacheRootIsRefused() throws Exception {
        // The test's own directory is owned by the user running it.
        org.junit.jupiter.api.Assumptions.assumeFalse(Integer.valueOf(0).equals(Files.getAttribute(scratch, "unix:uid")), "running as root");
        try (FakeCore fake = FakeCore.programs(scratch.resolve("foreign"))) {
            JacquardException error = assertThrows(JacquardException.class,
                    () -> fake.runtime.prepare(List.of(fake.source("rules.jac")), new PrepareOptions(Path.of("/"))));
            assertTrue(error.getMessage().contains("cache path / is not owned by the current user"), error.getMessage());
            assertEquals(0, fake.calls("check"));
        }
    }

    /** A runtime whose Core is {@code binary}, over {@code fake}'s prelude. */
    private static Runtime runtimeFor(FakeCore fake, Path binary) throws Exception {
        return new Runtime(RuntimeOptions.defaults().withBinary(binary.toString()).withPrelude(fake.root.resolve("prelude")));
    }

    /**
     * An executable over the 512 MiB fingerprint ceiling is refused from its
     * size: it is sparse and unreadable, so a read would fail with "cannot
     * read" instead.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void anExecutableOverTheFingerprintCeilingIsRefusedBeforeItIsRead() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("huge"), Compat.CORE_VERSION)) {
            Path huge = sparse(fake.root.resolve("huge-jacquard"), Core.EXECUTABLE_LIMIT + 1);
            Files.setPosixFilePermissions(huge, PosixFilePermissions.fromString("-wx------"));
            JacquardException error = assertThrows(JacquardException.class,
                    () -> runtimeFor(fake, huge).prepare(List.of(fake.source("rules.jac")), fake.options()));
            assertTrue(error.getMessage().startsWith("Core executable ") && error.getMessage().endsWith(" exceeds 512 MiB"), error.getMessage());
        }
    }

    /**
     * A cancel or an interrupt during fingerprinting stops it after the piece
     * being read (a test seam counts the pieces of a 16 MiB executable), the
     * interrupt kept.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aStopDuringFingerprintingIsObserved() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("digest"), Compat.CORE_VERSION)) {
            Path big = sparse(fake.root.resolve("big-jacquard"), 16L << 20);
            Files.setPosixFilePermissions(big, PosixFilePermissions.fromString("rwx------"));
            java.util.concurrent.atomic.AtomicInteger pieces = new java.util.concurrent.atomic.AtomicInteger();
            Cancel cancel = new Cancel();
            try {
                Core.digestChunkHook = () -> {
                    pieces.incrementAndGet();
                    cancel.cancel();
                };
                JacquardException error = assertThrows(JacquardException.class,
                        () -> runtimeFor(fake, big).prepare(List.of(fake.source("rules.jac")), fake.options(), cancel));
                assertEquals(Core.ABORTED, error.getMessage());
                assertEquals(1, pieces.get());
                pieces.set(0);
                Core.digestChunkHook = () -> {
                    pieces.incrementAndGet();
                    Thread.currentThread().interrupt();
                };
                error = assertThrows(JacquardException.class,
                        () -> runtimeFor(fake, big).prepare(List.of(fake.source("rules.jac")), fake.options()));
                assertTrue(Thread.interrupted(), "the interrupt status was cleared");
                assertEquals(Core.ABORTED, error.getMessage());
                assertEquals(1, pieces.get());
            } finally {
                Core.digestChunkHook = null;
                Thread.interrupted();
            }
            assertEquals(0, fake.calls("--version"));
        }
    }

    /**
     * A cancel after the first of two empty prelude files stops the walk
     * before the second, whichever comes first: an empty file has no piece
     * to read, so only the check before each entry can see the stop.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aStopBetweenEmptyFilesIsObserved() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("empty"), Compat.CORE_VERSION)) {
            Files.writeString(fake.root.resolve("prelude/01-prim.jqd"), "");
            Files.createFile(fake.root.resolve("prelude/02-empty.jqd"));
            List<Path> digested = new java.util.concurrent.CopyOnWriteArrayList<>();
            Cancel cancel = new Cancel();
            Core.treeFileHook = path -> {
                digested.add(path);
                cancel.cancel();
            };
            try {
                assertTrue(fake.prepareFails("rules.jac", cancel).getMessage().endsWith(Core.ABORTED));
            } finally {
                Core.treeFileHook = null;
            }
            assertEquals(1, digested.size(), digested.toString());
            assertEquals(0, fake.calls("check"));
        }
    }

    /** A tree over the entry cap is refused, zero-byte files counted (a test seam lowers the cap to 3). */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aTreeOverTheEntryCapIsRefused() throws Exception {
        assertEquals(100_000, Core.TREE_ENTRIES);
        try (FakeCore fake = FakeCore.failing(scratch.resolve("entries"), Compat.CORE_VERSION)) {
            for (String name : List.of("a.jqd", "b.jqd", "c.jqd")) {
                Files.createFile(fake.root.resolve("prelude").resolve(name));
            }
            Core.treeEntries = 3;
            try {
                String message = fake.prepareFails().getMessage();
                assertTrue(message.startsWith("cannot read prelude") && message.endsWith("more than 3 entries under " + fake.root.resolve("prelude")),
                        message);
            } finally {
                Core.treeEntries = Core.TREE_ENTRIES;
            }
            assertEquals(0, fake.calls("check"));
        }
    }

    /** A source list that counts the elements read, and fails a read past {@code limit}. */
    private static final class CountingList extends java.util.AbstractList<Path> {
        final java.util.concurrent.atomic.AtomicInteger reads = new java.util.concurrent.atomic.AtomicInteger();
        final List<Integer> indices = new java.util.concurrent.CopyOnWriteArrayList<>();
        private final int size;
        private final Path element;
        private final int limit;
        private final java.util.function.IntConsumer onRead;

        CountingList(int size, Path element, int limit, java.util.function.IntConsumer onRead) {
            this.size = size;
            this.element = element;
            this.limit = limit;
            this.onRead = onRead;
        }

        @Override
        public Path get(int index) {
            if (reads.incrementAndGet() > limit) {
                throw new AssertionError("read past the source cap");
            }
            indices.add(index);
            onRead.accept(index);
            return element;
        }

        @Override
        public int size() {
            return size;
        }
    }

    /**
     * A source list over 1,024 is refused from its size, before any element
     * is read; {@code Collections.nCopies(Integer.MAX_VALUE, ...)} hung the
     * extension scan before. A list of exactly 1,024 passes that check.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aHugeSourceListIsRefusedBeforeAnyElementIsRead() throws Exception {
        assertEquals(1024, Runtime.SOURCE_FILES);
        try (FakeCore fake = FakeCore.programs(scratch.resolve("count"))) {
            Path source = fake.source("rules.jac");
            CountingList huge = new CountingList(Integer.MAX_VALUE, source, 2048, index -> {});
            JacquardException error = assertThrows(JacquardException.class, () -> fake.runtime.prepare(huge, fake.options()));
            assertEquals("more than 1024 sources", error.getMessage());
            assertEquals(JacquardException.Kind.PREPARATION, error.kind());
            assertEquals(0, huge.reads.get());
            CountingList over = new CountingList(1025, source, 2048, index -> {});
            assertEquals("more than 1024 sources", assertThrows(JacquardException.class, () -> fake.runtime.prepare(over, fake.options())).getMessage());
            assertEquals(0, over.reads.get());
            CountingList most = new CountingList(1024, source, 2048, index -> {});
            fake.runtime.prepare(most, fake.options());
            assertEquals(1024, most.reads.get());
        }
    }

    /** A cancel or an interrupt set while the list is validated stops it before the next element is read. */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aStopDuringSourceValidationIsObserved() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("validate"))) {
            Path source = fake.source("rules.jac");
            Cancel cancel = new Cancel();
            CountingList cancelled = new CountingList(3, source, 3, index -> {
                if (index == 1) {
                    cancel.cancel();
                }
            });
            assertEquals(Core.ABORTED, assertThrows(JacquardException.class, () -> fake.runtime.prepare(cancelled, fake.options(), cancel)).getMessage());
            assertEquals(List.of(0, 1), cancelled.indices);
            CountingList interrupted = new CountingList(3, source, 3, index -> {
                if (index == 1) {
                    Thread.currentThread().interrupt();
                }
            });
            try {
                assertEquals(Core.ABORTED, assertThrows(JacquardException.class, () -> fake.runtime.prepare(interrupted, fake.options())).getMessage());
                assertTrue(Thread.interrupted(), "the interrupt status was cleared");
            } finally {
                Thread.interrupted();
            }
            assertEquals(List.of(0, 1), interrupted.indices);
            assertEquals(0, fake.calls("--version"));
        }
    }

    /** A fake whose {@code run} leaves ten files in its store and fails. */
    private static FakeCore failingStore(Path scratch) throws Exception {
        return FakeCore.create(scratch, Compat.CORE_VERSION, null, "check) echo 'first : () ->{} Int' ;;\n"
                + "hash) echo '0:first " + FakeCore.FIRST + "' ;;\n"
                + "run) mkdir -p \"$4\" && for i in 0 1 2 3 4 5 6 7 8 9; do : > \"$4/f$i\"; done; exit 4 ;;");
    }

    /** The cache's entries, each a staging directory or a published entry. */
    private static List<String> cacheEntries(FakeCore fake) throws Exception {
        try (Stream<Path> entries = Files.list(fake.root.resolve("cache"))) {
            return entries.map(path -> path.getFileName().toString()).sorted().toList();
        }
    }

    /**
     * A failed preparation's cleanup removes its staging directory, but a
     * cancel or an interrupt during the cleanup ends it at once (a test seam
     * sets the stop after the second removal), leaving the unpublished
     * staging directory; nothing is published either way.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aStopDuringTheFailureCleanupEndsIt() throws Exception {
        try (FakeCore fake = failingStore(scratch.resolve("cleanup"))) {
            List<Path> removed = new java.util.concurrent.CopyOnWriteArrayList<>();
            Runtime.cleanupHook = removed::add;
            try {
                assertTrue(fake.prepareFails().getMessage().endsWith("Core run failed: exit status 4"));
                assertEquals(13, removed.size(), removed.toString());
                assertEquals(List.of(), cacheEntries(fake));
                removed.clear();
                Cancel cancel = new Cancel();
                Runtime.cleanupHook = path -> {
                    removed.add(path);
                    if (removed.size() == 2) {
                        cancel.cancel();
                    }
                };
                assertEquals(Core.ABORTED, fake.prepareFails("rules.jac", cancel).getMessage());
                assertEquals(2, removed.size(), removed.toString());
                List<String> left = cacheEntries(fake);
                assertEquals(1, left.size(), left.toString());
                assertTrue(left.get(0).startsWith("prepare-"), left.toString());
                assertTrue(Files.exists(fake.root.resolve("cache").resolve(left.get(0)).resolve("store")));
                removed.clear();
                Runtime.cleanupHook = path -> {
                    removed.add(path);
                    if (removed.size() == 2) {
                        Thread.currentThread().interrupt();
                    }
                };
                assertEquals(Core.ABORTED, fake.prepareFails().getMessage());
                assertTrue(Thread.interrupted(), "the interrupt status was cleared");
                assertEquals(2, removed.size(), removed.toString());
                assertEquals(2, cacheEntries(fake).size());
                assertTrue(cacheEntries(fake).stream().allMatch(name -> name.startsWith("prepare-")), cacheEntries(fake).toString());
            } finally {
                Runtime.cleanupHook = null;
                Thread.interrupted();
            }
        }
    }

    /** The failure cleanup removes at most the tree cap's entries (a test seam lowers it to 3), leaving the rest unpublished. */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void theFailureCleanupStopsAtTheCap() throws Exception {
        try (FakeCore fake = failingStore(scratch.resolve("capped"))) {
            List<Path> removed = new java.util.concurrent.CopyOnWriteArrayList<>();
            Runtime.cleanupHook = removed::add;
            Core.treeEntries = 3;
            try {
                assertTrue(fake.prepareFails().getMessage().endsWith("Core run failed: exit status 4"));
            } finally {
                Core.treeEntries = Core.TREE_ENTRIES;
                Runtime.cleanupHook = null;
            }
            assertEquals(3, removed.size(), removed.toString());
            List<String> left = cacheEntries(fake);
            assertEquals(1, left.size(), left.toString());
            assertTrue(left.get(0).startsWith("prepare-"), left.toString());
        }
    }

    /**
     * A cache root reached through a symbolic link is checked as the real
     * directory it names (the link's own mode reads 0777), and its entries
     * live there; a group-writable real root is refused with a hint.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aSymlinkedCacheRootIsFollowedToItsRealDirectory() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("link"))) {
            Path real = Files.createDirectory(fake.root.resolve("real-cache"), PosixFilePermissions.asFileAttribute(Owner.DIRECTORY));
            Path link = Files.createSymbolicLink(fake.root.resolve("linked-cache"), real);
            Program program = fake.runtime.prepare(List.of(fake.source("rules.jac")), new PrepareOptions(link));
            assertEquals(real.toRealPath(), program.directory().getParent());
            try (Stream<Path> entries = Files.list(real)) {
                assertEquals(1L, entries.count());
            }
            assertEquals(program.directory(), fake.runtime.prepare(List.of(fake.source("rules.jac")), new PrepareOptions(link)).directory());
            Files.setPosixFilePermissions(real, PosixFilePermissions.fromString("rwxrwx---"));
            JacquardException error = assertThrows(JacquardException.class,
                    () -> fake.runtime.prepare(List.of(fake.source("rules.jac")), new PrepareOptions(link)));
            assertEquals("cache path " + real.toRealPath() + " is group- or world-writable; only its owner may write the cache"
                    + " (make it owner-only, for example chmod 700 " + real.toRealPath() + ")", error.getMessage());
        }
    }

    /**
     * A preparation removes only its own staging directory: directories it
     * did not make, whatever their names or ages, are left alone.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aPreparationLeavesDirectoriesItDidNotMake() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("foreign"))) {
            Path cache = Files.createDirectories(fake.root.resolve("cache"));
            Files.setPosixFilePermissions(cache, Owner.DIRECTORY);
            Path notes = Files.createDirectories(cache.resolve("prepare-release-notes"));
            Files.writeString(notes.resolve("draft.txt"), "keep");
            Path left = Files.createDirectories(cache.resolve("prepare-1-00000000-0"));
            java.nio.file.attribute.FileTime old = java.nio.file.attribute.FileTime.fromMillis(System.currentTimeMillis() - 86_400_000L);
            Files.setLastModifiedTime(notes, old);
            Files.setLastModifiedTime(left, old);
            Path entry = fake.runtime.prepare(List.of(fake.source("rules.jac")), fake.options()).directory();
            assertEquals("keep", Files.readString(notes.resolve("draft.txt")));
            assertTrue(Files.exists(left), "a directory the preparation did not make was removed");
            assertEquals(List.of(entry.getFileName().toString(), left.getFileName().toString(), notes.getFileName().toString())
                    .stream().sorted().toList(), cacheEntries(fake));
        }
    }

    /**
     * Once an entry is published its staging path is vacated, and whatever
     * appears there afterwards is not the preparation's: it is left alone.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aDirectoryAtTheVacatedStagingPathIsLeftAlone() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("vacated"))) {
            Path[] replacement = new Path[1];
            try {
                Runtime.publishedHook = staging -> {
                    try {
                        replacement[0] = Files.createDirectory(staging);
                        Files.writeString(staging.resolve("unrelated.txt"), "keep");
                    } catch (java.io.IOException error) {
                        throw new java.io.UncheckedIOException(error);
                    }
                };
                fake.runtime.prepare(List.of(fake.source("rules.jac")), fake.options());
            } finally {
                Runtime.publishedHook = null;
            }
            assertEquals("keep", Files.readString(replacement[0].resolve("unrelated.txt")), "the preparation removed a directory it did not make");
        }
    }

    /** A cancel or interrupt that lands as a warm load finishes fails the preparation instead of returning a program. */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aStopAsAWarmLoadFinishesIsReported() throws Exception {
        try (FakeCore fake = FakeCore.programs(scratch.resolve("loaded"))) {
            List<Path> sources = List.of(fake.source("rules.jac"));
            fake.runtime.prepare(sources, fake.options());
            Cancel cancel = new Cancel();
            try {
                Runtime.loadedHook = cancel::cancel;
                assertEquals(Core.ABORTED, assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options(), cancel)).getMessage());
                Runtime.loadedHook = () -> Thread.currentThread().interrupt();
                assertEquals(Core.ABORTED, assertThrows(JacquardException.class, () -> fake.runtime.prepare(sources, fake.options())).getMessage());
                assertTrue(Thread.interrupted(), "the interrupt status was cleared");
            } finally {
                Runtime.loadedHook = null;
                Thread.interrupted();
            }
            // Without a stop the same entry loads.
            fake.runtime.prepare(sources, fake.options());
        }
    }
}
