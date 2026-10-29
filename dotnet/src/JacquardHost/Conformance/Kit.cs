using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Conformance;

/// <summary>The pinned kit could not be loaded or verified.</summary>
public sealed class KitException : Exception
{
    /// <summary>A failure with its reason.</summary>
    public KitException(string message)
        : base(message)
    {
    }
}

/// <summary>One host-side action in a recording.</summary>
/// <param name="Direction"><c>"core_to_host"</c> for a read, else a host write.</param>
/// <param name="RawHex">Raw carrier bytes to write, or null.</param>
/// <param name="Frame">The frame to write; empty for reads and raw writes.</param>
/// <param name="AfterTerminal">Whether the recording sends this after Core's terminal.</param>
public sealed record TranscriptStep(string Direction, string? RawHex, JsonObject Frame, bool AfterTerminal);

/// <summary>One positive or hostile recording.</summary>
/// <param name="Name">The recording's name.</param>
/// <param name="Kind"><c>"positive"</c> or a hostile kind.</param>
/// <param name="Phase">The phase a hostile input targets.</param>
/// <param name="Expect">The recorded expectations.</param>
/// <param name="Observed">The recorded summary.</param>
/// <param name="Status"><c>"conforms"</c> or <c>"diverges"</c>.</param>
/// <param name="ExitStatus">The worker's recorded exit status.</param>
/// <param name="CoreOutput">Core's recorded frames.</param>
/// <param name="HostInput">The host's recorded actions.</param>
public sealed record Transcript(
    string Name,
    string Kind,
    string Phase,
    JsonObject Expect,
    JsonObject Observed,
    string Status,
    long ExitStatus,
    IReadOnlyList<JsonObject> CoreOutput,
    IReadOnlyList<TranscriptStep> HostInput);

/// <summary>
/// The pinned <c>host-protocol-v0</c> conformance kit, carried as embedded
/// resources and verified against the pinned digests when loaded.
/// </summary>
public sealed class Kit
{
    /// <summary>The number of recordings in the pinned kit.</summary>
    public const int TranscriptCount = 21;

    private static readonly string[] Files = { "kit/kit.json", "kit/transcripts.json", "kit/fixtures.jac", "kit/README.md", "vectors.json" };

    private readonly Dictionary<string, Hash> identities;

    private Kit(JsonObject manifest, IReadOnlyList<Transcript> transcripts, Dictionary<string, Hash> identities)
    {
        Manifest = manifest;
        Transcripts = transcripts;
        this.identities = identities;
    }

    /// <summary>The decoded <c>kit/kit.json</c>.</summary>
    public JsonObject Manifest { get; }

    /// <summary>The recordings, in kit order.</summary>
    public IReadOnlyList<Transcript> Transcripts { get; }

    /// <summary>The kit's named identities, such as <c>"kit-echo"</c>.</summary>
    public IReadOnlyDictionary<string, Hash> Identities => identities;

    /// <summary>One pinned kit file, such as <c>"kit/fixtures.jac"</c>, or null.</summary>
    public static byte[]? File(string name)
    {
        if (!Files.Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        using Stream? stream = typeof(Kit).Assembly.GetManifestResourceStream("host-protocol-v0/" + name);
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Text(JsonObject obj, string key) => obj.Get(key)?.AsString() ?? string.Empty;

    private static Json DecodeFile(string name)
    {
        byte[] data = File(name) ?? throw new KitException("missing kit file " + name);
        try
        {
            return JsonCodec.Decode(data, 256);
        }
        catch (ProtocolException error)
        {
            throw new KitException(name + ": " + error.Message);
        }
    }

    /// <summary>Verifies the embedded files against the pinned digests and decodes them.</summary>
    /// <exception cref="KitException">A file is missing, differs from the pin, or has an unexpected shape.</exception>
    public static Kit Load()
    {
        foreach (KeyValuePair<string, string> entry in Compat.KitDigests)
        {
            byte[] data = File(entry.Key) ?? throw new KitException("missing kit file " + entry.Key);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new KitException("pinned conformance digest mismatch: " + entry.Key);
            }
        }

        if (DecodeFile("kit/kit.json") is not JsonObject manifest || DecodeFile("kit/transcripts.json") is not JsonArray entries)
        {
            throw new KitException("kit files have an unexpected shape");
        }

        var pending = new List<string>();
        if (manifest.Get("pending_decisions")?.AsArray() is { } decisions)
        {
            foreach (Json decision in decisions)
            {
                pending.Add(decision is JsonObject obj ? Text(obj, "name") : string.Empty);
            }
        }

        if (!Json.Of(Compat.Protocol).Equals(manifest.Get("protocol"))
            || !Json.Of(Compat.Carrier).Equals(manifest.Get("carrier"))
            || !Json.Of(Compat.CoreVersion).Equals(manifest.Get("core_version"))
            || entries.Items.Count != TranscriptCount
            || !pending.SequenceEqual(Compat.PendingDecisions))
        {
            throw new KitException("kit metadata does not match the pin");
        }

        var identities = new Dictionary<string, Hash>(StringComparer.Ordinal);
        if (manifest.Get("identities") is JsonObject named)
        {
            foreach (KeyValuePair<string, Json> entry in named)
            {
                try
                {
                    identities[entry.Key] = Hash.Parse(entry.Value.AsString() ?? string.Empty);
                }
                catch (ProtocolException error)
                {
                    throw new KitException(error.Message);
                }
            }
        }

        return new Kit(manifest, entries.Items.Select(ParseTranscript).ToArray(), identities);
    }

    private static List<JsonObject> Objects(JsonObject entry, string key, string name)
    {
        var found = new List<JsonObject>();
        if (entry.Get(key)?.AsArray() is not { } list)
        {
            return found;
        }

        foreach (Json item in list)
        {
            found.Add(item as JsonObject ?? throw new KitException(name + ": " + key + " entry is not an object"));
        }

        return found;
    }

    private static Transcript ParseTranscript(Json value)
    {
        JsonObject entry = value as JsonObject ?? throw new KitException("transcript is not an object");
        string name = Text(entry, "name");
        List<JsonObject> coreOutput = Objects(entry, "core_output", name);
        var hostInput = new List<TranscriptStep>();
        foreach (JsonObject step in Objects(entry, "host_input", name))
        {
            hostInput.Add(new TranscriptStep(
                Text(step, "direction"),
                step.Get("raw_hex")?.AsString(),
                step.Get("frame") as JsonObject ?? JsonObject.Empty,
                Json.Of(true).Equals(step.Get("after_terminal"))));
        }

        if (entry.Get("expect") is not JsonObject expect || entry.Get("observed") is not JsonObject observed || name.Length == 0)
        {
            throw new KitException("transcript is missing required fields");
        }

        return new Transcript(
            name,
            Text(entry, "kind"),
            Text(entry, "phase"),
            expect,
            observed,
            Text(entry, "status"),
            entry.Get("exit_status")?.AsInt() ?? 0,
            coreOutput,
            hostInput);
    }

    /// <summary>The transcript with this name, or null.</summary>
    public Transcript? Find(string name) => Transcripts.FirstOrDefault(transcript => transcript.Name == name);

    /// <summary>The named identity.</summary>
    /// <exception cref="ArgumentException">The kit has no identity by that name.</exception>
    public Hash Identity(string name) =>
        identities.TryGetValue(name, out Hash? hash) ? hash : throw new ArgumentException("the kit has no identity named " + name, nameof(name));
}
