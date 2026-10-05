using Xunit;

namespace VRoidDiva.Tests;

public class GameLocatorTests
{
    private static string MakeGame(string library)
    {
        string game = Path.Combine(library, "steamapps", "common", GameLocator.SteamFolderName);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, GameLocator.GameExecutable), "");
        return game;
    }

    [Fact]
    public void FindsTheGameInASecondarySteamLibrary()
    {
        using var temp = new TempDirectory();
        string steam = Path.Combine(temp.Path, "Steam");
        string otherLibrary = Path.Combine(temp.Path, "Games", "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        string game = MakeGame(otherLibrary);

        // libraryfolders.vdf escapes backslashes; forward slashes keep the test portable.
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path"  "{{steam.Replace("\\", "\\\\")}}" }
                "1" { "path"  "{{otherLibrary.Replace("\\", "\\\\")}}" "apps" { "1761390" "123" } }
            }
            """);

        Assert.Equal(game, GameLocator.FindGameFolder(new[] { steam }));
    }

    [Fact]
    public void ReturnsNullWhenTheGameIsMissing()
    {
        using var temp = new TempDirectory();
        Assert.Null(GameLocator.FindGameFolder(new[] { temp.Path, Path.Combine(temp.Path, "nope") }));
    }

    [Fact]
    public void ReadsTheModsFolderFromTheModLoaderConfig()
    {
        using var temp = new TempDirectory();
        string game = MakeGame(temp.Path);

        Assert.Equal(Path.Combine(game, "mods"), GameLocator.ModsFolder(game));
        Assert.False(GameLocator.IsModLoaderInstalled(game));

        File.WriteAllText(Path.Combine(game, "dinput8.dll"), "");
        File.WriteAllText(Path.Combine(game, "config.toml"), "enabled = true\nmods = \"my_mods\"\n");
        Assert.True(GameLocator.IsModLoaderInstalled(game));
        Assert.Equal(Path.Combine(game, "my_mods"), GameLocator.ModsFolder(game));
    }

    [Fact]
    public void PrefersTheMainCpk()
    {
        using var temp = new TempDirectory();
        string game = MakeGame(temp.Path);
        Assert.Null(GameLocator.FindReferenceCpk(game));

        File.WriteAllText(Path.Combine(game, "diva_dlc00.cpk"), "");
        Assert.EndsWith("diva_dlc00.cpk", GameLocator.FindReferenceCpk(game));

        File.WriteAllText(Path.Combine(game, "diva_main.cpk"), "");
        Assert.EndsWith("diva_main.cpk", GameLocator.FindReferenceCpk(game));
    }

    [Theory]
    [InlineData("Miku: \"Snow\" / Winter?", "Miku_ _Snow_ _ Winter_")]
    [InlineData("  ...  ", "VRoid Module")]
    [InlineData("初音ミク", "初音ミク")]
    public void FolderNamesAreSafe(string name, string expected) =>
        Assert.Equal(expected, GameLocator.SafeFolderName(name));
}
