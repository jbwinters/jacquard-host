using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using JacquardLang.Host.Carrier;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// Runs one short command to completion, bounded in time and output, the way
/// the SDK runs Core's <c>--version</c>, <c>check</c>, <c>hash</c>, and
/// <c>run</c> commands.
/// </summary>
/// <remarks>
/// <para>
/// As in the adapter's worker carrier, the command's stdout and stderr are
/// read only after <c>poll(2)</c> reports them ready, on the caller's thread,
/// in waits of 1 to 5 ms that recheck the timeout and the
/// <see cref="CancellationToken"/>; .NET's own stream reads, which cannot be
/// interrupted, are never left waiting. Its stdin is a pipe the host closes
/// at once, so it reads end of file.
/// </para>
/// <para>
/// On Linux, when a <c>setsid</c> is installed at <c>/usr/bin/setsid</c> or
/// <c>/bin/setsid</c> (util-linux's, or a compatible one such as BusyBox's),
/// the command runs as <c>setsid -- &lt;command&gt;</c>. .NET's child is never
/// a process group leader, so <c>setsid</c> starts a new session and execs
/// the command without forking: the command keeps the child's pid and leads
/// a new process group whose id is that pid, shared by every descendant that
/// does not leave it. The host kills that group with <c>kill(-pgid,
/// SIGKILL)</c> when the command exits, and on timeout or cancellation.
/// After the leader exits and .NET has reaped it, a live process holding the
/// leader's pid means that pid was reused, so the old group is empty and
/// nothing is killed; the window between that check and the kill is the same
/// as Rust's <c>kill(-pgid)</c> after a full pid wraparound. When
/// <c>setsid</c> cannot exec the command (exit status 126 or 127 with its own
/// <c>"setsid: "</c> message), the run is reported as a start failure. A
/// descendant that leaves the group (calls <c>setsid</c> itself) is neither
/// detected nor killed; the host closes its ends of the output after the
/// exit, so its next write fails.
/// </para>
/// <para>
/// Elsewhere (Linux without <c>setsid</c>), the command's descendants are
/// recorded from <c>/proc</c> about every 10 ms, each by pid and start time,
/// and every recorded descendant still alive with that start time is killed
/// when the command returns. Each record reads at most
/// <see cref="ProcEntries"/> process entries and <see cref="ProcBytes"/>
/// bytes in all (at most <see cref="StatBytes"/> of each <c>stat</c> file,
/// and never more than the bytes left), keeps at most
/// <see cref="ProcEntries"/> descendants in all, and observes the deadline
/// and the token before each entry, ending at once on a stop. On a timeout
/// or cancellation (a stop), the command is first sent <c>SIGSTOP</c>
/// (guarded against pid reuse by its start time), so it can start no more
/// children and its descendants stay its descendants; then a last record is
/// made within the same caps and at most <see cref="StopScanMs"/> after the
/// stop; then the command is killed and every recorded descendant, the kills
/// ending <see cref="StopScanMs"/> + <see cref="StopKillMs"/> after the stop;
/// then the host waits for the command to exit until
/// <see cref="StopBudgetMs"/> after the stop. Each budget is checked before
/// each entry or descendant, so the cleanup returns within
/// <see cref="StopBudgetMs"/> plus one <c>stat</c> read and one signal. The
/// kills when the command exits by itself run to the end; a stop that lands
/// there lets them go on for at most <see cref="StopKillMs"/>. A descendant
/// past the caps or the budgets, or one started and reparented between two
/// records, keeps running. On macOS, which has no <c>/proc</c>, nothing is
/// recorded: only .NET's <see cref="Process.Kill(bool)"/> tree kill on a
/// stop applies.
/// </para>
/// </remarks>
internal static class CoreCommand
{
    /// <summary>The most of each stream read after the command has exited.</summary>
    internal const int ExitDrain = 4 << 20;

    /// <summary>How long after a stop the last record of descendants may run (without groups).</summary>
    internal const int StopScanMs = 250;

    /// <summary>How much longer after that the kills of recorded descendants may run.</summary>
    internal const int StopKillMs = 250;

    /// <summary>
    /// The most time from a stop to the end of the cleanup, the wait for the
    /// killed command to exit included: <see cref="StopScanMs"/> +
    /// <see cref="StopKillMs"/> + 250 ms, each budget checked before each
    /// step.
    /// </summary>
    internal const int StopBudgetMs = StopScanMs + StopKillMs + 250;

    /// <summary>The longest single wait between checks.</summary>
    private const int PollSliceMs = 5;

    /// <summary>The most of each stream one step reads, so a fast writer cannot hold the caller.</summary>
    private const int Slice = 1 << 20;

    /// <summary>How often descendants are recorded where there are no process groups.</summary>
    private const long SampleIntervalMs = 10;

    /// <summary>The most <c>/proc</c> entries one record reads, and the most descendants recorded in all.</summary>
    internal const int ProcEntries = 16_384;

    /// <summary>The most bytes of <c>stat</c> files one record reads.</summary>
    internal const int ProcBytes = 4 << 20;

    /// <summary>The most bytes read of one <c>stat</c> file; the fields read come well before.</summary>
    internal const int StatBytes = 1024;

    private const short Ready = Posix.PollIn | Posix.PollHup | Posix.PollErr | Posix.PollNval;

    /// <summary>Test seam: when set, commands run without a process group, as where <c>setsid</c> is absent.</summary>
    internal static volatile bool WithoutGroups;

    /// <summary>Test seam: whether descendants are recorded where there are no groups.</summary>
    internal static volatile bool RecordDescendants = true;

    /// <summary>Test seam: runs after each record with every pid recorded so far. Null outside tests.</summary>
    internal static volatile Action<IReadOnlyCollection<int>>? RecordedHook;

    /// <summary>Test seam: the directory read as <c>/proc</c>.</summary>
    internal static volatile string ProcRoot = "/proc";

    /// <summary>Test seam: the entry cap of one record.</summary>
    internal static volatile int ProcEntryCap = ProcEntries;

    /// <summary>Test seam: the byte cap of one record.</summary>
    internal static volatile int ProcByteCap = ProcBytes;

    /// <summary>
    /// Test seam: runs after each <c>stat</c> file a record or the kills of
    /// recorded descendants read, with its path, how many bytes were read,
    /// and whether a stop ends that work. Null outside tests.
    /// </summary>
    internal static volatile Action<string, int, bool>? StatReadHook;

    /// <summary>Test seam: the clock the budgets after a stop are measured with, in <see cref="Stopwatch"/> ticks. Null outside tests.</summary>
    internal static volatile Func<long>? ClockHook;

    /// <summary>Test seam: runs before the host sends the command itself a signal on a stop without groups, with its pid and the signal. Null outside tests.</summary>
    internal static volatile Action<int, int>? SignalHook;

    /// <summary>Test seam: receives the end of each wait for a killed command to exit, on the budgets' clock. Null outside tests.</summary>
    internal static volatile Action<long>? ExitWaitHook;

    /// <summary>Test seam: receives the pid of each command once it has started. Null outside tests.</summary>
    internal static volatile Action<int>? StartedHook;

    /// <summary>Test seam: runs after the cleanup that follows the exit, before the stop is checked again. Null outside tests.</summary>
    internal static volatile Action? ExitedHook;

    /// <summary>Test seam: whether the group is killed when the command exits.</summary>
    internal static volatile bool KillGroupAtExit = true;

    /// <summary>Test seam: whether the group is killed on a stop.</summary>
    internal static volatile bool KillGroupOnStop = true;

    /// <summary>The <c>setsid</c> that starts each command in its own group, or null where groups are not used.</summary>
    internal static string? Setsid { get; } = FindSetsid();

    /// <summary>Whether the next command runs in its own process group.</summary>
    internal static bool Grouped => Setsid is not null && !WithoutGroups;

    private static string? FindSetsid()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/stat"))
        {
            return null;
        }

        foreach (string candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> is a regular file this process may execute (<c>access(X_OK)</c>).</summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            return Unix.Stat(path, follow: true).Kind == Unix.Kind.Regular && Unix.MayExecute(path);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The command's executable, resolved on the <c>PATH</c> when it names no
    /// directory, so that one <c>setsid</c> cannot exec is reported as a
    /// start failure, as without <c>setsid</c>.
    /// </summary>
    private static string Executable(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            if (IsExecutable(name))
            {
                return name;
            }
        }
        else
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(':'))
            {
                string candidate = Path.Combine(directory.Length == 0 ? "." : directory, name);
                if (IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        throw CarrierException.Loss("could not start " + Identities.Excerpt(name) + " (not an executable file)");
    }

    /// <summary>A command that exited.</summary>
    /// <param name="ExitStatus">The exit status; .NET reports a signal N as 128 + N.</param>
    /// <param name="Stdout">At most the requested limit of stdout.</param>
    /// <param name="StdoutOverflow">Whether stdout was longer than its limit.</param>
    /// <param name="Stderr">At most the requested limit of stderr.</param>
    internal sealed record Output(int ExitStatus, byte[] Stdout, bool StdoutOverflow, byte[] Stderr);

    /// <summary>A bounded prefix of one stream, read only when poll reports it ready.</summary>
    private sealed class Capture
    {
        private readonly Stream stream;
        private readonly int limit;
        private readonly byte[] chunk = new byte[8192];
        private byte[] kept = Array.Empty<byte>();
        private int length;

        internal Capture(Stream stream, int fd, int limit)
        {
            this.stream = stream;
            Fd = fd;
            this.limit = limit;
        }

        internal int Fd { get; }

        internal bool Overflow { get; private set; }

        internal bool Ended { get; private set; }

        /// <summary>Reads at most <paramref name="budget"/> bytes already in the pipe; returns how many. Never blocks.</summary>
        internal int Step(int budget)
        {
            int total = 0;
            if (Ended)
            {
                return 0;
            }

            try
            {
                Span<Posix.PollFd> fds = stackalloc Posix.PollFd[1];
                while (total < budget)
                {
                    fds[0] = new Posix.PollFd { Fd = Fd, Events = Posix.PollIn };
                    Posix.Poll(fds, 0);
                    if ((fds[0].Revents & Ready) == 0)
                    {
                        break;
                    }

                    int n = stream.Read(chunk, 0, Math.Min(chunk.Length, budget - total));
                    if (n <= 0)
                    {
                        Ended = true;
                        break;
                    }

                    Keep(n);
                    total += n;
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or CarrierException)
            {
                Ended = true;
            }

            return total;
        }

        private void Keep(int n)
        {
            int room = limit - length;
            if (n > room)
            {
                Overflow = true;
            }

            int take = Math.Min(n, room);
            if (take > 0)
            {
                if (length + take > kept.Length)
                {
                    Array.Resize(ref kept, Math.Min(limit, Math.Max(length + take, kept.Length * 2)));
                }

                Array.Copy(chunk, 0, kept, length, take);
                length += take;
            }
        }

        internal byte[] Bytes() => kept.AsSpan(0, length).ToArray();
    }

    /// <summary>The command's descendants seen while it ran, by pid, with their start times (Linux).</summary>
    private sealed class Recorded
    {
        private readonly Dictionary<int, string> started = new();
        private long next = Stopwatch.GetTimestamp();

        /// <summary>
        /// Records the command's current descendants, at most once per
        /// <see cref="SampleIntervalMs"/>, reading at most the entry and byte
        /// caps; a stop ends the record at the next entry, recording nothing
        /// from it. The last record, made on a stop once the command is
        /// frozen, runs whenever it is asked, within the caps, until
        /// <paramref name="end"/> (checked before each entry); what it read by
        /// then is recorded.
        /// </summary>
        internal void Sample(int root, long expires, CancellationToken cancellationToken, long end = long.MaxValue)
        {
            bool final = end != long.MaxValue;
            long now = Stopwatch.GetTimestamp();
            if (!RecordDescendants || (now < next && !final) || !OperatingSystem.IsLinux())
            {
                return;
            }

            next = now + (Stopwatch.Frequency * SampleIntervalMs / 1000);
            var children = new Dictionary<int, List<(int Pid, string Start)>>();
            int cap = ProcEntryCap;
            int byteCap = ProcByteCap;
            int entries = 0;
            long bytes = 0;
            foreach (string entry in ProcDirectories())
            {
                if (final ? Now() >= end : Stopped(expires, cancellationToken) is not null)
                {
                    if (!final)
                    {
                        return;
                    }

                    break;
                }

                if (++entries > cap || bytes >= byteCap)
                {
                    break;
                }

                // Never more than the bytes left, so a record reads at most its cap.
                if (ReadStat(entry, ref bytes, (int)Math.Min(StatBytes, byteCap - bytes), stoppable: !final) is { } stat)
                {
                    if (!children.TryGetValue(stat.Parent, out List<(int, string)>? list))
                    {
                        list = new List<(int, string)>();
                        children[stat.Parent] = list;
                    }

                    list.Add((stat.Pid, stat.Start));
                }
            }

            var queue = new Queue<int>();
            var seen = new HashSet<int> { root };
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                if (!children.TryGetValue(queue.Dequeue(), out List<(int Pid, string Start)>? list))
                {
                    continue;
                }

                foreach ((int pid, string start) in list)
                {
                    if (seen.Add(pid))
                    {
                        if (started.Count < ProcEntries)
                        {
                            started.TryAdd(pid, start);
                        }

                        queue.Enqueue(pid);
                    }
                }
            }

            RecordedHook?.Invoke(new List<int>(started.Keys));
        }

        /// <summary>
        /// Kills every recorded descendant still alive with the start time it
        /// was recorded with, so a reused pid is not killed (the start time is
        /// read just before the kill, as the JDK's process handles do). It
        /// ends at <paramref name="end"/>, checked before each descendant;
        /// when that is unset (at the command's own exit) it runs to the end,
        /// bounded by the number recorded, unless a stop lands, which lets it
        /// go on for at most <see cref="StopKillMs"/> more.
        /// </summary>
        internal void KillAlive(long expires, CancellationToken cancellationToken, long end = long.MaxValue)
        {
            long bytes = 0;
            bool afterStop = end != long.MaxValue;
            foreach ((int pid, string start) in started)
            {
                if (!afterStop && Stopped(expires, cancellationToken) is not null)
                {
                    afterStop = true;
                    end = After(Now(), StopKillMs);
                }

                if (afterStop && Now() >= end)
                {
                    return;
                }

                if (ReadStat(Path.Combine(ProcRoot, pid.ToString(CultureInfo.InvariantCulture)), ref bytes, StatBytes, !afterStop) is { } stat && stat.Start == start && !stat.Zombie)
                {
                    Unix.Kill(pid);
                }
            }
        }
    }

    private static IEnumerable<string> ProcDirectories()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(ProcRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using IEnumerator<string> cursor = entries.GetEnumerator();
        while (true)
        {
            try
            {
                if (!cursor.MoveNext())
                {
                    yield break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            string name = Path.GetFileName(cursor.Current);
            if (name.Length > 0 && name.Length <= 10 && IsDigits(name))
            {
                yield return cursor.Current;
            }
        }
    }

    private static bool IsDigits(string text)
    {
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A process's pid, parent, start time, and whether it is a zombie, from
    /// at most <see cref="StatBytes"/> of <c>/proc/&lt;pid&gt;/stat</c>,
    /// read after the last <c>)</c> (the command name may hold any byte),
    /// and at most <paramref name="limit"/>; null when it cannot be read.
    /// Adds the bytes read to <paramref name="bytes"/>. Only reads for a
    /// record or a descendant's kill are passed to <see cref="StatReadHook"/>.
    /// </summary>
    private static (int Pid, int Parent, int Group, string Start, bool Zombie)? ReadStat(string directory, ref long bytes, int limit = StatBytes, bool stoppable = false, bool report = true)
    {
        try
        {
            string path = Path.Combine(directory, "stat");
            byte[] buffer = new byte[Math.Clamp(limit, 0, StatBytes)];
            int read = 0;
            using (var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, BufferSize = 0 }))
            {
                int n;
                while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
                {
                    read += n;
                }
            }

            bytes += read;
            if (report)
            {
                StatReadHook?.Invoke(path, read, stoppable);
            }

            string stat = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            int close = stat.LastIndexOf(')');
            if (close < 0 || !int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return null;
            }

            // state ppid pgrp session tty_nr tpgid flags minflt cminflt majflt
            // cmajflt utime stime cutime cstime priority nice num_threads
            // itrealvalue starttime ...
            // A field cut by the byte cap is not read: the start time must be followed by another field.
            string[] fields = stat[(close + 1)..].Trim().Split(' ');
            if (fields.Length < 21
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent)
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int group))
            {
                return null;
            }

            return (pid, parent, group, fields[19], fields[0] is "Z" or "X");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Kills group <paramref name="group"/>, the pid of <paramref name="leader"/>,
    /// unless the leader has exited and a live process now holds that pid:
    /// while a group has members its id cannot be reused, so a reaped
    /// leader's pid held by a live process means the old group is empty.
    /// </summary>
    internal static void KillGroup(int group, Process leader)
    {
        long bytes = 0;
        if (HasExited(leader) && ReadStat("/proc/" + group.ToString(CultureInfo.InvariantCulture), ref bytes, report: false) is { Zombie: false })
        {
            return;
        }

        Unix.KillGroup(group);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether <c>setsid</c> itself failed to exec the command: it exits 126
    /// (cannot execute) or 127 (not found) and its message, unlike the
    /// command's, starts with <c>"setsid: "</c> (util-linux and BusyBox both
    /// print that prefix). The command never ran then.
    /// </summary>
    internal static bool SetsidFailed(Output output) =>
        output.ExitStatus is 126 or 127 && System.Text.Encoding.UTF8.GetString(output.Stderr).StartsWith("setsid: ", StringComparison.Ordinal);

    /// <summary>
    /// Runs <paramref name="command"/>, adding <paramref name="environment"/>
    /// to the inherited environment, and returns once it has exited.
    /// </summary>
    /// <param name="command">The executable and its arguments.</param>
    /// <param name="environment">Variables added to the inherited environment.</param>
    /// <param name="timeout">Bounds the whole run, from the start to the exit.</param>
    /// <param name="stdoutLimit">How much stdout is kept; the rest is read and counted as overflow.</param>
    /// <param name="stderrLimit">How much stderr is kept.</param>
    /// <param name="cancellationToken">Stops the command; it is then killed.</param>
    /// <param name="setsid">The <c>setsid</c> to run the command through where groups are used, in place of <see cref="Setsid"/>.</param>
    /// <exception cref="CarrierException">
    /// A loss when the command cannot start; a timeout when it does not exit
    /// in time; aborted when the token is cancelled. The command is killed in
    /// the last two cases.
    /// </exception>
    internal static Output Run(
        IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        int stdoutLimit,
        int stderrLimit,
        CancellationToken cancellationToken,
        string? setsid = null)
    {
        if (command.Count == 0 || command[0].Length == 0)
        {
            throw new ArgumentException("command must be a nonempty executable argument list", nameof(command));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive");
        }

        long expires = Worker.Expires(Stopwatch.GetTimestamp(), timeout);
        if (Stopped(expires, cancellationToken) is { } early)
        {
            throw early;
        }

        bool grouped = Grouped;
        var info = new ProcessStartInfo(grouped ? setsid ?? Setsid! : command[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (grouped)
        {
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(Executable(command[0]));
        }

        for (int i = 1; i < command.Count; i++)
        {
            info.ArgumentList.Add(command[i]);
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> variable in environment)
            {
                info.Environment[variable.Key] = variable.Value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            throw CarrierException.Loss("could not start " + Identities.Excerpt(command[0]) + " (" + error.Message + ")");
        }

        int pid = process.Id;
        StartedHook?.Invoke(pid);
        Recorded? recorded = grouped ? null : new Recorded();

        // Read now so the SIGSTOP on a stop cannot reach a process that took a reused pid.
        long unused = 0;
        string? coreStart = recorded is not null && OperatingSystem.IsLinux() ? ReadStat("/proc/" + pid.ToString(CultureInfo.InvariantCulture), ref unused, report: false)?.Start : null;
        Stream? stdout = null;
        Stream? stderr = null;
        bool cleaned = false;
        try
        {
            try
            {
                // Its stdin reads end of file at once.
                process.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The command already closed its end.
            }

            stdout = process.StandardOutput.BaseStream;
            stderr = process.StandardError.BaseStream;
            var output = new Capture(stdout, Descriptor(stdout), stdoutLimit);
            var errors = new Capture(stderr, Descriptor(stderr), stderrLimit);
            Output result = Await(process, pid, grouped, recorded, output, errors, expires, cancellationToken, ref cleaned);
            if (grouped && SetsidFailed(result))
            {
                string detail = System.Text.Encoding.UTF8.GetString(result.Stderr).Trim();
                int line = detail.IndexOf('\n', StringComparison.Ordinal);
                throw CarrierException.Loss("could not start " + Identities.Excerpt(command[0]) + " (" + (line < 0 ? detail : detail[..line]) + ")");
            }

            return result;
        }
        finally
        {
            if (!cleaned)
            {
                // A stop: everything below ends within StopBudgetMs of it.
                long stop = Now();
                if (grouped)
                {
                    // While the command is alive, or unreaped, its group id is reserved.
                    if (KillGroupOnStop)
                    {
                        KillGroup(pid, process);
                    }

                    KillQuietly(process, entireTree: false);
                    AwaitExit(process, After(stop, StopBudgetMs));
                    if (KillGroupOnStop)
                    {
                        // Members forked while the first kill ran.
                        KillGroup(pid, process);
                    }
                }
                else
                {
                    // Not .NET's tree kill, which reads every process on the
                    // machine: the command is frozen at once, so it starts
                    // nothing more and keeps its children; a last record
                    // within its budget; the command killed; then every
                    // recorded descendant within theirs.
                    if (!HasExited(process))
                    {
                        if (OperatingSystem.IsLinux())
                        {
                            Signal(pid, coreStart, Unix.Sigstop);
                            recorded!.Sample(pid, expires, cancellationToken, After(stop, StopScanMs));
                            SignalHook?.Invoke(pid, Unix.Sigkill);
                        }

                        KillQuietly(process, entireTree: !OperatingSystem.IsLinux());
                    }

                    recorded!.KillAlive(expires, cancellationToken, After(stop, StopScanMs + StopKillMs));
                    AwaitExit(process, After(stop, StopBudgetMs));
                }
            }

            // A descendant still writing has no reader now, so its next write fails.
            stdout?.Dispose();
            stderr?.Dispose();
            process.Dispose();
        }
    }

    private static int Descriptor(Stream stream) =>
        stream is PipeStream pipe
            ? (int)pipe.SafePipeHandle.DangerousGetHandle()
            : throw CarrierException.Loss("the command's output is " + stream.GetType().Name + ", not a pipe");

    private static Output Await(
        Process process,
        int pid,
        bool grouped,
        Recorded? recorded,
        Capture stdout,
        Capture stderr,
        long expires,
        CancellationToken cancellationToken,
        ref bool cleaned)
    {
        int idle = 1;
        Span<Posix.PollFd> fds = stackalloc Posix.PollFd[2];
        while (true)
        {
            // The exit is observed before the pipes are read, so everything
            // the command itself wrote is read below.
            bool exited = HasExited(process);
            if (!exited)
            {
                recorded?.Sample(pid, expires, cancellationToken);
            }

            int read = stdout.Step(Slice) + stderr.Step(Slice);
            if (exited)
            {
                // The cleanup always runs to the end, as Rust kills the group
                // when the command exits.
                if (grouped)
                {
                    if (KillGroupAtExit)
                    {
                        KillGroup(pid, process);
                    }
                }
                else
                {
                    // A stop lets these kills go on for a bounded time; it is reported below.
                    recorded!.KillAlive(expires, cancellationToken);
                }

                cleaned = true;
                stdout.Step(ExitDrain);
                stderr.Step(ExitDrain);
                ExitedHook?.Invoke();

                // The cleanup may have outlived the deadline or a stop.
                if (Stopped(expires, cancellationToken) is { } late)
                {
                    throw late;
                }

                return new Output(process.ExitCode, stdout.Bytes(), stdout.Overflow, stderr.Bytes());
            }

            if (Stopped(expires, cancellationToken) is { } stopped)
            {
                throw stopped;
            }

            if (read > 0)
            {
                idle = 1;
                continue;
            }

            int count = 0;
            foreach (Capture capture in new[] { stdout, stderr })
            {
                if (!capture.Ended)
                {
                    fds[count++] = new Posix.PollFd { Fd = capture.Fd, Events = Posix.PollIn };
                }
            }

            Posix.Poll(fds[..count], Math.Min(idle, Worker.RemainingMs(expires, Stopwatch.GetTimestamp())));
            idle = Math.Min(idle * 2, PollSliceMs);
        }
    }

    /// <summary>Why the run must stop now, or null.</summary>
    private static CarrierException? Stopped(long expires, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CarrierException.Aborted("command aborted");
        }

        return Stopwatch.GetTimestamp() >= expires ? CarrierException.Timeout("command did not exit in time") : null;
    }

    private static void KillQuietly(Process process, bool entireTree)
    {
        try
        {
            process.Kill(entireTree);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // Already gone, or a descendant could not be signalled.
        }
    }

    /// <summary>
    /// Waits until <paramref name="end"/> at most for a killed command to
    /// exit, in short <c>poll(2)</c> sleeps, so the wait neither blocks on a
    /// .NET wait handle nor consumes a <see cref="Thread.Interrupt"/>.
    /// </summary>
    private static void AwaitExit(Process process, long end)
    {
        ExitWaitHook?.Invoke(end);
        while (!HasExited(process) && Now() < end)
        {
            Posix.Poll(Span<Posix.PollFd>.Empty, 1);
        }
    }

    /// <summary>
    /// Sends <paramref name="signal"/> to the command <paramref name="pid"/>
    /// unless it is gone, a zombie, or no longer has the start time
    /// <paramref name="start"/> it had when it started (its pid was reused);
    /// nothing is sent when that start time could not be read.
    /// </summary>
    private static void Signal(int pid, string? start, int signal)
    {
        long bytes = 0;
        if (start is not null && ReadStat("/proc/" + pid.ToString(CultureInfo.InvariantCulture), ref bytes, report: false) is { Zombie: false } stat && stat.Start == start)
        {
            SignalHook?.Invoke(pid, signal);
            Unix.Signal(pid, signal);
        }
    }

    /// <summary>The time the budgets after a stop are measured with.</summary>
    private static long Now() => ClockHook?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>The time <paramref name="milliseconds"/> after <paramref name="start"/>.</summary>
    private static long After(long start, int milliseconds) => start + (Stopwatch.Frequency * milliseconds / 1000);
}
