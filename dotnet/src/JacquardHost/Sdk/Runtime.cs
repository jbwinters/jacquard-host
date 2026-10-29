using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// Prepares programs against one Core installation. A runtime is immutable
/// and thread-safe. The <c>--version</c> check and prelude discovery run once
/// per Core executable and prelude contents in this process, across runtimes
/// and threads.
/// </summary>
[SuppressMessage("Naming", "CA1724", Justification = "Runtime names the same concept in every SDK; it lives in JacquardLang.Host.Sdk.")]
public sealed class Runtime
{
    /// <summary>Prepared programs live here unless <see cref="PrepareOptions.CacheDir"/> is set.</summary>
    public const string DefaultCacheDir = ".scratch/jacquard-host-dotnet";

    /// <summary>The most source bytes one preparation reads, separators included.</summary>
    public const long SourceLimit = 16L << 20;

    /// <summary>The most source files one preparation takes; a longer list is refused from its count before any element is read.</summary>
    public const int SourceFiles = 1024;

    internal const string ProgramSchema = "jacquard-dotnet-program-v1";

    /// <summary>Core's raw outputs, kept as verified files of each cache entry, from which every binding is derived.</summary>
    internal const string CheckOutput = "core/check.out";

    internal const string HashOutput = "core/hash.out";

    /// <summary>The most bytes of <c>program.json</c> a load reads.</summary>
    private const int MetadataLimit = 32 << 20;

    private const int PollMinMs = 1;
    private const int PollMaxMs = 10;

    private static readonly string[] PreludeFiles = { "01-prim.jqd", "02-data.jqd", "19-audit.jqd" };

    /// <summary>Version checks and prelude discoveries, by key, shared by every runtime in this process.</summary>
    private static readonly ConcurrentDictionary<string, Flight> Flights = new(StringComparer.Ordinal);

    private static long staging;

    private int commands;

    /// <summary>
    /// Validates options; Core does not run yet.
    /// </summary>
    /// <exception cref="JacquardException">A binding failure for a missing prelude or invalid options.</exception>
    public Runtime(RuntimeOptions? options = null)
    {
        options ??= RuntimeOptions.Defaults;
        string? bin = options.Binary;
        if (string.IsNullOrEmpty(bin))
        {
            bin = Environment.GetEnvironmentVariable("JACQUARD_BIN");
        }

        string? prelude = options.Prelude;
        if (string.IsNullOrEmpty(prelude))
        {
            prelude = Environment.GetEnvironmentVariable("JACQUARD_PRELUDE");
        }

        if (string.IsNullOrEmpty(prelude))
        {
            throw JacquardException.Binding("set JACQUARD_PRELUDE or RuntimeOptions.Prelude; Core and prelude must match the compatibility pin");
        }

        if (prelude.Length > Core.PathLimit || prelude.Contains('\0', StringComparison.Ordinal) || Core.AbsoluteLength(prelude) > Core.PathLimit)
        {
            throw JacquardException.Binding("prelude " + Identities.Excerpt(prelude) + " is not a path or is longer than any path");
        }

        if (options.Deadline is { } deadline && deadline <= TimeSpan.Zero)
        {
            throw JacquardException.Binding("deadline must be positive");
        }

        Limits selected = options.Limits ?? Limits.Hard;
        try
        {
            selected.Validate();
        }
        catch (ProtocolException error)
        {
            throw JacquardException.Binding("invalid runtime limits: " + error.Message);
        }

        Binary = string.IsNullOrEmpty(bin) ? "jacquard" : bin;
        Prelude = Path.GetFullPath(prelude);
        Deadline = options.Deadline;
        Limits = selected;
    }

    /// <summary>The Core executable as configured, before it is resolved.</summary>
    public string Binary { get; }

    /// <summary>The absolute prelude directory.</summary>
    public string Prelude { get; }

    internal TimeSpan? Deadline { get; }

    internal Limits Limits { get; }

    /// <summary>Test seam: how long each Core command may run.</summary>
    internal TimeSpan CommandTimeout { get; set; } = Core.CommandTimeout;

    /// <summary>Test seam: how many Core commands this runtime has run.</summary>
    internal int CoreCommands => Volatile.Read(ref commands);

    /// <summary>Test seam: runs on a preparing thread when it starts to wait for another thread's version check or prelude discovery. Null outside tests.</summary>
    internal static volatile Action? FlightWaitHook;

    /// <summary>Test seam: runs after each source file is read, with its path. Null outside tests.</summary>
    internal static volatile Action<string>? SourceReadHook;

    /// <summary>Test seam: runs when a cache entry has been loaded, just before its program is returned. Null outside tests.</summary>
    internal static volatile Action? LoadedHook;

    /// <summary>Test seam: runs with the vacated staging path once an entry is published. Null outside tests.</summary>
    internal static volatile Action<string>? PublishedHook;

    /// <summary>Test seam: runs after the failure cleanup removes each entry, with its path. Null outside tests.</summary>
    internal static volatile Action<string>? CleanupHook;

    private string Command(string executable, CancellationToken cancellationToken, params string[] arguments)
    {
        Interlocked.Increment(ref commands);
        return Core.Run(executable, Prelude, CommandTimeout, cancellationToken, arguments);
    }

    /// <summary>One discovery: empty until a run succeeds; a failed run leaves it empty for the next caller.</summary>
    private sealed class Flight
    {
        internal IReadOnlyList<string>? Outputs { get; set; }

        internal bool Running { get; set; }
    }

    /// <summary>
    /// Runs <paramref name="work"/> once per key. Concurrent callers wait for
    /// one run, each observing its own token in sleeps of 1 to 10 ms; the run
    /// itself is bounded by the command timeout. A successful result stays
    /// cached, and a failed run leaves the next caller to try again.
    /// </summary>
    private static IReadOnlyList<string> Once(string key, Func<IReadOnlyList<string>> work, CancellationToken cancellationToken)
    {
        Flight flight = Flights.GetOrAdd(key, static _ => new Flight());
        int idle = PollMinMs;
        bool waiting = false;
        while (true)
        {
            lock (flight)
            {
                if (flight.Outputs is { } outputs)
                {
                    return outputs;
                }

                if (!flight.Running)
                {
                    flight.Running = true;
                    break;
                }
            }

            if (!waiting)
            {
                FlightWaitHook?.Invoke();
            }

            waiting = true;
            if (cancellationToken.IsCancellationRequested)
            {
                throw JacquardException.Preparation(Core.Aborted);
            }

            // poll(2) as a sleep: it neither waits on a .NET handle nor consumes a Thread.Interrupt.
            Posix.Poll(Span<Posix.PollFd>.Empty, idle);
            idle = Math.Min(idle * 2, PollMaxMs);
        }

        try
        {
            IReadOnlyList<string> outputs = work();
            lock (flight)
            {
                flight.Outputs = outputs;
            }

            return outputs;
        }
        finally
        {
            lock (flight)
            {
                flight.Running = false;
            }
        }
    }

    /// <summary>Runs <c>--version</c> once per executable digest; accepts output whose last field is the pinned version.</summary>
    private void CheckVersion(string executable, string binaryHash, CancellationToken cancellationToken) =>
        Once("version:" + binaryHash, () =>
        {
            string version = Command(executable, cancellationToken, "--version").Trim();
            string[] fields = version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !string.Equals(fields[^1], Compat.CoreVersion, StringComparison.Ordinal))
            {
                throw JacquardException.Preparation("expected pinned Core version " + Compat.CoreVersion + ", found \""
                    + Identities.Excerpt(version) + "\"; the exact release is also required");
            }

            return Array.Empty<string>();
        }, cancellationToken);

    /// <summary>Core's <c>hash</c> output for each prelude file, which must parse.</summary>
    private IReadOnlyList<string> PreludeOutputs(string executable, string key, CancellationToken cancellationToken) =>
        Once("prelude:" + key, () =>
        {
            var outputs = new List<string>();
            foreach (string name in PreludeFiles)
            {
                string output = Command(executable, cancellationToken, "hash", Path.Combine(Prelude, name));
                Identities.HashGroups(output);
                outputs.Add(output);
            }

            return outputs.AsReadOnly();
        }, cancellationToken);

    /// <summary>The bindings of a program: what <c>program.json</c> records beside its identity and files.</summary>
    internal sealed record Bindings(
        IReadOnlyList<Identities.Entry> Names,
        IReadOnlyList<Identities.Entry> Constructors,
        SortedDictionary<string, string> Signatures,
        IReadOnlyList<IReadOnlyList<Identities.Entry>> Groups)
    {
        /// <summary>Whether two bindings record the same names, constructors, signatures, and groups in the same order.</summary>
        internal bool Same(Bindings other) =>
            Names.SequenceEqual(other.Names)
            && Constructors.SequenceEqual(other.Constructors)
            && Signatures.Count == other.Signatures.Count
            && Signatures.SequenceEqual(other.Signatures)
            && Groups.Count == other.Groups.Count
            && Groups.Zip(other.Groups).All(pair => pair.First.SequenceEqual(pair.Second));
    }

    /// <summary>
    /// Derives every binding from Core's raw outputs: <c>check --print-sigs</c>
    /// for the signatures, and <c>hash</c> of each prelude file and of the
    /// source for the names, constructors, and declaration groups.
    /// </summary>
    internal static Bindings Derive(string check, IReadOnlyList<string> preludeHashes, string hash)
    {
        var signatures = new SortedDictionary<string, string>(Json.CodePointOrder);
        foreach (string line in check.Split('\n'))
        {
            string trimmed = line.Trim();
            int split = trimmed.IndexOf(" : ", StringComparison.Ordinal);
            if (split >= 0)
            {
                string name = trimmed[..split];
                if (!name.Contains(' ', StringComparison.Ordinal) && !name.Contains('\t', StringComparison.Ordinal))
                {
                    signatures[name] = trimmed[(split + 3)..];
                }
            }
        }

        var groups = new List<List<Identities.Entry>>();
        foreach (string output in preludeHashes)
        {
            groups.AddRange(Identities.HashGroups(output));
        }

        groups.AddRange(Identities.HashGroups(hash));
        Identities.CheckGroups(groups);
        var names = new List<Identities.Entry>();
        var constructorOrder = new List<string>();
        var constructors = new Dictionary<string, Hash>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<Identities.Entry> group in groups)
        {
            foreach (Identities.Entry entry in group)
            {
                if (seen.Add(entry.Name))
                {
                    names.Add(entry);
                }

                if (!constructors.ContainsKey(entry.Name))
                {
                    constructorOrder.Add(entry.Name);
                }

                constructors[entry.Name] = entry.Hash;
            }
        }

        var constructorList = constructorOrder.Select(name => new Identities.Entry(name, constructors[name])).ToList();
        return new Bindings(names, constructorList, signatures, groups.Select(group => (IReadOnlyList<Identities.Entry>)group).ToList());
    }

    /// <summary>
    /// Checks, hashes, and installs first-party declaration sources
    /// (<c>.jac</c> files, or <c>.jqd</c> files in dependency order) into a
    /// verified cache keyed by the source bytes, the Core executable's bytes,
    /// every prelude file, and the protocol. Each Core command is bounded by
    /// a 30-second timeout and bounded output. Cancelling
    /// <paramref name="cancellationToken"/> stops the running command and
    /// fails the preparation.
    /// </summary>
    /// <param name="sources">The source files; more than <see cref="SourceFiles"/> is refused from the count alone.</param>
    /// <param name="options">The cache directory; null selects the defaults.</param>
    /// <param name="cancellationToken">Stops the preparation.</param>
    /// <returns>The prepared, verified program.</returns>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> or one of its elements is null.</exception>
    public PreparedProgram Prepare(IReadOnlyList<string> sources, PrepareOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (cancellationToken.IsCancellationRequested)
        {
            throw JacquardException.Preparation(Core.Aborted);
        }

        if (!Unix.Supported)
        {
            throw JacquardException.Preparation("the .NET SDK prepares programs on Linux and macOS only");
        }

        PreparedProgram program;
        try
        {
            program = PrepareChecked(sources, options ?? PrepareOptions.Defaults, cancellationToken);
        }
        catch (JacquardException error)
        {
            // Checked here, not in a filter: a filter runs before the
            // preparation's cleanup, which may observe the stop. Report the
            // stop, not a misleading read or cache failure it caused.
            if (cancellationToken.IsCancellationRequested && !error.Message.EndsWith(Core.Aborted, StringComparison.Ordinal))
            {
                throw JacquardException.Preparation(Core.Aborted);
            }

            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw JacquardException.Preparation("cannot prepare: " + error.Message);
        }

        // A stop during the last of the work is reported, not a program.
        if (cancellationToken.IsCancellationRequested)
        {
            throw JacquardException.Preparation(Core.Aborted);
        }

        return program;
    }

    private PreparedProgram PrepareChecked(IReadOnlyList<string> sources, PrepareOptions options, CancellationToken cancellationToken)
    {
        JacquardException Usage() => JacquardException.Preparation("pass .jac files or .jqd files in dependency order");

        // The count comes from Count alone, before any element is read.
        int count = sources.Count;
        if (count == 0)
        {
            throw Usage();
        }

        if (count > SourceFiles)
        {
            throw JacquardException.Preparation("more than " + SourceFiles.ToString(CultureInfo.InvariantCulture) + " sources");
        }

        string? syntax = null;

        // Each element is read once, so the files read below are the ones checked here.
        var checkedSources = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw JacquardException.Preparation(Core.Aborted);
            }

            string source = Core.CheckedPath(sources[i], "source");
            checkedSources.Add(source);
            string kind = Extension(source);
            syntax ??= kind;
            if (!(kind is ".jac" or ".jqd") || !string.Equals(syntax, kind, StringComparison.Ordinal))
            {
                throw Usage();
            }
        }

        string configured = Path.GetFullPath(Core.CheckedPath(options.CacheDir ?? DefaultCacheDir, "cache directory"));
        string executable = Core.Executable(Binary);
        string binaryHash = Core.FileDigest(executable, cancellationToken);
        CheckVersion(executable, binaryHash, cancellationToken);
        string preludeHash;
        try
        {
            preludeHash = Core.MapDigest(Core.Tree(Prelude, Core.Access.Any, cancellationToken));
        }
        catch (JacquardException error)
        {
            throw JacquardException.Preparation("cannot read prelude " + Prelude + ": " + error.Message);
        }

        byte[] bytes = ReadSources(checkedSources, cancellationToken);
        var identity = new SortedDictionary<string, string>(Json.CodePointOrder)
        {
            ["binary"] = binaryHash,
            ["prelude"] = preludeHash,
            ["protocol"] = Compat.Protocol,
            ["schema"] = ProgramSchema,
            ["source"] = Core.Digest(bytes),
            ["syntax"] = syntax!,
        };
        string cache;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(configured);
            }
            else
            {
                Directory.CreateDirectory(configured, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // A symbolic link at the cache root is followed once, here; the
            // directory it names is checked, and nothing inside is followed.
            cache = new DirectoryInfo(configured).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? configured;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw JacquardException.Preparation("cannot create " + configured + ": " + error.Message);
        }

        string directory = Path.Combine(cache, Core.MapDigest(identity));

        // Only the cache's owner may write it; nothing inside it can resist
        // someone else who may.
        Core.Check(cache, Core.Access.OwnerOnly);
        if (Unix.Stat(directory, follow: false).Kind != Unix.Kind.Missing)
        {
            return Load(directory, identity, executable, cancellationToken);
        }

        string stage = StagingDirectory(cache);
        bool published = false;
        try
        {
            string snapshot = Path.Combine(stage, "source" + syntax);
            Write(snapshot, bytes, "cannot write the source snapshot");
            string check = Command(executable, cancellationToken, "check", snapshot, "--print-sigs");
            IReadOnlyList<string> preludeHashes = PreludeOutputs(executable, binaryHash + preludeHash, cancellationToken);
            string hash = Command(executable, cancellationToken, "hash", snapshot);
            Bindings bindings = Derive(check, preludeHashes, hash);
            string store = Path.Combine(stage, "store");
            Command(executable, cancellationToken, "run", snapshot, "--store", store);
            CreateDirectory(Path.Combine(stage, "core"));
            Write(Path.Combine(stage, CheckOutput), Encoding.UTF8.GetBytes(check), "cannot write Core's output");
            Write(Path.Combine(stage, HashOutput), Encoding.UTF8.GetBytes(hash), "cannot write Core's output");
            for (int i = 0; i < PreludeFiles.Length; i++)
            {
                Write(Path.Combine(stage, PreludeOutput(PreludeFiles[i])), Encoding.UTF8.GetBytes(preludeHashes[i]), "cannot write Core's output");
            }

            // Core made the store with its own permissions; the entry is owner-only.
            var metadata = new Metadata(identity, bindings, Core.Tree(stage, Core.Access.Restrict, cancellationToken));
            Write(Path.Combine(stage, "program.json"), Encoding.UTF8.GetBytes(metadata.ToJson()), "cannot write program.json");
            try
            {
                Directory.Move(stage, directory);

                // The staging path is vacated; whatever appears there later is not this call's.
                published = true;
                PublishedHook?.Invoke(stage);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A concurrent preparation published the same artifact first.
                if (Unix.Stat(directory, follow: false).Kind == Unix.Kind.Missing)
                {
                    throw JacquardException.Preparation("cannot publish " + directory + ": " + error.Message);
                }
            }
        }
        finally
        {
            if (!published)
            {
                RemoveTree(stage, cancellationToken);
            }
        }

        return Load(directory, identity, executable, cancellationToken);
    }

    private static string PreludeOutput(string file) => "core/prelude-" + file + ".out";

    private static string Extension(string source)
    {
        string name = Path.GetFileName(source);
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }

    /// <summary>
    /// The sources joined by newlines. Each must be a regular file (a
    /// symbolic link to one is followed); the total is checked against
    /// <see cref="SourceLimit"/> from the files' sizes before each is read,
    /// and no read goes past it. Before each file the token is observed.
    /// </summary>
    private static byte[] ReadSources(List<string> sources, CancellationToken cancellationToken)
    {
        JacquardException Over() => JacquardException.Preparation("sources exceed " + (SourceLimit >> 20).ToString(CultureInfo.InvariantCulture) + " MiB in total");
        using var content = new MemoryStream();
        long total = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw JacquardException.Preparation(Core.Aborted);
            }

            string source = sources[i];
            Unix.Status status = Core.StatOrFail(source, follow: true, "cannot read ");
            if (status.Kind != Unix.Kind.Regular)
            {
                throw JacquardException.Preparation("cannot read " + source + ": not a regular file");
            }

            total += (i > 0 ? 1 : 0) + status.Size;
            if (total > SourceLimit)
            {
                throw Over();
            }

            if (i > 0)
            {
                content.WriteByte((byte)'\n');
            }

            int room = (int)(SourceLimit - content.Length);
            byte[] buffer = new byte[Math.Min(room + 1L, status.Size + 1L)];
            int read = 0;
            try
            {
                using var stream = new FileStream(source, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, BufferSize = 0 });
                while (true)
                {
                    if (read == buffer.Length)
                    {
                        // The file grew past its size: read one more byte to see whether it is over.
                        if (buffer.Length > room)
                        {
                            break;
                        }

                        Array.Resize(ref buffer, (int)Math.Min(room + 1L, buffer.Length * 2L));
                    }

                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw JacquardException.Preparation("cannot read " + source + ": " + error.Message);
            }

            if (read > room)
            {
                throw Over();
            }

            content.Write(buffer, 0, read);
            SourceReadHook?.Invoke(source);
        }

        return content.ToArray();
    }

    private static void CreateDirectory(string path)
    {
        try
        {
            if (!Unix.MakeDirectory(path, 0x1C0))
            {
                throw JacquardException.Preparation("cannot create " + path + ": it already exists");
            }
        }
        catch (IOException error)
        {
            throw JacquardException.Preparation("cannot create " + path + ": " + error.Message);
        }
    }

    /// <summary>Writes a new owner-only file.</summary>
    private static void Write(string path, byte[] bytes, string context)
    {
        try
        {
            using var stream = new FileStream(path, Core.OwnerOnly(FileMode.CreateNew));
            stream.Write(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw JacquardException.Preparation(context + ": " + error.Message);
        }
    }

    /// <summary>A new owner-only staging directory, created exclusively, so a directory the call did not make is never used or removed.</summary>
    private static string StagingDirectory(string cache)
    {
        long stamp = Stopwatch.GetTimestamp() & 0xffffffffL;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string path = Path.Combine(cache, string.Format(
                CultureInfo.InvariantCulture,
                "prepare-{0}-{1:x8}-{2}",
                Environment.ProcessId,
                stamp,
                Interlocked.Increment(ref staging)));
            try
            {
                if (Unix.MakeDirectory(path, 0x1C0))
                {
                    return path;
                }
            }
            catch (IOException error)
            {
                throw JacquardException.Preparation("cannot create " + path + ": " + error.Message);
            }
        }

        throw JacquardException.Preparation("cannot create a staging directory");
    }

    /// <summary>
    /// Removes a staging directory unless it was published (and so no longer
    /// exists), in a streamed post-order walk that holds one directory
    /// enumerator per level on an explicit stack. It removes at most the
    /// tree's entry cap, and ends at the first cancellation, checked before
    /// each entry. What it leaves is an unpublished <c>prepare-*</c>
    /// directory, which is never loaded as a cache entry: entries are named by
    /// their identity digest.
    /// </summary>
    [SuppressMessage("Design", "CA1031", Justification = "Cleanup is best effort; what it cannot remove is left for the user and never becomes a cache entry.")]
    internal static void RemoveTree(string root, CancellationToken cancellationToken)
    {
        int cap = Core.TreeEntryCap;
        int removed = 0;
        var stack = new Stack<(string Directory, IEnumerator<string> Entries)>();
        try
        {
            if (Unix.Stat(root, follow: false).Kind != Unix.Kind.Directory)
            {
                return;
            }

            stack.Push((root, Directory.EnumerateFileSystemEntries(root).GetEnumerator()));
            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested || removed >= cap)
                {
                    return;
                }

                (string directory, IEnumerator<string> entries) = stack.Peek();
                if (!entries.MoveNext())
                {
                    stack.Pop().Entries.Dispose();
                    Directory.Delete(directory, recursive: false);
                    removed++;
                    CleanupHook?.Invoke(directory);
                    continue;
                }

                string path = entries.Current;
                if (Unix.Stat(path, follow: false).Kind == Unix.Kind.Directory)
                {
                    stack.Push((path, Directory.EnumerateFileSystemEntries(path).GetEnumerator()));
                    continue;
                }

                File.Delete(path);
                removed++;
                CleanupHook?.Invoke(path);
            }
        }
        catch (Exception)
        {
            // Left for the user to remove; it never becomes a cache entry.
        }
        finally
        {
            while (stack.Count > 0)
            {
                stack.Pop().Entries.Dispose();
            }
        }
    }

    /// <summary>
    /// Loads a cache entry, refusing an accidental or partial change: the
    /// entry and everything in it must be owned by this user and writable by
    /// no one else, every file but <c>program.json</c> must match the digests
    /// it records, and every binding it records must equal what is derived
    /// again from Core's raw outputs, which are among those verified files. A
    /// coordinated rewrite of several files is accepted: only the cache's
    /// owner can make one, and the owner is trusted.
    /// </summary>
    private PreparedProgram Load(string directory, SortedDictionary<string, string> identity, string executable, CancellationToken cancellationToken)
    {
        JacquardException Fail(string detail) => JacquardException.Preparation("prepared cache " + directory
            + " is incomplete or changed (" + detail + "); remove that cache entry and prepare again");

        SortedDictionary<string, string> observed;
        try
        {
            // Regular files only, bounded, so no read below can block.
            observed = Core.Tree(directory, Core.Access.OwnerOnly, cancellationToken);
        }
        catch (JacquardException error)
        {
            if (error.Message == Core.Aborted)
            {
                throw;
            }

            throw Fail(error.Message);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw JacquardException.Preparation(Core.Aborted);
        }

        if (!observed.ContainsKey("program.json"))
        {
            throw Fail("no program.json");
        }

        Metadata metadata = Metadata.FromJson(ReadVerified(directory, "program.json", null, MetadataLimit, Fail)) ?? throw Fail("unreadable program.json");
        observed.Remove("program.json");
        if (!metadata.Identity.SequenceEqual(identity) || !metadata.Files.SequenceEqual(observed))
        {
            throw Fail("cached artifact digests do not match");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw JacquardException.Preparation(Core.Aborted);
        }

        Bindings derived;
        try
        {
            var preludeHashes = new List<string>();
            foreach (string file in PreludeFiles)
            {
                preludeHashes.Add(Text(ReadVerified(directory, PreludeOutput(file), observed, Core.StdoutLimit, Fail), Fail));
            }

            derived = Derive(
                Text(ReadVerified(directory, CheckOutput, observed, Core.StdoutLimit, Fail), Fail),
                preludeHashes,
                Text(ReadVerified(directory, HashOutput, observed, Core.StdoutLimit, Fail), Fail));
        }
        catch (JacquardException error) when (!error.Message.StartsWith("prepared cache ", StringComparison.Ordinal))
        {
            throw Fail("Core's cached output does not parse: " + error.Message);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw JacquardException.Preparation(Core.Aborted);
        }

        if (!derived.Same(metadata.Bindings))
        {
            throw Fail("program.json bindings differ from Core's cached output");
        }

        var program = new PreparedProgram(this, directory, metadata.Bindings, executable);
        LoadedHook?.Invoke();
        return program;
    }

    /// <summary>
    /// The bytes of a regular file in a cache entry, not followed if it is a
    /// link, at most <paramref name="limit"/>; when <paramref name="digests"/>
    /// is given, they must have the digest it records.
    /// </summary>
    private static byte[] ReadVerified(string directory, string name, SortedDictionary<string, string>? digests, int limit, Func<string, JacquardException> fail)
    {
        if (digests is not null && !digests.ContainsKey(name))
        {
            throw fail("no " + name);
        }

        string path = Path.Combine(directory, name);
        byte[] bytes;
        try
        {
            Unix.Status status = Unix.Stat(path, follow: false);
            if (status.Kind != Unix.Kind.Regular)
            {
                throw fail(name + " is not a regular file");
            }

            if (status.Size > limit)
            {
                throw fail(name + " is too long");
            }

            using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, BufferSize = 0 });
            byte[] buffer = new byte[status.Size + 1];
            int read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += n;
            }

            if (read > status.Size)
            {
                throw fail(name + " changed while it was read");
            }

            bytes = buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw fail(name + ": " + error.Message);
        }

        if (digests is not null && !string.Equals(Core.Digest(bytes), digests[name], StringComparison.Ordinal))
        {
            throw fail("cached artifact digests do not match");
        }

        return bytes;
    }

    private static string Text(byte[] bytes, Func<string, JacquardException> fail) => Core.Utf8(bytes) ?? throw fail("Core's cached output is not UTF-8");

    /// <summary>The prepared metadata in <c>program.json</c>.</summary>
    internal sealed record Metadata(SortedDictionary<string, string> Identity, Bindings Bindings, SortedDictionary<string, string> Files)
    {
        private static JsonObject Strings(IEnumerable<KeyValuePair<string, string>> map) =>
            Json.Object(map.Select(pair => new KeyValuePair<string, Json>(pair.Key, Json.Of(pair.Value))));

        private static JsonArray Pairs(IEnumerable<Identities.Entry> entries) =>
            Json.Of(entries.Select(entry => (Json)Json.Of(new Json[] { Json.Of(entry.Name), Json.Of(entry.Hash.Hex) })));

        internal string ToJson()
        {
            JsonArray groups = Json.Of(Bindings.Groups.Select(group => (Json)Json.Of(group.Select(entry =>
                (Json)Json.Object(("hash", Json.Of(entry.Hash.Hex)), ("name", Json.Of(entry.Name)))))));
            JsonObject json = Json.Object(
                ("constructors", Pairs(Bindings.Constructors)),
                ("files", Strings(Files)),
                ("groups", groups),
                ("identity", Strings(Identity)),
                ("names", Pairs(Bindings.Names)),
                ("signatures", Strings(Bindings.Signatures)));
            return JsonCodec.Stringify(json, 8);
        }

        private static SortedDictionary<string, string>? Map(JsonObject json, string key)
        {
            if (json.Get(key) is not JsonObject fields)
            {
                return null;
            }

            var output = new SortedDictionary<string, string>(Json.CodePointOrder);
            foreach (KeyValuePair<string, Json> field in fields)
            {
                if (field.Value.AsString() is not { } text)
                {
                    return null;
                }

                output[field.Key] = text;
            }

            return output;
        }

        private static Identities.Entry? Entry(string? name, Json? hash)
        {
            string? hex = hash?.AsString();
            return name is null || hex is null || !Protocol.Hash.LowerHex(hex, 64) ? null : new Identities.Entry(name, new Hash(hex));
        }

        private static List<Identities.Entry>? Pairs(JsonObject json, string key)
        {
            if (json.Get(key)?.AsArray() is not { } items)
            {
                return null;
            }

            var output = new List<Identities.Entry>(items.Count);
            foreach (Json item in items)
            {
                if (item.AsArray() is not { Count: 2 } pair || Entry(pair[0].AsString(), pair[1]) is not { } entry)
                {
                    return null;
                }

                output.Add(entry);
            }

            return output;
        }

        /// <summary>The metadata, or null when it is not what <see cref="ToJson"/> writes.</summary>
        internal static Metadata? FromJson(byte[] text)
        {
            JsonObject json;
            try
            {
                json = JsonCodec.DecodeObject(text, 8);
            }
            catch (ProtocolException)
            {
                return null;
            }

            if (json.Get("groups")?.AsArray() is not { } groupItems)
            {
                return null;
            }

            var groups = new List<IReadOnlyList<Identities.Entry>>(groupItems.Count);
            foreach (Json group in groupItems)
            {
                if (group.AsArray() is not { } entries)
                {
                    return null;
                }

                var parsed = new List<Identities.Entry>(entries.Count);
                foreach (Json entry in entries)
                {
                    if (entry is not JsonObject fields || Entry(fields.Get("name")?.AsString(), fields.Get("hash")) is not { } value)
                    {
                        return null;
                    }

                    parsed.Add(value);
                }

                groups.Add(parsed);
            }

            SortedDictionary<string, string>? identity = Map(json, "identity");
            List<Identities.Entry>? names = Pairs(json, "names");
            List<Identities.Entry>? constructors = Pairs(json, "constructors");
            SortedDictionary<string, string>? signatures = Map(json, "signatures");
            SortedDictionary<string, string>? files = Map(json, "files");
            if (identity is null || names is null || constructors is null || signatures is null || files is null)
            {
                return null;
            }

            return new Metadata(identity, new Bindings(names, constructors, signatures, groups), files);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => "Runtime[binary=" + Identities.Excerpt(Binary) + ", prelude=" + Prelude + "]";
}
