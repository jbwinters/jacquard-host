using System;

namespace JacquardLang.Host;

/// <summary>A protocol refusal carrying its frozen v0 diagnostic code, such as E1601.</summary>
public sealed class ProtocolException : Exception
{
    /// <summary>A refusal with a frozen code and a message without the code.</summary>
    public ProtocolException(string code, string detail)
        : base(code + ": " + detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <summary>The frozen diagnostic code, such as <c>"E1601"</c>.</summary>
    public string Code { get; }

    /// <summary>The message without the code.</summary>
    public string Detail { get; }

    /// <summary>E1601: malformed input.</summary>
    public static ProtocolException Malformed(string detail) => new("E1601", detail);

    /// <summary>E1602: a selected or hard limit is exceeded.</summary>
    public static ProtocolException Exceeded(string detail) => new("E1602", detail);

    /// <summary>E1604: a value or type with no v0 encoding.</summary>
    public static ProtocolException Unsupported(string detail) => new("E1604", detail);

    /// <summary>E1608: a message out of order for the session state.</summary>
    public static ProtocolException Order(string detail) => new("E1608", detail);
}
