using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// The prepared program's public names and identities, used to map native
/// values. It is immutable once the program is prepared, and thread-safe.
/// </summary>
public sealed class ProgramData
{
    internal static readonly ImmutableHashSet<string> Builtins = ImmutableHashSet.Create(StringComparer.Ordinal, "true", "false", "some", "none", "cons", "nil");

    private readonly Dictionary<string, Hash> names;
    private readonly Dictionary<string, Hash> constructors;
    private readonly Dictionary<Hash, string> labels = new();
    private readonly Dictionary<Hash, string> primitives = new();

    /// <summary>Each declaration group's head identity and its member identities.</summary>
    private readonly Dictionary<Hash, HashSet<Hash>> members = new();

    internal ProgramData(IReadOnlyDictionary<string, Hash> names, IReadOnlyDictionary<string, Hash> constructors, IReadOnlyList<IReadOnlyList<Identities.Entry>> groups, Limits limits)
    {
        this.names = new Dictionary<string, Hash>(names, StringComparer.Ordinal);
        this.constructors = new Dictionary<string, Hash>(constructors, StringComparer.Ordinal);
        Limits = limits;
        foreach (KeyValuePair<string, Hash> entry in constructors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            labels[entry.Value] = entry.Key;
        }

        foreach (string name in new[] { "int", "real", "text", "bool", "hash", "list", "option" })
        {
            if (names.TryGetValue(name, out Hash? identity))
            {
                primitives[identity] = name;
            }
        }

        foreach (IReadOnlyList<Identities.Entry> group in groups)
        {
            if (group.Count > 0)
            {
                members[group[0].Hash] = new HashSet<Hash>(group.Skip(1).Select(entry => entry.Hash));
            }
        }
    }

    /// <summary>The session limits that bound mapped values.</summary>
    public Limits Limits { get; }

    /// <summary>
    /// The exact identity of a public type, constructor, effect, or term name
    /// in the prepared program and its discovered prelude.
    /// </summary>
    /// <exception cref="JacquardException">A binding failure for an unknown name.</exception>
    public Hash Identity(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return names.TryGetValue(Identities.DisplayToStore(name), out Hash? identity)
            ? identity
            : throw JacquardException.Binding("unknown name " + Identities.Excerpt(name));
    }

    /// <summary>The public name of a constructor or operation identity, or null.</summary>
    public string? Label(Hash identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return labels.TryGetValue(identity, out string? label) ? label : null;
    }

    internal Hash? Name(string name) => names.TryGetValue(name, out Hash? identity) ? identity : null;

    internal Hash? ConstructorId(string name) => constructors.TryGetValue(Identities.DisplayToStore(name), out Hash? identity) ? identity : null;

    /// <summary>A <see cref="Dynamic.Variant"/> after checking that the constructor exists.</summary>
    /// <exception cref="JacquardException">
    /// A binding failure for an unknown constructor, or for more fields than
    /// the v0 hard limit of collection items.
    /// </exception>
    public Dynamic Make(string name, params Dynamic[] fields)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length > Limits.Hard.MaxCollectionItems)
        {
            throw JacquardException.Binding("constructor of " + fields.Length.ToString(CultureInfo.InvariantCulture)
                + " fields exceeds the v0 hard limit of " + Limits.Hard.MaxCollectionItems.ToString(CultureInfo.InvariantCulture));
        }

        if (ConstructorId(name) is null)
        {
            throw JacquardException.Binding("unknown constructor " + Identities.Excerpt(name));
        }

        return new Dynamic.Variant(Identities.DisplayToStore(name), fields);
    }

    /// <summary>Encodes a native value, bounded by the selected limits.</summary>
    /// <exception cref="JacquardException">
    /// An invalid argument naming the value's location; a codec that throws
    /// anything else is reported so too, its exception kept as the inner
    /// exception.
    /// </exception>
    public Value Encode<T>(Codec<T> codec, T value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        try
        {
            return Encoder.Root(this, "value").Encode(codec, value);
        }
        catch (Exception error) when (Caller.Wraps(error))
        {
            throw CodecFailed("value", error);
        }
    }

    /// <summary>A caller's codec that threw while encoding at <paramref name="path"/>: an invalid argument quoting an excerpt, the exception kept.</summary>
    internal static JacquardException CodecFailed(string path, Exception error) =>
        JacquardException.InvalidAt(path, "its codec failed: " + Identities.Describe(error), error);

    /// <summary>
    /// Decodes a boundary value into a native value. The value is first
    /// measured, within the selected <c>max_value_nodes</c> and nesting
    /// limits, so a shared or deep value cannot make decoding unbounded;
    /// every value Core sends is within them.
    /// </summary>
    /// <exception cref="JacquardException">
    /// A binding failure when it does not decode; a codec that throws anything
    /// else is reported so too, its exception kept as the inner exception.
    /// </exception>
    public T Decode<T>(Codec<T> codec, Value value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(value);
        Measure(value);
        try
        {
            return codec.Decode(new Decoder(this), value);
        }
        catch (Exception error) when (Caller.Wraps(error))
        {
            throw JacquardException.Binding("value: its codec failed: " + Identities.Describe(error), error);
        }
    }

    /// <summary>
    /// Refuses a value with more nodes than <c>max_value_nodes</c>, or nested
    /// past the JSON depth, walking it with an explicit stack that never
    /// holds more than the node limit.
    /// </summary>
    private void Measure(Value value)
    {
        int nodes = 0;
        var stack = new Stack<(Value Value, int Level)>();
        stack.Push((value, 0));
        while (stack.Count > 0)
        {
            (Value current, int level) = stack.Pop();
            if (++nodes > Limits.MaxValueNodes)
            {
                throw JacquardException.Binding("value exceeds max_value_nodes");
            }

            // A value at the top of a frame's payload is at JSON depth 3, and each level adds 2.
            if (3L + (2L * level) > Limits.MaxJsonDepth)
            {
                throw JacquardException.Binding("value exceeds the v0 JSON depth");
            }

            IReadOnlyList<Value> children = current switch
            {
                TupleValue tuple => tuple.Items,
                ConstructorValue constructor => constructor.Arguments,
                _ => Array.Empty<Value>(),
            };
            if ((long)nodes + stack.Count + children.Count > Limits.MaxValueNodes)
            {
                throw JacquardException.Binding("value exceeds max_value_nodes");
            }

            foreach (Value child in children)
            {
                stack.Push((child, level + 1));
            }
        }
    }

    internal string? Primitive(BoundaryType expected) =>
        expected is NominalType nominal && primitives.TryGetValue(nominal.Identity, out string? name) ? name : null;

    internal bool IsNullary(Value value, string name) =>
        value is ConstructorValue constructor && constructor.Arguments.Count == 0 && string.Equals(name, Label(constructor.Identity), StringComparison.Ordinal);

    /// <summary>Walks a proper List, bounded by the collection limit.</summary>
    /// <exception cref="JacquardException">A binding failure when it is not a proper List within the limit.</exception>
    internal List<Value> ListItems(Value value)
    {
        var items = new List<Value>();
        Value cursor = value;
        while (true)
        {
            if (cursor is ConstructorValue constructor)
            {
                string? label = Label(constructor.Identity);
                if (constructor.Arguments.Count == 2 && label == "cons")
                {
                    if (items.Count >= Limits.MaxCollectionItems)
                    {
                        throw JacquardException.Binding("list exceeds the selected limit");
                    }

                    items.Add(constructor.Arguments[0]);
                    cursor = constructor.Arguments[1];
                    continue;
                }

                if (constructor.Arguments.Count == 0 && label == "nil")
                {
                    return items;
                }
            }

            throw JacquardException.Binding("expected a proper List");
        }
    }

    /// <summary>
    /// Checks that an encoded value has the checked boundary type, as far as
    /// the prepared metadata describes it: primitives, Lists, Options, and
    /// tuples exactly, and other data by its constructor's declaration group.
    /// Called only on measured values, so the walk is bounded by their depth
    /// and node count.
    /// </summary>
    [SuppressMessage("Maintainability", "CA1502", Justification = "One case per boundary type, as in the other SDKs.")]
    internal void Conform(Value value, BoundaryType expected, string path)
    {
        if (expected is TupleType tuple)
        {
            if (value is TupleValue values && values.Items.Count == tuple.Items.Count)
            {
                for (int i = 0; i < values.Items.Count; i++)
                {
                    Conform(values.Items[i], tuple.Items[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                }

                return;
            }

            throw Refuse(path, "a " + tuple.Items.Count.ToString(CultureInfo.InvariantCulture) + "-item tuple");
        }

        var nominal = (NominalType)expected;
        BoundaryType? child = nominal.Arguments.Count == 0 ? null : nominal.Arguments[0];
        string? primitive = Primitive(expected);
        if (primitive is null)
        {
            bool known = members.TryGetValue(nominal.Identity, out HashSet<Hash>? group);
            if (value is ConstructorValue constructor && (!known || group!.Contains(constructor.Identity)))
            {
                return;
            }

            throw Refuse(path, "a " + LabelOfType(nominal.Identity) + " value");
        }

        switch (primitive)
        {
            case "int":
                if (value is not IntValue)
                {
                    throw Refuse(path, "an integer");
                }

                break;
            case "real":
                if (value is not RealValue)
                {
                    throw Refuse(path, "a Real (double)");
                }

                break;
            case "text":
                if (value is not TextValue)
                {
                    throw Refuse(path, "text");
                }

                break;
            case "hash":
                if (value is not HashValue)
                {
                    throw Refuse(path, "a Hash");
                }

                break;
            case "bool":
                if (!IsNullary(value, "true") && !IsNullary(value, "false"))
                {
                    throw Refuse(path, "a bool");
                }

                break;
            case "list":
                List<Value> items;
                try
                {
                    items = ListItems(value);
                }
                catch (JacquardException)
                {
                    throw JacquardException.InvalidAt(path, "expected a List");
                }

                if (child is not null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        Conform(items[i], child, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    }
                }

                break;
            default:
                if (IsNullary(value, "none"))
                {
                    return;
                }

                if (value is ConstructorValue some && some.Arguments.Count == 1 && Label(some.Identity) == "some")
                {
                    if (child is not null)
                    {
                        Conform(some.Arguments[0], child, path);
                    }

                    return;
                }

                throw Refuse(path, "an Option");
        }
    }

    private static JacquardException Refuse(string path, string what) => JacquardException.InvalidAt(path, "expected " + what);

    private string LabelOfType(Hash identity)
    {
        foreach (KeyValuePair<string, Hash> entry in names.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (entry.Value.Equals(identity))
            {
                return entry.Key;
            }
        }

        return identity.Hex;
    }
}

/// <summary>Which exceptions from a caller's code the SDK wraps in a <see cref="JacquardException"/>.</summary>
internal static class Caller
{
    /// <summary>
    /// Every exception from caller code except the SDK's own (already a
    /// <see cref="JacquardException"/>) and running out of memory, which no
    /// wrapping could report.
    /// </summary>
    internal static bool Wraps(Exception error) => error is not JacquardException and not OutOfMemoryException;
}
