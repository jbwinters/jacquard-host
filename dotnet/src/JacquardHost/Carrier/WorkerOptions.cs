using System;
using System.Collections.Generic;
using System.Linq;

namespace JacquardLang.Host.Carrier;

/// <summary>Options for one worker lifetime.</summary>
public sealed record WorkerOptions
{
    private readonly string[] command = Array.Empty<string>();
    private readonly Dictionary<string, string>? environment;

    /// <summary>The default command, inherited environment, and default deadline.</summary>
    public static WorkerOptions Defaults { get; } = new();

    /// <summary>
    /// The Core executable and leading arguments; <c>host worker --store DIR</c>
    /// is appended. Empty means <c>$JACQUARD_BIN</c>, then <c>jacquard</c>.
    /// </summary>
    public IReadOnlyList<string> Command
    {
        get => command;
        init => command = (value ?? throw new ArgumentNullException(nameof(value))).ToArray();
    }

    /// <summary>Replaces the inherited environment when not null.</summary>
    public IReadOnlyDictionary<string, string>? Environment
    {
        get => environment;
        init => environment = value is null ? null : new Dictionary<string, string>(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Bounds the whole lifetime: spawn, pipes, handlers, and exit; null
    /// means <see cref="Worker.DefaultDeadline"/>.
    /// </summary>
    public TimeSpan? Deadline { get; init; }

    /// <summary>Refuses an empty or null command element or a deadline that is not positive.</summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public void Validate()
    {
        if (command.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("command must be a nonempty executable argument list", nameof(Command));
        }

        if (Deadline is { } deadline && deadline <= TimeSpan.Zero)
        {
            throw new ArgumentException("deadline must be positive", nameof(Deadline));
        }
    }

    internal IReadOnlyList<string> ResolvedCommand()
    {
        if (command.Length > 0)
        {
            return command;
        }

        string? bin = System.Environment.GetEnvironmentVariable("JACQUARD_BIN");
        return new[] { string.IsNullOrEmpty(bin) ? "jacquard" : bin };
    }
}
