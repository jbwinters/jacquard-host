using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>
/// Core commands are bounded: their exit status, bounded output, and added
/// environment are reported; a timeout or a cancellation kills the command
/// and its process group; a background child is killed when the command
/// exits, and an unrelated process holding the output is not; nothing a
/// descendant holds can hold the caller; and no descriptor is left behind.
/// </summary>
[TestFixture]
[Platform("Linux")]
public sealed class CoreCommandTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private string root = string.Empty;

    [SetUp]
    public void Prepare() => root = SdkSupport.Scratch("command");

    [TearDown]
    public void CleanUp()
    {
        CoreCommand.WithoutGroups = false;
        CoreCommand.RecordDescendants = true;
        CoreCommand.RecordedHook = null;
        CoreCommand.StartedHook = null;
        CoreCommand.ExitedHook = null;
        CoreCommand.KillGroupAtExit = true;
        CoreCommand.KillGroupOnStop = true;
        CoreCommand.ProcRoot = "/proc";
        CoreCommand.ProcEntryCap = CoreCommand.ProcEntries;
        CoreCommand.ProcByteCap = CoreCommand.ProcBytes;
        CoreCommand.StatReadHook = null;
        CoreCommand.ClockHook = null;
        CoreCommand.SignalHook = null;
        CoreCommand.ExitWaitHook = null;
        foreach (string file in Directory.EnumerateFiles(root, "*.pid"))
        {
            Support.Kill(int.Parse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture));
        }

        SdkSupport.RemoveScratch(root);
    }

    private static bool HasSetsid => CoreCommand.Setsid is not null;

    private string PidPath(string name) => Path.Combine(root, name + ".pid");

    private static CoreCommand.Output Run(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default, int stdoutLimit = 1 << 20, int stderrLimit = 4096) =>
        CoreCommand.Run(SdkSupport.Sh(script), null, timeout ?? Minute, stdoutLimit, stderrLimit, cancellationToken);

    [Test]
    public void TheExitStatusOutputAndAddedEnvironmentAreReported()
    {
        CoreCommand.Output output = CoreCommand.Run(
            SdkSupport.Sh("echo out; echo err >&2; echo \"$JQ_ADDED\"; exit 3"),
            new Dictionary<string, string> { ["JQ_ADDED"] = "added" },
            Minute,
            1 << 20,
            4096,
            default);
        Assert.That(output.ExitStatus, Is.EqualTo(3));
        Assert.That(SdkSupport.Text(output.Stdout), Is.EqualTo("out\nadded\n"));
        Assert.That(SdkSupport.Text(output.Stderr), Is.EqualTo("err\n"));
        Assert.That(output.StdoutOverflow, Is.False);

        // The command's stdin reads end of file at once.
        Assert.That(SdkSupport.Text(Run("cat; echo done").Stdout), Is.EqualTo("done\n"));
    }

    [Test]
    public void OutputBeyondTheLimitsIsReadAndCountedButNotKept()
    {
        CoreCommand.Output output = Run("head -c 3000000 /dev/zero; head -c 100000 /dev/zero >&2", stdoutLimit: 1000, stderrLimit: 10);
        Assert.That(output.ExitStatus, Is.EqualTo(0));
        Assert.That(output.Stdout, Has.Length.EqualTo(1000));
        Assert.That(output.StdoutOverflow, Is.True);
        Assert.That(output.Stderr, Has.Length.EqualTo(10));
    }

    [Test]
    public void ACommandThatDoesNotExitInTimeIsKilledWithItsChild()
    {
        CarrierException error = Support.Throws<CarrierException>(() =>
            Run("sleep 300 & echo $! > '" + PidPath("child") + "'; wait", TimeSpan.FromMilliseconds(500)))!;
        Assert.That(error.IsTimeout, Is.True, error.Message);
        SdkSupport.Dies("the timed-out command's child", SdkSupport.PidFile(PidPath("child")));
    }

    [Test]
    public void CancellationKillsTheCommandAndItsChild()
    {
        using var cancel = new CancellationTokenSource();
        Thread canceller = Support.After(TimeSpan.Zero, () =>
        {
            SdkSupport.PidFile(PidPath("child"));
            cancel.Cancel();
        });
        CarrierException error = Support.Throws<CarrierException>(() =>
            Run("sleep 300 & echo $! > '" + PidPath("child") + "'; wait", cancellationToken: cancel.Token))!;
        Assert.That(error.IsAborted, Is.True, error.Message);
        SdkSupport.Dies("the cancelled command's child", SdkSupport.PidFile(PidPath("child")));
        canceller.Join();
    }

    /// <summary>Under <c>setsid</c> the command keeps its pid (no fork) and leads its own process group.</summary>
    [Test]
    public void TheCommandKeepsItsPidAndLeadsItsOwnGroup()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        int started = 0;
        CoreCommand.StartedHook = pid => started = pid;
        string[] fields = SdkSupport.Text(Run("echo $$; cut -d')' -f2 /proc/$$/stat | cut -d' ' -f4,5").Stdout).Split('\n');
        Assert.That(int.Parse(fields[0], CultureInfo.InvariantCulture), Is.EqualTo(started), "setsid forked");
        string[] ids = fields[1].Split(' ');
        Assert.That(ids, Is.EqualTo(new[] { started.ToString(CultureInfo.InvariantCulture), started.ToString(CultureInfo.InvariantCulture) }), "pgid and sid");
    }

    /// <summary>
    /// <c>sh -c 'sleep 300 &amp; echo $!'</c> exits at once, leaving a child
    /// that holds its output; the group kill at the exit ends it, and the
    /// command still succeeds, as in Rust. With that kill disabled (a test
    /// seam) the child survives: the kill is what ends it.
    /// </summary>
    [Test]
    public void ABackgroundChildIsKilledWhenTheCommandExits()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        CoreCommand.Output output = Run("sleep 300 & echo $!");
        Assert.That(output.ExitStatus, Is.EqualTo(0));
        SdkSupport.Dies("the background child", int.Parse(SdkSupport.Text(output.Stdout).Trim(), CultureInfo.InvariantCulture));

        CoreCommand.KillGroupAtExit = false;
        output = Run("sleep 300 & echo $!");
        int survivor = int.Parse(SdkSupport.Text(output.Stdout).Trim(), CultureInfo.InvariantCulture);
        File.WriteAllText(PidPath("survivor"), survivor + "\n");
        Assert.That(Support.Alive(survivor), Is.True, "without the exit kill the child should survive");
    }

    /// <summary>
    /// Once the leader has exited and been reaped, a live process holding its
    /// pid means the id was reused: the old group is empty, and the group
    /// kill sends nothing. Here an unrelated group leader stands for the
    /// process that took the pid after a wraparound; without the check the
    /// kill would end its group.
    /// </summary>
    [Test]
    public void AGroupIdReusedAfterTheLeaderIsReapedIsNotKilled()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        using Process leader = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })!;
        leader.WaitForExit();
        Assert.That(leader.HasExited, Is.True);
        string pidFile = PidPath("reused");
        using Process other = Process.Start(new ProcessStartInfo("/bin/sh", new[] { "-c", "exec " + CoreCommand.Setsid + " sh -c 'echo $$ > \"" + pidFile + "\"; exec sleep 300'" })
        {
            UseShellExecute = false,
        })!;
        int unrelated = SdkSupport.PidFile(pidFile);
        CoreCommand.KillGroup(unrelated, leader);
        Thread.Sleep(200);
        Assert.That(Support.Alive(unrelated), Is.True, "a group whose id was reused was killed");
    }

    /// <summary>A child that closed its output and keeps running is in the group, so it is killed at the exit too.</summary>
    [Test]
    public void ABackgroundChildThatClosedItsOutputIsKilled()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        CoreCommand.Output output = Run("sleep 300 </dev/null >/dev/null 2>&1 & echo $! > '" + PidPath("quiet") + "'");
        Assert.That(output.ExitStatus, Is.EqualTo(0));
        SdkSupport.Dies("the quiet child", SdkSupport.PidFile(PidPath("quiet")));
    }

    /// <summary>
    /// A process outside the command's group that holds the command's
    /// output is neither killed nor waited for: the command returns, and the
    /// holder lives on.
    /// </summary>
    [Test]
    public void AnUnrelatedProcessHoldingTheOutputIsNotKilled()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        Process? holder = null;
        CoreCommand.StartedHook = pid =>
        {
            // Started by the test, not the command: it opens the command's stdout pipe through /proc.
            holder = Process.Start(new ProcessStartInfo("/bin/sh", new[] { "-c", "exec sleep 300 >>/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/fd/1" })
            {
                UseShellExecute = false,
            });
        };
        try
        {
            CoreCommand.Output output = Support.Within(Minute, () => Run("sleep 0.5; echo done"));
            Assert.That(SdkSupport.Text(output.Stdout), Is.EqualTo("done\n"));
            Assert.That(holder, Is.Not.Null);
            Assert.That(Support.Alive(holder!.Id), Is.True, "an unrelated holder was killed");
        }
        finally
        {
            if (holder is not null)
            {
                Support.Kill(holder.Id);
                holder.WaitForExit(10_000);
                holder.Dispose();
            }
        }
    }

    /// <summary>A stop during a long command kills its whole group: a child and a grandchild.</summary>
    [Test]
    public void AStopKillsTheWholeGroup()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        using var cancel = new CancellationTokenSource();
        string grandchild = PidPath("grandchild");
        string child = PidPath("child");
        Thread canceller = Support.After(TimeSpan.Zero, () =>
        {
            SdkSupport.PidFile(grandchild);
            cancel.Cancel();
        });
        Support.Throws<CarrierException>(() => Run(
            "(sleep 300 & echo $! > '" + grandchild + "'; wait) & echo $! > '" + child + "'; wait",
            cancellationToken: cancel.Token));
        canceller.Join();
        SdkSupport.Dies("the child", SdkSupport.PidFile(child));
        SdkSupport.Dies("the grandchild", SdkSupport.PidFile(grandchild));
    }

    /// <summary>
    /// Without a group (a test seam), a background child recorded while the
    /// command ran is killed when it exits; with recording off it survives,
    /// so the recorded kill is what ends it.
    /// </summary>
    [Test]
    public void WithoutGroupsARecordedDescendantIsKilledAtTheExit()
    {
        CoreCommand.WithoutGroups = true;
        var recorded = new List<int>();
        CoreCommand.RecordedHook = pids =>
        {
            lock (recorded)
            {
                recorded.Clear();
                recorded.AddRange(pids);
            }
        };
        string script = "sleep 300 </dev/null >/dev/null 2>&1 & echo $! > '" + PidPath("recorded") + "'; sleep 0.3";
        Assert.That(Run(script).ExitStatus, Is.EqualTo(0));
        int pid = SdkSupport.PidFile(PidPath("recorded"));
        lock (recorded)
        {
            Assert.That(recorded, Does.Contain(pid), "the child was never recorded");
        }

        SdkSupport.Dies("the recorded child", pid);

        CoreCommand.RecordDescendants = false;
        File.Delete(PidPath("recorded"));
        Run(script);
        Assert.That(Support.Alive(SdkSupport.PidFile(PidPath("recorded"))), Is.True, "without recording the child should survive");
    }

    [Test]
    public void WithoutGroupsAStopKillsTheCommandAndItsChild()
    {
        CoreCommand.WithoutGroups = true;
        CarrierException error = Support.Throws<CarrierException>(() =>
            Run("sleep 300 & echo $! > '" + PidPath("child") + "'; wait", TimeSpan.FromMilliseconds(500)))!;
        Assert.That(error.IsTimeout, Is.True);
        SdkSupport.Dies("the child", SdkSupport.PidFile(PidPath("child")));
    }

    /// <summary>A cancellation that lands during the exit cleanup is reported, not the output.</summary>
    [Test]
    public void AStopDuringTheExitCleanupIsReported()
    {
        using var cancel = new CancellationTokenSource();
        CoreCommand.ExitedHook = cancel.Cancel;
        CarrierException error = Support.Throws<CarrierException>(() => Run("echo hi", cancellationToken: cancel.Token))!;
        Assert.That(error.IsAborted, Is.True);
    }

    /// <summary>
    /// A fake <c>/proc</c> of <paramref name="count"/> processes whose parent
    /// is <paramref name="parent"/>, each <c>stat</c> file padded to 64 KiB.
    /// Their pids start at 5,000,001, above Linux's largest pid (2^22), so a
    /// kill sent to one reaches no process.
    /// </summary>
    private string FakeProc(string name, int count, int parent)
    {
        string proc = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        string padding = string.Concat(Enumerable.Repeat(" 0", 32 * 1024));
        for (int i = 0; i < count; i++)
        {
            string pid = (5_000_001 + i).ToString(CultureInfo.InvariantCulture);
            string directory = Directory.CreateDirectory(Path.Combine(proc, pid)).FullName;
            File.WriteAllText(
                Path.Combine(directory, "stat"),
                pid + " (fake) S " + parent.ToString(CultureInfo.InvariantCulture) + " " + pid + " " + pid + " 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 "
                + (1000 + i).ToString(CultureInfo.InvariantCulture) + padding + "\n");
        }

        return proc;
    }

    /// <summary>A command that runs until <paramref name="release"/> exists, for at most 30 seconds.</summary>
    private static string Waiting(string release) =>
        "i=0; while [ ! -e '" + release + "' ] && [ $i -lt 3000 ]; do sleep 0.01; i=$((i+1)); done";

    /// <summary>
    /// Without groups, one record reads at most its entry cap of
    /// <c>/proc</c> entries and its byte cap of <c>stat</c> bytes, and at
    /// most 1,024 bytes of each <c>stat</c> file, however many processes and
    /// bytes there are (a fake <c>/proc</c> through a test seam, with lowered
    /// caps).
    /// </summary>
    [Test]
    public void AProcRecordReadsAtMostItsCaps()
    {
        CoreCommand.WithoutGroups = true;
        CoreCommand.ProcRoot = FakeProc("proc", 50, 1);
        foreach ((int entryCap, int byteCap, int expected) in new[] { (20, CoreCommand.ProcBytes, 20), (1000, 5 * 1024, 5) })
        {
            CoreCommand.ProcEntryCap = entryCap;
            CoreCommand.ProcByteCap = byteCap;
            var reads = new List<int>();
            int records = 0;
            using var cancel = new CancellationTokenSource();
            CoreCommand.StatReadHook = (path, bytes, stoppable) =>
            {
                if (stoppable && records == 0)
                {
                    reads.Add(bytes);
                }
            };
            CoreCommand.RecordedHook = pids =>
            {
                if (++records == 1)
                {
                    cancel.Cancel();
                }
            };
            CarrierException error = Support.Throws<CarrierException>(() => Run(Waiting(Path.Combine(root, "never")), cancellationToken: cancel.Token))!;
            Assert.That(error.IsAborted, Is.True);
            Assert.That(reads, Has.Count.EqualTo(expected), "caps " + entryCap + "/" + byteCap);
            Assert.That(reads, Is.All.EqualTo(CoreCommand.StatBytes));
        }
    }

    /// <summary>
    /// The byte cap holds when it is not a multiple of the 1,024 bytes read
    /// of each <c>stat</c> file: with a 1,025-byte cap, a record reads 1,024
    /// bytes and then the 1 byte left, never a second full file.
    /// </summary>
    [Test]
    public void AProcRecordNeverReadsPastANonAlignedByteCap()
    {
        CoreCommand.WithoutGroups = true;
        CoreCommand.ProcRoot = FakeProc("proc", 10, 1);
        CoreCommand.ProcByteCap = 1025;
        var reads = new List<int>();
        int records = 0;
        using var cancel = new CancellationTokenSource();
        CoreCommand.StatReadHook = (path, bytes, stoppable) =>
        {
            if (stoppable && records == 0)
            {
                reads.Add(bytes);
            }
        };
        CoreCommand.RecordedHook = pids =>
        {
            if (++records == 1)
            {
                cancel.Cancel();
            }
        };
        CarrierException error = Support.Throws<CarrierException>(() => Run(Waiting(Path.Combine(root, "never")), cancellationToken: cancel.Token))!;
        Assert.That(error.IsAborted, Is.True);
        Assert.That(reads, Is.EqualTo(new[] { 1024, 1 }));
    }

    /// <summary>The state letter of a live process, from <c>/proc/&lt;pid&gt;/stat</c>.</summary>
    private static char State(int pid)
    {
        string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        return stat[(stat.LastIndexOf(')') + 2)..][0];
    }

    /// <summary>
    /// On a stop without groups, the command is frozen with SIGSTOP before
    /// the last record, the last record ends at its 250 ms budget, and only
    /// then is the command killed. A cancellation during a record ends that
    /// record at the next entry (the third of 50). The budget is measured on
    /// a test clock that each read of the last record advances by 100 ms
    /// (and each reading by 1 ms), so exactly three entries fit in it: the
    /// fourth would start 304 ms after the stop. Without the budget, the
    /// last record would read all 40 entries of its cap.
    /// </summary>
    [Test]
    public void AStopFreezesTheCommandBeforeItsLastRecordAndBoundsIt()
    {
        CoreCommand.WithoutGroups = true;
        CoreCommand.ProcRoot = FakeProc("proc", 50, 1);
        CoreCommand.ProcEntryCap = 40;
        long clock = 1_000_000;
        long hundredMs = Stopwatch.Frequency / 10;
        long oneMs = Stopwatch.Frequency / 1000;
        CoreCommand.ClockHook = () => Interlocked.Add(ref clock, oneMs);
        int command = 0;
        CoreCommand.StartedHook = pid => command = pid;
        var events = new List<string>();
        int stoppable = 0;
        char frozen = '?';
        using var cancel = new CancellationTokenSource();
        CoreCommand.SignalHook = (pid, signal) => events.Add((pid == command ? "command " : "other ") + signal.ToString(CultureInfo.InvariantCulture));
        CoreCommand.StatReadHook = (path, bytes, canStop) =>
        {
            if (canStop)
            {
                if (++stoppable == 3)
                {
                    cancel.Cancel();
                }

                return;
            }

            if (!events.Contains("read"))
            {
                // SIGSTOP is delivered asynchronously; the command must reach the stopped state.
                Support.Eventually(() => State(command) == 'T', TimeSpan.FromSeconds(10));
                frozen = State(command);
            }

            events.Add("read");
            Interlocked.Add(ref clock, hundredMs);
        };
        CarrierException error = Support.Throws<CarrierException>(() => Run(Waiting(Path.Combine(root, "never")), cancellationToken: cancel.Token))!;
        Assert.That(error.IsAborted, Is.True);
        Assert.That(stoppable, Is.EqualTo(3));
        Assert.That(events, Is.EqualTo(new[] { "command 19", "read", "read", "read", "command 9" }));
        Assert.That(frozen, Is.EqualTo('T'));
        SdkSupport.Dies("the command", command);
    }

    /// <summary>
    /// The cleanup after a stop ends at its budgets on the test clock, even
    /// when the last record and the kills of 300 recorded descendants would
    /// take 600 ms each: the clock stands still except that each
    /// <c>stat</c> read after the stop advances it by 2 ms. The last record
    /// reads at 0, 2, ..., 248 ms after the stop (125 reads) and ends at
    /// 250 ms, when the command is killed; the kills read at 250, ..., 498 ms
    /// (125 reads) and end at 500 ms; the wait for the command to exit ends
    /// at 750 ms. Without the budgets, all 300 entries would be read in each.
    /// </summary>
    [Test]
    public void TheCleanupAfterAStopEndsAtItsBudgets()
    {
        CoreCommand.WithoutGroups = true;
        string proc = Path.Combine(root, "proc");
        CoreCommand.ProcRoot = proc;
        CoreCommand.StartedHook = pid => FakeProc("proc", 300, pid);
        long start = 1_000_000_000;
        long clock = start;
        long ms = Stopwatch.Frequency / 1000;
        CoreCommand.ClockHook = () => Interlocked.Read(ref clock);
        int recorded = 0;
        using var cancel = new CancellationTokenSource();
        CoreCommand.RecordedHook = pids =>
        {
            if (recorded == 0)
            {
                recorded = pids.Count;
                cancel.Cancel();
            }
        };
        var reads = new List<long>();
        long killed = -1;
        CoreCommand.SignalHook = (pid, signal) =>
        {
            if (signal == Unix.Sigkill)
            {
                killed = Interlocked.Read(ref clock) - start;
                reads.Add(-1);
            }
        };
        CoreCommand.StatReadHook = (path, bytes, canStop) =>
        {
            if (!canStop)
            {
                reads.Add(Interlocked.Read(ref clock) - start);
                Interlocked.Add(ref clock, 2 * ms);
            }
        };
        var waits = new List<long>();
        CoreCommand.ExitWaitHook = end => waits.Add(end - start);
        Support.Throws<CarrierException>(() => Run(Waiting(Path.Combine(root, "never")), cancellationToken: cancel.Token));
        Assert.That(recorded, Is.EqualTo(300));
        int split = reads.IndexOf(-1);
        Assert.That(split, Is.GreaterThanOrEqualTo(0), "the command was not killed");
        List<long> record = reads.Take(split).ToList();
        List<long> kills = reads.Skip(split + 1).ToList();
        Assert.That(record, Is.EqualTo(Enumerable.Range(0, 125).Select(i => 2L * i * ms)), "the last record ends at 250 ms");
        Assert.That(killed, Is.EqualTo(250 * ms), "the command is killed when the record ends");
        Assert.That(kills, Is.EqualTo(Enumerable.Range(125, 125).Select(i => 2L * i * ms)), "the kills end at 500 ms");
        Assert.That(reads.Count - 1, Is.LessThan(600), "the record and the kills were not cut at their budgets");
        Assert.That(waits, Is.EqualTo(new[] { (long)CoreCommand.StopBudgetMs * ms }), "the exit wait ends at 750 ms");
    }

    /// <summary>
    /// A stop that lands during the kills of recorded descendants when the
    /// command exits does not leave the rest running: ten fake descendants
    /// are recorded, the command exits, a cancellation lands during the
    /// second kill check, the other eight are still checked (within the kill
    /// budget), and the stop is reported.
    /// </summary>
    [Test]
    public void AStopDuringTheExitKillsFinishesThem()
    {
        CoreCommand.WithoutGroups = true;
        string release = Path.Combine(root, "release");
        string proc = Path.Combine(root, "proc");
        CoreCommand.ProcRoot = proc;
        CoreCommand.StartedHook = pid => FakeProc("proc", 10, pid);
        int recorded = 0;
        CoreCommand.RecordedHook = pids =>
        {
            recorded = pids.Count;
            CoreCommand.RecordDescendants = false;
            File.WriteAllText(release, string.Empty);
        };
        int checks = 0;
        int afterStop = 0;
        using var cancel = new CancellationTokenSource();
        CoreCommand.StatReadHook = (path, bytes, stoppable) =>
        {
            if (!stoppable)
            {
                afterStop++;
            }
            else if (!CoreCommand.RecordDescendants && ++checks == 2)
            {
                cancel.Cancel();
            }
        };
        CarrierException error = Support.Throws<CarrierException>(() => Run(Waiting(release), cancellationToken: cancel.Token))!;
        Assert.That(error.IsAborted, Is.True);
        Assert.That(recorded, Is.EqualTo(10));
        Assert.That(checks, Is.EqualTo(2));
        Assert.That(afterStop, Is.EqualTo(8));
    }

    [Test]
    public void ACommandThatCannotStartIsALoss()
    {
        CarrierException error = Support.Throws<CarrierException>(() =>
            CoreCommand.Run(new[] { Path.Combine(root, "missing") }, null, Minute, 1024, 1024, default))!;
        Assert.That(error.IsLoss && !error.IsTimeout && !error.IsAborted, Is.True);
        Assert.That(error.Message, Does.StartWith("could not start "));
        CoreCommand.WithoutGroups = true;
        Assert.That(Support.Throws<CarrierException>(() =>
            CoreCommand.Run(new[] { Path.Combine(root, "missing") }, null, Minute, 1024, 1024, default))!.Message, Does.StartWith("could not start "));
    }

    /// <summary>An executable <c>setsid</c> cannot exec (its interpreter is missing) is a start failure, not the command's exit status.</summary>
    [Test]
    public void ACommandSetsidCannotExecIsALoss()
    {
        Assume.That(HasSetsid, "no setsid at /usr/bin or /bin");
        string broken = Path.Combine(root, "broken");
        File.WriteAllText(broken, "#!/no/such/interpreter\n");
        File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CarrierException error = Support.Throws<CarrierException>(() => CoreCommand.Run(new[] { broken }, null, Minute, 1024, 1024, default))!;
        Assert.That(error.IsLoss, Is.True);
        Assert.That(error.Message, Does.StartWith("could not start " + broken + " (setsid: "), error.Message);
    }

    /// <summary>
    /// A descendant that escapes the group and floods the output holds
    /// neither the caller nor the heap: the command returns once it exits,
    /// with at most the kept limit of output.
    /// </summary>
    [Test]
    public void AnEscapedFlooderHoldsNeitherTheCallerNorTheHeap()
    {
        string flooder = PidPath("flooder");
        CoreCommand.Output output = Support.Within(Minute, () =>
            Run("setsid sh -c 'echo $$ > \"" + flooder + "\"; exec yes' & sleep 0.2", stdoutLimit: 4096));
        Assert.That(output.Stdout.Length, Is.LessThanOrEqualTo(4096));
        // It is not the host's to kill (it left the group); its next write
        // after the host closed the pipe fails, and the test ends it.
        Support.Kill(SdkSupport.PidFile(flooder));
    }

    /// <summary>Commands release every descriptor they opened by the time they return (counted at once, with no wait a finalizer could satisfy).</summary>
    [Test]
    public void CommandsLeaveNoDescriptorsBehind()
    {
        Run("true");
        int before = Support.Descriptors();
        for (int i = 0; i < 20; i++)
        {
            Run("echo $i; echo e >&2");
            Support.Throws<CarrierException>(() => Run("sleep 300", TimeSpan.FromMilliseconds(50)));
        }

        Assert.That(Support.Descriptors(), Is.EqualTo(before));
    }
}
