using System;
using System.Collections.Generic;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host;

/// <summary>The kind of an invocation's result.</summary>
public enum ResultKind
{
    /// <summary>Core returned a value.</summary>
    Ok,

    /// <summary>Core returned an error result with diagnostics.</summary>
    Error,

    /// <summary>Core ended the session with a fatal frame.</summary>
    Fatal,

    /// <summary>Core acknowledged a shutdown.</summary>
    ShutdownAck,

    /// <summary>The worker was lost: spawn failure, early exit, deadline, or abort.</summary>
    CarrierLoss,

    /// <summary>Core sent a frame the protocol refuses.</summary>
    ProtocolError,

    /// <summary>The host refused its own frame.</summary>
    HostError,
}

/// <summary>The classification of an unsuccessful invocation.</summary>
public enum Failure
{
    /// <summary>The worker's lifetime deadline passed.</summary>
    DeadlineExceeded,

    /// <summary>The worker was lost or the call aborted.</summary>
    WorkerLost,

    /// <summary>An operation failed or its completion is unknown.</summary>
    OperationFailed,

    /// <summary>Any other failure.</summary>
    Other,
}

/// <summary>Core's frames and evidence beside the host's observations.</summary>
public sealed class InvocationResult
{
    internal InvocationResult(ResultKind kind)
    {
        Kind = kind;
    }

    /// <summary>The result kind.</summary>
    public ResultKind Kind { get; internal set; }

    /// <summary>The value of a successful outcome, else null.</summary>
    public Value? Value { get; internal set; }

    /// <summary>Core's diagnostics, for an error outcome or a fatal frame.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = Array.Empty<Diagnostic>();

    /// <summary>Core's evidence terminal name for an outcome, else null.</summary>
    public string? Terminal { get; internal set; }

    /// <summary>Core's evidence for an outcome, else null.</summary>
    public JsonObject? Evidence { get; internal set; }

    /// <summary>Every frame Core sent, in order.</summary>
    public IReadOnlyList<JsonObject> CoreFrames => CoreFrameList;

    /// <summary>Every frame the host sent, in order.</summary>
    public IReadOnlyList<JsonObject> HostFrames => HostFrameList;

    /// <summary>The effect requests Core made.</summary>
    public IReadOnlyList<EffectRequest> Requests { get; internal set; } = Array.Empty<EffectRequest>();

    /// <summary>The worker's exit status, when known and not ended by the host's SIGKILL.</summary>
    public int? ExitStatus { get; internal set; }

    /// <summary><c>"SIGKILL"</c> when the host killed the worker; see <see cref="Carrier.Worker.Signal"/>.</summary>
    public string? Signal { get; internal set; }

    /// <summary>The retained stderr prefix.</summary>
    public IReadOnlyList<byte> Stderr { get; internal set; } = Array.Empty<byte>();

    /// <summary>The host's evidence (<c>jacquard-host-dotnet-evidence-v0</c>); empty on success.</summary>
    public JsonObject HostEvidence { get; internal set; } = JsonObject.Empty;

    /// <summary>Whether Core returned a successful outcome.</summary>
    public bool Ok => Kind == ResultKind.Ok;

    internal List<JsonObject> CoreFrameList { get; } = new();

    internal List<JsonObject> HostFrameList { get; } = new();

    /// <summary>The first Core diagnostic code, else the host evidence code.</summary>
    public string Code
    {
        get
        {
            if (Diagnostics.Count > 0 && Diagnostics[0].Code.Length > 0)
            {
                return Diagnostics[0].Code;
            }

            return HostEvidence.Get("code")?.AsString() ?? string.Empty;
        }
    }

    /// <summary>A summary for people.</summary>
    public string Message
    {
        get
        {
            if (Ok)
            {
                return "Jacquard invocation succeeded";
            }

            string detail = Diagnostics.Count > 0
                ? Diagnostics[0].Summary + " " + Diagnostics[0].Cause
                : HostEvidence.Get("detail")?.AsString() ?? KindName(Kind);
            string code = Code;
            return (code.Length == 0 ? KindName(Kind) : code) + ": " + detail;
        }
    }

    /// <summary>The kind's name, as the other adapters spell it, such as <c>"carrier_loss"</c>.</summary>
    public static string KindName(ResultKind kind) => kind switch
    {
        ResultKind.Ok => "ok",
        ResultKind.Error => "error",
        ResultKind.Fatal => "fatal",
        ResultKind.ShutdownAck => "shutdown_ack",
        ResultKind.CarrierLoss => "carrier_loss",
        ResultKind.ProtocolError => "protocol_error",
        ResultKind.HostError => "host_error",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The value, or an <see cref="InvocationException"/> classifying the failure.</summary>
    /// <exception cref="InvocationException">The invocation did not succeed.</exception>
    public Value ValueOrThrow()
    {
        if (Ok && Value is not null)
        {
            return Value;
        }

        throw new InvocationException(this);
    }

    /// <inheritdoc/>
    public override string ToString() => "InvocationResult[" + KindName(Kind) + ", " + Message + "]";
}

/// <summary>An unsuccessful invocation.</summary>
public sealed class InvocationException : Exception
{
    internal InvocationException(InvocationResult result)
        : base(result.Message)
    {
        Result = result;
    }

    /// <summary>The result, with Core's frames and the host's evidence.</summary>
    public InvocationResult Result { get; }

    /// <summary>The classification.</summary>
    public Failure Failure
    {
        get
        {
            if (Json.Of("deadline").Equals(Result.HostEvidence.Get("reason")))
            {
                return Failure.DeadlineExceeded;
            }

            if (Result.Kind == ResultKind.CarrierLoss)
            {
                return Failure.WorkerLost;
            }

            return Result.Terminal is "host_failure" or "completion_unknown" ? Failure.OperationFailed : Failure.Other;
        }
    }
}
