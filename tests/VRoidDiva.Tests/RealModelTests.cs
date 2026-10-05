using MikuMikuLibrary.Archives;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Textures;
using VRoidDiva.Vrm;
using Xunit;
using Xunit.Abstractions;

namespace VRoidDiva.Tests;

/// <summary>
/// Runs real character .vrm files through the whole pipeline. Opt-in: set
/// VROIDDIVA_SAMPLE_VRMS to one or more .vrm paths separated by the platform's path
/// separator. The models must be full-size humanoids (the result's height is checked).
/// </summary>
public class RealModelTests
{
    private readonly ITestOutputHelper mOutput;

    public RealModelTests(ITestOutputHelper output) => mOutput = output;

    [Fact]
    public void ConvertsSampleModels()
    {
        string variable = Environment.GetEnvironmentVariable("VROIDDIVA_SAMPLE_VRMS");
        if (string.IsNullOrEmpty(variable))
            return;

        foreach (string vrmPath in variable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            using var temp = new TempDirectory();
            string reference = Skeletons.WriteReferenceFarc(temp.Path);
            string modDirectory = Path.Combine(temp.Path, "mod");

            var model = VrmModel.Load(vrmPath);
            mOutput.WriteLine($"{Path.GetFileName(vrmPath)}: VRM {model.SpecVersion}, {model.Primitives.Count} primitives, " +
                              $"{model.Primitives.Sum(x => x.Positions.Length)} vertices, {model.Materials.Count} materials");

            Assert.Equal(0, Program.Main(new[]
            {
                "convert", vrmPath, "--reference", reference, "--out", modDirectory, "--module-id", "4999"
            }));

            using var farc = BinaryFile.Load<FarcArchive>(Path.Combine(modDirectory, "rom", "objset", "mikitm4999.farc"));
            var textures = BinaryFile.Load<TextureSet>(Copy(farc, "mikitm4999_tex.bin"));
            var objectSet = new ObjectSet();
            objectSet.Load(Copy(farc, "mikitm4999_obj.bin"), textures, null);

            var body = objectSet.Objects[0];
            int vertices = body.Meshes.Sum(x => x.Positions.Length);
            mOutput.WriteLine($"  -> {body.Meshes.Count} meshes, {vertices} vertices, {body.Materials.Count} materials, " +
                              $"{textures.Textures.Count} textures, {body.Skin.Bones.Count} bones");

            Assert.Equal(model.Primitives.Sum(x => x.Indices.Length),
                body.Meshes.Sum(m => m.SubMeshes.Sum(s => s.Indices.Length)));

            foreach (var mesh in body.Meshes)
            {
                Assert.True(mesh.Positions.Length <= 0xFFFE);
                Assert.All(mesh.Positions, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
                foreach (var subMesh in mesh.SubMeshes)
                {
                    Assert.True(subMesh.BoneIndices.Length <= 64);
                    Assert.All(subMesh.Indices, i => Assert.True(i < mesh.Positions.Length));
                }

                foreach (var weights in mesh.BlendWeights)
                    Assert.InRange(weights.X + weights.Y + weights.Z + weights.W, 0.999f, 1.001f);
            }

            // The model must end up roughly as tall as the reference character.
            float minY = body.Meshes.Min(m => m.Positions.Min(p => p.Y));
            float maxY = body.Meshes.Max(m => m.Positions.Max(p => p.Y));
            mOutput.WriteLine($"  -> height {minY:F3} .. {maxY:F3}");
            Assert.InRange(maxY - minY, 1.0f, 2.0f);
        }
    }

    private static MemoryStream Copy(FarcArchive farc, string name)
    {
        using var entry = farc.Open(name, EntryStreamMode.MemoryStream);
        var memory = new MemoryStream();
        entry.Source.Seek(0, SeekOrigin.Begin);
        entry.Source.CopyTo(memory);
        memory.Position = 0;
        return memory;
    }
}
