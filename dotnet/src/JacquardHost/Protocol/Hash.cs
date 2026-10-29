using System;

namespace JacquardLang.Host.Protocol;

/// <summary>A HASH_V0 identity: exactly 64 lowercase hexadecimal digits.</summary>
public sealed record Hash : IComparable<Hash>
{
    private const string Message = "HASH_V0 must be exactly 64 lowercase hexadecimal digits";

    /// <summary>Validates; prefer <see cref="Parse"/>, which reports E1601.</summary>
    /// <exception cref="ArgumentException"><paramref name="hex"/> is not 64 lowercase hexadecimal digits.</exception>
    public Hash(string hex)
    {
        if (!LowerHex(hex, 64))
        {
            throw new ArgumentException(Message, nameof(hex));
        }

        Hex = hex;
    }

    /// <summary>The 64 lowercase hexadecimal digits.</summary>
    public string Hex { get; }

    /// <summary>Validates a HASH_V0 identity.</summary>
    /// <exception cref="ProtocolException">E1601 when <paramref name="text"/> is not 64 lowercase hexadecimal digits.</exception>
    public static Hash Parse(string? text)
    {
        if (!LowerHex(text, 64))
        {
            throw ProtocolException.Malformed(Message);
        }

        return new Hash(text!);
    }

    internal static Hash FromJson(Json? value) => Parse(value?.AsString());

    internal static bool LowerHex(string? text, int length)
    {
        if (text is null || text.Length != length)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(Hash? other) => other is null ? 1 : string.CompareOrdinal(Hex, other.Hex);

    /// <inheritdoc/>
    public bool Equals(Hash? other) => other is not null && string.Equals(Hex, other.Hex, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Hex);

    /// <inheritdoc/>
    public override string ToString() => Hex;
}
