// Generated from core-pin.json by scripts/core_pin.py; do not edit by hand.

using System.Collections.Generic;

namespace JacquardLang.Host;

/// <summary>The Jacquard Core release and host protocol this adapter is pinned to.</summary>
public static class Compat
{
    /// <summary>The host protocol.</summary>
    public const string Protocol = "jacquard-host-v0";

    /// <summary>The carrier.</summary>
    public const string Carrier = "stdio-u32-json-v0";

    /// <summary>The pinned Core version.</summary>
    public const string CoreVersion = "0.3.0";

    /// <summary>The pinned Core release tag.</summary>
    public const string CoreTag = "jacquard-core-0.3.0";

    /// <summary>The pinned Core commit.</summary>
    public const string CoreCommit = "daf87fb4837cc69339116746818f64821dc4cb8e";

    /// <summary>SHA-256 digests of the pinned conformance kit files.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> KitDigests { get; } = new KeyValuePair<string, string>[]
    {
        new("kit/kit.json", "a01b9401d7c2df4e17771b9c0d22f9ea5ce1a923dfb7d3396c27821c0e058a61"),
        new("kit/transcripts.json", "ebaf590e3d1b7e8399f56de5ad85043ac3658416dd7f0af7b55b9f9f3ce93240"),
        new("kit/fixtures.jac", "037bb8209ca0344ba0a2cd0fcc16e69f13c8f9b91e3404dc3f3161e351831d80"),
        new("vectors.json", "9ba1bff8eee0b6dbea6475dc0c7e9cc6313d847ad80bd8d832457115eb1c738a"),
    };

    /// <summary>Decisions Core recorded as pending; replay reproduces them.</summary>
    public static IReadOnlyList<string> PendingDecisions { get; } = new[] { "noncanonical-target-hash" };
}
