using System.Text.RegularExpressions;

namespace VRoidDiva;

/// <summary>Finds the Mega Mix+ install and the files the converter needs inside it.</summary>
public static class GameLocator
{
    public const string SteamFolderName = "Hatsune Miku Project DIVA Mega Mix Plus";
    public const string GameExecutable = "DivaMegaMix.exe";

    /// <summary>Searches the Steam libraries for the game. Returns null when it isn't found.</summary>
    public static string FindGameFolder(IEnumerable<string> steamRoots = null)
    {
        foreach (string library in FindSteamLibraries(steamRoots ?? DefaultSteamRoots()))
        {
            string candidate = Path.Combine(library, "steamapps", "common", SteamFolderName);
            if (IsGameFolder(candidate))
                return candidate;
        }

        return null;
    }

    public static bool IsGameFolder(string folder) =>
        !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, GameExecutable));

    /// <summary>DIVA Mod Loader is installed when its dinput8.dll sits next to the game.</summary>
    public static bool IsModLoaderInstalled(string gameFolder) =>
        File.Exists(Path.Combine(gameFolder, "dinput8.dll"));

    /// <summary>The game's main .cpk, used as the skeleton reference.</summary>
    public static string FindReferenceCpk(string gameFolder)
    {
        string main = Path.Combine(gameFolder, "diva_main.cpk");
        if (File.Exists(main))
            return main;

        return Directory.Exists(gameFolder)
            ? Directory.EnumerateFiles(gameFolder, "*.cpk")
                .OrderByDescending(x => Path.GetFileName(x).Contains("main", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
    }

    /// <summary>The mods folder from DIVA Mod Loader's config.toml ("mods" by default).</summary>
    public static string ModsFolder(string gameFolder)
    {
        string folder = "mods";
        string config = Path.Combine(gameFolder, "config.toml");

        if (File.Exists(config))
        {
            var match = Regex.Match(File.ReadAllText(config), @"^\s*mods\s*=\s*""([^""]+)""", RegexOptions.Multiline);
            if (match.Success)
                folder = match.Groups[1].Value;
        }

        return Path.IsPathRooted(folder) ? folder : Path.Combine(gameFolder, folder);
    }

    /// <summary>A folder name made from the module name (Windows-safe).</summary>
    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        string cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "VRoid Module" : cleaned;
    }

    internal static IEnumerable<string> FindSteamLibraries(IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in steamRoots.Where(Directory.Exists))
        {
            if (seen.Add(Path.GetFullPath(root)))
                yield return root;

            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), @"""path""\s+""([^""]+)"""))
            {
                string library = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(library) && seen.Add(Path.GetFullPath(library)))
                    yield return library;
            }
        }
    }

    private static IEnumerable<string> DefaultSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            if (ReadRegistry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath") is string user)
                yield return user.Replace('/', '\\');
            if (ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") is string machine)
                yield return machine;
            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
        }
    }

    private static string ReadRegistry(string key, string value)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Microsoft.Win32.Registry.GetValue(key, value, null) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
