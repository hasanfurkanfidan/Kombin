using System.Text;
using System.Text.RegularExpressions;

namespace Kombin.Api.Tests;

/// <summary>One feature using a type of another: <c>Recommendation</c> uses <c>Profile.StyleProfiles</c>. <see cref="Type"/> is relative to <c>Kombin.Api.Features</c>.</summary>
internal readonly record struct FeatureReference(string From, string To, string Type);

/// <summary>
/// Finds every place where code under <c>Features/&lt;A&gt;</c> names a type of <c>Features/&lt;B&gt;</c>, from the source text. Reflection on the
/// compiled assembly would miss what the compiler inlines (constants, enum members) and what a signature does not show, and an unused
/// <c>using</c> is not a dependency, so the text is read instead, like <c>CopyGlossaryTests</c> does:
/// <list type="bullet">
/// <item>comments are dropped and string literals emptied (their interpolation holes are code and stay), so a URL or a sentence is never a reference;</item>
/// <item>a <c>using</c> of another feature's namespace brings in its types: each type of that namespace whose name appears in the code is a reference;</item>
/// <item>a fully qualified name (<c>Kombin.Api.Features.B.Sub.Type.Member</c>, also in <c>.cshtml</c>), a <c>using static</c> and an alias name the type directly.</item>
/// </list>
/// A name in a namespace the feature imports is matched by simple name, so two imported namespaces that both hold a type called <c>Result</c> would
/// both be listed: a harmless false positive that the allow-list then simply records.
/// </summary>
internal static partial class FeatureReferenceScanner
{
    public const string Prefix = "Kombin.Api.Features.";

    /// <summary>Every top-level type of the feature namespaces, by full name without the prefix (<c>Catalog.Jobs.ProductEmbeddingJob</c>).</summary>
    public static IReadOnlyDictionary<string, Type> TypesOf(System.Reflection.Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => type.DeclaringType is null && type.Namespace is { } ns && ns.StartsWith(Prefix, StringComparison.Ordinal) && !type.Name.Contains('<', StringComparison.Ordinal))
            .ToDictionary(type => $"{type.Namespace![Prefix.Length..]}.{type.Name}", StringComparer.Ordinal);

    /// <summary>Scans <c>.cs</c> and <c>.cshtml</c> files below <paramref name="featuresDirectory"/>; the first folder of a file is its feature.</summary>
    public static Dictionary<FeatureReference, List<string>> ScanDirectory(string featuresDirectory, IReadOnlyDictionary<string, Type> types)
    {
        var found = new Dictionary<FeatureReference, List<string>>();

        foreach (var file in Directory.EnumerateFiles(featuresDirectory, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".cshtml", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(featuresDirectory, file).Replace('\\', '/');
            var feature = relative.Split('/')[0];

            foreach (var reference in Scan(File.ReadAllText(file), feature, types, razor: file.EndsWith(".cshtml", StringComparison.Ordinal)))
            {
                var key = new FeatureReference(feature, reference.Split('.')[0], reference);

                if (!found.TryGetValue(key, out var files))
                {
                    found[key] = files = [];
                }

                files.Add(relative);
            }
        }

        return found;
    }

    /// <summary>The types of other features the text names, relative to <c>Kombin.Api.Features</c>.</summary>
    public static IReadOnlySet<string> Scan(string source, string ownFeature, IReadOnlyDictionary<string, Type> types, bool razor = false)
    {
        var code = razor ? source : CodeText.Strip(source);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var typesByNamespace = types.Keys.GroupBy(name => name[..name.LastIndexOf('.')]).ToDictionary(group => group.Key, group => group.Select(name => name[(name.LastIndexOf('.') + 1)..]).ToList());

        if (!razor)
        {
            code = NamespaceDeclaration().Replace(code, " ");
            var identifiers = new HashSet<string>(IdentifierPattern().Matches(UsingDirective().Replace(code, " ")).Select(match => match.Value), StringComparer.Ordinal);

            foreach (Match directive in UsingDirective().Matches(code))
            {
                var name = directive.Groups["name"].Value;
                if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = name[Prefix.Length..];
                if (FeatureOf(relative) == ownFeature)
                {
                    continue;
                }

                if (types.ContainsKey(relative))
                {
                    // `using static ...Type;` or `using Alias = ...Type;`
                    found.Add(relative);
                }
                else if (typesByNamespace.TryGetValue(relative, out var names))
                {
                    foreach (var typeName in names.Where(identifiers.Contains))
                    {
                        found.Add($"{relative}.{typeName}");
                    }
                }
            }

            code = UsingDirective().Replace(code, " ");
        }

        foreach (Match qualified in QualifiedName().Matches(code))
        {
            var relative = qualified.Groups["path"].Value;
            if (FeatureOf(relative) == ownFeature)
            {
                continue;
            }

            // The longest prefix that is a type: `Catalog.Jobs.ProductEmbeddingJob.MaxBackfillLimit` -> `Catalog.Jobs.ProductEmbeddingJob`.
            var parts = relative.Split('.');
            var type = Enumerable.Range(1, parts.Length).Reverse().Select(count => string.Join('.', parts.Take(count))).FirstOrDefault(types.ContainsKey);

            // A namespace that names no type (a `@using` in a Razor file) is still a dependency on that feature: keep it visible.
            found.Add(type ?? relative);
        }

        return found;
    }

    private static string FeatureOf(string relative) => relative.Split('.')[0];

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?<name>[A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline)]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"\bnamespace\s+[\w.]+")]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(@"\bKombin\.Api\.Features\.(?<path>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)")]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentifierPattern();
}

/// <summary>Removes what is not code from C# source: comments, and the text of string and character literals (the code inside interpolation holes stays).</summary>
internal static class CodeText
{
    public static string Strip(string source)
    {
        var reader = new Reader(source);
        reader.ReadCode(insideHole: false);

        return reader.Output.ToString();
    }

    private sealed class Reader(string source)
    {
        private int _position;

        public StringBuilder Output { get; } = new(source.Length);

        private char Current => _position < source.Length ? source[_position] : '\0';

        private char Next => _position + 1 < source.Length ? source[_position + 1] : '\0';

        public void ReadCode(bool insideHole)
        {
            var depth = 0;

            while (_position < source.Length)
            {
                var c = Current;

                if (c == '/' && Next == '/')
                {
                    while (_position < source.Length && Current != '\n')
                    {
                        _position++;
                    }
                }
                else if (c == '/' && Next == '*')
                {
                    _position += 2;
                    while (_position < source.Length && !(Current == '*' && Next == '/'))
                    {
                        _position++;
                    }

                    _position += 2;
                    Output.Append(' ');
                }
                else if (c == '\'')
                {
                    ReadCharacter();
                }
                else if (StartsString(out var interpolated, out var verbatim, out var quotes))
                {
                    ReadString(interpolated, verbatim, quotes);
                }
                else
                {
                    if (insideHole && c == '{')
                    {
                        depth++;
                    }
                    else if (insideHole && c == '}')
                    {
                        if (depth == 0)
                        {
                            _position++;
                            return;
                        }

                        depth--;
                    }

                    Output.Append(c);
                    _position++;
                }
            }
        }

        /// <summary>At <c>"</c>, <c>$"</c>, <c>@"</c>, <c>$@"</c>, <c>@$"</c> or a raw string's quotes: consumes the opening and says which kind.</summary>
        private bool StartsString(out bool interpolated, out bool verbatim, out int quotes)
        {
            interpolated = verbatim = false;
            quotes = 1;

            if (Current is not ('"' or '$' or '@'))
            {
                return false;
            }

            var index = _position;

            // Up to three prefix characters (`$$$"""` is a raw string whose holes are three braces wide).
            while (index < source.Length && index - _position < 3 && source[index] is '$' or '@')
            {
                interpolated |= source[index] == '$';
                verbatim |= source[index] == '@';
                index++;
            }

            if (index >= source.Length || source[index] != '"')
            {
                // `@name` (a verbatim identifier) and the like are not strings.
                interpolated = verbatim = false;

                return false;
            }

            var run = 0;
            while (index + run < source.Length && source[index + run] == '"')
            {
                run++;
            }

            // `""` alone is an empty string; three or more quotes open a raw string.
            quotes = run >= 3 ? run : 1;
            _position = index + quotes;

            return true;
        }

        private void ReadString(bool interpolated, bool verbatim, int quotes)
        {
            Output.Append("\"\"");

            if (quotes >= 3)
            {
                // Raw string: ends at the same number of quotes. Interpolation holes of raw strings are not followed.
                while (_position < source.Length && !IsRun(quotes))
                {
                    _position++;
                }

                _position += quotes;

                return;
            }

            while (_position < source.Length)
            {
                var c = Current;

                if (c == '"')
                {
                    if (verbatim && Next == '"')
                    {
                        _position += 2;
                        continue;
                    }

                    _position++;

                    return;
                }

                if (c == '\\' && !verbatim)
                {
                    _position += 2;
                }
                else if (interpolated && c == '{')
                {
                    if (Next == '{')
                    {
                        _position += 2;
                        continue;
                    }

                    _position++;
                    Output.Append(' ');
                    ReadCode(insideHole: true);
                    Output.Append(' ');
                }
                else
                {
                    _position++;
                }
            }
        }

        private void ReadCharacter()
        {
            _position++;

            while (_position < source.Length && Current != '\'')
            {
                _position += Current == '\\' ? 2 : 1;
            }

            _position++;
            Output.Append("''");
        }

        private bool IsRun(int count)
        {
            for (var offset = 0; offset < count; offset++)
            {
                if (_position + offset >= source.Length || source[_position + offset] != '"')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
