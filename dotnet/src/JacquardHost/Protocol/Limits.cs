using System;
using System.Collections.Generic;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>The v0 ceilings a host selects; each must lie in <c>1..=</c> its hard value.</summary>
public sealed record Limits(
    int MaxArguments,
    int MaxCollectionItems,
    int MaxDiagnosticBytes,
    int MaxDiagnostics,
    int MaxEffectRequests,
    int MaxEffects,
    int MaxFrameBytes,
    int MaxHostMessageBytes,
    int MaxJsonDepth,
    int MaxOperations,
    int MaxStderrBytes,
    int MaxTextBytes,
    int MaxValueNodes)
{
    /// <summary>The largest integer the other adapters accept exactly in limits and ordinals.</summary>
    internal const long MaxSafeInteger = (1L << 53) - 1;

    /// <summary>The frozen v0 ceilings Core advertises in <c>core_hello</c>.</summary>
    public static Limits Hard { get; } = new(64, 1024, 65536, 32, 1024, 64, 1_048_576, 4096, 64, 256, 65536, 262_144, 4096);

    /// <summary>The wire names, in protocol (sorted) order.</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "max_arguments",
        "max_collection_items",
        "max_diagnostic_bytes",
        "max_diagnostics",
        "max_effect_requests",
        "max_effects",
        "max_frame_bytes",
        "max_host_message_bytes",
        "max_json_depth",
        "max_operations",
        "max_stderr_bytes",
        "max_text_bytes",
        "max_value_nodes",
    };

    private int[] Values() => new[]
    {
        MaxArguments, MaxCollectionItems, MaxDiagnosticBytes, MaxDiagnostics, MaxEffectRequests, MaxEffects,
        MaxFrameBytes, MaxHostMessageBytes, MaxJsonDepth, MaxOperations, MaxStderrBytes, MaxTextBytes, MaxValueNodes,
    };

    private static Limits Of(int[] v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12]);

    /// <summary>These limits with the one named by its wire name, such as <c>"max_frame_bytes"</c>, replaced.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a limit's wire name.</exception>
    public Limits With(string name, int value)
    {
        int index = Names.ToList().IndexOf(name);
        if (index < 0)
        {
            throw new ArgumentException("unknown limit " + name, nameof(name));
        }

        int[] v = Values();
        v[index] = value;
        return Of(v);
    }

    /// <summary>Refuses a limit outside <c>1..=</c> its hard ceiling.</summary>
    /// <exception cref="ProtocolException">E1602 for the first limit out of bounds.</exception>
    public void Validate()
    {
        int[] v = Values();
        int[] hard = Hard.Values();
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] < 1 || v[i] > hard[i])
            {
                throw ProtocolException.Exceeded("limit " + Names[i] + " exceeds its bounds");
            }
        }
    }

    /// <summary>The wire object for <c>host_select</c>.</summary>
    public JsonObject ToJson()
    {
        int[] v = Values();
        return Json.Object(Names.Select((name, i) => new KeyValuePair<string, Json>(name, Json.Of(v[i]))));
    }

    /// <summary>Parses and validates an exact limits object.</summary>
    /// <exception cref="ProtocolException">E1601 for another key set or a non-integer, E1602 for a limit out of bounds.</exception>
    public static Limits FromJson(Json value)
    {
        JsonObject fields = Codec.Exact(value, Names.ToArray());
        int[] hard = Hard.Values();
        int[] parsed = new int[hard.Length];
        for (int i = 0; i < hard.Length; i++)
        {
            string name = Names[i];
            long? number = fields[name].AsInt();
            if (number is null || number < -MaxSafeInteger || number > MaxSafeInteger)
            {
                throw ProtocolException.Malformed("limit " + name + " must be an integer");
            }

            if (number < 1 || number > hard[i])
            {
                throw ProtocolException.Exceeded("limit " + name + " exceeds its bounds");
            }

            parsed[i] = (int)number.Value;
        }

        return Of(parsed);
    }
}
