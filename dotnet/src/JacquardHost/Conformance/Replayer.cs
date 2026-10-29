using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Conformance;

/// <summary>The observed result of replaying one recording.</summary>
/// <param name="Name">The recording's name.</param>
/// <param name="MatchesRecording">Whether nothing differed from the recording.</param>
/// <param name="Status"><c>"conforms"</c> or <c>"diverges"</c>.</param>
/// <param name="Differences">Semantic differences from the recording.</param>
/// <param name="ProseDrift">Paths of non-normative diagnostic prose that changed.</param>
/// <param name="Summary">The observed summary, comparable with the recording's.</param>
/// <param name="CoreFrames">The frames Core sent, at most one past the recording's count.</param>
/// <param name="ExitStatus">The worker's exit status, when known.</param>
/// <param name="Signal"><c>"SIGKILL"</c> when the host killed the worker.</param>
/// <param name="Stderr">The retained stderr prefix.</param>
public sealed record Replay(
    string Name,
    bool MatchesRecording,
    string Status,
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> ProseDrift,
    JsonObject Summary,
    IReadOnlyList<JsonObject> CoreFrames,
    int? ExitStatus,
    string? Signal,
    IReadOnlyList<byte> Stderr);

/// <summary>
/// Drives the kit's recordings, including the intentional hostile host
/// inputs, against a worker through the .NET carrier, and compares what Core
/// sends with each recording, modulo non-normative diagnostic prose.
/// </summary>
public static class Replayer
{
    private const string DiagnosticSchema = "jacquard-diagnostic-v1";
    private static readonly string[] Prose = { "summary", "cause", "next_step", "contrast" };

    private static string? KindOf(JsonObject frame) => frame.Get("kind")?.AsString();

    private static bool IsTerminal(JsonObject frame) => KindOf(frame) is "outcome" or "fatal" or "shutdown_ack";

    private static bool IsDiagnostic(JsonObject obj) => Json.Of(DiagnosticSchema).Equals(obj.Get("schema"));

    private static Json StripProse(Json value) => value switch
    {
        JsonArray array => Json.Of(array.Items.Select(StripProse)),
        JsonObject obj => Json.Object(obj
            .Where(field => !(IsDiagnostic(obj) && Prose.Contains(field.Key, StringComparer.Ordinal)))
            .Select(field => new KeyValuePair<string, Json>(field.Key, StripProse(field.Value)))),
        _ => value,
    };

    private static List<string> ProseDriftOf(Json? expected, Json? actual, string path)
    {
        var drift = new List<string>();
        if (expected is JsonArray items && actual is JsonArray others)
        {
            for (int i = 0; i < items.Items.Count; i++)
            {
                drift.AddRange(ProseDriftOf(items.Items[i], i < others.Items.Count ? others.Items[i] : null, path + "/" + i.ToString(CultureInfo.InvariantCulture)));
            }
        }
        else if (expected is JsonObject fields && actual is JsonObject actualFields)
        {
            if (IsDiagnostic(fields))
            {
                foreach (string key in Prose)
                {
                    if (CanonicalOption(fields.Get(key)) != CanonicalOption(actualFields.Get(key)))
                    {
                        drift.Add(path + "/" + key);
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, Json> field in fields)
                {
                    drift.AddRange(ProseDriftOf(field.Value, actualFields.Get(field.Key), path + "/" + field.Key));
                }
            }
        }

        return drift;
    }

    /// <summary>Mirrors JavaScript's <c>String()</c> for the JSON values <c>kit.ts</c> coerces.</summary>
    public static string JsString(Json value) => value switch
    {
        JsonString text => text.Value,
        JsonNull => "null",
        JsonBool flag => flag.Value ? "true" : "false",
        JsonInt number => number.Value.ToString(CultureInfo.InvariantCulture),
        JsonArray array => string.Join(",", array.Items.Select(item => item is JsonNull ? string.Empty : JsString(item))),
        JsonObject => "[object Object]",
        _ => string.Empty,
    };

    /// <summary>
    /// Compares decoded frames. Frames are at most <c>max_json_depth</c> deep,
    /// so the 256 bound never refuses one; keys are sorted by code point,
    /// matching the kit's canonical JSON.
    /// </summary>
    private static string Canonical(Json value)
    {
        try
        {
            return JsonCodec.Stringify(value, 256);
        }
        catch (ProtocolException error)
        {
            return "unencodable: " + error.Message;
        }
    }

    /// <summary>An absent prose field compares equal to null, as in <c>kit.ts</c>.</summary>
    private static string CanonicalOption(Json? value) => Canonical(value ?? Json.Null);

    private static JsonArray Frames(IEnumerable<JsonObject> list) => Json.Of(list);

    private static byte[] HexBytes(string text)
    {
        if (text.Length % 2 != 0 || !text.All(Uri.IsHexDigit))
        {
            throw new ReplayFailure("raw_hex is not hexadecimal");
        }

        return Convert.FromHexString(text);
    }

    private sealed class ReplayFailure : Exception
    {
        internal ReplayFailure(string message)
            : base(message)
        {
        }
    }

    private sealed class Drive
    {
        /// <summary>The most frames kept: one past the recording's, enough to show a divergence.</summary>
        private readonly int limit;

        internal Drive(int limit)
        {
            this.limit = limit;
        }

        internal List<JsonObject> Observed { get; } = new();

        internal bool Ended { get; private set; }

        internal long Skipped { get; private set; }

        internal Json Refusal { get; private set; } = Json.Null;

        /// <summary>
        /// Reads one frame. A worker that keeps sending past the recording is
        /// not read further, so it cannot fill memory before the deadline;
        /// disposal then kills it.
        /// </summary>
        private bool Read(Worker worker)
        {
            if (Observed.Count >= limit)
            {
                throw new ReplayFailure("Core sent more frames than the recording");
            }

            JsonObject? frame = worker.ReadFrame(Limits.Hard);
            if (frame is null)
            {
                return false;
            }

            Ended |= IsTerminal(frame);
            Observed.Add(frame);
            return true;
        }

        internal void Run(Worker worker, Transcript transcript)
        {
            try
            {
                foreach (TranscriptStep step in transcript.HostInput)
                {
                    if (step.Direction == "core_to_host")
                    {
                        Read(worker);
                    }
                    else if (step.RawHex is not null)
                    {
                        worker.WriteBytes(HexBytes(step.RawHex));
                        worker.CloseInput();
                    }
                    else if (step.AfterTerminal && Ended)
                    {
                        Refusal = Json.Of("E1608");
                    }
                    else if (Ended)
                    {
                        Skipped++;
                    }
                    else
                    {
                        Write(worker, step.Frame);
                    }
                }

                worker.CloseInput();
                while (Read(worker))
                {
                    // Read to end of stream.
                }

                worker.AwaitExit();
            }
            catch (CarrierException error)
            {
                throw new ReplayFailure(error.Message);
            }
        }

        private static void Write(Worker worker, JsonObject frame)
        {
            byte[] data;
            try
            {
                data = JsonCodec.FrameBytes(frame, Limits.Hard.MaxFrameBytes, Limits.Hard.MaxJsonDepth);
            }
            catch (ProtocolException error)
            {
                throw new ReplayFailure(error.Message);
            }

            try
            {
                worker.WriteBytes(data);
            }
            catch (CarrierException error) when (error.IsLoss && error.Message.StartsWith("worker write failed", StringComparison.Ordinal))
            {
                // Core may close its input before a hostile recording ends.
            }
        }
    }

    /// <summary>
    /// Drives one recording against the worker and compares what Core sends
    /// with the recording, modulo non-normative diagnostic prose.
    /// </summary>
    public static Replay ReplayOne(Transcript transcript, string store, WorkerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        var differences = new List<string>();
        Worker worker;
        try
        {
            worker = Worker.Start(store, options, cancellationToken);
        }
        catch (ArgumentException error)
        {
            differences.Add(error.Message);
            return new Replay(transcript.Name, false, "diverges", differences, Array.Empty<string>(), JsonObject.Empty, Array.Empty<JsonObject>(), null, null, Array.Empty<byte>());
        }

        var drive = new Drive(transcript.CoreOutput.Count + 1);
        try
        {
            drive.Run(worker, transcript);
        }
        catch (ReplayFailure error)
        {
            differences.Add(error.Message);
        }
        finally
        {
            worker.Dispose();
        }

        return Compare(transcript, drive, worker, differences);
    }

    private static Replay Compare(Transcript transcript, Drive drive, Worker worker, List<string> differences)
    {
        List<JsonObject> observed = drive.Observed;
        int? exitStatus = worker.ExitStatus;
        JsonObject end = observed.FirstOrDefault(IsTerminal) ?? JsonObject.Empty;
        string? endKind = KindOf(end);
        JsonObject? result = endKind == "outcome" ? end.Get("result") as JsonObject : null;
        Json? diagnostics = endKind == "fatal" ? end.Get("diagnostics")
            : result is not null && Json.Of("error").Equals(result.Get("kind")) ? result.Get("diagnostics")
            : null;
        JsonObject diagnostic = diagnostics?.AsArray() is { Count: > 0 } entries && entries[0] is JsonObject first ? first : JsonObject.Empty;
        Json primary = diagnostic.Get("code") ?? Json.Null;
        Json terminalName = endKind switch
        {
            "fatal" => Json.Of("fatal"),
            "shutdown_ack" => Json.Of("shutdown"),
            _ => (end.Get("evidence") as JsonObject)?.Get("core") is JsonObject core ? core.Get("terminal") ?? Json.Null : Json.Null,
        };
        long requests = observed.Count(frame => KindOf(frame) == "effect_request");
        long terminals = observed.Count(IsTerminal);
        JsonObject summary = Json.Object(
            ("effect_requests", Json.Of(requests)),
            ("host_inputs_skipped_after_terminal", Json.Of(drive.Skipped)),
            ("host_local_refusal", drive.Refusal),
            ("primary_code", primary),
            ("terminal", terminalName),
            ("terminal_frames", Json.Of(terminals)),
            ("terminal_kind", end.Get("kind") ?? Json.Null));
        JsonObject expect = transcript.Expect;
        bool sameFrames = Canonical(StripProse(Frames(observed))) == Canonical(StripProse(Frames(transcript.CoreOutput)));
        bool conforms;
        if (transcript.Kind == "positive")
        {
            // As in kit.ts, an absent expectation never equals an observed value or null.
            conforms = terminalName.Equals(expect.Get("terminal")) && ExitIs(exitStatus, 0) && sameFrames;
            TranscriptStep? failure = transcript.HostInput.FirstOrDefault(step => Json.Of("effect_failure").Equals(step.Frame.Get("kind")));
            if (failure?.Frame.Get("message") is { } message)
            {
                string expected = JsString(message);
                string? cause = diagnostic.Get("cause")?.AsString();
                conforms &= cause is not null && cause.EndsWith(expected, StringComparison.Ordinal);
            }
        }
        else
        {
            long? maximum = expect.Get("terminal_frames_max")?.AsInt();
            bool forbid = Json.Of(true).Equals(expect.Get("forbid_outside_action"));
            int exit = Json.Of("E1611").Equals(expect.Get("code")) ? 74 : 0;
            conforms = Json.Of(requests).Equals(expect.Get("effect_requests_before_failure"))
                && maximum is not null && terminals <= maximum
                && (!forbid || requests == 0)
                && ExitIs(exitStatus, exit);
            if (transcript.Phase == "terminal")
            {
                conforms &= drive.Refusal.Equals(expect.Get("code")) && terminalName.Equals(Json.Of("ok"));
            }
            else
            {
                conforms &= primary.Equals(expect.Get("code"));
            }
        }

        string status = conforms ? "conforms" : "diverges";
        if (status != transcript.Status)
        {
            differences.Add("conformance status differs from the recording");
        }

        if (!ExitIs(exitStatus, transcript.ExitStatus))
        {
            differences.Add("exit status differs from the recording");
        }

        if (Canonical(summary) != Canonical(transcript.Observed))
        {
            differences.Add("observed summary differs from the recording");
        }

        if (!sameFrames)
        {
            differences.Add("Core frames differ semantically from the recording");
        }

        List<string> drift = ProseDriftOf(Frames(transcript.CoreOutput), Frames(observed), string.Empty);
        return new Replay(transcript.Name, differences.Count == 0, status, differences, drift, summary, observed, exitStatus, worker.Signal, worker.Stderr);
    }

    private static bool ExitIs(int? exitStatus, long code) => exitStatus is not null && exitStatus.Value == code;

    /// <summary>Replays every recording in order, loading the pinned kit when <paramref name="kit"/> is null.</summary>
    /// <exception cref="KitException">The pinned kit cannot be loaded.</exception>
    public static IReadOnlyList<Replay> ReplayKit(string store, WorkerOptions options, Kit? kit = null, CancellationToken cancellationToken = default)
    {
        Kit loaded = kit ?? Kit.Load();
        return loaded.Transcripts.Select(transcript => ReplayOne(transcript, store, options, cancellationToken)).ToList();
    }
}
