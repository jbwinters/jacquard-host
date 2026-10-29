using System;
using System.Collections.Generic;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>A parsed Core frame after <c>core_hello</c>: an <see cref="EffectRequest"/> or a <see cref="Terminal"/>.</summary>
public abstract class CoreFrame
{
    private protected CoreFrame(JsonObject raw)
    {
        Raw = raw;
    }

    /// <summary>The exact frame.</summary>
    public JsonObject Raw { get; }
}

/// <summary>Core asking the host to perform one operation.</summary>
public sealed class EffectRequest : CoreFrame
{
    internal EffectRequest(long ordinal, Hash effect, Hash operation, IReadOnlyList<Value> arguments, JsonObject raw)
        : base(raw)
    {
        Ordinal = ordinal;
        Effect = effect;
        Operation = operation;
        Arguments = arguments.ToArray();
    }

    /// <summary>The request ordinal, from one.</summary>
    public long Ordinal { get; }

    /// <summary>The effect identity.</summary>
    public Hash Effect { get; }

    /// <summary>The operation identity.</summary>
    public Hash Operation { get; }

    /// <summary>The decoded arguments.</summary>
    public IReadOnlyList<Value> Arguments { get; }
}

/// <summary>Core's final frame: an <see cref="Outcome"/>, a <see cref="Fatal"/>, or a <see cref="ShutdownAck"/>.</summary>
public abstract class Terminal : CoreFrame
{
    private protected Terminal(JsonObject raw)
        : base(raw)
    {
    }
}

/// <summary>
/// An invocation outcome: a value (null for an error result), or
/// diagnostics, with Core's evidence and terminal name.
/// </summary>
public sealed class Outcome : Terminal
{
    internal Outcome(Value? value, IReadOnlyList<Diagnostic> diagnostics, string terminalName, JsonObject evidence, JsonObject raw)
        : base(raw)
    {
        Value = value;
        Diagnostics = diagnostics.ToArray();
        TerminalName = terminalName;
        Evidence = evidence;
    }

    /// <summary>The value of an ok result, else null.</summary>
    public Value? Value { get; }

    /// <summary>The diagnostics of an error result.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Core's evidence terminal name, such as <c>"ok"</c>.</summary>
    public string TerminalName { get; }

    /// <summary>Core's evidence.</summary>
    public JsonObject Evidence { get; }
}

/// <summary>A session-level failure.</summary>
public sealed class Fatal : Terminal
{
    internal Fatal(IReadOnlyList<Diagnostic> diagnostics, JsonObject raw)
        : base(raw)
    {
        Diagnostics = diagnostics.ToArray();
    }

    /// <summary>Core's diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

/// <summary>The acknowledgement of a host shutdown.</summary>
public sealed class ShutdownAck : Terminal
{
    internal ShutdownAck(JsonObject raw)
        : base(raw)
    {
    }
}

/// <summary>
/// One <c>jacquard-diagnostic-v1</c> entry; <see cref="Raw"/> keeps the exact
/// object. <see cref="Code"/> is empty when Core sent null.
/// </summary>
/// <param name="Code">The diagnostic code, or empty.</param>
/// <param name="Domain">The domain.</param>
/// <param name="Severity">The severity.</param>
/// <param name="Summary">The summary (non-normative prose).</param>
/// <param name="Cause">The cause (non-normative prose).</param>
/// <param name="NextStep">The next step (non-normative prose).</param>
/// <param name="Raw">The exact object.</param>
public sealed record Diagnostic(string Code, string Domain, string Severity, string Summary, string Cause, string NextStep, JsonObject Raw);

/// <summary>The category of a host effect failure.</summary>
public enum FailureCategory
{
    /// <summary>The host does not provide the operation (not started).</summary>
    UnsupportedOperation,

    /// <summary>The host refused to authorize the operation (not started).</summary>
    RefusedAuthority,

    /// <summary>An outside action failed without effect.</summary>
    OutsideFailure,

    /// <summary>An outside action's completion is unknown.</summary>
    CompletionUnknown,
}

/// <summary>The reason a pending request is withdrawn.</summary>
public enum CancelReason
{
    /// <summary>The caller cancelled.</summary>
    Cancelled,

    /// <summary>A deadline passed.</summary>
    Timeout,

    /// <summary>The host is shutting down.</summary>
    HostShutdown,
}

/// <summary>The host session state.</summary>
public enum State
{
    /// <summary>Awaiting <c>core_hello</c>.</summary>
    Hello,

    /// <summary>Hello received; awaiting the host's selection.</summary>
    Negotiating,

    /// <summary>Limits selected.</summary>
    Selected,

    /// <summary>Shutdown sent.</summary>
    ShuttingDown,

    /// <summary>The invocation is running.</summary>
    Running,

    /// <summary>An effect request awaits its response.</summary>
    Waiting,

    /// <summary>A terminal response was sent; awaiting the outcome.</summary>
    Finishing,

    /// <summary>Core's terminal was received.</summary>
    Closed,
}

/// <summary>The wire names of the protocol enumerations.</summary>
public static class WireNames
{
    /// <summary>The wire name, such as <c>"refused_authority"</c>.</summary>
    public static string WireName(this FailureCategory category) => category switch
    {
        FailureCategory.UnsupportedOperation => "unsupported_operation",
        FailureCategory.RefusedAuthority => "refused_authority",
        FailureCategory.OutsideFailure => "outside_failure",
        FailureCategory.CompletionUnknown => "completion_unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// <summary>The one completion the protocol permits with the category.</summary>
    public static string Completion(this FailureCategory category) => category switch
    {
        FailureCategory.UnsupportedOperation => "not_started",
        FailureCategory.RefusedAuthority => "not_started",
        FailureCategory.OutsideFailure => "failed",
        FailureCategory.CompletionUnknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// <summary>The wire name, such as <c>"host_shutdown"</c>.</summary>
    public static string WireName(this CancelReason reason) => reason switch
    {
        CancelReason.Cancelled => "cancelled",
        CancelReason.Timeout => "timeout",
        CancelReason.HostShutdown => "host_shutdown",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>The state's protocol name, such as <c>"shutting_down"</c>.</summary>
    public static string WireName(this State state) => state switch
    {
        State.Hello => "hello",
        State.Negotiating => "negotiating",
        State.Selected => "selected",
        State.ShuttingDown => "shutting_down",
        State.Running => "running",
        State.Waiting => "waiting",
        State.Finishing => "finishing",
        State.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
