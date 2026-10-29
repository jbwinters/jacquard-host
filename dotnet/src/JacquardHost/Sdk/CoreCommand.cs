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
/// when the command returns; on timeout or cancellation .NET's
/// <see cref="Process.Kill(bool)"/> kills the command's tree too. A
/// descendant started and reparented between two records keeps running. On
/// macOS, which has no <c>/proc</c>, nothing is recorded: only the tree kill
/// on a stop applies.
/// </para>
/// </remarks>
internal static class CoreCommand
{
    /// <summary>The most of each stream read after the command has exited.</summary>
    internal const int ExitDrain = 4 << 20;

    /// <summary>How long the host waits for a killed command to exit.</summary>
    private const int ExitGraceMs = 1000;

    /// <summary>The longest single wait between checks.</summary>
    private const int PollSliceMs = 5;

    /// <summary>The most of each stream one step reads, so a fast writer cannot hold the caller.</summary>
    private const int Slice = 1 << 20;

    /// <summary>How often descendants are recorded where there are no process groups.</summary>
    private const long SampleIntervalMs = 10;

    private const short Ready = Posix.PollIn | Posix.PollHup | Posix.PollErr | Posix.PollNval;

    /// <summary>Test seam: when set, commands run without a process group, as where <c>setsid</c> is absent.</summary>
    internal static volatile bool WithoutGroups;

    /// <summary>Test seam: whether descendants are recorded where there are no groups.</summary>
    internal static volatile bool RecordDescendants = true;

    /// <summary>Test seam: runs after each record with every pid recorded so far. Null outside tests.</summary>
    internal static volatile Action<IReadOnlyCollection<int>>? RecordedHook;

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

    private static bool IsExecutable(string path)
    {
        try
        {
            Unix.Status status = Unix.Stat(path, follow: true);
            return status.Kind == Unix.Kind.Regular && (status.Mode & 0x49) != 0;
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

        /// <summary>Records the command's current descendants, at most once per <see cref="SampleIntervalMs"/>.</summary>
        internal void Sample(int root)
        {
            long now = Stopwatch.GetTimestamp();
            if (!RecordDescendants || now < next || !OperatingSystem.IsLinux())
            {
                return;
            }

            next = now + (Stopwatch.Frequency * SampleIntervalMs / 1000);
            var children = new Dictionary<int, List<(int Pid, string Start)>>();
            foreach (string entry in ProcEntries())
            {
                if (ReadStat(entry) is { } stat)
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
                        started.TryAdd(pid, start);
                        queue.Enqueue(pid);
                    }
                }
            }

            RecordedHook?.Invoke(new List<int>(started.Keys));
        }

        /// <summary>
        /// Kills every recorded descendant still alive with the start time it
        /// was recorded with, so a reused pid is not killed (the start time is
        /// read just before the kill, as the JDK's process handles do).
        /// </summary>
        internal void KillAlive()
        {
            foreach ((int pid, string start) in started)
            {
                if (ReadStat("/proc/" + pid.ToString(CultureInfo.InvariantCulture)) is { } stat && stat.Start == start && !stat.Zombie)
                {
                    Unix.Kill(pid);
                }
            }
        }
    }

    private static IEnumerable<string> ProcEntries()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories("/proc");
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
    /// <c>/proc/&lt;pid&gt;/stat</c>, read after the last <c>)</c> (the
    /// command name may hold any byte); null when it cannot be read.
    /// </summary>
    private static (int Pid, int Parent, int Group, string Start, bool Zombie)? ReadStat(string directory)
    {
        try
        {
            string stat = File.ReadAllText(Path.Combine(directory, "stat"));
            int close = stat.LastIndexOf(')');
            if (close < 0 || !int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                return null;
            }

            // state ppid pgrp session tty_nr tpgid flags minflt cminflt majflt
            // cmajflt utime stime cutime cstime priority nice num_threads
            // itrealvalue starttime ...
            string[] fields = stat[(close + 1)..].Trim().Split(' ');
            if (fields.Length < 20
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
        if (HasExited(leader) && ReadStat("/proc/" + group.ToString(CultureInfo.InvariantCulture)) is { Zombie: false })
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
        CancellationToken cancellationToken)
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
        var info = new ProcessStartInfo(grouped ? Setsid! : command[0])
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
                if (grouped)
                {
                    // While the command is alive, or unreaped, its group id is reserved.
                    if (KillGroupOnStop)
                    {
                        KillGroup(pid, process);
                    }

                    KillQuietly(process, entireTree: false);
                    AwaitExit(process);
                    if (KillGroupOnStop)
                    {
                        // Members forked while the first kill ran.
                        KillGroup(pid, process);
                    }
                }
                else
                {
                    if (!HasExited(process))
                    {
                        KillQuietly(process, entireTree: true);
                        AwaitExit(process);
                    }

                    recorded!.KillAlive();
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
                recorded?.Sample(pid);
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
                    recorded!.KillAlive();
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
    /// Waits up to one second for a killed command to exit, in short
    /// <c>poll(2)</c> sleeps, so the wait neither blocks on a .NET wait handle
    /// nor consumes a <see cref="Thread.Interrupt"/>.
    /// </summary>
    private static void AwaitExit(Process process)
    {
        long end = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ExitGraceMs / 1000);
        while (!HasExited(process) && Stopwatch.GetTimestamp() < end)
        {
            Posix.Poll(Span<Posix.PollFd>.Empty, 1);
        }
    }
}
