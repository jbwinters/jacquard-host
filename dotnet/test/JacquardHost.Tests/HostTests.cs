using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Conformance;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>Kit replay through the .NET carrier, and host fault handling against the stand-in worker.</summary>
[TestFixture]
public sealed class HostTests
{
    private static readonly Limits Hard = Limits.Hard;

    [TearDown]
    public void ClearSeams()
    {
        Host.RequestDecodedHook = null;
        Host.HandlerThreadStartedHook = null;
        Host.HandlerCheckHook = null;
    }

    [Test]
    public void NullArgumentsAreRefusedBeforeAWorkerStarts()
    {
        Host host = Faulty("ok", TimeSpan.FromSeconds(10));
        Throws<ArgumentNullException>(() => host.Invoke(null!, Unit(), Array.Empty<Value>(), new OperationRegistry()));
        Throws<ArgumentNullException>(() => host.Invoke(Pinned.Identity("kit-ready"), Unit(), Array.Empty<Value>(), null!));
        Throws<ArgumentNullException>(() => _ = new Host(null!));
    }

    private static Replay ReplayStandin(Transcript transcript, string original) =>
        Replayer.ReplayOne(transcript, "unused", Standin("replay", original, TimeSpan.FromSeconds(20)));

    [Test]
    public void AllRecordingsReplayThroughTheDotnetCarrier()
    {
        foreach (Transcript transcript in Pinned.Transcripts)
        {
            Replay replay = ReplayStandin(transcript, transcript.Name);
            Assert.That(
                replay.Differences.Count == 0 && replay.ProseDrift.Count == 0 && replay.Status == transcript.Status,
                Is.True,
                transcript.Name + ": differences " + string.Join("; ", replay.Differences) + ", prose drift " + string.Join("; ", replay.ProseDrift) + ", status " + replay.Status);
        }
    }

    [Test]
    public void ReplayTreatsAbsentExpectationsLikeTheReference()
    {
        Transcript missing = Pinned.Find("pure-success")! with { Expect = JsonObject.Empty };
        Assert.That(ReplayStandin(missing, "pure-success").Status, Is.EqualTo("diverges"), "absent terminal expectation conformed");
        Transcript unbounded = Pinned.Find("unknown-version")! with { Expect = Pinned.Find("unknown-version")!.Expect.Without("terminal_frames_max") };
        Assert.That(ReplayStandin(unbounded, "unknown-version").Status, Is.EqualTo("diverges"), "missing terminal_frames_max conformed");
    }

    private static Json SummaryOf(JsonObject frame) => ((JsonObject)((JsonObject)frame["result"])["diagnostics"].AsArray()![0])["summary"];

    [Test]
    public void ProseDriftIsReportedWithoutNormalizing()
    {
        Transcript original = Pinned.Find("refused-authority")!;
        var output = original.CoreOutput.ToList();
        JsonObject last = output[^1];
        var result = (JsonObject)last["result"];
        var diagnostics = result["diagnostics"].AsArray()!.ToList();
        var diagnostic = (JsonObject)diagnostics[0];
        diagnostics[0] = diagnostic.With("summary", Json.Of(diagnostic["summary"].AsString() + " changed"));
        output[^1] = last.With("result", result.With("diagnostics", Json.Of(diagnostics)));
        Replay replay = ReplayStandin(original with { CoreOutput = output }, original.Name);
        Assert.That(replay.Differences, Is.Empty);
        Assert.That(replay.ProseDrift, Has.Count.EqualTo(1));
        Assert.That(SummaryOf(replay.CoreFrames[^1]), Is.EqualTo(SummaryOf(original.CoreOutput[^1])));
    }

    [Test]
    public void ReplayStopsReadingAWorkerThatSendsPastTheRecording()
    {
        // 300 frames of about 600 KB follow the terminal. Replay keeps one
        // frame past the recording's and stops reading; disposal kills the worker.
        Transcript pure = Pinned.Transcripts[0];
        Replay replay = Within(TimeSpan.FromSeconds(30), () => Replayer.ReplayOne(pure, "unused", Standin("flood-after-terminal", string.Empty, TimeSpan.FromSeconds(10))));
        Assert.That(replay.MatchesRecording, Is.False);
        Assert.That(replay.Differences, Does.Contain("Core sent more frames than the recording"));
        Assert.That(replay.CoreFrames, Has.Count.EqualTo(pure.CoreOutput.Count + 1));
    }

    [Test]
    public void MalformedExtraAndNoncanonicalCoreFramesFailClosed()
    {
        foreach ((string mode, string code) in new[] { ("unknown-field", "E1601"), ("invalid-hash", "E1601"), ("extra", "E1608") })
        {
            InvocationResult result = InvokeReady(Faulty(mode, TimeSpan.FromSeconds(10)));
            Assert.That(result.Kind == ResultKind.ProtocolError && result.Code == code, Is.True, mode + ": " + result);
        }
    }

    [Test]
    public void CarrierFailuresRetainHostEvidence()
    {
        foreach (string mode in new[] { "exit", "truncated", "silent", "stall", "nonzero" })
        {
            bool expired = mode is "silent" or "stall";
            InvocationResult result = InvokeReady(Faulty(mode, TimeSpan.FromSeconds(expired ? 2 : 10)));
            Assert.That(
                result.Kind == ResultKind.CarrierLoss && result.Code == "E1611" && Json.Of(Host.EvidenceSchema).Equals(result.HostEvidence.Get("schema")),
                Is.True,
                mode + ": " + result.HostEvidence);
            bool sawOutcome = result.CoreFrames.Any(frame => Json.Of("outcome").Equals(frame.Get("kind")));
            Assert.That(sawOutcome, Is.EqualTo(mode is "stall" or "nonzero"), mode + ": outcome observed");
            Assert.That(Reason(result) == "deadline", Is.EqualTo(expired), mode + ": " + Reason(result));
            InvocationException error = Throws<InvocationException>(() => result.ValueOrThrow())!;
            Assert.That(error.Failure, Is.EqualTo(expired ? Failure.DeadlineExceeded : Failure.WorkerLost), mode);
        }
    }

    [Test]
    public void FramesAfterSelectionAreReadUnderTheSelectedLimits()
    {
        InvocationResult result = InvokeReady(Faulty("oversized-early", TimeSpan.FromSeconds(10), Hard.With("max_frame_bytes", 1024)));
        Assert.That(result.Kind == ResultKind.ProtocolError && result.Code == "E1602", Is.True, result.ToString());
    }

    [Test]
    public void StderrIsDrainedAndRetentionObeysTheSelectedCeiling()
    {
        // The stand-in writes 2 MiB of stderr before its hello; the host
        // drains it while it waits, and keeps the selected prefix.
        InvocationResult result = InvokeReady(Faulty("stderr", TimeSpan.FromSeconds(10), Hard.With("max_stderr_bytes", 17)));
        Assert.That(result.Ok, Is.True, result.ToString());
        Assert.That(Encoding.ASCII.GetString(result.Stderr.ToArray()), Is.EqualTo(new string('x', 17)));
    }

    [Test]
    public void AWorkerThatNeverReadsCannotHoldAWritePastTheDeadline()
    {
        // The invoke frame (about 220 KB) is larger than a pipe holds, and the
        // worker never reads it. A blocking write would never return.
        var watch = Stopwatch.StartNew();
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => Faulty("no-read", TimeSpan.FromSeconds(2))
            .Invoke(Pinned.Identity("kit-ready"), Echo(withWorld: false), new Value[] { new TextValue(new string('x', 220_000)) }, new OperationRegistry()));
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(result.HostFrames, Has.Count.EqualTo(1), "the invoke frame was not the write in progress");
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(Throws<InvocationException>(() => result.ValueOrThrow())!.Failure, Is.EqualTo(Failure.DeadlineExceeded));
    }

    [Test]
    public void CancellationAbortsWithoutRetry()
    {
        using var cancel = new CancellationTokenSource();
        After(TimeSpan.FromMilliseconds(300), cancel.Cancel);
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeReady(Faulty("silent", TimeSpan.FromSeconds(20)), cancel.Token);
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(Throws<InvocationException>(() => result.ValueOrThrow())!.Failure, Is.EqualTo(Failure.WorkerLost));
    }

    [Test]
    public void AStopBeforeTheSpawnStartsNoWorker()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        InvocationResult result = InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10)), cancel.Token);
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
        Assert.That(result.CoreFrames, Is.Empty);
        Assert.That(result.ExitStatus, Is.Null);
    }

    [Test]
    public void LocalRefusalStartsNoWorkerAndInvalidOptionsAreRejected()
    {
        var missing = new WorkerOptions { Command = new[] { "/no/such/executable" } };
        InvocationResult result = InvokeReady(new Host("unused", new HostOptions { Carrier = missing, Limits = Hard.With("max_frame_bytes", 20) }));
        Assert.That(result.Kind == ResultKind.HostError && result.Code == "E1602" && result.ExitStatus is null, Is.True, result.ToString());
        Assert.That(Reason(result), Is.EqualTo("invoke_refused_locally"));
        InvocationResult failed = InvokeReady(new Host("unused", new HostOptions { Carrier = missing }));
        Assert.That(Reason(failed), Is.EqualTo("worker_spawn_failed"), failed.ToString());
        Assert.That(failed.Message, Does.Contain("/no/such/executable"));
        foreach (HostOptions options in new[]
        {
            new HostOptions { Carrier = new WorkerOptions { Deadline = TimeSpan.Zero } },
            new HostOptions { Carrier = new WorkerOptions { Deadline = TimeSpan.FromSeconds(-1) } },
            new HostOptions { Carrier = new WorkerOptions { Command = new[] { string.Empty } } },
            new HostOptions { Limits = Hard.With("max_arguments", 0) },
        })
        {
            Throws<ArgumentException>(() => _ = new Host("unused", options), "accepted " + options);
        }
    }

    [Test]
    public void RegistryRefusesDuplicatesAndSortsEntries()
    {
        Hash a = HashOf('a');
        Hash b = HashOf('b');
        var registry = new OperationRegistry();
        registry.Bind(b, a, (context, args) => Value.Unit);
        registry.Bind(a, b, (context, args) => Value.Unit);
        Throws<ArgumentException>(() => registry.Bind(a, b, (context, args) => Value.Unit));
        IReadOnlyList<OperationEntry> entries = registry.Entries();
        Assert.That(entries[0].Effect, Is.EqualTo(a));
        Assert.That(entries[1].Effect, Is.EqualTo(b));
    }

    private static OperationRegistry Registry(Handler handler) => new OperationRegistry().Bind(Pinned.Identity("kit-world"), Pinned.Identity("send"), handler);

    private static InvocationResult InvokeEcho(Host host, Handler handler, CancellationToken cancellationToken = default) =>
        host.Invoke(Pinned.Identity("kit-echo"), Echo(), new Value[] { new TextValue("ping") }, Registry(handler), cancellationToken);

    /// <summary>Runs one handler against the stand-in replaying <c>one-once-effect-success</c>; returns the host's response.</summary>
    private static JsonObject Response(Handler handler)
    {
        InvocationResult result = InvokeEcho(Replaying("one-once-effect-success"), handler);

        // The stand-in replays its recorded outcome whatever the host answers.
        Assert.That(result.Ok, Is.True, result.ToString());
        Assert.That(result.HostFrames, Has.Count.EqualTo(3));
        return result.HostFrames[2];
    }

    private static void AssertFailure(JsonObject frame, FailureCategory category, string message)
    {
        Assert.That(frame.Get("kind"), Is.EqualTo(Json.Of("effect_failure")), frame.ToString());
        Assert.That(frame.Get("category"), Is.EqualTo(Json.Of(category.WireName())), frame.ToString());
        Assert.That(frame.Get("completion"), Is.EqualTo(Json.Of(category.Completion())), frame.ToString());
        Assert.That(frame.Get("message"), Is.EqualTo(Json.Of(message)), frame.ToString());
    }

    [Test]
    public void HandlerAnswersMapToTheirFailureCategories()
    {
        IReadOnlyList<Value>? seen = null;
        JsonObject ok = Response((context, args) =>
        {
            seen = args;
            return new TextValue("pong");
        });
        Assert.That(seen, Is.EqualTo(new Value[] { new TextValue("ping") }));
        Assert.That(ok.Get("kind"), Is.EqualTo(Json.Of("effect_ok")));
        AssertFailure(Response((context, args) => throw HandlerException.Refused("no authority")), FailureCategory.RefusedAuthority, "no authority");
        AssertFailure(Response((context, args) => throw new InvalidOperationException("private detail")), FailureCategory.CompletionUnknown, "handler threw an exception");
        AssertFailure(Response((context, args) => throw new IOException("private detail")), FailureCategory.CompletionUnknown, "handler threw an exception");
        AssertFailure(Response((context, args) => null!), FailureCategory.OutsideFailure, "handler result has no encoding within the selected limits");
        AssertFailure(Response((context, args) => new IntValue(long.MaxValue)), FailureCategory.OutsideFailure, "handler result has no encoding within the selected limits");

        // Messages are cut to max_host_message_bytes at a code point, and a lone
        // surrogate becomes U+FFFD.
        AssertFailure(
            Response((context, args) => throw HandlerException.OutsideFailed("\ud800" + string.Concat(Enumerable.Repeat("é", 3000)))),
            FailureCategory.OutsideFailure,
            "�" + string.Concat(Enumerable.Repeat("é", 2046)));
    }

    [Test]
    public void AResultFarOverTheFrameCeilingIsAnOutsideFailure()
    {
        var watch = Stopwatch.StartNew();
        JsonObject frame = Response((context, args) => CodecTests.HugeResult());
        AssertFailure(frame, FailureCategory.OutsideFailure, "handler result has no encoding within the selected limits");
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void ATextFarOverTheTextCeilingIsAnOutsideFailure()
    {
        var text = new TextValue(new string('é', 32 << 20));
        AssertFailure(Response((context, args) => text), FailureCategory.OutsideFailure, "handler result has no encoding within the selected limits");
    }

    [Test]
    public void ACancellationWhileTheRequestIsDecodedStartsNoHandlerThread()
    {
        // The seam runs on the caller's thread once the request is decoded,
        // just before the caller's own check; the cancellation lands there.
        using var cancel = new CancellationTokenSource();
        bool ran = false;
        int started = 0;
        Host.RequestDecodedHook = cancel.Cancel;
        Host.HandlerThreadStartedHook = () => Interlocked.Increment(ref started);
        InvocationResult result = InvokeEcho(Replaying("one-once-effect-success"), (context, args) =>
        {
            ran = true;
            return Value.Unit;
        }, cancel.Token);
        Assert.That(Volatile.Read(ref started), Is.EqualTo(0), "a handler thread started after the stop");
        Assert.That(ran, Is.False, "the handler ran after the stop");
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
        Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a response was sent after the stop");
    }

    [Test]
    public void ACancellationJustBeforeTheHandlerIsCalledRunsNoHandler()
    {
        // The seam runs on the handler thread just before its own check.
        using var cancel = new CancellationTokenSource();
        bool ran = false;
        bool stopped = false;
        Host.HandlerCheckHook = () =>
        {
            stopped = true;
            cancel.Cancel();
        };
        InvocationResult result = InvokeEcho(Replaying("one-once-effect-success"), (context, args) =>
        {
            ran = true;
            return Value.Unit;
        }, cancel.Token);
        Assert.That(Volatile.Read(ref stopped), Is.True, "the seam never ran");
        Assert.That(Volatile.Read(ref ran), Is.False, "the handler ran after the stop");
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
    }

    [Test]
    public void ADeadlineJustBeforeTheHandlerIsCalledRunsNoHandler()
    {
        // The seam holds the handler thread until the lifetime has passed.
        bool ran = false;
        Host host = Replaying("one-once-effect-success", TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        Host.HandlerCheckHook = () =>
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(4))
            {
                Thread.Sleep(10);
            }
        };
        InvocationResult result = InvokeEcho(host, (context, args) =>
        {
            ran = true;
            return Value.Unit;
        });
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(Eventually(() => watch.Elapsed > TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(6)), Is.True);
        Assert.That(Volatile.Read(ref ran), Is.False, "the handler ran after the deadline");
    }

    [Test]
    public void AHandlerSeesTheStopThroughItsContext()
    {
        // A cooperative handler waits on its token; cancellation releases it,
        // and the host sends no late response.
        using var cancel = new CancellationTokenSource();
        using var finished = new ManualResetEventSlim();
        bool sawCancelled = false;
        InvocationResult result = InvokeEcho(Replaying("one-once-effect-success"), (context, args) =>
        {
            cancel.CancelAfter(200);
            context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            sawCancelled = context.IsCancelled && context.CancellationToken.IsCancellationRequested;
            finished.Set();
            return new TextValue("late");
        }, cancel.Token);
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
        Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
        Assert.That(finished.Wait(TimeSpan.FromSeconds(5)), Is.True, "the handler did not finish");
        Assert.That(sawCancelled, Is.True);
    }

    [Test]
    public void AnAbandonedHandlerDoesNotHoldTheCaller()
    {
        // The handler ignores cancellation for three seconds; the call ends at
        // its one-second deadline, and no late response follows.
        using var finished = new ManualResetEventSlim();
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(1)), (context, args) =>
        {
            Thread.Sleep(3000);
            finished.Set();
            return new TextValue("late");
        });
        TimeSpan elapsed = watch.Elapsed;
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(2.5)), "the caller waited for the handler");
        Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
        Assert.That(finished.Wait(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public void ARegistryOverTheOperationCeilingIsRefusedLocally()
    {
        var registry = new OperationRegistry();
        for (int i = 0; i <= Hard.MaxOperations; i++)
        {
            registry.Bind(Pinned.Identity("kit-world"), new Hash(i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)), (context, args) => Value.Unit);
        }

        InvocationResult result = Faulty("ok", TimeSpan.FromSeconds(10)).Invoke(Pinned.Identity("kit-echo"), Echo(), new Value[] { new TextValue("ping") }, registry);
        Assert.That(result.Kind == ResultKind.HostError && result.Code == "E1602" && Reason(result) == "invoke_refused_locally", Is.True, result.ToString());
    }

    [Test]
    public void CancellationWhileAHugeResultIsEncodedEndsTheCallPromptly()
    {
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeEcho(Replaying("one-once-effect-success"), (context, args) =>
        {
            // Cancel from elsewhere once the result is being encoded. A
            // bounded encoder has finished by then; an unbounded one is still
            // encoding, and only then sees the cancellation.
            After(TimeSpan.FromMilliseconds(100), cancel.Cancel);
            return CodecTests.HugeResult();
        }, cancel.Token);
        Assert.That(result.Ok || Reason(result) == "aborted", Is.True, result.ToString());
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void DisposalFromAnotherThreadEndsAWaitingRead()
    {
        Worker worker = Worker.Start("unused", Standin("silent", string.Empty, TimeSpan.FromSeconds(30)), CancellationToken.None);
        After(TimeSpan.FromMilliseconds(300), worker.Dispose);
        var watch = Stopwatch.StartNew();
        CarrierException error = Within(TimeSpan.FromSeconds(10), () => Throws<CarrierException>(() => worker.ReadFrame(Hard))!);
        Assert.That(error.IsAborted, Is.True, error.Message);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(Eventually(() => worker.Signal == "SIGKILL"), Is.True, "the worker was not killed");
    }
}
