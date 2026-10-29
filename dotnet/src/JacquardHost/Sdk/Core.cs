using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>Bounded runs of Core's public commands, digests of prepared files, and the cache's trust boundary.</summary>
internal static class Core
{
    /// <summary>Each Core command must finish within this time.</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    internal const int StdoutLimit = 8 << 20;
    internal const int StderrLimit = 4096;

    /// <summary>Why a preparation stopped early.</summary>
    internal const string Aborted = "preparation was cancelled";

    /// <summary>The most bytes of the Core executable that are fingerprinted.</summary>
    internal const long ExecutableLimit = 512L << 20;

    /// <summary>The most bytes <see cref="Tree"/> reads under one root.</summary>
    internal const long TreeLimit = 256L << 20;

    /// <summary>The most entries (files and directories, empty ones included) <see cref="Tree"/> visits under one root.</summary>
    internal const int TreeEntries = 100_000;

    /// <summary>The longest path, in chars, the SDK passes to the file system (Linux's PATH_MAX).</summary>
    internal const int PathLimit = 4096;

    private const int Chunk = 1 << 16;

    /// <summary>Test seam: the entry cap <see cref="Tree"/> and the failure cleanup apply.</summary>
    internal static volatile int TreeEntryCap = TreeEntries;

    /// <summary>Test seam: runs after each chunk <see cref="FileDigest"/> reads. Null outside tests.</summary>
    internal static volatile Action? DigestChunkHook;

    /// <summary>Test seam: runs after <see cref="Tree"/> digests each file, with its path. Null outside tests.</summary>
    internal static volatile Action<string>? TreeFileHook;

    /// <summary>What <see cref="Tree"/> does about who may write what it walks.</summary>
    internal enum Access
    {
        /// <summary>Nothing: a Core installation's prelude.</summary>
        Any,

        /// <summary>Removes every group and other permission, as each new cache entry's files are made owner-only.</summary>
        Restrict,

        /// <summary>Refuses the root or any entry this user does not own or that its group or others may write.</summary>
        OwnerOnly,
    }

    /// <summary>
    /// Runs one Core subcommand with the prelude set, returning its stdout.
    /// The command is bounded by <paramref name="timeout"/>, its output by the
    /// limits above, and it stops at once when the token is cancelled.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static string Run(string binary, string prelude, TimeSpan timeout, CancellationToken cancellationToken, params string[] arguments)
    {
        string name = arguments.Length == 0 ? string.Empty : arguments[0];
        var command = new List<string> { binary };
        command.AddRange(arguments);
        CoreCommand.Output output;
        try
        {
            output = CoreCommand.Run(
                command,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["JACQUARD_PRELUDE"] = prelude },
                timeout,
                StdoutLimit,
                StderrLimit,
                cancellationToken);
        }
        catch (CarrierException error)
        {
            string detail = error.IsTimeout
                ? "no exit within " + (timeout.TotalMilliseconds % 1000 == 0
                    ? ((long)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds"
                    : ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms")
                : error.IsAborted ? Aborted : error.Message;
            throw JacquardException.Preparation("Core " + name + " failed: " + detail);
        }

        if (output.ExitStatus != 0)
        {
            string detail = Encoding.UTF8.GetString(output.Stderr).Trim();
            throw JacquardException.Preparation("Core " + name + " failed: "
                + (detail.Length == 0 ? "exit status " + output.ExitStatus.ToString(CultureInfo.InvariantCulture) : detail));
        }

        if (output.StdoutOverflow)
        {
            throw JacquardException.Preparation("Core " + name + " output exceeds 8 MiB");
        }

        return Utf8(output.Stdout) ?? throw JacquardException.Preparation("Core " + name + " output is not UTF-8");
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The bytes as strict UTF-8 text, or null when they are not.</summary>
    internal static string? Utf8(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 digest of bytes, in lowercase hexadecimal.</summary>
    internal static string Digest(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// The SHA-256 digest of the Core executable's bytes, read in pieces. A
    /// file larger than <see cref="ExecutableLimit"/> is refused from its size
    /// before it is read, and the limit is enforced again while it is read.
    /// Before each piece it observes the token. <paramref name="identity"/>
    /// is the file's status before the read, which must be unchanged after
    /// it, so the digest describes the file with that identity.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static string FileDigest(string path, CancellationToken cancellationToken, out Unix.Status identity)
    {
        JacquardException over = JacquardException.Preparation("Core executable " + path + " exceeds "
            + (ExecutableLimit >> 20).ToString(CultureInfo.InvariantCulture) + " MiB");
        Unix.Status status = StatOrFail(path, follow: true, "cannot read ");
        if (status.Kind != Unix.Kind.Regular)
        {
            throw JacquardException.Preparation("cannot read " + path + ": not a regular file");
        }

        if (status.Size > ExecutableLimit)
        {
            throw over;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Chunk];
        long read = 0;
        try
        {
            using FileStream stream = OpenRead(path);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw JacquardException.Preparation(Aborted);
                }

                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0)
                {
                    break;
                }

                read += n;
                if (read > ExecutableLimit)
                {
                    throw over;
                }

                hash.AppendData(buffer, 0, n);
                DigestChunkHook?.Invoke();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw JacquardException.Preparation("cannot read " + path + ": " + error.Message);
        }

        if (!StatOrFail(path, follow: true, "cannot read ").SameFile(status))
        {
            throw Changed("Core executable", path);
        }

        identity = status;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>One file or directory whose identity a preparation keeps until Core's commands have finished.</summary>
    internal readonly record struct Pin(string What, string Path, bool Follow, Unix.Status Status);

    /// <summary>The identity of <paramref name="status"/> as text, for a key: device, inode, size, and both times.</summary>
    internal static string IdentityKey(Unix.Status status) => string.Join(
        ":",
        status.Device.ToString(CultureInfo.InvariantCulture),
        status.Inode.ToString(CultureInfo.InvariantCulture),
        status.Size.ToString(CultureInfo.InvariantCulture),
        status.Modified.ToString(CultureInfo.InvariantCulture),
        status.Changed.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Refuses the preparation unless every pinned file and directory still
    /// has the identity it had when it was fingerprinted (see
    /// <see cref="Unix.Status.SameFile"/>). It reads one status per pin, at
    /// most <see cref="TreeEntries"/> plus one, and observes the token before
    /// each.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static void Unchanged(IReadOnlyList<Pin> pins, CancellationToken cancellationToken)
    {
        foreach (Pin pin in pins)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw JacquardException.Preparation(Aborted);
            }

            Unix.Status now;
            try
            {
                now = Unix.Stat(pin.Path, pin.Follow);
            }
            catch (IOException)
            {
                throw Changed(pin.What, pin.Path);
            }

            if (!now.SameFile(pin.Status))
            {
                throw Changed(pin.What, pin.Path);
            }
        }
    }

    private static JacquardException Changed(string what, string path) =>
        JacquardException.Preparation(what + " " + path + " changed during preparation; prepare again once it no longer changes");

    /// <summary>Options that write a file, created owner-only (read and write for its owner) where the file system has POSIX permissions.</summary>
    internal static FileStreamOptions OwnerOnly(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite, BufferSize = 0 });

    /// <summary>The status of <paramref name="path"/>, or a preparation failure starting with <paramref name="context"/>.</summary>
    internal static Unix.Status StatOrFail(string path, bool follow, string context)
    {
        Unix.Status status;
        try
        {
            status = Unix.Stat(path, follow);
        }
        catch (IOException error)
        {
            throw JacquardException.Preparation(context + path + ": " + error.Message);
        }

        if (status.Kind == Unix.Kind.Missing)
        {
            throw JacquardException.Preparation(context + path + ": no such file or directory");
        }

        return status;
    }

    /// <summary>
    /// Digests every regular file under <paramref name="root"/>, keyed by
    /// <c>/</c>-separated relative path, refusing other entry kinds (symbolic
    /// links, FIFOs, and devices included, so no read can block on one), more
    /// than <see cref="TreeEntries"/> entries, and more than
    /// <see cref="TreeLimit"/> bytes in all, which is checked against each
    /// file's size before it is read and again while it is read. The walk
    /// streams each directory and keeps an explicit stack, so nothing beyond
    /// the entry cap is held and no directory depth can exhaust the call
    /// stack; before each entry and each piece of a file it observes the
    /// token. <paramref name="access"/> says whether entries are also checked
    /// for owner-only access, or made owner-only. When
    /// <paramref name="pins"/> is given, the root's status and each entry's
    /// status, read before the entry is digested, are added to it.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static SortedDictionary<string, string> Tree(string root, Access access, CancellationToken cancellationToken, List<Pin>? pins = null)
    {
        var files = new SortedDictionary<string, string>(Json.CodePointOrder);
        Check(root, access);
        pins?.Add(new Pin("prelude", root, Follow: true, StatOrFail(root, follow: true, "cannot read ")));
        long total = 0;
        int entries = 0;
        int cap = TreeEntryCap;
        var stack = new Stack<IEnumerator<string>>();
        try
        {
            stack.Push(Directory.EnumerateFileSystemEntries(root).GetEnumerator());
            while (stack.Count > 0)
            {
                IEnumerator<string> top = stack.Peek();
                if (cancellationToken.IsCancellationRequested)
                {
                    throw JacquardException.Preparation(Aborted);
                }

                if (!top.MoveNext())
                {
                    stack.Pop().Dispose();
                    continue;
                }

                string path = top.Current;
                if (++entries > cap)
                {
                    throw JacquardException.Preparation("more than " + cap.ToString(CultureInfo.InvariantCulture) + " entries under " + root);
                }

                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                Unix.Status status = StatOrFail(path, follow: false, "cannot read ");
                pins?.Add(new Pin("prelude entry", path, Follow: false, status));
                if (status.Kind == Unix.Kind.Directory)
                {
                    Check(path, access);
                    stack.Push(Directory.EnumerateFileSystemEntries(path).GetEnumerator());
                }
                else if (status.Kind == Unix.Kind.Regular)
                {
                    Check(path, access);
                    JacquardException over = JacquardException.Preparation("files under " + root + " exceed "
                        + (TreeLimit >> 20).ToString(CultureInfo.InvariantCulture) + " MiB");
                    if (status.Size > TreeLimit - total)
                    {
                        throw over;
                    }

                    files[relative] = BoundedDigest(path, TreeLimit - total, ref total, over, cancellationToken);
                    TreeFileHook?.Invoke(path);
                }
                else
                {
                    throw JacquardException.Preparation("unsupported filesystem entry in prepared artifacts: " + relative);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw JacquardException.Preparation("cannot read " + root + ": " + error.Message);
        }
        finally
        {
            while (stack.Count > 0)
            {
                stack.Pop().Dispose();
            }
        }

        return files;
    }

    /// <summary>The digest of at most <paramref name="room"/> bytes of a file, adding what it read to <paramref name="total"/>.</summary>
    private static string BoundedDigest(string path, long room, ref long total, JacquardException over, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[Chunk];
        long read = 0;
        using (FileStream stream = OpenRead(path))
        {
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw JacquardException.Preparation(Aborted);
                }

                read += n;
                if (read > room)
                {
                    throw over;
                }

                hash.AppendData(buffer, 0, n);
            }
        }

        total += read;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Canonical JSON for a string map: keys in code-point order.</summary>
    internal static string Canonical(IEnumerable<KeyValuePair<string, string>> map)
    {
        var fields = new List<KeyValuePair<string, Json>>();
        foreach (KeyValuePair<string, string> pair in map)
        {
            fields.Add(new(pair.Key, Json.Of(pair.Value)));
        }

        return JsonCodec.Stringify(Json.Object(fields), 4);
    }

    /// <summary>The digest of a string map's canonical JSON.</summary>
    internal static string MapDigest(IEnumerable<KeyValuePair<string, string>> map) => Digest(Encoding.UTF8.GetBytes(Canonical(map)));

    /// <summary>
    /// <paramref name="path"/>, refused when it is empty, holds a NUL (no path
    /// does), or is longer than any path, as given or made absolute, before
    /// the file system or any message sees it whole.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure quoting an excerpt.</exception>
    internal static string CheckedPath(string? path, string what)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path), what + " is null");
        }

        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            throw JacquardException.Preparation(what + " " + Identities.Excerpt(path) + " is not a path");
        }

        // As given first, so a huge path is not copied again; then as the
        // file system will see it, since a relative path grows.
        if (path.Length > PathLimit || AbsoluteLength(path) > PathLimit)
        {
            throw JacquardException.Preparation(what + " " + Identities.Excerpt(path) + " is longer than any path");
        }

        return path;
    }

    /// <summary>The length of <paramref name="path"/> made absolute, or one past the limit when it cannot be.</summary>
    internal static int AbsoluteLength(string path)
    {
        try
        {
            return Path.GetFullPath(path).Length;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return PathLimit + 1;
        }
    }

    /// <summary>Finds an executable the way a shell would, then resolves a final symbolic link.</summary>
    /// <exception cref="JacquardException">A preparation failure for a missing executable.</exception>
    internal static string Executable(string binary)
    {
        JacquardException Missing() => JacquardException.Preparation("cannot find Core executable " + Identities.Excerpt(binary)
            + "; configure JACQUARD_BIN using the setup guide");

        // No path is this long (PATH_MAX is 4096 on Linux), and none holds a
        // NUL, so such a name is refused before it is used.
        if (binary.Length > PathLimit || binary.Contains('\0', StringComparison.Ordinal))
        {
            throw Missing();
        }

        string? candidate = null;
        if (binary.Contains('/', StringComparison.Ordinal))
        {
            candidate = binary;
        }
        else
        {
            // Like Go's exec.LookPath, relative entries (including empty ones,
            // which mean the current directory) are not searched.
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':'))
            {
                if (Path.IsPathRooted(entry) && IsExecutable(Path.Combine(entry, binary)))
                {
                    candidate = Path.Combine(entry, binary);
                    break;
                }
            }
        }

        if (candidate is null)
        {
            throw Missing();
        }

        if (!IsExecutable(candidate))
        {
            if (IsRegular(candidate))
            {
                throw JacquardException.Preparation("Core executable " + Identities.Excerpt(binary)
                    + " may not be executed by the current user; give it execute permission or configure JACQUARD_BIN");
            }

            throw Missing();
        }

        try
        {
            FileSystemInfo? target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? candidate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw JacquardException.Preparation("cannot resolve Core executable " + Identities.Excerpt(binary) + ": " + error.Message);
        }
    }

    /// <summary>Whether <paramref name="path"/> is a regular file this process may execute (<c>access(X_OK)</c>), not merely one with some execute bit.</summary>
    private static bool IsExecutable(string path) => IsRegular(path) && Unix.MayExecute(path);

    private static bool IsRegular(string path)
    {
        if (path.Length > PathLimit)
        {
            return false;
        }

        try
        {
            return Unix.Stat(path, follow: true).Kind == Unix.Kind.Regular;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies <paramref name="access"/> to <paramref name="path"/>, which is
    /// not followed if it is a link: refuses it unless this user owns it and
    /// neither its group nor others may write it, or removes every group and
    /// other permission. Nothing inside a cache entry can resist someone who
    /// may rewrite it, so the guarantee is that only the cache's owner can.
    /// </summary>
    /// <returns>The status that was checked; empty for <see cref="Access.Any"/> or where there are no POSIX permissions.</returns>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static Unix.Status Check(string path, Access access)
    {
        if (access == Access.Any || !Unix.Supported)
        {
            return default;
        }

        Unix.Status status = StatOrFail(path, follow: false, "cannot read ");
        if (access == Access.Restrict)
        {
            if ((status.Mode & 0x3F) != 0)
            {
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path, (UnixFileMode)(status.Mode & 0x1C0));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw JacquardException.Preparation("cannot make " + path + " owner-only: " + error.Message);
                }
            }

            return status;
        }

        if (status.Owner != Unix.EffectiveUser())
        {
            throw JacquardException.Preparation("cache path " + path + " is not owned by the current user; only its owner may write the cache");
        }

        if ((status.Mode & 0x12) != 0)
        {
            throw JacquardException.Preparation("cache path " + path + " is group- or world-writable; only its owner may write the cache"
                + " (make it owner-only, for example chmod " + (status.Kind == Unix.Kind.Directory ? "700 " : "600 ") + path + ")");
        }

        return status;
    }

    /// <summary>
    /// Refuses the cache root <paramref name="root"/>, a real path with no
    /// links in it, unless every directory above it, up to <c>/</c>, is safe
    /// (see <see cref="UnsafeDirectories"/>). Otherwise another user could
    /// rename the root away and put another directory at its path at any
    /// time, and every later use of the path, a prepared program's workers
    /// included, would use it.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure naming every unsafe directory, or cancelled.</exception>
    internal static void CheckAncestors(string root, PasswordListing listing, CancellationToken cancellationToken)
    {
        if (Path.GetDirectoryName(root) is { } parent && UnsafeDirectories(parent, "cache directory " + root, listing, cancellationToken) is { } problem)
        {
            throw JacquardException.Preparation(problem);
        }
    }

    /// <summary>
    /// Refuses <paramref name="directory"/>, a real path with no links in it,
    /// and the directories above it before any missing level below it is
    /// made, so a refused preparation makes nothing.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure naming every unsafe directory, or cancelled.</exception>
    internal static void CheckDirectories(string directory, string root, PasswordListing listing, CancellationToken cancellationToken)
    {
        if (UnsafeDirectories(directory, "cache directory " + root, listing, cancellationToken) is { } problem)
        {
            throw JacquardException.Preparation(problem);
        }
    }

    /// <summary>
    /// The password database verdict of the private-group rule
    /// (<see cref="OnlyUserHasPrimaryGroup"/>) within one preparation or one
    /// save: the listing runs at most once for one account, user id, and
    /// group, and the verdict is not kept after that call. Not thread-safe;
    /// one call uses it.
    /// </summary>
    internal sealed class PasswordListing
    {
        private string? name;
        private uint uid;
        private uint gid;
        private bool verdict;

        /// <summary>The verdict for the account, listing the password database the first time.</summary>
        /// <exception cref="JacquardException">The token was cancelled while <c>getent</c> ran.</exception>
        internal bool OnlyUserHasPrimaryGroup(string name, uint uid, uint gid, CancellationToken cancellationToken)
        {
            if (this.name is null || !string.Equals(this.name, name, StringComparison.Ordinal) || this.uid != uid || this.gid != gid)
            {
                bool listed = Core.OnlyUserHasPrimaryGroup(name, uid, gid, cancellationToken);
                (this.name, this.uid, this.gid, verdict) = (name, uid, gid, listed);
            }

            return verdict;
        }
    }

    /// <summary>
    /// Test seam: answers the password database lookups of the private-group
    /// rule in place of <see cref="Unix.User"/>. Null outside tests.
    /// </summary>
    internal static volatile Func<uint, Unix.Account?>? UserHook;

    /// <summary>
    /// Test seam: answers the group database lookups of the private-group
    /// rule in place of <see cref="Unix.GroupOf"/>. Null outside tests.
    /// </summary>
    internal static volatile Func<uint, Unix.GroupEntry?>? GroupHook;

    /// <summary>
    /// Test seam: runs in place of <c>getent passwd</c> for the private-group
    /// rule, with the caller's token, returning what the command printed and
    /// its exit status, or throwing as <see cref="CoreCommand.Run"/> does.
    /// Null outside tests.
    /// </summary>
    internal static volatile Func<CancellationToken, CoreCommand.Output>? GetentHook;

    /// <summary>Test seam: where <c>getent</c> is looked for, in order; the first present is used.</summary>
    internal static volatile string[] GetentPaths = { "/usr/bin/getent", "/bin/getent" };

    /// <summary>
    /// Test seam: reads the status (not following a final link) of the
    /// <c>getent</c> file and the directories above it in place of
    /// <see cref="Unix.Stat(string, bool)"/>. Null outside tests.
    /// </summary>
    internal static volatile Func<string, Unix.Status>? GetentStatHook;

    /// <summary>Test seam: the arguments <c>getent</c> runs with.</summary>
    internal static volatile string[] GetentArguments = { "passwd" };

    /// <summary>How long <c>getent passwd</c> may run, in milliseconds.</summary>
    internal const int GetentTimeoutMs = 5000;

    /// <summary>Test seam: the time limit applied, in milliseconds.</summary>
    internal static volatile int GetentTimeoutCap = GetentTimeoutMs;

    /// <summary>The most stdout of <c>getent passwd</c> read; more is refused.</summary>
    internal const int GetentStdoutLimit = 16 << 20;

    /// <summary>The most password database entries the private-group rule reads; more is refused.</summary>
    internal const int PasswdEntries = 100_000;

    /// <summary>The most unsafe directories one refusal names; the rest are counted.</summary>
    internal const int UnsafeListed = 16;

    /// <summary>
    /// Checks <paramref name="first"/>, a real path with no links in it, and
    /// every directory above it up to <c>/</c>, with <c>lstat</c>: each must
    /// be a directory owned by this user or by root, and neither its group
    /// nor others may write it, unless it has the sticky bit (as OpenSSH
    /// checks the directories above its configuration) or the group is this
    /// user's private group. In a sticky directory only the owner of an entry
    /// (or the directory's owner, or root) may rename or remove it, and the
    /// next directory down is checked in turn. The private group is Debian
    /// OpenSSH's rule (no account but the user has the group as its primary
    /// group, and the group lists no member but the user) with stricter
    /// conditions: the directory's group is the user's primary group, that
    /// group's name is the user's name, it lists no members or only the
    /// user, the password database as enumerated lists the user with that
    /// primary group and no other account with it (see
    /// <see cref="OnlyUserHasPrimaryGroup"/>), and the directory has no
    /// access ACL (whose mask, on Linux, is what the group bits show). Every
    /// unsafe directory is named, nearest first, up to
    /// <see cref="UnsafeListed"/>. The enumeration runs at most once per
    /// <paramref name="listing"/>, which one preparation or save shares
    /// between its checks, and its verdict is not kept after it.
    /// </summary>
    /// <param name="first">The nearest directory to check.</param>
    /// <param name="subject">What the directories hold, which starts the message.</param>
    /// <param name="listing">The password database verdict of this preparation or save.</param>
    /// <param name="cancellationToken">Stops the password database enumeration.</param>
    /// <returns>The refusal naming every unsafe directory, or null when all are safe or there are no POSIX permissions.</returns>
    /// <exception cref="JacquardException">A directory's status cannot be read, or the token was cancelled during the enumeration.</exception>
    internal static string? UnsafeDirectories(string first, string subject, PasswordListing listing, CancellationToken cancellationToken)
    {
        if (!Unix.Supported)
        {
            return null;
        }

        uint user = Unix.EffectiveUser();
        bool looked = false;
        Unix.Account? account = null;
        Unix.GroupEntry? group = null;
        var unsafeOnes = new List<string>();
        int count = 0;
        for (string? directory = first; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            Unix.Status status = StatOrFail(directory, follow: false, "cannot read ");
            string? reason = null;
            if (status.Kind != Unix.Kind.Directory)
            {
                reason = "not a directory";
            }
            else if (status.Owner != user && status.Owner != 0)
            {
                reason = "owned by another user";
            }
            else if ((status.Mode & 0x200) == 0)
            {
                // 0o002: other write; 0o020: group write; 0o1000: sticky.
                if ((status.Mode & 0x2) != 0)
                {
                    reason = "writable by others";
                }
                else if ((status.Mode & 0x10) != 0)
                {
                    if (!looked)
                    {
                        looked = true;
                        account = UserHook is { } users ? users(user) : Unix.User(user);
                        if (account is { } known)
                        {
                            group = GroupHook is { } groups ? groups(known.Group) : Unix.GroupOf(known.Group);
                        }
                    }

                    if (!PrivateGroup(status, account, group) || !listing.OnlyUserHasPrimaryGroup(account!.Value.Name, user, status.Group, cancellationToken))
                    {
                        reason = "writable by its group, which is not your private group";
                    }
                    else if (Unix.HasAccessAcl(directory))
                    {
                        // On Linux an ACL's mask shows in the group bits, and its entries may grant others write.
                        reason = "writable by its group, with an access control list";
                    }
                }
            }

            if (reason is not null)
            {
                if (++count <= UnsafeListed)
                {
                    unsafeOnes.Add(directory + " (" + reason + ")");
                }
            }
        }

        if (count == 0)
        {
            return null;
        }

        string more = count > UnsafeListed ? "; and " + (count - UnsafeListed).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty;

        // The remedy comes before the list, so a message cut at its limit keeps it.
        return subject + " could be replaced by another user through the directories above it."
            + " Make each of these writable only by you (for example chmod go-w DIR), or use a location under directories only you or root may write: "
            + string.Join("; ", unsafeOnes) + more;
    }

    /// <summary>
    /// The group-database half of the private-group rule for a
    /// group-writable <paramref name="status"/>; the password database half
    /// is <see cref="OnlyUserHasPrimaryGroup"/>.
    /// </summary>
    private static bool PrivateGroup(Unix.Status status, Unix.Account? account, Unix.GroupEntry? group) =>
        account is { } user && group is not null
        && status.Group == user.Group
        && string.Equals(group.Name, user.Name, StringComparison.Ordinal)
        && (group.Members.Count == 0 || (group.Members.Count == 1 && string.Equals(group.Members[0], user.Name, StringComparison.Ordinal)));

    /// <summary>
    /// Whether the password database, as <c>getent passwd</c> lists it in a
    /// separate process, lists the account <paramref name="name"/> with user
    /// id <paramref name="uid"/> and primary group <paramref name="gid"/>,
    /// and no account of another user id with that primary group
    /// (<c>gr_mem</c> omits primary-group members, so this is what shows
    /// them), as Debian OpenSSH's <c>secure_permissions</c> scans with
    /// <c>getpwent</c>. Listing it in another process shares no enumeration
    /// position with code in this one. <c>getent</c> is the first of
    /// <c>/usr/bin/getent</c> and <c>/bin/getent</c> present; it and every
    /// directory above its real path must be root's and not writable by
    /// group or others. It runs through <see cref="CoreCommand.Run"/>
    /// (bounded by <see cref="GetentTimeoutMs"/>, its stdout by
    /// <see cref="GetentStdoutLimit"/>, and stopped by the token). False, so
    /// the directory is refused, when there is no such <c>getent</c> (or not
    /// on Linux), when it times out, exits nonzero, or prints more than the
    /// limit, invalid UTF-8, more than <see cref="PasswdEntries"/> entries,
    /// or a line that is not seven colon-separated fields with a nonempty
    /// name and decimal user and group ids, or ends without a newline, when
    /// a name is listed twice, or when the account is missing or listed with
    /// other ids. Accounts a source does not enumerate cannot be seen.
    /// </summary>
    /// <exception cref="JacquardException">The token was cancelled while <c>getent</c> ran.</exception>
    internal static bool OnlyUserHasPrimaryGroup(string name, uint uid, uint gid, CancellationToken cancellationToken)
    {
        CoreCommand.Output output;
        try
        {
            if (GetentHook is { } hook)
            {
                output = hook(cancellationToken);
            }
            else
            {
                if (!OperatingSystem.IsLinux() || TrustedGetent() is not { } getent)
                {
                    return false;
                }

                var command = new List<string> { getent };
                command.AddRange(GetentArguments);
                output = CoreCommand.Run(command, null, TimeSpan.FromMilliseconds(GetentTimeoutCap), GetentStdoutLimit, StderrLimit, cancellationToken);
            }
        }
        catch (CarrierException error)
        {
            if (error.IsAborted)
            {
                throw JacquardException.Preparation(Aborted);
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }

        return output.ExitStatus == 0 && !output.StdoutOverflow && Utf8(output.Stdout) is { } text && SoleAccount(text, name, uid, gid);
    }

    /// <summary>
    /// The real path of the first of <see cref="GetentPaths"/> present, when
    /// it is a regular file and it and every directory above it are root's
    /// and not writable by group or others; null otherwise.
    /// </summary>
    /// <exception cref="IOException">A status cannot be read.</exception>
    private static string? TrustedGetent()
    {
        foreach (string candidate in GetentPaths)
        {
            if (Unix.Stat(candidate, follow: true).Kind == Unix.Kind.Missing)
            {
                continue;
            }

            string real = Unix.RealPath(candidate);
            if (Lstat(real) is not { Kind: Unix.Kind.Regular } file || !RootOnly(file))
            {
                return null;
            }

            for (string? directory = Path.GetDirectoryName(real); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (Lstat(directory) is not { Kind: Unix.Kind.Directory } level || !RootOnly(level))
                {
                    return null;
                }
            }

            return real;
        }

        return null;

        static Unix.Status Lstat(string path) => GetentStatHook is { } hook ? hook(path) : Unix.Stat(path, follow: false);
    }

    /// <summary>Whether <paramref name="status"/> is root's and neither its group nor others may write it.</summary>
    internal static bool RootOnly(Unix.Status status) => status.Owner == 0 && (status.Mode & 0x12) == 0;

    /// <summary>
    /// Parses <c>getent passwd</c>'s output strictly (see
    /// <see cref="OnlyUserHasPrimaryGroup"/>); whether it holds.
    /// </summary>
    internal static bool SoleAccount(string text, string name, uint uid, uint gid)
    {
        if (text.Length == 0 || text[^1] != '\n')
        {
            return false;
        }

        // At most the stdout limit of names is kept.
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool listed = false;
        int read = 0;
        for (int start = 0; start < text.Length;)
        {
            int end = text.IndexOf('\n', start);
            ReadOnlySpan<char> line = text.AsSpan(start, end - start);
            start = end + 1;
            if (++read > PasswdEntries || line.Count(':') != 6)
            {
                return false;
            }

            string[] fields = line.ToString().Split(':');
            if (fields[0].Length == 0 || Id(fields[2]) is not { } entryUid || Id(fields[3]) is not { } entryGid || !names.Add(fields[0]))
            {
                return false;
            }

            if (string.Equals(fields[0], name, StringComparison.Ordinal))
            {
                if (entryUid != uid || entryGid != gid)
                {
                    return false;
                }

                listed = true;
            }
            else if (entryGid == gid && entryUid != uid)
            {
                return false;
            }
        }

        return listed;
    }

    /// <summary>A decimal id with no sign, no leading zero, and no other character; null otherwise.</summary>
    private static uint? Id(string text) =>
        text.Length > 0 && (text.Length == 1 || text[0] != '0')
        && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
            ? id
            : null;
}
