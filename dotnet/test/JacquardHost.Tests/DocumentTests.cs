using System;
using System.Collections.Generic;
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
    public void TheReadmeExamplesAreTheCompiledOnes()
    {
        // The README's examples are the files below under Readme/, in this
        // order, minus their namespace lines; the live tests run them.
        string[] sources = { "Shipping.cs", "Incidents.cs", "Documents.cs", "Echo.cs" };
        string readme = File.ReadAllText(Path.Combine(DotnetRoot(), "README.md"));
        var examples = new List<string>();
        for (int start = readme.IndexOf("```csharp\n", StringComparison.Ordinal); start >= 0; start = readme.IndexOf("```csharp\n", start + 10, StringComparison.Ordinal))
        {
            int end = readme.IndexOf("```", start + 10, StringComparison.Ordinal);
            examples.Add(readme[(start + 10)..end]);
        }

        Assert.That(examples, Has.Count.EqualTo(sources.Length), "README.md's csharp examples");
        for (int i = 0; i < sources.Length; i++)
        {
            string source = File.ReadAllText(Path.Combine(DotnetRoot(), "test", "JacquardHost.Tests", "Readme", sources[i]));
            string body = source[(source.IndexOf('\n', source.IndexOf("namespace ", StringComparison.Ordinal)) + 1)..].TrimStart();
            Assert.That(examples[i], Is.EqualTo(body), "README.md's example " + (i + 1) + " differs from test/JacquardHost.Tests/Readme/" + sources[i]);
        }
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
