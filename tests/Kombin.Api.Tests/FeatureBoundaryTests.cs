using System.Text;

namespace Kombin.Api.Tests;

/// <summary>
/// Task B7: the backend is one project with a slice per feature (<c>Features/&lt;Feature&gt;</c>), and nothing but this test stops one feature from
/// reaching into another one's internals. It lists every reference from one feature to a type of another (<see cref="FeatureReferenceScanner"/>)
/// and fails for a reference that is not on <see cref="Allowed"/>, the frozen list of what existed when the test was written. It does not forbid
/// anything that exists; it stops the list from growing by accident. CLAUDE.md, "Feature boundaries", says what to do when it fails.
/// </summary>
public sealed class FeatureBoundaryTests
{
    /// <summary>Why a listed reference is there.</summary>
    internal enum Kind
    {
        /// <summary>Fine as it is: the feature is meant to call that code (a documented seam, a use case reused on purpose).</summary>
        Seam,

        /// <summary>Accepted for now, would be shared code today: the entry says where it should live.</summary>
        Legacy,
    }

    /// <summary>
    /// One line of the allow-list: <paramref name="From"/> uses these <paramref name="Types"/> of <paramref name="To"/> (names relative to the
    /// target feature: <c>GetProduct.GetProductEndpoint</c> is <c>Kombin.Api.Features.Catalog.GetProduct.GetProductEndpoint</c>), and <paramref name="Reason"/> says why.
    /// </summary>
    internal sealed record Entry(string From, string To, Kind Kind, string Reason, params string[] Types);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lazy<IReadOnlyDictionary<string, Type>> FeatureTypes = new(() => FeatureReferenceScanner.TypesOf(typeof(Program).Assembly));

    private static readonly Lazy<Dictionary<FeatureReference, List<string>>> Observed = new(() => FeatureReferenceScanner.ScanDirectory(FeaturesDirectory(), FeatureTypes.Value));

    private static string FeaturesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var features = Path.Combine(directory.FullName, "src", "Kombin.Api", "Features");

            if (File.Exists(Path.Combine(directory.FullName, "Kombin.slnx")) && Directory.Exists(features))
            {
                return features;
            }
        }

        throw new InvalidOperationException("Kombin.slnx not found above the test binaries.");
    }

    private static HashSet<FeatureReference> AllowedReferences() =>
        [.. FeatureBoundaryAllowList.Entries.SelectMany(entry => entry.Types.Select(type => new FeatureReference(entry.From, entry.To, $"{entry.To}.{type}")))];

    [Fact]
    public void NoFeatureReachesIntoAnotherOneBeyondTheAllowList()
    {
        var allowed = AllowedReferences();
        var added = Observed.Value.Where(pair => !allowed.Contains(pair.Key)).OrderBy(pair => pair.Key.From, StringComparer.Ordinal).ThenBy(pair => pair.Key.Type, StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0, Describe(added));
    }

    private static string Describe(List<KeyValuePair<FeatureReference, List<string>>> added)
    {
        if (added.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine("A feature now uses a type of another feature that is not on the allow-list (FeatureBoundaryAllowList). New references:");

        foreach (var group in added.GroupBy(pair => (pair.Key.From, pair.Key.To)))
        {
            text.AppendLine(string.Concat("  ", group.Key.From, " -> ", group.Key.To, ":"));

            foreach (var (reference, files) in group)
            {
                text.AppendLine(string.Concat("    ", reference.Type[(group.Key.To.Length + 1)..], "   (in ", string.Join(", ", files.Distinct().Take(3)), ")"));
            }
        }

        text.AppendLine("Do not add it to the list by reflex. Shared code lives in src/Kombin.Api/Common (see CLAUDE.md, \"Feature boundaries\"): move the type there, or call the other feature through an endpoint-level seam.");

        return text.ToString();
    }

    [Fact]
    public void TheAllowListHoldsNoReferenceThatNoLongerExists()
    {
        var observed = Observed.Value.Keys.ToHashSet();
        var stale = AllowedReferences().Where(reference => !observed.Contains(reference)).OrderBy(reference => reference.From, StringComparer.Ordinal).ThenBy(reference => reference.Type, StringComparer.Ordinal).ToList();

        Assert.True(
            stale.Count == 0,
            "These allow-list entries are no longer used; delete them (the list only shrinks): " + string.Join("; ", stale.Select(reference => $"{reference.From} -> {reference.Type}")));
    }

    [Fact]
    public void EveryAllowListEntryIsExplainedAndNamesARealTypeOfAnotherFeature()
    {
        var problems = new List<string>();

        foreach (var entry in FeatureBoundaryAllowList.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Reason))
            {
                problems.Add($"{entry.From} -> {entry.To}: no reason");
            }

            if (entry.From == entry.To)
            {
                problems.Add($"{entry.From} -> {entry.To}: a feature does not need an entry for itself");
            }

            problems.AddRange(entry.Types.Where(type => !FeatureTypes.Value.ContainsKey($"{entry.To}.{type}")).Select(type => $"{entry.From} -> {entry.To}.{type}: no such type"));
        }

        var duplicates = AllowedReferences().Count - FeatureBoundaryAllowList.Entries.Sum(entry => entry.Types.Length);
        Assert.True(duplicates == 0, "A reference is listed twice.");
        Assert.Empty(problems);
    }

    /// <summary>A file's feature is its folder, and the scan trusts that: a file whose namespace says another feature would hide its references.</summary>
    [Fact]
    public void EveryFileDeclaresTheNamespaceOfItsFolder()
    {
        var features = FeaturesDirectory();
        var wrong = new List<string>();

        foreach (var file in Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories).Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(features, file).Replace('\\', '/');
            var feature = relative.Split('/')[0];
            var declared = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(file), @"^\s*namespace\s+([\w.]+)", System.Text.RegularExpressions.RegexOptions.Multiline);

            if (declared.Success && declared.Groups[1].Value != $"Kombin.Api.Features.{feature}" && !declared.Groups[1].Value.StartsWith($"Kombin.Api.Features.{feature}.", StringComparison.Ordinal))
            {
                wrong.Add($"{relative}: {declared.Groups[1].Value}");
            }
        }

        Assert.True(wrong.Count == 0, "Files whose namespace is not their feature's: " + string.Join("; ", wrong));
    }

    /// <summary>The scan finds a reference that is known to exist, so a broken scanner cannot make every other test pass by finding nothing.</summary>
    [Fact]
    public void TheScannerFindsTheDocumentedIdentityToAnalyticsSeam()
    {
        Assert.Contains(new FeatureReference("Identity", "Analytics", "Analytics.IAnalyticsIdentityLinker"), Observed.Value.Keys);
        Assert.Contains(new FeatureReference("Admin", "Identity", "Identity.AccountDeletionRules"), Observed.Value.Keys);
        Assert.True(Observed.Value.Count > 50);
    }

    // -- the scanner itself -------------------------------------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, Type> Fake = new Dictionary<string, Type>
    {
        ["Catalog.CatalogErrors"] = typeof(object),
        ["Catalog.Jobs.ProductEmbeddingJob"] = typeof(object),
        ["Catalog.Jobs.Other"] = typeof(object),
        ["Profile.UserProfiles"] = typeof(object),
    };

    [Fact]
    public void AUsingBringsInOnlyTheTypesTheCodeNames()
    {
        const string Source = """
            using Kombin.Api.Features.Catalog;
            using Kombin.Api.Features.Catalog.Jobs;

            namespace Kombin.Api.Features.Wardrobe;

            internal sealed class Handler
            {
                public object Run() => CatalogErrors.Something ?? new ProductEmbeddingJob();
            }
            """;

        var found = FeatureReferenceScanner.Scan(Source, "Wardrobe", Fake);

        Assert.Equal(["Catalog.CatalogErrors", "Catalog.Jobs.ProductEmbeddingJob"], found.Order());
    }

    [Fact]
    public void CommentsStringsAndTheOwnFeatureAreNotReferences()
    {
        var source = string.Join(
            Environment.NewLine,
            "using Kombin.Api.Features.Catalog;",
            "using Kombin.Api.Features.Profile;",
            "namespace Kombin.Api.Features.Wardrobe;",
            "// CatalogErrors is only mentioned here, and /* UserProfiles */ here.",
            "internal sealed class Handler",
            "{",
            "    private const string Url = \"https://example.com/CatalogErrors\"; // UserProfiles",
            "    private const string Verbatim = @\"UserProfiles \"\" CatalogErrors\";",
            "    private const char Quote = '\"';",
            "    private const string Raw = \"\"\"\"\"\"\"\"\"CatalogErrors\"\"\"\"\"\"\"\"\"\";",
            "}");

        Assert.Empty(FeatureReferenceScanner.Scan(source, "Wardrobe", Fake));
        Assert.Empty(FeatureReferenceScanner.Scan("using Kombin.Api.Features.Catalog; class A { object x = CatalogErrors.Y; }", "Catalog", Fake));
    }

    [Fact]
    public void AnInterpolationHoleIsCodeAndAQualifiedNameNeedsNoUsing()
    {
        const string Source = """
            namespace Kombin.Api.Features.Wardrobe;

            internal sealed class Handler
            {
                public string A() => $"{(true ? "x" : "y")} and {Kombin.Api.Features.Catalog.CatalogErrors.Something}";

                public int B() => Kombin.Api.Features.Catalog.Jobs.ProductEmbeddingJob.MaxBackfillLimit;
            }
            """;

        Assert.Equal(["Catalog.CatalogErrors", "Catalog.Jobs.ProductEmbeddingJob"], FeatureReferenceScanner.Scan(Source, "Wardrobe", Fake).Order());
    }

    [Fact]
    public void UsingStaticAndAnAliasNameTheTypeDirectly()
    {
        const string Source = """
            using static Kombin.Api.Features.Profile.UserProfiles;
            using Errors = Kombin.Api.Features.Catalog.CatalogErrors;

            namespace Kombin.Api.Features.Wardrobe;

            internal sealed class Handler;
            """;

        Assert.Equal(["Catalog.CatalogErrors", "Profile.UserProfiles"], FeatureReferenceScanner.Scan(Source, "Wardrobe", Fake).Order());
    }

    [Fact]
    public void RazorFilesAreReadForQualifiedNamesOnly()
    {
        const string Source = "@model Kombin.Api.Features.Admin.Pages.CatalogModel\n<p>CatalogErrors @Kombin.Api.Features.Catalog.Jobs.ProductEmbeddingJob.MaxBackfillLimit</p>";

        Assert.Equal(["Catalog.Jobs.ProductEmbeddingJob"], FeatureReferenceScanner.Scan(Source, "Admin", Fake, razor: true));
    }
}
