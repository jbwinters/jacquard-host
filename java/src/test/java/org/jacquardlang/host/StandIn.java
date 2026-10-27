package org.jacquardlang.host;

import java.io.DataInputStream;
import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.kit.Kit;
import org.jacquardlang.host.kit.Step;
import org.jacquardlang.host.kit.Transcript;

/**
 * A recorded or faulty worker for the offline tests. It implements no Core
 * semantics and is not part of the adapter's interface. The descendant modes
 * use {@code sleep} and {@code sh}, so they are Unix-only.
 */
public final class StandIn {
    /** Names the stand-in behaviour. */
    static final String MODE_VAR = "JACQUARD_JAVA_STANDIN";
    /** Names the transcript replayed in {@code replay} mode. */
    static final String CASE_VAR = "JACQUARD_JAVA_STANDIN_CASE";

    private static final OutputStream OUT = new FileOutputStream(FileDescriptor.out);
    private static final OutputStream ERR = new FileOutputStream(FileDescriptor.err);
    private static final DataInputStream IN = new DataInputStream(new FileInputStream(FileDescriptor.in));

    private StandIn() {}

    public static void main(String[] args) throws Exception {
        String mode = env(MODE_VAR);
        switch (mode) {
            case "exit" -> System.exit(0);
            case "silent" -> block();
            case "truncated" -> {
                OUT.write(new byte[] {0, 0, 0});
                OUT.flush();
                System.exit(0);
            }
            case "descendant" -> {
                // Leave a descendant holding stdout, like a launcher that does not exec.
                Process child = spawn(List.of("sleep", "30"), ProcessBuilder.Redirect.INHERIT, ProcessBuilder.Redirect.DISCARD);
                report(child.pid());
                System.exit(0);
            }
            case "child-silent" -> {
                // A child holding stdout and stderr while the worker stays alive.
                Process child = spawn(List.of("sleep", "30"), ProcessBuilder.Redirect.INHERIT, ProcessBuilder.Redirect.INHERIT);
                report(child.pid());
                block();
            }
            case "grandchild-silent" -> {
                // A grandchild holding the pipes under a shell that waits for it.
                spawn(List.of("sh", "-c", "sleep 30 & echo $! >&2; wait"), ProcessBuilder.Redirect.INHERIT,
                        ProcessBuilder.Redirect.INHERIT);
                block();
            }
            case "daemon-silent" -> {
                // A descendant reparented while the worker is alive: its shell exits at once.
                daemonize(">/dev/null");
                block();
            }
            case "stderr-descendant", "stderr-descendant-nonzero" -> {
                // Answer normally, but leave a child holding only stderr.
                Process child = spawn(List.of("sleep", "30"), ProcessBuilder.Redirect.DISCARD, ProcessBuilder.Redirect.INHERIT);
                report(child.pid());
                System.exit(fault(kit(), mode.equals("stderr-descendant") ? "ok" : "nonzero"));
            }
            case "escaped-descendant", "escaped-descendant-unknown-field" -> {
                // A daemonized descendant holds stderr; it is not the worker's
                // descendant by the time the host could look for it.
                daemonize(">/dev/null");
                System.exit(fault(kit(), mode.equals("escaped-descendant") ? "ok" : "unknown-field"));
            }
            case "stdin-descendant" -> {
                // A child holds stdin and never reads; the worker reads the
                // selection and exits, so the invoke write can only block.
                ProcessBuilder builder = new ProcessBuilder("sleep", "30")
                        .redirectInput(ProcessBuilder.Redirect.INHERIT)
                        .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                        .redirectError(ProcessBuilder.Redirect.DISCARD);
                report(builder.start().pid());
                Kit kit = kit();
                send(kit.transcripts().get(0).coreOutput().get(0));
                readFrame();
                System.exit(0);
            }
            case "replay" -> System.exit(replay(kit(), env(CASE_VAR)));
            case "" -> System.exit(2);
            default -> System.exit(fault(kit(), mode));
        }
    }

    private static String env(String name) {
        String value = System.getenv(name);
        return value == null ? "" : value;
    }

    private static Process spawn(List<String> command, ProcessBuilder.Redirect stdout, ProcessBuilder.Redirect stderr) {
        try {
            return new ProcessBuilder(command)
                    .redirectInput(ProcessBuilder.Redirect.from(new java.io.File("/dev/null")))
                    .redirectOutput(stdout)
                    .redirectError(stderr)
                    .start();
        } catch (IOException error) {
            System.exit(3);
            return null;
        }
    }

    /** Starts {@code sleep 30} through a shell that exits at once, reporting its pid on stderr. */
    private static void daemonize(String stdout) throws InterruptedException {
        Process shell = spawn(List.of("sh", "-c", "sleep 30 </dev/null " + stdout + " & echo $! >&2"),
                ProcessBuilder.Redirect.INHERIT, ProcessBuilder.Redirect.INHERIT);
        shell.waitFor();
    }

    private static void report(long pid) throws IOException {
        ERR.write(Long.toString(pid).getBytes(StandardCharsets.US_ASCII));
        ERR.flush();
    }

    private static Kit kit() {
        try {
            return Kit.load();
        } catch (Exception error) {
            System.exit(3);
            return null;
        }
    }

    private static void send(Map<String, Json> frame) {
        try {
            byte[] payload = JsonCodec.stringify(Json.of(frame), 256).getBytes(StandardCharsets.UTF_8);
            OUT.write(ByteBuffer.allocate(4 + payload.length).putInt(payload.length).put(payload).array());
            OUT.flush();
        } catch (Exception error) {
            // The host may have closed the pipe.
        }
    }

    private static boolean discard(long count) {
        try {
            // Read rather than skip: FileInputStream.skip seeks, which a pipe refuses.
            byte[] chunk = new byte[8192];
            for (long left = count; left > 0; ) {
                int n = IN.read(chunk, 0, (int) Math.min(chunk.length, left));
                if (n < 0) {
                    return false;
                }
                left -= n;
            }
            return true;
        } catch (IOException error) {
            return false;
        }
    }

    private static boolean readFrame() {
        try {
            int length = IN.readInt();
            return discard(Integer.toUnsignedLong(length));
        } catch (IOException error) {
            return false;
        }
    }

    private static int replay(Kit kit, String name) {
        Transcript transcript = null;
        for (Transcript candidate : kit.transcripts()) {
            if (candidate.name().equals(name)) {
                transcript = candidate;
            }
        }
        if (transcript == null) {
            return 3;
        }
        int index = 0;
        boolean raw = false;
        for (Step step : transcript.hostInput()) {
            if (step.direction().equals("core_to_host")) {
                Map<String, Json> frame = transcript.coreOutput().get(index++);
                send(frame);
                Json kind = frame.get("kind");
                String sent = kind == null ? "" : kind.asString();
                if ("outcome".equals(sent) || "fatal".equals(sent) || "shutdown_ack".equals(sent)) {
                    return (int) transcript.exitStatus();
                }
            } else if (step.rawHex() != null) {
                discard(step.rawHex().length() / 2);
                raw = true;
            } else if (!step.afterTerminal() && !raw) {
                readFrame();
            }
        }
        return (int) transcript.exitStatus();
    }

    private static void block() throws InterruptedException {
        while (true) {
            Thread.sleep(3_600_000);
        }
    }

    private static int fault(Kit kit, String mode) throws Exception {
        Transcript pure = kit.transcripts().get(0);
        Map<String, Json> hello = new TreeMap<>(pure.coreOutput().get(0));
        Map<String, Json> outcome = pure.coreOutput().get(1);
        if (mode.equals("stderr")) {
            byte[] noise = new byte[2 * 1024 * 1024];
            Arrays.fill(noise, (byte) 'x');
            ERR.write(noise);
            ERR.flush();
        }
        if (mode.equals("unknown-field")) {
            hello.put("extra", Json.of(1));
        }
        send(hello);
        if (mode.equals("oversized-early")) {
            // Without waiting for host_select, send a frame larger than the
            // selected max_frame_bytes but within the hard limit.
            send(Json.object("padding", Json.of("x".repeat(4096))));
        }
        if (mode.equals("no-read")) {
            block();
        }
        int count = 1;
        while (readFrame()) {
            if (count == 2) {
                if (mode.equals("invalid-hash")) {
                    outcome = withCallable(outcome, "A".repeat(64));
                }
                send(outcome);
                if (mode.equals("extra")) {
                    send(outcome);
                }
                if (mode.equals("stall")) {
                    block();
                }
                closeStdout();
                if (mode.equals("slow-exit")) {
                    Thread.sleep(1500);
                }
                if (mode.equals("hang-after-close")) {
                    block();
                }
                return mode.equals("nonzero") ? 74 : 0;
            }
            count++;
        }
        return 0;
    }

    private static Map<String, Json> withCallable(Map<String, Json> outcome, String callable) {
        Map<String, Json> evidence = new TreeMap<>(outcome.get("evidence").asObject());
        Map<String, Json> core = new TreeMap<>(evidence.get("core").asObject());
        Map<String, Json> target = new TreeMap<>(core.get("target").asObject());
        target.put("callable", Json.of(callable));
        core.put("target", Json.of(target));
        evidence.put("core", Json.of(core));
        Map<String, Json> changed = new TreeMap<>(outcome);
        changed.put("evidence", Json.of(evidence));
        return changed;
    }

    /**
     * Closes descriptor 1 so the host's pipe sees end of stream before exit:
     * the JDK replaces a closed standard descriptor with /dev/null.
     */
    private static void closeStdout() throws IOException {
        OUT.flush();
        OUT.close();
    }
}
