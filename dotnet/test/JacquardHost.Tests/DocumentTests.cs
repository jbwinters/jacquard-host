using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace JacquardLang.Host.Tests;

/// <summary>Files that must stay identical to their sources.</summary>
[TestFixture]
public sealed class DocumentTests
{
    /// <summary>The <c>dotnet/</c> directory this test assembly was built from.</summary>
    private static string DotnetRoot() =>
        typeof(DocumentTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "DotnetRoot").Value!;

    [Test]
    public void TheReadmeExampleIsTheCompiledOne()
    {
        // The README's example is Readme/Echo.cs, which a live test runs
        // against Core, minus its namespace line.
        string readme = File.ReadAllText(Path.Combine(DotnetRoot(), "README.md"));
        int start = readme.IndexOf("```csharp\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "README.md has no csharp example");
        int end = readme.IndexOf("```", start + 10, StringComparison.Ordinal);
        string example = readme[(start + 10)..end];
        string source = File.ReadAllText(Path.Combine(DotnetRoot(), "test", "JacquardHost.Tests", "Readme", "Echo.cs"));
        string body = source[(source.IndexOf('\n', source.IndexOf("namespace ", StringComparison.Ordinal)) + 1)..].TrimStart();
        Assert.That(example, Is.EqualTo(body), "README.md's example differs from test/JacquardHost.Tests/Readme/Echo.cs");
    }

    [Test]
    public void TheNoticeMatchesTheRepositoryNotice()
    {
        // A package or source copy of dotnet/ carries dotnet/NOTICE but not
        // the repository's root NOTICE, so the two must stay identical.
        string repository = Path.Combine(DotnetRoot(), "..", "NOTICE");
        if (!File.Exists(repository))
        {
            Assert.Ignore("outside a checkout");
        }

        Assert.That(File.ReadAllBytes(Path.Combine(DotnetRoot(), "NOTICE")), Is.EqualTo(File.ReadAllBytes(repository)), "dotnet/NOTICE differs from the repository NOTICE; copy it again");
    }
}
