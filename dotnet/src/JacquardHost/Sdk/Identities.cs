using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>Public names and identities printed by Core's <c>hash</c> and <c>check --print-sigs</c> commands, and the SDK's text bounds.</summary>
internal static class Identities
{
    /// <summary>The longest name, in chars, the SDK looks up; a longer caller name is refused before it is looked up, hashed, or converted.</summary>
    internal const int NameLimit = 4096;

    /// <summary>The longest excerpt of caller text quoted in a message, in chars.</summary>
    internal const int ExcerptLimit = 64;

    /// <summary>
    /// The deepest type a signature may nest (a type with no arguments has
    /// depth 1): the protocol's value-depth limit (<c>max_json_depth</c>, 64),
    /// since no deeper value could be passed.
    /// </summary>
    internal const int SignatureDepth = 64;

    /// <summary>The most types one signature may name.</summary>
    internal const int SignatureNodes = 4096;

    /// <summary>The most tokens one signature may have, which bounds the parser's work before any type is counted.</summary>
    internal const int SignatureTokens = 4 * SignatureNodes;

    /// <summary>One public name and identity printed by <c>jacquard hash</c>.</summary>
    internal sealed record Entry(string Name, Hash Hash);

    /// <summary><paramref name="text"/> for a message: whole when short, otherwise its first 64 chars and its length.</summary>
    internal static string Excerpt(string? text)
    {
        if (text is null)
        {
            return "null";
        }

        if (text.Length <= ExcerptLimit)
        {
            return text;
        }

        int end = char.IsHighSurrogate(text[ExcerptLimit - 1]) ? ExcerptLimit - 1 : ExcerptLimit;
        return string.Concat(text.AsSpan(0, end), "... (" + text.Length.ToString(CultureInfo.InvariantCulture) + " chars)");
    }

    /// <summary>
    /// <paramref name="error"/> for a message: its type and an excerpt of its
    /// message, never its whole text. Reading a caller's exception message
    /// runs the caller's code; a failure there is described by type alone.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A caller's Message property may throw anything; the description falls back to the type.")]
    internal static string Describe(Exception error)
    {
        string? message;
        try
        {
            message = error.Message;
        }
        catch (Exception)
        {
            message = null;
        }

        string type = error.GetType().FullName ?? error.GetType().Name;
        return message is null ? type : type + ": " + Excerpt(message);
    }

    /// <summary>The refusal of a caller's <paramref name="what"/> name longer than <see cref="NameLimit"/>, quoting an excerpt.</summary>
    internal static string TooLong(string what, string name) =>
        what + " name " + Excerpt(name) + " exceeds " + NameLimit.ToString(CultureInfo.InvariantCulture) + " chars";

    /// <summary><paramref name="name"/>, refused when it is longer than <see cref="NameLimit"/>, before it is looked up or hashed.</summary>
    /// <exception cref="JacquardException">A binding failure quoting an excerpt.</exception>
    internal static string Bounded(string name, string what) =>
        name.Length > NameLimit ? throw JacquardException.Binding(TooLong(what, name)) : name;

    /// <summary>
    /// Converts a display name such as <c>WorkToday</c> to its store
    /// spelling, <c>work-today</c>; lowercase names are unchanged. A name
    /// longer than <see cref="NameLimit"/> is refused, never expanded.
    /// </summary>
    /// <exception cref="JacquardException">A binding failure for a name over the limit.</exception>
    internal static string DisplayToStore(string name)
    {
        Bounded(name, "a");
        if (name.Length == 0)
        {
            return name;
        }

        // A lone surrogate decodes as U+FFFD, which is not upper case.
        _ = Rune.DecodeFromUtf16(name, out Rune first, out _);
        if (!Rune.IsUpper(first))
        {
            return name;
        }

        var output = new StringBuilder(name.Length + 8);
        int index = 0;
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (index > 0 && Rune.IsUpper(rune))
            {
                output.Append('-');
            }

            output.Append(Rune.ToLowerInvariant(rune).ToString());
            index++;
        }

        return output.ToString();
    }

    /// <summary>The form used to look up effects and operations: store spelling, with <c>_</c> accepted for <c>-</c>.</summary>
    /// <exception cref="JacquardException">A binding failure for a name over the limit.</exception>
    internal static string Normal(string name, string what) => DisplayToStore(Bounded(name, what)).Replace('_', '-');

    /// <summary>Parses one <c>N:name hash</c> line, or returns null.</summary>
    private static (string Index, Entry Entry)? HashLine(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string index = line[..colon];
        string rest = line[(colon + 1)..];
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            return null;
        }

        string name = rest[..space];
        foreach (char c in index)
        {
            if (c < '0' || c > '9')
            {
                return null;
            }
        }

        if (name.Length == 0)
        {
            return null;
        }

        foreach (Rune rune in name.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                return null;
            }
        }

        string hex = rest[(space + 1)..];
        return Hash.LowerHex(hex, 64) ? (index, new Entry(name, new Hash(hex))) : null;
    }

    /// <summary>
    /// Parses <c>jacquard hash</c> output into declaration groups: a type or
    /// effect identity followed by its constructors or operations, in Core's
    /// printed order.
    /// </summary>
    /// <exception cref="JacquardException">A binding failure when the output names no identity.</exception>
    internal static List<List<Entry>> HashGroups(string output)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            (string Index, Entry Entry)? parsed = HashLine(line.Trim());
            if (parsed is { } found)
            {
                if (!groups.TryGetValue(found.Index, out List<Entry>? group))
                {
                    group = new List<Entry>();
                    groups[found.Index] = group;
                    order.Add(found.Index);
                }

                group.Add(found.Entry);
            }
        }

        if (order.Count == 0)
        {
            throw JacquardException.Binding("Core hash command returned no public identities");
        }

        var result = new List<List<Entry>>(order.Count);
        foreach (string index in order)
        {
            result.Add(groups[index]);
        }

        return result;
    }

    /// <summary>Refuses a public name that denotes different identities in different declaration groups.</summary>
    /// <exception cref="JacquardException">A binding failure naming the ambiguous name.</exception>
    internal static void CheckGroups(List<List<Entry>> groups)
    {
        var seen = new Dictionary<string, HashSet<Hash>>(StringComparer.Ordinal);
        foreach (List<Entry> group in groups)
        {
            var current = new Dictionary<string, HashSet<Hash>>(StringComparer.Ordinal);
            var names = new List<string>();
            foreach (Entry entry in group)
            {
                if (!current.TryGetValue(entry.Name, out HashSet<Hash>? set))
                {
                    set = new HashSet<Hash>();
                    current[entry.Name] = set;
                    names.Add(entry.Name);
                }

                set.Add(entry.Hash);
            }

            foreach (string name in names)
            {
                if (!seen.TryGetValue(name, out HashSet<Hash>? previous))
                {
                    previous = new HashSet<Hash>();
                    seen[name] = previous;
                }

                if (previous.Count > 0 && !previous.IsSupersetOf(current[name]))
                {
                    throw JacquardException.Binding("ambiguous public name " + Excerpt(name)
                        + " occurs in different declaration groups; use distinct names or explicit low-level identities");
                }

                previous.UnionWith(current[name]);
            }
        }
    }

    private static JacquardException TooDeep() =>
        JacquardException.Binding("signature nests types deeper than " + SignatureDepth.ToString(CultureInfo.InvariantCulture)
            + " levels, the protocol's value-depth limit (max_json_depth)");

    /// <summary>Tokens of a printed signature, at most a few per type, so the token count is bounded too.</summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        int at = 0;
        int end = text.Length;
        while (true)
        {
            while (at < end && char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            if (at == end)
            {
                return tokens;
            }

            if (tokens.Count >= SignatureTokens)
            {
                throw JacquardException.Binding("signature is longer than " + SignatureTokens.ToString(CultureInfo.InvariantCulture) + " tokens");
            }

            int length;
            char first = text[at];
            if (string.CompareOrdinal(text, at, "->{", 0, 3) == 0)
            {
                length = 3;
            }
            else if ("(),{}".Contains(first, StringComparison.Ordinal))
            {
                length = 1;
            }
            else if ((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z'))
            {
                length = 1;
                while (at + length < end)
                {
                    char c = text[at + length];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || ".?!-".Contains(c, StringComparison.Ordinal)))
                    {
                        break;
                    }

                    length++;
                }
            }
            else
            {
                throw JacquardException.Binding("unsupported signature near " + Excerpt(text.Substring(at, Math.Min(12, end - at))));
            }

            tokens.Add(text.Substring(at, length));
            at += length;
        }
    }

    /// <summary>
    /// Reads a closed v0 signature printed by <c>check --print-sigs</c>, such as
    /// <c>(List Line, Int) -&gt;{Inventory} Option Text</c>. Polymorphic and
    /// effect-polymorphic signatures are refused, and so is one with more than
    /// <see cref="SignatureTokens"/> tokens, one whose types nest deeper than
    /// <see cref="SignatureDepth"/> levels, or one with more than
    /// <see cref="SignatureNodes"/> types. The nesting is refused before the
    /// parser recurses past it, so no signature can exhaust the stack.
    /// </summary>
    /// <exception cref="JacquardException">A binding failure naming why the signature has no binding.</exception>
    internal static CallInterface ParseSignature(string text, IReadOnlyDictionary<string, Hash> names) => new Parser(Tokens(text), names).Parse();

    /// <summary>
    /// Refuses a type deeper than <see cref="SignatureDepth"/> or, with
    /// <paramref name="nodes"/>, more than <see cref="SignatureNodes"/> types.
    /// It walks with an explicit stack, so a deep type cannot exhaust it.
    /// </summary>
    private static void Measure(BoundaryType type, ref int nodes)
    {
        var stack = new Stack<(BoundaryType Type, int Level)>();
        stack.Push((type, 1));
        while (stack.Count > 0)
        {
            (BoundaryType current, int level) = stack.Pop();
            if (level > SignatureDepth)
            {
                throw TooDeep();
            }

            if (++nodes > SignatureNodes)
            {
                throw JacquardException.Binding("signature names more than " + SignatureNodes.ToString(CultureInfo.InvariantCulture) + " types");
            }

            IReadOnlyList<BoundaryType> children = current is NominalType nominal ? nominal.Arguments : ((TupleType)current).Items;
            foreach (BoundaryType child in children)
            {
                stack.Push((child, level + 1));
            }
        }
    }

    private sealed class Parser
    {
        private static readonly HashSet<string> Ends = new(StringComparer.Ordinal) { string.Empty, ",", ")", "}", "->{" };

        private readonly List<string> tokens;
        private readonly IReadOnlyDictionary<string, Hash> names;
        private int index;
        private int frames;

        internal Parser(List<string> tokens, IReadOnlyDictionary<string, Hash> names)
        {
            this.tokens = tokens;
            this.names = names;
        }

        private string Peek() => index < tokens.Count ? tokens[index] : string.Empty;

        private string Take(string expected)
        {
            string token = Peek();
            if (token.Length == 0 || (expected.Length > 0 && !string.Equals(token, expected, StringComparison.Ordinal)))
            {
                throw JacquardException.Binding("expected \"" + expected + "\", found \"" + Excerpt(token) + "\"");
            }

            index++;
            return token;
        }

        private Hash Identity(string display)
        {
            if (names.TryGetValue(DisplayToStore(display), out Hash? hash) || names.TryGetValue(display, out hash))
            {
                return hash;
            }

            throw JacquardException.Binding("no identity for " + Excerpt(display));
        }

        private List<BoundaryType> Items(string close)
        {
            var output = new List<BoundaryType>();
            if (string.Equals(Peek(), close, StringComparison.Ordinal))
            {
                index++;
                return output;
            }

            while (true)
            {
                output.Add(Type());
                if (string.Equals(Peek(), ",", StringComparison.Ordinal))
                {
                    index++;
                    if (string.Equals(Peek(), close, StringComparison.Ordinal))
                    {
                        index++;
                        return output;
                    }

                    continue;
                }

                Take(close);
                return output;
            }
        }

        private BoundaryType Atom()
        {
            string token = Take(string.Empty);
            if (token == "(")
            {
                List<BoundaryType> items = Items(")");
                if (items.Count == 1 && !string.Equals(tokens[index - 2], ",", StringComparison.Ordinal))
                {
                    return items[0];
                }

                return new TupleType(items);
            }

            if (!char.IsLetter(token[0]))
            {
                throw JacquardException.Binding("unsupported type \"" + Excerpt(token) + "\"");
            }

            return new NominalType(Identity(token));
        }

        private BoundaryType Type()
        {
            // Each nested parse is inside a type one level shallower (Core
            // prints no redundant parentheses), so this bounds the recursion
            // before it goes further.
            if (++frames > SignatureDepth)
            {
                throw TooDeep();
            }

            BoundaryType head = Atom();
            if (head is NominalType nominal)
            {
                var arguments = new List<BoundaryType>();
                while (!Ends.Contains(Peek()))
                {
                    arguments.Add(Atom());
                }

                head = new NominalType(nominal.Identity, arguments);
            }

            frames--;
            return head;
        }

        internal CallInterface Parse()
        {
            Take("(");
            List<BoundaryType> parameters = Items(")");
            Take("->{");
            var effects = new List<Hash>();
            if (!string.Equals(Peek(), "}", StringComparison.Ordinal))
            {
                while (true)
                {
                    Hash identity = Identity(Take(string.Empty));
                    if (!effects.Contains(identity))
                    {
                        effects.Add(identity);
                    }

                    if (!string.Equals(Peek(), ",", StringComparison.Ordinal))
                    {
                        break;
                    }

                    index++;
                }
            }

            Take("}");
            BoundaryType result = Type();
            if (Peek().Length != 0)
            {
                throw JacquardException.Binding("trailing signature tokens");
            }

            effects.Sort();
            int nodes = 0;
            foreach (BoundaryType parameter in parameters)
            {
                Measure(parameter, ref nodes);
            }

            Measure(result, ref nodes);
            return new CallInterface(parameters, result, effects);
        }
    }
}
