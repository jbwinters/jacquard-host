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
    /// Writes the invocation's frames and host evidence. The record contains
    /// argument and result values; it is written only when called. A new file
    /// is created owner-only; an existing file keeps its permissions.
    /// </summary>
    /// <exception cref="IOException">The file cannot be written, or the path is longer than any path.</exception>
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
        using var stream = new FileStream(path, Core.OwnerOnly(FileMode.Create));
        stream.Write(bytes);
    }

    /// <inheritdoc/>
    public override string ToString() => "Invocation[" + function.Name + ", " + Raw + "]";
}
