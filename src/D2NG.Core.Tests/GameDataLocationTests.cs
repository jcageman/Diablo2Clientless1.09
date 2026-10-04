using D2NG.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests;

/// <summary>
/// The data files live beside the run configs rather than in the build, so which folder wins decides
/// whether a table regenerated after a realm patch is actually the one being read. The fallback to
/// the folder beside the assembly is what lets the test projects ship their own fixtures.
/// </summary>
public class GameDataLocationTests : IDisposable
{
    private readonly string _configured = Directory.CreateTempSubdirectory("d2ng-configured-").FullName;

    public void Dispose()
    {
        GameDataLocation.Use(null);
        Directory.Delete(_configured, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheConfiguredFolderIsPreferredOverTheOneBesideTheAssembly()
    {
        var name = $"{Guid.NewGuid():N}.txt";
        File.WriteAllText(Path.Combine(_configured, name), "configured");
        GameDataLocation.Use(_configured);

        Assert.True(GameDataLocation.TryResolve(name, out var path));
        Assert.Equal(_configured, Path.GetDirectoryName(path));
    }

    [Fact]
    public void AFileMissingFromTheConfiguredFolderFallsBackToTheBuild()
    {
        GameDataLocation.Use(_configured);

        Assert.True(GameDataLocation.TryResolve(GameDataLocation.ItemDataFile, out var path));
        Assert.Equal(GameDataLocation.DefaultDirectory, Path.GetDirectoryName(path));
    }

    [Fact]
    public void AFileInNeitherFolderIsReportedWithBothPlacesItLooked()
    {
        GameDataLocation.Use(_configured);

        var error = Assert.Throws<FileNotFoundException>(() => GameDataLocation.Resolve("not-a-real-table.txt"));
        Assert.Contains(_configured, error.Message, StringComparison.Ordinal);
        Assert.Contains(GameDataLocation.DefaultDirectory, error.Message, StringComparison.Ordinal);
    }

    /// <summary>The item tables are the ones a host has to check up front; nothing parses without them.</summary>
    [Fact]
    public void TheRequiredFilesAreTheItemTables()
    {
        Assert.Equal(
            [GameDataLocation.ItemDataFile, GameDataLocation.ItemPropertiesFile],
            GameDataLocation.RequiredFiles.Order());

        // The test project supplies both as fixtures, so nothing should be reported missing here.
        Assert.Empty(GameDataLocation.MissingRequiredFiles());
    }
}
