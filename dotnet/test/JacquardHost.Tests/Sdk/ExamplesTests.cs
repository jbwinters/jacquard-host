using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JacquardLang.Host.Examples;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>
/// The example projects run as commands through the .NET SDK against the
/// real Core named by <c>JACQUARD_BIN</c> and <c>JACQUARD_PRELUDE</c>: the
/// live tests are ignored without them, and fail instead when
/// <c>JACQUARD_REQUIRE_LIVE</c> is set. The usage test needs no Core.
/// </summary>
[TestFixture]
[Platform("Linux,MacOsX")]
public sealed class ExamplesTests
{
    private string root = string.Empty;

    private static string ExamplesDirectory => Path.GetFullPath(Path.Combine(SdkLiveTests.DotnetRoot(), "..", "examples"));

    [SetUp]
    public void Prepare() => root = SdkSupport.Scratch("examples");

    [TearDown]
    public void CleanUp() => SdkSupport.RemoveScratch(root);

    /// <summary>One command's output and its failure, if any.</summary>
    private sealed record Outcome(string Output, Exception? Failure)
    {
        internal bool Ok => Failure is null;
    }

    private static Outcome Command(params string[] arguments)
    {
        var output = new StringWriter();
        try
        {
            Examples.Examples.Run(arguments, output, ExamplesDirectory);
            return new Outcome(output.ToString(), null);
        }
        catch (Exception error) when (error is JacquardException or UsageException)
        {
            return new Outcome(output.ToString(), error);
        }
    }

    private static JsonObject Summary(string output) =>
        JsonCodec.DecodeObject(System.Text.Encoding.UTF8.GetBytes(output.Trim().Split('\n')[^1]), 4);

    [Test]
    public void AllFourProjectsRunAsCommands()
    {
        SdkLiveTests.RequireLive();
        foreach (string project in Examples.Examples.Projects)
        {
            string workdir = Path.Combine(root, project);
            Outcome outcome = Command("--workdir", workdir, project);
            Assert.That(outcome.Ok, Is.True, project + ": " + outcome.Failure + "\n" + outcome.Output);
            switch (project)
            {
                case "quickstart":
                    Assert.That(outcome.Output, Is.EqualTo("Order total 35: shipping 5\nOrder total 75: shipping 0\n"));
                    break;
                case "planner":
                    Assert.That(outcome.Output, Does.Contain("\"score\": 16").And.Contain("planner.plan: ok"));
                    break;
                case "triage":
                    IReadOnlyList<Json> rows = JsonCodec.Decode(System.Text.Encoding.UTF8.GetBytes(outcome.Output.Trim()), 16).AsArray()!;
                    Assert.That(rows.Select(row => row.AsObject()!["urgency"].AsString()), Is.EqualTo(new[] { "PAGE", "TODAY", "WATCH" }), outcome.Output);
                    break;
                default:
                    Assert.That(outcome.Output, Does.Contain("\"$tag\": \"Fulfilled\""));
                    Outcome repeated = Command("--workdir", workdir, project);
                    Assert.That(repeated.Ok && repeated.Output.Contains("DuplicateOrder", StringComparison.Ordinal) && repeated.Output.Contains("\"deliveries\":1", StringComparison.Ordinal), Is.True, repeated.Output);
                    break;
            }
        }

        string recovery = Path.Combine(root, "recovery");
        Outcome failed = Command("--workdir", recovery, "--fault", "publish-failed", "fulfillment");
        Assert.That(failed.Failure, Is.InstanceOf<JacquardException>(), "publish failure: " + failed.Output);
        var error = (JacquardException)failed.Failure!;
        Assert.That(error.Failure, Is.EqualTo(Failure.OperationFailed));
        Assert.That(error.Result!.Code, Is.EqualTo("E1613"));
        Assert.That(failed.Output, Does.Contain("\"pendingDeliveries\":1"));

        // A failed explicit delivery prints no count.
        Outcome undelivered = Command("--workdir", recovery, "--fault", "publish-failed", "--deliver-pending", "fulfillment");
        Assert.That(undelivered.Failure is UsageException && !undelivered.Output.Contains("delivered", StringComparison.Ordinal), Is.True, undelivered.Output);
        foreach (string expected in new[] { "{\"delivered\":1}", "{\"delivered\":0}" })
        {
            Outcome delivered = Command("--workdir", recovery, "--deliver-pending", "fulfillment");
            Assert.That(delivered.Ok && delivered.Output.Contains(expected, StringComparison.Ordinal), Is.True, "deliver pending: " + delivered.Failure + " " + delivered.Output);
        }

        string inspect = Path.Combine(root, "triage-inspect");
        Outcome inspected = Command("--workdir", Path.Combine(root, "triage"), "--inspect", inspect, "triage");
        Assert.That(inspected.Ok, Is.True, inspected.Failure?.ToString());
        Assert.That(File.Exists(inspect + ".0.json"), Is.True, "inspection not saved");
        Assert.That(File.GetUnixFileMode(inspect + ".0.json"), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(Command("unknown").Failure, Is.InstanceOf<UsageException>());
        Assert.That(Command("--fault", "nope", "fulfillment").Failure, Is.InstanceOf<UsageException>());
    }

    [Test]
    public void PartialCompletionFaultsRecoverExplicitlyAcrossRestart()
    {
        SdkLiveTests.RequireLive();
        foreach ((string fault, string code, long delivered) in new[] { ("after-commit", "E1612", 1L), ("publish-failed", "E1613", 1L), ("after-delivery", "E1612", 0L) })
        {
            string workdir = Path.Combine(root, fault);
            Outcome faulted = Command("--workdir", workdir, "--fault", fault, "fulfillment");
            Assert.That(faulted.Failure, Is.InstanceOf<JacquardException>(), fault);
            Assert.That(((JacquardException)faulted.Failure!).Result!.Code, Is.EqualTo(code), fault);
            Assert.That(faulted.Output, Does.Contain("\"bookings\":1"), fault);

            // A restart sees the committed booking; recovery is explicit.
            Outcome recovered = Command("--workdir", workdir, "--deliver-pending", "fulfillment");
            Assert.That(recovered.Ok && recovered.Output.Contains("{\"delivered\":" + delivered + "}", StringComparison.Ordinal), Is.True, fault + ": " + recovered.Output);
            Outcome repeated = Command("--workdir", workdir, "fulfillment");
            Assert.That(repeated.Ok && repeated.Output.Contains("DuplicateOrder", StringComparison.Ordinal) && repeated.Output.Contains("\"pendingDeliveries\":0", StringComparison.Ordinal), Is.True, fault + ": " + repeated.Output);
            Assert.That(Summary(repeated.Output)["deliveries"], Is.EqualTo(Json.Of(1)), fault);
        }
    }

    /// <summary>
    /// The store's temporary file is created exclusively, so a file already
    /// at its name (not the store's) makes the commit fail and is left as it
    /// was.
    /// </summary>
    [Test]
    public void TheStoreRemovesOnlyTheTemporaryFileItCreated()
    {
        string inventory = Path.Combine(root, "inventory.json");
        Examples.Examples.Warehouse warehouse = Examples.Examples.Warehouse.Open(inventory, string.Empty);

        // The name the next commit will use: the store's counter plus one.
        long upcoming = (long)typeof(Examples.Examples.Warehouse).GetField("next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)! + 1;
        string occupied = inventory + "." + Environment.ProcessId + "-" + upcoming + ".tmp";
        File.WriteAllText(occupied, "caller-owned");
        var order = new Examples.Examples.Order("o", new[] { new Examples.Examples.Line("sensor", 1) });
        Support.Throws<IOException>(() => warehouse.ReserveOrder(order));
        Assert.That(File.ReadAllText(occupied), Is.EqualTo("caller-owned"), "a file the store did not create was changed or removed");

        // The next commit takes the next name and succeeds.
        Assert.That(warehouse.ReserveOrder(order), Is.InstanceOf<Examples.Examples.Booked>());
        Assert.That(File.Exists(occupied), Is.True);
    }

    /// <summary>Runs the command in a fresh process; returns its exit status, stdout, and stderr.</summary>
    private (int Status, string Stdout, string Stderr) Process(params string[] arguments)
    {
        var info = new ProcessStartInfo(Support.Muxer())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        foreach (string argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "JacquardLang.Host.Tests.runtimeconfig.json"), Path.Combine(AppContext.BaseDirectory, "JacquardExamples.dll") }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["JACQUARD_EXAMPLES"] = ExamplesDirectory;
        info.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        var stdout = System.Threading.Tasks.Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderr = System.Threading.Tasks.Task.Run(() => process.StandardError.ReadToEnd());
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the command did not finish");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    [Test]
    public void TheCommandExits2ForUsageErrors()
    {
        foreach (string[] arguments in new[] { new[] { "unknown" }, new[] { "--fault", "nope", "fulfillment" }, new[] { "--workdir" }, Array.Empty<string>() })
        {
            (int status, _, string stderr) = Process(arguments);
            Assert.That(status, Is.EqualTo(2), string.Join(" ", arguments) + ": " + stderr);
            Assert.That(stderr.Trim(), Is.Not.Empty, string.Join(" ", arguments) + ": no message");
        }
    }

    [Test]
    public void TheCommandExits1ForAJacquardFailure()
    {
        SdkLiveTests.RequireLive();
        (int status, string stdout, string stderr) = Process("--workdir", Path.Combine(root, "work"), "--fault", "publish-failed", "fulfillment");
        Assert.That(status, Is.EqualTo(1), stderr);
        Assert.That(stderr, Does.Contain("E1613"));
        Assert.That(stdout, Does.Contain("\"pendingDeliveries\":1"));
    }
}
