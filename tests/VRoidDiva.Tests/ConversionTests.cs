using System.Numerics;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Textures;
using VRoidDiva.Diva;
using Xunit;

namespace VRoidDiva.Tests;

public class ConversionTests
{
    private const float Tolerance = 1e-4f;

    private sealed class ConvertedMod : IDisposable
    {
        public TempDirectory Temp { get; } = new();
        public string ModDirectory => Path.Combine(Temp.Path, "mod");
        public string Rom => Path.Combine(ModDirectory, "rom");
        public int ExitCode { get; set; }

        public void Dispose() => Temp.Dispose();
    }

    private static ConvertedMod Convert(bool vrm0, params string[] extraArgs)
    {
        var mod = new ConvertedMod();
        string vrmPath = Path.Combine(mod.Temp.Path, "model.vrm");
        File.WriteAllBytes(vrmPath, new SyntheticVrm { Vrm0 = vrm0 }.Build());
        string reference = Skeletons.WriteReferenceFarc(mod.Temp.Path);

        var args = new List<string>
        {
            "convert", vrmPath, "--reference", reference, "--out", mod.ModDirectory,
            "--module-id", "4321", "--cos", "777", "--objset-id", "45678"
        };
        args.AddRange(extraArgs);

        mod.ExitCode = Program.Main(args.ToArray());
        return mod;
    }

    private static MemoryStream ReadEntry(FarcArchive farc, string name)
    {
        using var entry = farc.Open(name, EntryStreamMode.MemoryStream);
        var memory = new MemoryStream();
        entry.Source.Seek(0, SeekOrigin.Begin);
        entry.Source.CopyTo(memory);
        memory.Position = 0;
        return memory;
    }

    private static (ObjectSet Objects, TextureSet Textures) LoadObjectSet(ConvertedMod mod)
    {
        using var farc = BinaryFile.Load<FarcArchive>(Path.Combine(mod.Rom, "objset", "mikitm4321.farc"));
        Assert.Equal(new[] { "mikitm4321_obj.bin", "mikitm4321_tex.bin" }, farc.FileNames.OrderBy(x => x));

        var textures = BinaryFile.Load<TextureSet>(ReadEntry(farc, "mikitm4321_tex.bin"));
        var objects = new ObjectSet();
        objects.Load(ReadEntry(farc, "mikitm4321_obj.bin"), textures, null);
        return (objects, textures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JointsLandOnTheDivaSkeleton(bool vrm0)
    {
        using var mod = Convert(vrm0);
        Assert.Equal(0, mod.ExitCode);

        var (objectSet, _) = LoadObjectSet(mod);
        var body = objectSet.Objects.Single(x => x.Name == "MIKITM4321_BODY_VRM");
        var mesh = Assert.Single(body.Meshes);

        foreach (var (humanBone, (_, divaPosition)) in Skeletons.Diva)
        {
            var actual = mesh.Positions[SyntheticVrm.FirstVertexOfJoint(humanBone)];
            Assert.True(Vector3.Distance(actual, divaPosition) < Tolerance,
                $"{humanBone}: expected {divaPosition}, got {actual} (VRM0={vrm0})");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelFacesTheSameWayAsTheGameCharacter(bool vrm0)
    {
        using var mod = Convert(vrm0);
        var (objectSet, _) = LoadObjectSet(mod);
        var mesh = objectSet.Objects[0].Meshes[0];

        // Heights (hips→head + hips→feet): VRM 0.60 + 0.82, DIVA 0.51 + 0.75.
        const float scale = 1.26f / 1.42f;

        // The head keeps the (identity) orientation of the vertical neck, so a vertex
        // 1 cm towards world +X ends up 1 cm * scale towards the character's left in
        // DIVA space for a VRM 1.0 model (faces +Z), and towards its right for VRM 0.x.
        int head = SyntheticVrm.FirstVertexOfJoint("head");
        var offset = mesh.Positions[head + 1] - mesh.Positions[head];
        var expected = new Vector3(vrm0 ? -0.01f * scale : 0.01f * scale, 0, 0);
        Assert.True(Vector3.Distance(offset, expected) < Tolerance, $"head offset {offset}, expected {expected}");

        var up = mesh.Positions[head + 2] - mesh.Positions[head];
        Assert.True(Vector3.Distance(up, new Vector3(0, 0.01f * scale, 0)) < Tolerance, $"head up offset {up}");
    }

    [Fact]
    public void LimbsAreStretchedToDivaLengths()
    {
        var divaElbow = Skeletons.Diva["leftLowerArm"].Position;
        int probe = SyntheticVrm.FirstVertexOfJoint("elbowOnUpperArm");

        using (var mod = Convert(false))
        {
            var mesh = LoadObjectSet(mod).Objects.Objects[0].Meshes[0];
            Assert.True(Vector3.Distance(mesh.Positions[probe], divaElbow) < Tolerance,
                $"stretched elbow at {mesh.Positions[probe]}, expected {divaElbow}");
        }

        // The aim helper is parented to the shoulder but must follow the upper arm.
        using (var mod = Convert(false))
        {
            var mesh = LoadObjectSet(mod).Objects.Objects[0].Meshes[0];
            int aimProbe = SyntheticVrm.FirstVertexOfJoint("elbowOnAimHelper");
            Assert.True(Vector3.Distance(mesh.Positions[aimProbe], divaElbow) < Tolerance,
                $"aim-helper elbow at {mesh.Positions[aimProbe]}, expected {divaElbow}");
        }

        using (var mod = Convert(false, "--no-stretch"))
        {
            var mesh = LoadObjectSet(mod).Objects.Objects[0].Meshes[0];
            var divaShoulder = Skeletons.Diva["leftUpperArm"].Position;
            var direction = Vector3.Normalize(divaElbow - divaShoulder);

            // Unstretched: the upper arm keeps its own (scaled) length but still points along the DIVA bone.
            float vrmLength = (0.40f - 0.14f) * 1.26f / 1.42f;
            var expected = divaShoulder + direction * vrmLength;
            Assert.True(Vector3.Distance(mesh.Positions[probe], expected) < Tolerance,
                $"unstretched elbow at {mesh.Positions[probe]}, expected {expected}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkinUsesTheGameBonesAndWeights(bool vrm0)
    {
        using var mod = Convert(vrm0);
        Assert.Equal(0, mod.ExitCode);

        var (objectSet, _) = LoadObjectSet(mod);
        var body = objectSet.Objects.Single(x => x.Name == "MIKITM4321_BODY_VRM");

        // Bone IDs and bind matrices must be the reference skeleton's own.
        var reference = new ReferenceSkeleton();
        reference.LoadFrom(Path.Combine(mod.Temp.Path, "mikitm001.farc"));
        foreach (var bone in body.Skin.Bones)
        {
            Assert.False(bone.IsEx);
            Assert.Equal(reference[bone.Name].Id, bone.Id);
            Assert.Equal(reference[bone.Name].InverseBindPose, bone.InverseBindPoseMatrix);
        }

        Assert.DoesNotContain(body.Skin.Bones, x => x.Name.EndsWith("_ex"));

        // The hair bone is not humanoid: it must follow the head.
        var mesh = body.Meshes[0];
        var subMesh = mesh.SubMeshes[0];
        int hairVertex = SyntheticVrm.FirstVertexOfJoint("hair");
        var hairBone = body.Skin.Bones[subMesh.BoneIndices[mesh.BlendIndices[hairVertex].X]];
        Assert.Equal("j_kao_wj", hairBone.Name);
        Assert.Equal(1f, mesh.BlendWeights[hairVertex].X, 4);

        // upperChest has no bone of its own in the reference (chest took kl_mune_b_wj).
        int upperChestVertex = SyntheticVrm.FirstVertexOfJoint("upperChest");
        var upperChestBone = body.Skin.Bones[subMesh.BoneIndices[mesh.BlendIndices[upperChestVertex].X]];
        Assert.Equal("kl_mune_b_wj", upperChestBone.Name);
    }

    [Fact]
    public void DatabasesAndTexturesAreConsistent()
    {
        using var mod = Convert(false);
        Assert.Equal(0, mod.ExitCode);

        var objectDatabase = BinaryFile.Load<ObjectDatabase>(Path.Combine(mod.Rom, "objset", "mod_obj_db.bin"));
        var set = Assert.Single(objectDatabase.ObjectSets);
        Assert.Equal("MIKITM4321", set.Name);
        Assert.Equal(45678u, set.Id);
        Assert.Equal("mikitm4321_obj.bin", set.FileName);
        Assert.Equal("mikitm4321_tex.bin", set.TextureFileName);
        Assert.Equal("mikitm4321.farc", set.ArchiveFileName);
        Assert.Equal(new[] { "MIKITM4321_BODY_VRM", "MIKITM4321_HIDDEN" }, set.Objects.Select(x => x.Name));

        var textureDatabase = BinaryFile.Load<TextureDatabase>(Path.Combine(mod.Rom, "objset", "mod_tex_db.bin"));
        var (objectSet, textureSet) = LoadObjectSet(mod);

        Assert.Equal(textureDatabase.Textures.Select(x => x.Id), objectSet.TextureIds);
        var texture = Assert.Single(textureSet.Textures);
        Assert.Equal(TextureFormat.DXT1, texture.Format);
        Assert.Equal(8, texture.Width);
        Assert.Equal(4, texture.MipMapCount);

        var material = Assert.Single(objectSet.Objects[0].Materials);
        Assert.Equal("SKIN", material.ShaderName);
        Assert.Equal(texture.Id, material.MaterialTextures[0].TextureId);
        Assert.Contains(objectSet.Objects[0].Materials[0].MaterialTextures, x => x.TextureId == textureDatabase.Textures[0].Id);

        // Hidden stand-in object: one degenerate triangle.
        var hidden = objectSet.Objects[1];
        Assert.NotNull(hidden.Skin);
        Assert.Single(hidden.Meshes);
        Assert.All(hidden.Meshes[0].Positions, p => Assert.Equal(hidden.Meshes[0].Positions[0], p));
    }

    [Fact]
    public void ModuleAndItemTablesDescribeTheModule()
    {
        using var mod = Convert(false, "--name", "My \"VRoid\" Girl");
        Assert.Equal(0, mod.ExitCode);

        using var moduleFarc = BinaryFile.Load<FarcArchive>(Path.Combine(mod.Rom, "mod_gm_module_tbl.farc"));
        var moduleTable = KeyValueFile.Parse(new StreamReader(ReadEntry(moduleFarc, "gm_module_id.bin")).ReadToEnd()).Values;
        Assert.Equal("4321", moduleTable["module.0.id"]);
        Assert.Equal("MIKU", moduleTable["module.0.chara"]);
        Assert.Equal("COS_777", moduleTable["module.0.cos"]);
        Assert.Equal("1", moduleTable["module.data_list.length"]);

        using var itemFarc = BinaryFile.Load<FarcArchive>(Path.Combine(mod.Rom, "mod_chritm_prop.farc"));
        string itemText = new StreamReader(ReadEntry(itemFarc, "mikitm_tbl.txt")).ReadToEnd();
        var items = KeyValueFile.Parse(itemText).Values;

        Assert.Equal("776", items["cos.0.id"]); // COS_777 is costume index 776
        Assert.Equal("4321", items["cos.0.item.0"]);
        Assert.Equal("4321", items["item.0.no"]);
        Assert.Equal("MIKITM4321", items["item.0.objset.0"]);
        Assert.Equal("1", items["item.0.type"]);
        Assert.Equal("9", items["item.0.sub_id"]);
        Assert.Equal("14", items["item.0.data.obj.length"]);

        // Every stock base part (RPK 1..14) is replaced exactly once; the model takes the chest.
        var parts = Enumerable.Range(0, 14)
            .ToDictionary(i => int.Parse(items[$"item.0.data.obj.{i}.rpk"]), i => items[$"item.0.data.obj.{i}.uid"]);
        Assert.Equal(Enumerable.Range(1, 14), parts.Keys.OrderBy(x => x));
        Assert.Equal("MIKITM4321_BODY_VRM", parts[3]);
        Assert.All(parts.Where(x => x.Key != 3), x => Assert.Equal("MIKITM4321_HIDDEN", x.Value));

        // Keys are sorted like the stock tables.
        var keys = itemText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x[..x.IndexOf('=')]).ToList();
        Assert.Equal(keys.OrderBy(x => x, StringComparer.Ordinal), keys);

        string strings = File.ReadAllText(Path.Combine(mod.Rom, "lang2", "mod_str_array.toml"));
        Assert.Equal("module.4321 = \"My \\\"VRoid\\\" Girl\"\n", strings);

        string config = File.ReadAllText(Path.Combine(mod.ModDirectory, "config.toml"));
        Assert.Contains("enabled = true", config);
        Assert.Contains("include = [\".\"]", config);
        Assert.DoesNotContain("dll", config);
    }

    [Fact]
    public void OtherCharactersUseTheirOwnItemTable()
    {
        using var mod = Convert(false, "--chara", "LUKA");
        Assert.Equal(0, mod.ExitCode);

        using var itemFarc = BinaryFile.Load<FarcArchive>(Path.Combine(mod.Rom, "mod_chritm_prop.farc"));
        Assert.Equal(new[] { "lukitm_tbl.txt" }, itemFarc.FileNames);
        Assert.True(File.Exists(Path.Combine(mod.Rom, "objset", "lukitm4321.farc")));
    }

    [Fact]
    public void InspectListsReferenceBones()
    {
        using var temp = new TempDirectory();
        string reference = Skeletons.WriteReferenceFarc(temp.Path);

        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(0, Program.Main(new[] { "inspect", reference }));
        }
        finally
        {
            Console.SetOut(original);
        }

        string text = output.ToString();
        Assert.Contains("kl_te_l_wj", text);
        Assert.DoesNotContain("n_hiji_l_wj_ex", text); // EX bones are not part of the shared skeleton
        Assert.Contains("leftUpperArm", text);
        Assert.Contains("MISSING", text); // e.g. fingers other than the middle one
    }

    [Fact]
    public void MissingReferenceIsAnError()
    {
        using var temp = new TempDirectory();
        string vrmPath = Path.Combine(temp.Path, "model.vrm");
        File.WriteAllBytes(vrmPath, new SyntheticVrm().Build());
        Assert.Equal(1, Program.Main(new[] { "convert", vrmPath, "--out", Path.Combine(temp.Path, "mod") }));
    }
}
