using System;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>Options for Core discovery and invocation bounds. A null or empty value means unset.</summary>
public sealed record RuntimeOptions
{
    /// <summary>Every option unset.</summary>
    public static RuntimeOptions Defaults { get; } = new();

    /// <summary>The Core executable; defaults to <c>$JACQUARD_BIN</c>, then <c>jacquard</c> on the <c>PATH</c>.</summary>
    public string? Binary { get; init; }

    /// <summary>Core's prelude directory; defaults to <c>$JACQUARD_PRELUDE</c>.</summary>
    public string? Prelude { get; init; }

    /// <summary>Bounds each invocation's worker lifetime; null means the host's default (30 seconds).</summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>The session limits; null selects the hard limits.</summary>
    public Limits? Limits { get; init; }
}

/// <summary>Options for one preparation.</summary>
public sealed record PrepareOptions
{
    /// <summary>The default cache directory.</summary>
    public static PrepareOptions Defaults { get; } = new();

    /// <summary>Holds verified prepared programs; null means <see cref="Runtime.DefaultCacheDir"/>.</summary>
    public string? CacheDir { get; init; }
}
