package org.jacquardlang.host.carrier;

import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.Map;

/**
 * Run by {@code CommandTest} in a JVM that is a child subreaper, so orphans
 * of its commands are reparented to it. Prints {@code subreaper true|false}
 * (whether an orphan of a plain child became this JVM's child), the exit
 * status and stdout of {@code sh -c 'sleep 300 & echo $!'}, and whether that
 * child is alive ten seconds later at most. Each line is one fact, so the
 * test asserts on them exactly.
 */
public final class SubreaperMain {
    private SubreaperMain() {}

    public static void main(String[] arguments) throws Exception {
        long self = ProcessHandle.current().pid();
        Process plain = new ProcessBuilder("/bin/sh", "-c", "(sleep 30 & echo $!)")
                .redirectInput(ProcessBuilder.Redirect.from(new File("/dev/null"))).redirectErrorStream(true).start();
        long orphan;
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(plain.getInputStream(), StandardCharsets.UTF_8))) {
            orphan = Long.parseLong(reader.readLine().strip());
        }
        plain.waitFor();
        boolean reparented = ProcessHandle.of(orphan).flatMap(ProcessHandle::parent).map(parent -> parent.pid() == self).orElse(false);
        ProcessHandle.of(orphan).ifPresent(ProcessHandle::destroyForcibly);
        System.out.println("subreaper " + reparented);
        Command.Output output = Command.run(List.of("/bin/sh", "-c", "sleep 300 & echo $!"), Map.of(), Duration.ofMinutes(1),
                4096, 4096, new Cancel());
        String stdout = new String(output.stdout(), StandardCharsets.UTF_8).strip();
        System.out.println("status " + output.exitStatus());
        System.out.println("child " + stdout);
        long child = Long.parseLong(stdout);
        long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
        while (alive(child) && System.nanoTime() < deadline) {
            Thread.sleep(10);
        }
        System.out.println("alive " + alive(child));
        ProcessHandle.of(child).ifPresent(ProcessHandle::destroyForcibly);
    }

    /** Alive and not a zombie: an orphan killed here stays a zombie child of this JVM until it exits. */
    private static boolean alive(long pid) {
        try {
            String stat = Files.readString(Path.of("/proc/" + pid + "/stat"), StandardCharsets.ISO_8859_1);
            String state = stat.substring(stat.lastIndexOf(')') + 1).trim().split(" ")[0];
            return !state.equals("Z") && !state.equals("X");
        } catch (Exception error) {
            return false;
        }
    }
}
