using System.Numerics;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;

namespace VRoidDiva.Diva;

public sealed class ReferenceBone
{
    public string Name;
    public uint Id;
    public Matrix4x4 InverseBindPose;
    public Matrix4x4 BindPose;
    public string ParentName;
    public string SourceObject;

    public Vector3 Position => BindPose.Translation;
}

/// <summary>
/// The DIVA character skeleton in its bind pose, harvested from the skins of
/// existing game objects (for example a stock Miku module). Using the game's
/// own bone IDs and inverse bind matrices guarantees the generated object is
/// driven correctly by the game's motions.
/// </summary>
public sealed class ReferenceSkeleton
{
    public Dictionary<string, ReferenceBone> Bones { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LoadedObjects { get; } = new();

    public bool Contains(string name) => Bones.ContainsKey(name);

    public ReferenceBone this[string name] => Bones[name];

    /// <summary>
    /// Stock Miku archives tried inside a .cpk when no entry is named: the default
    /// module's hair/head, body, hands and shoes items.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCpkEntries = new[]
    {
        "rom/objset/mikitm001.farc", "rom/objset/mikitm301.farc",
        "rom/objset/mikitm501.farc", "rom/objset/mikitm681.farc"
    };

    /// <summary>
    /// Loads bones from a .farc object set archive, a raw *_obj.bin, or a .cpk
    /// (in which case <paramref name="cpkEntries"/> selects the archives inside).
    /// </summary>
    public void LoadFrom(string path, IReadOnlyList<string> cpkEntries = null, Action<string> log = null)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();

        switch (extension)
        {
            case ".farc":
                using (var farc = BinaryFile.Load<FarcArchive>(path))
                    LoadFromArchive(farc, Path.GetFileName(path));
                break;

            case ".bin":
                using (var stream = File.OpenRead(path))
                    AddObjectSet(BinaryFile.Load<ObjectSet>(stream), Path.GetFileName(path));
                break;

            case ".cpk":
                LoadFromCpk(path, cpkEntries is { Count: > 0 } ? cpkEntries : DefaultCpkEntries, log);
                break;

            default:
                throw new ArgumentException($"Unsupported reference file '{path}'. Use a .farc, *_obj.bin or .cpk file.");
        }
    }

    private void LoadFromCpk(string path, IReadOnlyList<string> entryNames, Action<string> log)
    {
        using var cpk = BinaryFile.Load<CpkArchive>(path);
        int loaded = 0;

        foreach (string entryName in entryNames)
        {
            string normalized = entryName.Replace('\\', '/').TrimStart('/');
            string entry = cpk.FileNames.FirstOrDefault(x =>
                               x.Replace('\\', '/').TrimStart('/').Equals(normalized, StringComparison.OrdinalIgnoreCase))
                           ?? cpk.FileNames.FirstOrDefault(x =>
                               x.Replace('\\', '/').EndsWith("/" + Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                log?.Invoke($"  '{entryName}' is not in {Path.GetFileName(path)}, skipped");
                continue;
            }

            using var entryStream = cpk.Open(entry, EntryStreamMode.MemoryStream);
            var memory = CopyToMemory(entryStream.Source);
            using var farc = BinaryFile.Load<FarcArchive>(memory);
            LoadFromArchive(farc, entry);
            loaded++;
        }

        if (loaded == 0)
        {
            throw new FileNotFoundException(
                $"None of [{string.Join(", ", entryNames)}] were found inside {Path.GetFileName(path)}. " +
                "Extract the object set with Miku Miku Model and pass the .farc instead.");
        }
    }

    private void LoadFromArchive(FarcArchive farc, string label)
    {
        foreach (string fileName in farc.FileNames.Where(x => x.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase)))
        {
            using var entryStream = farc.Open(fileName, EntryStreamMode.MemoryStream);
            var memory = CopyToMemory(entryStream.Source);
            AddObjectSet(BinaryFile.Load<ObjectSet>(memory), $"{label}/{fileName}");
        }
    }

    private static MemoryStream CopyToMemory(Stream source)
    {
        // Copy out of the archive's EntryStream wrapper: it does not honour read offsets.
        var memory = new MemoryStream();
        source.Seek(0, SeekOrigin.Begin);
        source.CopyTo(memory);
        memory.Seek(0, SeekOrigin.Begin);
        return memory;
    }

    public void AddObjectSet(ObjectSet objectSet, string label)
    {
        foreach (var obj in objectSet.Objects.Where(x => x.Skin != null))
        {
            LoadedObjects.Add($"{label}:{obj.Name}");

            foreach (var bone in obj.Skin.Bones)
            {
                // EX bones are generated per object (osage, expressions, constraints);
                // only real skeleton bones can be shared between objects.
                if (bone.IsEx || Bones.ContainsKey(bone.Name))
                    continue;

                if (!Matrix4x4.Invert(bone.InverseBindPoseMatrix, out var bindPose))
                    continue;

                Bones[bone.Name] = new ReferenceBone
                {
                    Name = bone.Name,
                    Id = bone.Id,
                    InverseBindPose = bone.InverseBindPoseMatrix,
                    BindPose = bindPose,
                    ParentName = bone.Parent?.Name,
                    SourceObject = obj.Name
                };
            }
        }
    }
}
