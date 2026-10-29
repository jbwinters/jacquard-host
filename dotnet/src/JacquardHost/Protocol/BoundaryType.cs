using System;
using System.Collections.Generic;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>A closed v0 boundary type: <see cref="NominalType"/> or <see cref="TupleType"/>.</summary>
public abstract record BoundaryType
{
    private protected BoundaryType()
    {
    }

    internal static BoundaryType[] Copy(IEnumerable<BoundaryType> items, string name)
    {
        ArgumentNullException.ThrowIfNull(items, name);
        BoundaryType[] copy = items.ToArray();
        foreach (BoundaryType item in copy)
        {
            ArgumentNullException.ThrowIfNull(item, name);
        }

        return copy;
    }

    internal static bool Same(BoundaryType[] a, BoundaryType[] b) => a.Length == b.Length && a.Zip(b).All(pair => pair.First.Equals(pair.Second));

    internal static int HashItems(BoundaryType[] items)
    {
        HashCode hash = default;
        hash.Add(items.Length);
        foreach (BoundaryType item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A type identity applied to arguments.</summary>
public sealed record NominalType : BoundaryType
{
    private readonly BoundaryType[] arguments;

    /// <summary>A nominal type holding a copy of <paramref name="arguments"/>.</summary>
    public NominalType(Hash identity, IEnumerable<BoundaryType> arguments)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        this.arguments = Copy(arguments, nameof(arguments));
    }

    /// <summary>A nominal type with no arguments.</summary>
    public NominalType(Hash identity)
        : this(identity, Array.Empty<BoundaryType>())
    {
    }

    /// <summary>The type identity.</summary>
    public Hash Identity { get; }

    /// <summary>The type arguments.</summary>
    public IReadOnlyList<BoundaryType> Arguments => arguments;

    /// <inheritdoc/>
    public bool Equals(NominalType? other) => other is not null && Identity.Equals(other.Identity) && Same(arguments, other.arguments);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Identity, HashItems(arguments));
}

/// <summary>A positional product type.</summary>
public sealed record TupleType : BoundaryType
{
    private readonly BoundaryType[] items;

    /// <summary>A tuple type holding a copy of <paramref name="items"/>.</summary>
    public TupleType(IEnumerable<BoundaryType> items)
    {
        this.items = Copy(items, nameof(items));
    }

    /// <summary>A tuple type of <paramref name="items"/>.</summary>
    public TupleType(params BoundaryType[] items)
        : this((IEnumerable<BoundaryType>)items)
    {
    }

    /// <summary>The item types.</summary>
    public IReadOnlyList<BoundaryType> Items => items;

    /// <inheritdoc/>
    public bool Equals(TupleType? other) => other is not null && Same(items, other.items);

    /// <inheritdoc/>
    public override int GetHashCode() => HashItems(items);
}

/// <summary>The checked callable signature sent with an invocation.</summary>
public sealed record CallInterface
{
    private readonly BoundaryType[] parameters;
    private readonly Hash[] effects;

    /// <summary>A signature holding copies of <paramref name="parameters"/> and <paramref name="effects"/>.</summary>
    public CallInterface(IEnumerable<BoundaryType> parameters, BoundaryType result, IEnumerable<Hash> effects)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(effects);
        this.parameters = BoundaryType.Copy(parameters, nameof(parameters));
        Result = result;
        this.effects = effects.ToArray();
        foreach (Hash effect in this.effects)
        {
            ArgumentNullException.ThrowIfNull(effect, nameof(effects));
        }
    }

    /// <summary>The parameter types.</summary>
    public IReadOnlyList<BoundaryType> Parameters => parameters;

    /// <summary>The result type.</summary>
    public BoundaryType Result { get; }

    /// <summary>The effect row: a strictly sorted set of effect identities.</summary>
    public IReadOnlyList<Hash> Effects => effects;

    /// <inheritdoc/>
    public bool Equals(CallInterface? other) =>
        other is not null && BoundaryType.Same(parameters, other.parameters) && Result.Equals(other.Result) && effects.SequenceEqual(other.effects);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(BoundaryType.HashItems(parameters), Result, effects.Length);
}

/// <summary>An exact effect operation granted for the invocation.</summary>
/// <param name="Effect">The effect identity.</param>
/// <param name="Operation">The operation identity.</param>
public sealed record OperationEntry(Hash Effect, Hash Operation) : IComparable<OperationEntry>
{
    /// <summary>The effect identity.</summary>
    public Hash Effect { get; } = Effect ?? throw new ArgumentNullException(nameof(Effect));

    /// <summary>The operation identity.</summary>
    public Hash Operation { get; } = Operation ?? throw new ArgumentNullException(nameof(Operation));

    /// <inheritdoc/>
    public int CompareTo(OperationEntry? other)
    {
        if (other is null)
        {
            return 1;
        }

        int order = Effect.CompareTo(other.Effect);
        return order != 0 ? order : Operation.CompareTo(other.Operation);
    }
}
