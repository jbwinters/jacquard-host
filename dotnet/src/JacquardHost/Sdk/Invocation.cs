using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>One completed invocation with its raw evidence.</summary>
public sealed class Invocation
{
    private readonly Function function;

    internal Invocation(InvocationResult raw, Function function)
    {
        Raw = raw;
        this.function = function;
    }

    /// <summary>The host's result: Core's frames, the host's frames, and evidence.</summary>
    public InvocationResult Raw { get; }

    /// <summary>Whether Core reported success.</summary>
    public bool Ok => Raw.Ok;

    /// <summary>The primary diagnostic or host code.</summary>
    public string Code => Raw.Code;

    /// <summary>A summary for people.</summary>
    public string Message => Raw.Message;

    /// <summary>The classified failure, or null on success.</summary>
    public JacquardException? Error => Ok ? null : JacquardException.Invocation(Raw);

    /// <summary>Decodes the result.</summary>
    /// <exception cref="JacquardException">
    /// The invocation's failure, or a binding failure keeping the successful
    /// raw result when it does not decode; when the caller's codec or record
    /// constructor threw, that exception is the inner exception.
    /// </exception>
    public T Decode<T>(Codec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (Error is { } error)
        {
            throw error;
        }

        Value value = Raw.Value ?? throw JacquardException.Binding(function.Name + ": Core completed without a value", Raw, null);
        try
        {
            return function.Data.Decode(codec, value);
        }
        catch (JacquardException mapping)
        {
            // Already bounded, and the SDK's own words.
            throw JacquardException.Binding(
                JacquardException.Joined(function.Name + ": Core completed, but result mapping failed: ", mapping.Message),
                Raw,
                mapping.InnerException ?? mapping);
        }
    }

    /// <summary>Decodes the result into <see cref="Dynamic"/>.</summary>
    /// <exception cref="JacquardException">As for <see cref="Decode{T}"/>.</exception>
    public Dynamic ToDynamic() => Decode(Codecs.Dynamic);

    /// <summary>Lists the outcome and each effect request by operation name.</summary>
    public string Render()
    {
        var lines = new List<string> { function.Name + ": " + (Ok ? "ok" : Message) };
        foreach (EffectRequest request in Raw.Requests)
        {
            lines.Add("  " + request.Ordinal.ToString(CultureInfo.InvariantCulture) + ". " + (function.Data.Label(request.Operation) ?? request.Operation.Hex));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Test seam: runs with the temporary file's path after it is written
    /// and flushed, before it is checked and renamed over the target. Null
    /// outside tests.
    /// </summary>
    internal static volatile Action<string>? BeforeRenameHook;

    /// <summary>
    /// Writes the invocation's frames and host evidence. The record contains
    /// argument and result values; it is written only when called. Where the
    /// file system has POSIX permissions, the target's directory and every
    /// directory above it must be owned by this user or root and not writable
    /// by its group or others, unless it is sticky (such as <c>/tmp</c>) or
    /// the group is this user's private group, as for the cache's
    /// directories; otherwise the save is refused, since another user could
    /// replace the file before or after it is renamed. The record is written
    /// through one handle to a new file, created exclusively (never through
    /// a link) in the target's directory, owned by this process's user and
    /// owner-only (read and write for its owner), and flushed to disk; the
    /// file at its path is checked to be the one written (the same device
    /// and inode), and only then renamed over <paramref name="path"/>.
    /// Whatever was at the path is replaced, never written through: an
    /// existing file's permissions and owner are not kept, and a symbolic
    /// link there is replaced, not followed. Where the directory is sticky,
    /// a file another user owns cannot be replaced and the save fails.
    /// </summary>
    /// <exception cref="IOException">The file cannot be written, the target's directory is not safe from other users, the temporary file was replaced before its rename, or the path is longer than any path.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be written.</exception>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal) || path.Length > Core.PathLimit || Core.AbsoluteLength(path) > Core.PathLimit)
        {
            throw new IOException(Identities.Excerpt(path) + " is not a path or is longer than any path");
        }

        JsonObject host = Json.Object(
            ("evidence", Raw.HostEvidence),
            ("exit_status", Raw.ExitStatus is { } status ? Json.Of(status) : Json.Null),
            ("signal", Raw.Signal is { } signal ? Json.Of(signal) : Json.Null),
            ("stderr_base64", Json.Of(Convert.ToBase64String(Raw.Stderr.ToArray()))));
        JsonObject record = Json.Object(
            ("core_frames", Json.Of(Raw.CoreFrames)),
            ("function", Json.Of(function.Name)),
            ("host", host),
            ("host_frames", Json.Of(Raw.HostFrames)),
            ("schema", Json.Of("jacquard-host-inspection-v1")));
        string text;
        try
        {
            text = JsonCodec.Stringify(record, 128);
        }
        catch (ProtocolException error)
        {
            throw new IOException(error.Message, error);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full) ?? throw new IOException(Identities.Excerpt(path) + " names no file");
        string name = Path.GetFileName(full);
        if (name.Length == 0)
        {
            throw new IOException(Identities.Excerpt(path) + " names no file");
        }

        if (Unix.Supported)
        {
            // The directory is resolved once and used by its real path, so
            // the directories checked are the ones written.
            directory = Unix.RealPath(directory);
            full = Path.Combine(directory, name);
            string? problem;
            try
            {
                problem = Core.UnsafeDirectories(directory, "save target " + full);
            }
            catch (JacquardException error)
            {
                throw new IOException(error.Message, error);
            }

            if (problem is not null)
            {
                throw new IOException(JacquardException.Bounded(problem));
            }
        }

        string temporary = Path.Combine(directory, string.Format(
            CultureInfo.InvariantCulture,
            ".jacquard-save-{0}-{1}",
            Environment.ProcessId,
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()));
        bool renamed = false;
        Unix.Status written = default;
        try
        {
            // FileMode.CreateNew is O_CREAT|O_EXCL, which never follows a
            // link at the path. The one handle is written, flushed, and,
            // where there are POSIX permissions, held open until the rename
            // (elsewhere an open file cannot be renamed).
            var stream = new FileStream(temporary, Core.OwnerOnly(FileMode.CreateNew));
            try
            {
                if (Unix.Supported)
                {
                    written = Unix.Stat(stream.SafeFileHandle);
                }

                if (!OperatingSystem.IsWindows())
                {
                    // Exactly owner-only, whatever the umask left.
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                BeforeRenameHook?.Invoke(temporary);
                if (Unix.Supported && !Unix.Stat(temporary, follow: false).SameInode(written))
                {
                    // Not this call's file: it is neither renamed nor removed.
                    written = default;
                    throw new IOException("the temporary file for " + Identities.Excerpt(full) + " was replaced before it was renamed; nothing was saved");
                }

                if (!Unix.Supported)
                {
                    stream.Dispose();
                }

                File.Move(temporary, full, overwrite: true);
                renamed = true;
            }
            finally
            {
                stream.Dispose();
            }
        }
        finally
        {
            if (!renamed)
            {
                RemoveTemporary(temporary, written);
            }
        }
    }

    /// <summary>
    /// Removes the temporary file this call created exclusively, and only
    /// that: where it was replaced, or its identity is unknown, whatever is
    /// at the path is left behind under its distinctive name.
    /// </summary>
    private static void RemoveTemporary(string temporary, Unix.Status written)
    {
        try
        {
            if (!Unix.Supported || (written.Kind == Unix.Kind.Regular && Unix.Stat(temporary, follow: false).SameInode(written)))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left behind under its distinctive name.
        }
    }

    /// <inheritdoc/>
    public override string ToString() => "Invocation[" + function.Name + ", " + Raw + "]";
}
