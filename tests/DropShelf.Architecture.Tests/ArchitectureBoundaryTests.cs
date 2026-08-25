using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace DropShelf.Architecture.Tests;

public sealed class ArchitectureBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["DropShelf.Core"] = [],
            ["DropShelf.Infrastructure"] = ["DropShelf.Core"],
            ["DropShelf.Platform.Windows"] = ["DropShelf.Core"],
            ["DropShelf.Platform.macOS"] = ["DropShelf.Core"],
            ["DropShelf.App"] =
            [
                "DropShelf.Core",
                "DropShelf.Infrastructure",
                "DropShelf.Platform.Windows",
                "DropShelf.Platform.macOS",
            ],
        };

    [Fact]
    public void ProductionProjectsFollowTheDocumentedDependencyDirection()
    {
        string root = FindRepositoryRoot();
        string sourceDirectory = Path.Combine(root, "src");

        foreach ((string projectName, string[] expectedReferences) in AllowedProjectReferences)
        {
            string projectFile = Path.Combine(sourceDirectory, projectName, $"{projectName}.csproj");
            XDocument project = XDocument.Load(projectFile);
            string[] actualReferences =
            [
                .. project
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(
                    reference.Attribute("Include")?.Value.Replace('\\', '/')
                    ?? throw new InvalidOperationException($"ProjectReference in {projectFile} has no Include attribute.")))
                .Order(StringComparer.Ordinal),
            ];

            Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actualReferences);
        }
    }

    [Fact]
    public void CoreHasNoForbiddenFrameworkOrPlatformReferences()
    {
        string root = FindRepositoryRoot();
        string coreProjectPath = Path.Combine(root, "src", "DropShelf.Core", "DropShelf.Core.csproj");
        XDocument coreProject = XDocument.Load(coreProjectPath);
        string coreAssemblyPath = Path.Combine(AppContext.BaseDirectory, "DropShelf.Core.dll");
        Assembly coreAssembly = Assembly.LoadFrom(coreAssemblyPath);
        string[] forbiddenPrefixes = ["Avalonia", "Microsoft.Data.Sqlite", "DropShelf.Platform"];

        string[] forbiddenPackageReferences =
        [
            .. coreProject
                .Descendants("PackageReference")
                .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
                .Where(name => forbiddenPrefixes.Any(
                    prefix => name.StartsWith(prefix, StringComparison.Ordinal))),
        ];
        string[] forbiddenReferences =
        [
            .. coreAssembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(name => forbiddenPrefixes.Any(
                    prefix => name.StartsWith(prefix, StringComparison.Ordinal))),
        ];

        Assert.Empty(forbiddenPackageReferences);
        Assert.Empty(forbiddenReferences);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DropShelf.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root containing DropShelf.sln.");
    }
}
