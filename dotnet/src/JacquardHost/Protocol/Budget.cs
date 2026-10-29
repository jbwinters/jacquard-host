using System;

namespace JacquardLang.Host.Protocol;

/// <summary>
/// Counts nodes and checks collection and text sizes for one frame. Text is
/// charged against the frame too, so validating a value scans at most about
/// one frame of text, however many times a long text is referenced.
/// </summary>
public sealed class Budget
{
    private int nodes;

    /// <summary>The UTF-8 bytes of every text checked so far.</summary>
    private long textBytes;

    /// <summary>A fresh budget under <paramref name="limits"/>.</summary>
    public Budget(Limits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
    }

    /// <summary>The limits this budget charges against.</summary>
    public Limits Limits { get; }

    internal void Consume()
    {
        if (nodes >= Limits.MaxValueNodes)
        {
            throw ProtocolException.Exceeded("frame exceeds max_value_nodes");
        }

        nodes++;
    }

    internal void Count(int n, bool arguments)
    {
        if (n > Limits.MaxCollectionItems || (arguments && n > Limits.MaxArguments))
        {
            throw ProtocolException.Exceeded("collection exceeds selected limits");
        }
    }

    internal void Text(string text)
    {
        // A string's UTF-8 length is at least its length in chars, so these
        // refuse before scanning: the scan below covers at most one text and
        // one frame.
        if (text.Length > Limits.MaxTextBytes)
        {
            throw ProtocolException.Exceeded("Text exceeds max_text_bytes");
        }

        if (textBytes + text.Length > Limits.MaxFrameBytes)
        {
            throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
        }

        if (!JsonCodec.IsScalar(text))
        {
            throw ProtocolException.Malformed("a JSON string contains a lone surrogate");
        }

        long length = JsonCodec.Utf8Length(text);
        if (length > Limits.MaxTextBytes)
        {
            throw ProtocolException.Exceeded("Text exceeds max_text_bytes");
        }

        textBytes += length;
        if (textBytes > Limits.MaxFrameBytes)
        {
            throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
        }
    }
}
