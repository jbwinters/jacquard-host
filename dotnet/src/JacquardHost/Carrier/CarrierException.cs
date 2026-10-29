using System;

namespace JacquardLang.Host.Carrier;

/// <summary>
/// A carrier failure: either a loss (the worker could not start, closed a
/// pipe, exited unsuccessfully, was aborted, or outlived its deadline) or a
/// frame that violated the protocol.
/// </summary>
public sealed class CarrierException : Exception
{
    private CarrierException(string message, bool timeout, bool aborted, ProtocolException? protocolError)
        : base(message, protocolError)
    {
        IsTimeout = timeout;
        IsAborted = aborted;
        ProtocolError = protocolError;
    }

    /// <summary>Whether this is a loss rather than a protocol violation.</summary>
    public bool IsLoss => ProtocolError is null;

    /// <summary>Whether the worker outlived its lifetime deadline.</summary>
    public bool IsTimeout { get; }

    /// <summary>Whether the invocation was cancelled or the worker disposed.</summary>
    public bool IsAborted { get; }

    /// <summary>The protocol violation, or null for a loss.</summary>
    public ProtocolException? ProtocolError { get; }

    /// <summary>A carrier loss.</summary>
    public static CarrierException Loss(string message) => new(message, false, false, null);

    /// <summary>A frame that violated the protocol.</summary>
    public static CarrierException FromProtocol(ProtocolException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new CarrierException(error.Message, false, false, error);
    }

    internal static CarrierException Timeout(string message) => new(message, true, false, null);

    internal static CarrierException Aborted(string message) => new(message, false, true, null);
}
