package org.jacquardlang.host.kit;

import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.TreeMap;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.CarrierException;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.carrier.Worker;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.Limits;

/**
 * Drives the kit's recordings, including the intentional hostile host
 * inputs, against a worker through the Java carrier, and compares what Core
 * sends with each recording, modulo non-normative diagnostic prose.
 */
public final class Replayer {
    private Replayer() {}

    private static final List<String> PROSE = List.of("summary", "cause", "next_step", "contrast");
    private static final String DIAGNOSTIC_SCHEMA = "jacquard-diagnostic-v1";

    private static String kindOf(Map<String, Json> frame) {
        Json kind = frame.get("kind");
        return kind == null ? null : kind.asString();
    }

    private static boolean isTerminal(Map<String, Json> frame) {
        String kind = kindOf(frame);
        return "outcome".equals(kind) || "fatal".equals(kind) || "shutdown_ack".equals(kind);
    }

    private static boolean isDiagnostic(Map<String, Json> object) {
        return Json.of(DIAGNOSTIC_SCHEMA).equals(object.get("schema"));
    }

    private static Json stripProse(Json value) {
        return switch (value) {
            case Json.Arr array -> {
                List<Json> items = new ArrayList<>();
                for (Json item : array.items()) {
                    items.add(stripProse(item));
                }
                yield Json.of(items);
            }
            case Json.Obj object -> {
                boolean diagnostic = isDiagnostic(object.fields());
                TreeMap<String, Json> fields = new TreeMap<>(Json.CODE_POINT_ORDER);
                for (Map.Entry<String, Json> entry : object.fields().entrySet()) {
                    if (!(diagnostic && PROSE.contains(entry.getKey()))) {
                        fields.put(entry.getKey(), stripProse(entry.getValue()));
                    }
                }
                yield Json.of(fields);
            }
            default -> value;
        };
    }

    private static List<String> proseDrift(Json expected, Json actual, String path) {
        List<String> drift = new ArrayList<>();
        if (expected instanceof Json.Arr items && actual instanceof Json.Arr others) {
            for (int i = 0; i < items.items().size(); i++) {
                drift.addAll(proseDrift(items.items().get(i), i < others.items().size() ? others.items().get(i) : null, path + "/" + i));
            }
        } else if (expected instanceof Json.Obj fields && actual instanceof Json.Obj others) {
            if (isDiagnostic(fields.fields())) {
                for (String key : PROSE) {
                    if (!canonicalOption(fields.fields().get(key)).equals(canonicalOption(others.fields().get(key)))) {
                        drift.add(path + "/" + key);
                    }
                }
            } else {
                for (Map.Entry<String, Json> entry : fields.fields().entrySet()) {
                    drift.addAll(proseDrift(entry.getValue(), others.fields().get(entry.getKey()), path + "/" + entry.getKey()));
                }
            }
        }
        return drift;
    }

    /** Mirrors JavaScript's {@code String()} for the JSON values {@code kit.ts} coerces. */
    public static String jsString(Json value) {
        return switch (value) {
            case Json.Str text -> text.value();
            case Json.Null ignored -> "null";
            case Json.Bool flag -> Boolean.toString(flag.value());
            case Json.Int number -> Long.toString(number.value());
            case Json.Arr array -> {
                List<String> parts = new ArrayList<>();
                for (Json item : array.items()) {
                    parts.add(item instanceof Json.Null ? "" : jsString(item));
                }
                yield String.join(",", parts);
            }
            case Json.Obj ignored -> "[object Object]";
        };
    }

    /**
     * Compares decoded frames. Frames are at most {@code max_json_depth} deep,
     * so the 256 bound never refuses one; keys are sorted by code point,
     * matching the kit's canonical JSON.
     */
    private static String canonical(Json value) {
        try {
            return JsonCodec.stringify(value, 256);
        } catch (ProtocolException error) {
            return "unencodable: " + error.getMessage();
        }
    }

    /** An absent prose field compares equal to null, as in {@code kit.ts}. */
    private static String canonicalOption(Json value) {
        return canonical(value == null ? Json.NULL : value);
    }

    private static Json frames(List<Map<String, Json>> list) {
        List<Json> items = new ArrayList<>();
        for (Map<String, Json> frame : list) {
            items.add(Json.of(frame));
        }
        return Json.of(items);
    }

    private static byte[] hexBytes(String text) throws ReplayFailure {
        if (text.length() % 2 != 0 || !text.chars().allMatch(c -> Character.digit(c, 16) >= 0 && c < 0x80)) {
            throw new ReplayFailure("raw_hex is not hexadecimal");
        }
        return HexFormat.of().parseHex(text);
    }

    private static final class ReplayFailure extends Exception {
        private static final long serialVersionUID = 1L;

        ReplayFailure(String message) {
            super(message);
        }
    }

    private static final class Drive {
        final List<Map<String, Json>> observed = new ArrayList<>();
        /** The most frames kept: one past the recording's, enough to show a divergence. */
        final int limit;
        boolean ended;
        long skipped;
        Json refusal = Json.NULL;

        Drive(int limit) {
            this.limit = limit;
        }

        /**
         * Reads one frame. A worker that keeps sending past the recording is
         * not read further, so it cannot fill memory before the deadline;
         * disposal then kills it.
         */
        boolean read(Worker worker) throws CarrierException, ReplayFailure {
            if (observed.size() >= limit) {
                throw new ReplayFailure("Core sent more frames than the recording");
            }
            Map<String, Json> frame = worker.readFrame(Limits.HARD);
            if (frame == null) {
                return false;
            }
            ended |= isTerminal(frame);
            observed.add(frame);
            return true;
        }

        void run(Worker worker, Transcript transcript) throws ReplayFailure {
            try {
                for (Step step : transcript.hostInput()) {
                    if (step.direction().equals("core_to_host")) {
                        read(worker);
                    } else if (step.rawHex() != null) {
                        worker.writeBytes(hexBytes(step.rawHex()));
                        worker.closeInput();
                    } else if (step.afterTerminal() && ended) {
                        refusal = Json.of("E1608");
                    } else if (ended) {
                        skipped++;
                    } else {
                        byte[] data;
                        try {
                            data = JsonCodec.frameBytes(step.frame(), Limits.HARD.maxFrameBytes(), Limits.HARD.maxJsonDepth());
                        } catch (ProtocolException error) {
                            throw new ReplayFailure(error.getMessage());
                        }
                        try {
                            worker.writeBytes(data);
                        } catch (CarrierException error) {
                            // Core may close its input before a hostile recording ends.
                            if (!(error.isLoss() && String.valueOf(error.getMessage()).startsWith("worker write failed"))) {
                                throw error;
                            }
                        }
                    }
                }
                worker.closeInput();
                while (read(worker)) {
                    // Read to end of stream.
                }
                worker.awaitExit();
            } catch (CarrierException error) {
                throw new ReplayFailure(error.getMessage());
            }
        }
    }

    /**
     * Drives one recording against the worker and compares what Core sends
     * with the recording, modulo non-normative diagnostic prose.
     */
    public static Replay replayOne(Transcript transcript, String store, Options options, Cancel cancel) {
        List<String> differences = new ArrayList<>();
        Worker worker;
        try {
            worker = Worker.start(store, options, cancel);
        } catch (IllegalArgumentException error) {
            differences.add(error.getMessage());
            return new Replay(transcript.name(), false, "diverges", differences, List.of(), Map.of(), List.of(), null, null, new byte[0]);
        }
        Drive drive = new Drive(transcript.coreOutput().size() + 1);
        try {
            drive.run(worker, transcript);
        } catch (ReplayFailure error) {
            differences.add(error.getMessage());
        } finally {
            worker.dispose();
        }
        List<Map<String, Json>> observed = drive.observed;
        Integer exitStatus = worker.exitStatus();

        Map<String, Json> end = Map.of();
        for (Map<String, Json> frame : observed) {
            if (isTerminal(frame)) {
                end = frame;
                break;
            }
        }
        String endKind = kindOf(end);
        Map<String, Json> result = null;
        if ("outcome".equals(endKind) && end.get("result") != null) {
            result = end.get("result").asObject();
        }
        Json diagnostics = null;
        if ("fatal".equals(endKind)) {
            diagnostics = end.get("diagnostics");
        } else if (result != null && Json.of("error").equals(result.get("kind"))) {
            diagnostics = result.get("diagnostics");
        }
        Map<String, Json> diagnostic = Map.of();
        if (diagnostics != null && diagnostics.asArray() != null && !diagnostics.asArray().isEmpty()
                && diagnostics.asArray().get(0).asObject() != null) {
            diagnostic = diagnostics.asArray().get(0).asObject();
        }
        Json primary = diagnostic.getOrDefault("code", Json.NULL);
        Json terminalName;
        if ("fatal".equals(endKind)) {
            terminalName = Json.of("fatal");
        } else if ("shutdown_ack".equals(endKind)) {
            terminalName = Json.of("shutdown");
        } else {
            terminalName = Json.NULL;
            Json evidence = end.get("evidence");
            if (evidence != null && evidence.asObject() != null) {
                Json core = evidence.asObject().get("core");
                if (core != null && core.asObject() != null) {
                    terminalName = core.asObject().getOrDefault("terminal", Json.NULL);
                }
            }
        }
        long requests = observed.stream().filter(frame -> "effect_request".equals(kindOf(frame))).count();
        long terminals = observed.stream().filter(Replayer::isTerminal).count();
        Map<String, Json> summary = Json.object(
                "effect_requests", Json.of(requests),
                "host_inputs_skipped_after_terminal", Json.of(drive.skipped),
                "host_local_refusal", drive.refusal,
                "primary_code", primary,
                "terminal", terminalName,
                "terminal_frames", Json.of(terminals),
                "terminal_kind", end.getOrDefault("kind", Json.NULL));
        Map<String, Json> expect = transcript.expect();
        boolean sameFrames = canonical(stripProse(frames(observed))).equals(canonical(stripProse(frames(transcript.coreOutput()))));
        boolean conforms;
        if (transcript.kind().equals("positive")) {
            // As in kit.ts, an absent expectation never equals an observed value or null.
            conforms = terminalName.equals(expect.get("terminal")) && exitIs(exitStatus, 0) && sameFrames;
            for (Step step : transcript.hostInput()) {
                if (Json.of("effect_failure").equals(step.frame().get("kind"))) {
                    Json message = step.frame().get("message");
                    if (message != null) {
                        String expected = jsString(message);
                        Json cause = diagnostic.get("cause");
                        conforms &= cause != null && cause.asString() != null && cause.asString().endsWith(expected);
                    }
                    break;
                }
            }
        } else {
            Json maximumField = expect.get("terminal_frames_max");
            Long maximum = maximumField == null ? null : maximumField.asInt();
            boolean forbid = Json.of(true).equals(expect.get("forbid_outside_action"));
            int exit = Json.of("E1611").equals(expect.get("code")) ? 74 : 0;
            conforms = Json.of(requests).equals(expect.get("effect_requests_before_failure"))
                    && maximum != null && terminals <= maximum
                    && (!forbid || requests == 0)
                    && exitIs(exitStatus, exit);
            if (transcript.phase().equals("terminal")) {
                conforms &= drive.refusal.equals(expect.get("code")) && terminalName.equals(Json.of("ok"));
            } else {
                conforms &= primary.equals(expect.get("code"));
            }
        }
        String status = conforms ? "conforms" : "diverges";
        if (!status.equals(transcript.status())) {
            differences.add("conformance status differs from the recording");
        }
        if (!exitIs(exitStatus, transcript.exitStatus())) {
            differences.add("exit status differs from the recording");
        }
        if (!canonical(Json.of(summary)).equals(canonical(Json.of(transcript.observed())))) {
            differences.add("observed summary differs from the recording");
        }
        if (!sameFrames) {
            differences.add("Core frames differ semantically from the recording");
        }
        List<String> drift = proseDrift(frames(transcript.coreOutput()), frames(observed), "");
        return new Replay(transcript.name(), differences.isEmpty(), status, differences, drift, summary, observed, exitStatus,
                worker.signal(), worker.stderr());
    }

    private static boolean exitIs(Integer exitStatus, long code) {
        return exitStatus != null && Objects.equals((long) exitStatus, code);
    }

    /** Replays every recording in order, loading the pinned kit when {@code kit} is null. */
    public static List<Replay> replayKit(String store, Options options, Kit kit, Cancel cancel) throws KitException {
        Kit loaded = kit != null ? kit : Kit.load();
        List<Replay> replays = new ArrayList<>();
        for (Transcript transcript : loaded.transcripts()) {
            replays.add(replayOne(transcript, store, options, cancel));
        }
        return replays;
    }
}
