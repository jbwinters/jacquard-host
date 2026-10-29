using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Conformance;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// Live tests against the real Core named by <c>JACQUARD_BIN</c> and
/// <c>JACQUARD_PRELUDE</c>. Each test is ignored without them, and fails
/// instead when <c>JACQUARD_REQUIRE_LIVE</c> is set, as CI does.
/// </summary>
[TestFixture]
[Platform("Linux,MacOsX")]
public sealed class LiveTests
{
    private string root = string.Empty;
    private string store = string.Empty;
    private string binary = string.Empty;
    private Dictionary<string, string> env = new();

    [SetUp]
    public void Prepare()
    {
        string? bin = Environment.GetEnvironmentVariable("JACQUARD_BIN");
        string? prelude = Environment.GetEnvironmentVariable("JACQUARD_PRELUDE");
        if (string.IsNullOrEmpty(bin) || string.IsNullOrEmpty(prelude))
        {
            if (Environment.GetEnvironmentVariable("JACQUARD_REQUIRE_LIVE") is not null)
            {
                Assert.Fail("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
            }

            Assert.Ignore("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        }

        binary = bin!;
        root = Directory.CreateTempSubdirectory("jacquard-host-dotnet-live-").FullName;
        string source = Path.Combine(root, "fixtures.jac");
        File.WriteAllBytes(source, Kit.File("kit/fixtures.jac")!);
        store = Path.Combine(root, "store");
        env = Environment0();
        env["JACQUARD_PRELUDE"] = prelude!;

        // Output goes to a file, so a hung install cannot hold a pipe read.
        string log = Path.Combine(root, "install.log");
        var info = new ProcessStartInfo("sh") { UseShellExecute = false };
        foreach (string argument in new[] { "-c", "exec \"$0\" run \"$1\" --store \"$2\" >\"$3\" 2>&1", binary, source, store, log })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (KeyValuePair<string, string> variable in env)
        {
            info.Environment[variable.Key] = variable.Value;
        }

        using Process install = Process.Start(info)!;
        if (!install.WaitForExit(60_000))
        {
            install.Kill(entireProcessTree: true);
            Assert.Fail("installing fixtures timed out");
        }

        Assert.That(install.ExitCode, Is.EqualTo(0), "installing fixtures: " + File.ReadAllText(log));
    }

    [TearDown]
    public void Remove()
    {
        if (root.Length > 0 && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Host.HandlerCheckHook = null;
    }

    private WorkerOptions Options(TimeSpan? deadline = null) => new() { Command = new[] { binary }, Environment = env, Deadline = deadline };

    private Host NewHost(TimeSpan? deadline = null) => new(store, new HostOptions { Carrier = Options(deadline) });

    private static OperationRegistry Registry(Handler handler) => new OperationRegistry().Bind(Pinned.Identity("kit-world"), Pinned.Identity("send"), handler);

    private static InvocationResult InvokeEcho(Host host, OperationRegistry registry) =>
        host.Invoke(Pinned.Identity("kit-echo"), Echo(), new Value[] { new TextValue("ping") }, registry);

    [Test]
    public void LiveCoreMatchesAllRecordings()
    {
        IReadOnlyList<Replay> replays = Replayer.ReplayKit(store, Options(), Pinned);
        foreach (Replay replay in replays)
        {
            Assert.That(
                replay.Differences.Count == 0 && replay.ProseDrift.Count == 0,
                Is.True,
                replay.Name + ": " + string.Join("; ", replay.Differences) + ", prose drift " + string.Join("; ", replay.ProseDrift));
        }

        Assert.That(replays, Has.Count.EqualTo(Kit.TranscriptCount));
    }

    [Test]
    public void LivePureAndEffectfulInvocationsRetainEvidence()
    {
        Host host = NewHost();
        InvocationResult pure = host.Invoke(Pinned.Identity("kit-ready"), Unit(), Array.Empty<Value>(), new OperationRegistry());
        Assert.That(pure.ExitStatus, Is.EqualTo(0), pure.ToString());
        Assert.That(pure.ValueOrThrow(), Is.EqualTo(Value.Unit));
        int calls = 0;
        OperationRegistry registry = Registry((context, args) =>
        {
            Interlocked.Increment(ref calls);
            return args[0] is TextValue text ? new TextValue(text.Value + "!") : throw HandlerException.OutsideFailed("expected text");
        });
        InvocationResult result = InvokeEcho(host, registry);
        Assert.That(result.Requests, Has.Count.EqualTo(1));
        Assert.That(result.HostFrames[2].Get("kind"), Is.EqualTo(Json.Of("effect_ok")));
        Assert.That(((JsonObject)result.Evidence!["core"])["terminal"], Is.EqualTo(Json.Of("ok")));
        Assert.That(result.ValueOrThrow(), Is.EqualTo(new TextValue("ping!")));
        Assert.That(calls, Is.EqualTo(1));
        InvocationResult probe = host.ShutdownProbe();
        Assert.That(probe.Kind, Is.EqualTo(ResultKind.ShutdownAck), probe.ToString());
    }

    [Test]
    public void LiveReadmeExampleRuns()
    {
        // The README example takes its command from JACQUARD_BIN and inherits
        // this environment, which carries JACQUARD_PRELUDE.
        Assert.That(
            Readme.Echo.Run(store, Pinned.Identity("kit-echo"), Pinned.Identity("text"), Pinned.Identity("kit-world"), Pinned.Identity("send")),
            Is.EqualTo(new TextValue("ping!")));
    }

    [Test]
    public void LiveFailurePairsAreNeverRetried()
    {
        foreach ((Func<string, HandlerException> make, string expected) in new (Func<string, HandlerException>, string)[]
        {
            (HandlerException.Unsupported, "E1606"),
            (HandlerException.Refused, "E1607"),
            (HandlerException.OutsideFailed, "E1613"),
            (HandlerException.CompletionUnknown, "E1612"),
        })
        {
            int calls = 0;
            InvocationResult result = InvokeEcho(NewHost(), Registry((context, args) =>
            {
                Interlocked.Increment(ref calls);
                throw make("synthetic failure");
            }));
            Assert.That(result.Code, Is.EqualTo(expected), result.ToString());
            Assert.That(calls, Is.EqualTo(1), expected);
            Assert.That(result.Diagnostics[0].Cause, Does.EndWith("synthetic failure"), expected);
        }

        foreach ((string name, Handler handler) in new (string, Handler)[]
        {
            ("I/O exception", (context, args) => throw new IOException("private detail")),
            ("invalid operation", (context, args) => throw new InvalidOperationException("private detail")),
            ("argument", (context, args) => throw new ArgumentException("private detail")),
        })
        {
            InvocationResult result = InvokeEcho(NewHost(), Registry(handler));
            Assert.That(result.Code == "E1612" && !result.Message.Contains("private detail", StringComparison.Ordinal), Is.True, name + ": " + result);
            Assert.That(Throws<InvocationException>(() => result.ValueOrThrow())!.Failure, Is.EqualTo(Failure.OperationFailed), name);
        }
    }

    [Test]
    public void LiveHandlerDeadlineKillsTheWorkerWithoutALateResponse()
    {
        foreach (bool cooperative in new[] { true, false })
        {
            int calls = 0;
            using var finished = new ManualResetEventSlim();
            bool cancelledSeen = false;
            OperationRegistry registry = Registry((context, args) =>
            {
                Interlocked.Increment(ref calls);
                if (cooperative)
                {
                    // Waits for cancellation.
                    context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                }
                else
                {
                    // Ignores cancellation; the host abandons it.
                    Thread.Sleep(1000);
                }

                cancelledSeen = context.IsCancelled;
                finished.Set();
                return new TextValue("late");
            });
            var watch = Stopwatch.StartNew();
            InvocationResult result = InvokeEcho(NewHost(TimeSpan.FromMilliseconds(500)), registry);
            TimeSpan elapsed = watch.Elapsed;
            Assert.That(
                Reason(result) == "deadline" && result.Code == "E1611" && calls == 1 && result.HostFrames.Count == 2 && elapsed < TimeSpan.FromSeconds(2),
                Is.True,
                "cooperative=" + cooperative + ": " + result.HostEvidence + " frames=" + result.HostFrames.Count + " after " + elapsed);
            Assert.That(finished.Wait(TimeSpan.FromSeconds(3)), Is.True, "handler did not finish");
            Assert.That(cancelledSeen, Is.True, "cooperative=" + cooperative + ": handler context was not cancelled");
            Assert.That(result.Ok, Is.False);
        }
    }
}
