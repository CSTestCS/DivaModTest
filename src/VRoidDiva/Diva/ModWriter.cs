using System.Globalization;
using System.Text;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;

namespace VRoidDiva.Diva;

public sealed class ModInfo
{
    public string Name { get; init; }
    public string Author { get; init; }
    public string Description { get; init; }
    public string Version { get; init; } = "1.0";
}

/// <summary>
/// Writes a DIVA Mod Loader mod folder. Every database is a "mod_" database
/// containing only this mod's entries, so it coexists with other mods.
/// </summary>
public static class ModWriter
{
    public static IReadOnlyList<string> Write(string outputDirectory, ObjectSet objectSet, uint objectSetId,
        ModuleDefinition module, ModInfo info)
    {
        var written = new List<string>();
        string rom = Path.Combine(outputDirectory, "rom");
        string objsetDirectory = Path.Combine(rom, "objset");
        string langDirectory = Path.Combine(rom, "lang2");

        Directory.CreateDirectory(objsetDirectory);
        Directory.CreateDirectory(langDirectory);

        string baseName = module.ObjectSetName.ToLowerInvariant();
        string objFileName = $"{baseName}_obj.bin";
        string texFileName = $"{baseName}_tex.bin";
        string farcFileName = $"{baseName}.farc";

        // Object set + texture set, packed together like the stock objsets.
        objectSet.Format = BinaryFormat.FT;
        objectSet.TextureSet.Format = BinaryFormat.FT;

        var objStream = new MemoryStream();
        objectSet.Save(objStream, null, null, null, leaveOpen: true);
        var texStream = new MemoryStream();
        objectSet.TextureSet.Save(texStream, leaveOpen: true);

        string farcPath = Path.Combine(objsetDirectory, farcFileName);
        WriteFarc(farcPath, (objFileName, objStream), (texFileName, texStream));
        written.Add(farcPath);

        // Object database.
        var objectDatabase = new ObjectDatabase { Format = BinaryFormat.FT };
        var setInfo = new ObjectSetInfo
        {
            Name = module.ObjectSetName,
            Id = objectSetId,
            FileName = objFileName,
            TextureFileName = texFileName,
            ArchiveFileName = farcFileName
        };

        foreach (var obj in objectSet.Objects)
            setInfo.Objects.Add(new ObjectInfo { Id = obj.Id, Name = obj.Name });

        objectDatabase.ObjectSets.Add(setInfo);
        written.Add(SaveBinary(objectDatabase, Path.Combine(objsetDirectory, "mod_obj_db.bin")));

        // Texture database.
        var textureDatabase = new TextureDatabase { Format = BinaryFormat.FT };
        foreach (var texture in objectSet.TextureSet.Textures)
            textureDatabase.Textures.Add(new TextureInfo { Id = texture.Id, Name = texture.Name });
        written.Add(SaveBinary(textureDatabase, Path.Combine(objsetDirectory, "mod_tex_db.bin")));

        // Module and character item tables.
        string moduleTablePath = Path.Combine(rom, "mod_gm_module_tbl.farc");
        WriteFarc(moduleTablePath,
            ("gm_module_id.bin", new MemoryStream(ModuleTables.CreateModuleTable(module).ToBytes())));
        written.Add(moduleTablePath);

        string itemTablePath = Path.Combine(rom, "mod_chritm_prop.farc");
        WriteFarc(itemTablePath,
            (module.Character.ItemTableFileName, new MemoryStream(ModuleTables.CreateItemTable(module).ToBytes())));
        written.Add(itemTablePath);

        // Module name shown in the game.
        string stringArrayPath = Path.Combine(langDirectory, "mod_str_array.toml");
        File.WriteAllText(stringArrayPath, ModuleTables.CreateStringArray(module), new UTF8Encoding(false));
        written.Add(stringArrayPath);

        // DIVA Mod Loader manifest.
        string configPath = Path.Combine(outputDirectory, "config.toml");
        File.WriteAllText(configPath, CreateConfig(info), new UTF8Encoding(false));
        written.Add(configPath);

        return written;
    }

    internal static string CreateConfig(ModInfo info)
    {
        static string Q(string value) => $"\"{ModuleTables.EscapeToml(value ?? string.Empty)}\"";

        return string.Join("\n",
            "enabled = true",
            "include = [\".\"]",
            "",
            $"name = {Q(info.Name)}",
            $"description = {Q(info.Description)}",
            $"version = {Q(info.Version)}",
            $"date = {Q(DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))}",
            $"author = {Q(info.Author)}",
            "");
    }

    private static string SaveBinary(BinaryFile file, string path)
    {
        using (var stream = File.Create(path))
            file.Save(stream, leaveOpen: true);
        return path;
    }

    private static void WriteFarc(string path, params (string Name, MemoryStream Data)[] entries)
    {
        using var farc = new FarcArchive { IsCompressed = true, Alignment = 16 };

        foreach (var (name, data) in entries)
        {
            data.Seek(0, SeekOrigin.Begin);
            farc.Add(name, data, false, ConflictPolicy.Replace);
        }

        using var stream = File.Create(path);
        farc.Save(stream, leaveOpen: true);
    }
}
