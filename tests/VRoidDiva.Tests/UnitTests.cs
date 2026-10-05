using System.Numerics;
using VRoidDiva.Diva;
using VRoidDiva.Retarget;
using VRoidDiva.Vrm;
using Xunit;

namespace VRoidDiva.Tests;

public class UnitTests
{
    [Fact]
    public void RotationBetweenMapsDirections()
    {
        var pairs = new[]
        {
            (Vector3.UnitX, Vector3.UnitY),
            (Vector3.Normalize(new Vector3(1, 1, 0)), Vector3.Normalize(new Vector3(0.3f, -1, 0.2f))),
            (Vector3.UnitZ, -Vector3.UnitZ),
            (Vector3.UnitY, Vector3.UnitY)
        };

        foreach (var (from, to) in pairs)
        {
            var rotated = Vector3.Transform(from, Retargeter.RotationBetween(from, to));
            Assert.True(Vector3.Distance(rotated, to) < 1e-5f, $"{from} -> {to} gave {rotated}");
        }
    }

    [Fact]
    public void AxisScaleOnlyStretchesAlongTheAxis()
    {
        var axis = Vector3.Normalize(new Vector3(1, 2, 3));
        var scale = Retargeter.AxisScale(axis, 1.5f);

        Assert.True(Vector3.Distance(Vector3.Transform(axis, scale), axis * 1.5f) < 1e-5f);

        var perpendicular = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));
        Assert.True(Vector3.Distance(Vector3.Transform(perpendicular, scale), perpendicular) < 1e-5f);
    }

    [Fact]
    public void BasisIsIndependentOfFacing()
    {
        var front = Retargeter.Basis(new(0, 1, 0), new(0, 2, 0), new(1, 1.5f, 0), new(-1, 1.5f, 0));
        var back = Retargeter.Basis(new(0, 1, 0), new(0, 2, 0), new(-1, 1.5f, 0), new(1, 1.5f, 0));

        // Same up axis, opposite left / forward axes.
        Assert.Equal(new Vector3(front.M21, front.M22, front.M23), new Vector3(back.M21, back.M22, back.M23));
        Assert.Equal(-front.M31, back.M31, 5);
        Assert.Equal(-front.M33, back.M33, 5);
    }

    [Theory]
    [InlineData(255, 0, 0, 255)]
    [InlineData(10, 200, 30, 255)]
    [InlineData(128, 128, 128, 64)]
    public void SolidColorBlocksRoundTrip(byte r, byte g, byte b, byte a)
    {
        var image = new RgbaImage(4, 4, SyntheticVrm.SolidTexture(4, 4, r, g, b, a));
        bool alpha = a != 255;
        var encoded = BlockCompressor.Encode(image, alpha);
        Assert.Equal(alpha ? 16 : 8, encoded.Length);

        int colorOffset = alpha ? 8 : 0;
        ushort c0 = BitConverter.ToUInt16(encoded, colorOffset);
        int r5 = c0 >> 11, g6 = (c0 >> 5) & 63, b5 = c0 & 31;
        Assert.InRange(Math.Abs((r5 << 3 | r5 >> 2) - r), 0, 8);
        Assert.InRange(Math.Abs((g6 << 2 | g6 >> 4) - g), 0, 4);
        Assert.InRange(Math.Abs((b5 << 3 | b5 >> 2) - b), 0, 8);

        if (alpha)
            Assert.Equal(a, encoded[0]);
    }

    [Fact]
    public void GradientBlockUsesInterpolatedColours()
    {
        var pixels = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            byte v = (byte)(i * 17);
            pixels[i * 4] = v; pixels[i * 4 + 1] = v; pixels[i * 4 + 2] = v; pixels[i * 4 + 3] = 255;
        }

        var encoded = BlockCompressor.Encode(new RgbaImage(4, 4, pixels), false);
        uint indices = BitConverter.ToUInt32(encoded, 4);
        var used = Enumerable.Range(0, 16).Select(i => (indices >> (i * 2)) & 3).Distinct().Count();
        Assert.Equal(4, used);
    }

    [Fact]
    public void TexturesAreDownscaledAndMipmapped()
    {
        var image = new RgbaImage(64, 32, SyntheticVrm.SolidTexture(64, 32, 1, 2, 3, 255));
        var texture = TextureBuilder.Build(image, "T", 1, maxSize: 16);
        Assert.Equal(16, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(5, texture.MipMapCount); // 16, 8, 4, 2, 1
    }

    [Fact]
    public void KeyValueFileIsSortedOrdinally()
    {
        var file = new KeyValueFile();
        file.Set("item.10.no", 10);
        file.Set("item.2.no", 2);
        file.Set("item.length", 2);
        file.Set("cos.0.id", 0);
        file.Set("item.0.face_depth", 0f);

        string text = System.Text.Encoding.UTF8.GetString(file.ToBytes());
        Assert.Equal("cos.0.id=0\nitem.0.face_depth=0.000000\nitem.10.no=10\nitem.2.no=2\nitem.length=2\n", text);
    }

    [Theory]
    [InlineData("N00_000_00_FaceMouth_00_FACE (Instance)", "SKIN")]
    [InlineData("N00_000_00_Body_00_SKIN (Instance)", "SKIN")]
    [InlineData("N00_000_Hair_00_HAIR (Instance)", "CLOTH")]
    [InlineData("N00_001_01_Tops_01_CLOTH (Instance)", "CLOTH")]
    public void ShaderSelection(string material, string shader) =>
        Assert.Equal(shader, ObjectBuilder.ChooseShader(material));

    [Fact]
    public void MeshesAreSplitAtTheBoneLimit()
    {
        // 100 triangles, each skinned to its own bone.
        const int triangles = 100;
        var primitive = new RetargetedPrimitive
        {
            Source = new VrmPrimitive
            {
                Name = "many",
                Indices = Enumerable.Range(0, triangles * 3).Select(x => (uint)x).ToArray(),
                TexCoords = new Vector2[triangles * 3]
            },
            Positions = Enumerable.Range(0, triangles * 3).Select(x => new Vector3(x, 0, 0)).ToArray(),
            Normals = new Vector3[triangles * 3],
            BoneIndices = Enumerable.Range(0, triangles * 3).SelectMany(v => new[] { v / 3, -1, -1, -1 }).ToArray(),
            BoneWeights = Enumerable.Range(0, triangles * 3).SelectMany(_ => new[] { 1f, 0, 0, 0 }).ToArray()
        };

        var meshes = ObjectBuilder.CreateMeshes(primitive, 0).ToList();
        Assert.Equal(2, meshes.Count);
        Assert.Equal(new[] { "many", "many_1" }, meshes.Select(x => x.Name));
        Assert.Equal(triangles * 3, meshes.Sum(x => x.SubMeshes[0].Indices.Length));

        foreach (var mesh in meshes)
        {
            var subMesh = mesh.SubMeshes[0];
            Assert.True(subMesh.BoneIndices.Length <= 64);
            Assert.All(subMesh.Indices, i => Assert.InRange((int)i, 0, mesh.Positions.Length - 1));
            Assert.All(mesh.BlendIndices, b => Assert.InRange(b.X, 0, subMesh.BoneIndices.Length - 1));

            // Each vertex still points at its original bone.
            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                int original = (int)mesh.Positions[v].X / 3;
                Assert.Equal(original, subMesh.BoneIndices[mesh.BlendIndices[v].X]);
            }
        }
    }

    [Fact]
    public void Vrm0ThumbsAreRenamedToVrm1Names()
    {
        // The synthetic model has no thumbs; build one through the JSON directly.
        var bytes = new SyntheticVrm { Vrm0 = true }.Build();
        var document = GltfDocument.Load(bytes);
        var bones = document.Json["extensions"]!["VRM"]!["humanoid"]!["humanBones"]!.AsArray();
        bones.Add(new System.Text.Json.Nodes.JsonObject { ["bone"] = "leftThumbProximal", ["node"] = 1 });
        bones.Add(new System.Text.Json.Nodes.JsonObject { ["bone"] = "leftThumbIntermediate", ["node"] = 2 });

        var model = VrmModel.Load(document);
        Assert.Equal(1, model.HumanBones["leftThumbMetacarpal"]);
        Assert.Equal(2, model.HumanBones["leftThumbProximal"]);
        Assert.False(model.HumanBones.ContainsKey("leftThumbIntermediate"));
    }

    [Fact]
    public void BoneMapOverridesReplaceCandidates()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "map.json");
        File.WriteAllText(path, "{\"leftHand\": [\"custom_bone\"], \"jaw\": []}");

        var map = BoneMap.CreateDefault();
        map.ApplyOverrides(path);
        Assert.Equal(new[] { "custom_bone" }, map.Bones["leftHand"].DivaCandidates);
        Assert.Empty(map.Bones["jaw"].DivaCandidates);

        File.WriteAllText(path, "{\"notABone\": []}");
        Assert.Throws<InvalidDataException>(() => map.ApplyOverrides(path));
    }
}
