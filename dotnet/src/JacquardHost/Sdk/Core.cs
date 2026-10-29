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
    /// Before each piece it observes the token.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static string FileDigest(string path, CancellationToken cancellationToken)
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

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

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
    /// for owner-only access, or made owner-only.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static SortedDictionary<string, string> Tree(string root, Access access, CancellationToken cancellationToken)
    {
        var files = new SortedDictionary<string, string>(Json.CodePointOrder);
        Check(root, access);
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

        if (candidate is null || !IsExecutable(candidate))
        {
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

    private static bool IsExecutable(string path)
    {
        if (path.Length > PathLimit)
        {
            return false;
        }

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
    /// Applies <paramref name="access"/> to <paramref name="path"/>, which is
    /// not followed if it is a link: refuses it unless this user owns it and
    /// neither its group nor others may write it, or removes every group and
    /// other permission. Nothing inside a cache entry can resist someone who
    /// may rewrite it, so the guarantee is that only the cache's owner can.
    /// </summary>
    /// <exception cref="JacquardException">A preparation failure.</exception>
    internal static void Check(string path, Access access)
    {
        if (access == Access.Any || !Unix.Supported)
        {
            return;
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

            return;
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
    }
}
