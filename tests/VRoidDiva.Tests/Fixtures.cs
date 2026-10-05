using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Materials;
using MikuMikuLibrary.Numerics;
using MikuMikuLibrary.Objects;
using DivaObject = MikuMikuLibrary.Objects.Object;

namespace VRoidDiva.Tests;

/// <summary>Joint layout shared by the synthetic VRM and DIVA skeletons.</summary>
internal static class Skeletons
{
    public sealed record Joint(string Name, string Parent, Vector3 Position);

    /// <summary>VRoid-like T-pose, character facing +Z (left hand at +X), 1.6 m tall.</summary>
    public static readonly Joint[] VrmTPose =
    {
        new("hips", null, new(0, 0.90f, 0)),
        new("spine", "hips", new(0, 1.00f, 0)),
        new("chest", "spine", new(0, 1.15f, 0)),
        new("upperChest", "chest", new(0, 1.28f, 0)),
        new("neck", "upperChest", new(0, 1.42f, 0)),
        new("head", "neck", new(0, 1.50f, 0)),
        new("leftShoulder", "upperChest", new(0.03f, 1.38f, 0)),
        new("leftUpperArm", "leftShoulder", new(0.14f, 1.38f, 0)),
        new("leftLowerArm", "leftUpperArm", new(0.40f, 1.38f, 0)),
        new("leftHand", "leftLowerArm", new(0.63f, 1.38f, 0)),
        new("leftMiddleProximal", "leftHand", new(0.71f, 1.38f, 0)),
        new("rightShoulder", "upperChest", new(-0.03f, 1.38f, 0)),
        new("rightUpperArm", "rightShoulder", new(-0.14f, 1.38f, 0)),
        new("rightLowerArm", "rightUpperArm", new(-0.40f, 1.38f, 0)),
        new("rightHand", "rightLowerArm", new(-0.63f, 1.38f, 0)),
        new("rightMiddleProximal", "rightHand", new(-0.71f, 1.38f, 0)),
        new("leftUpperLeg", "hips", new(0.09f, 0.86f, 0)),
        new("leftLowerLeg", "leftUpperLeg", new(0.09f, 0.48f, 0)),
        new("leftFoot", "leftLowerLeg", new(0.09f, 0.08f, 0)),
        new("leftToes", "leftFoot", new(0.09f, 0.02f, 0.10f)),
        new("rightUpperLeg", "hips", new(-0.09f, 0.86f, 0)),
        new("rightLowerLeg", "rightUpperLeg", new(-0.09f, 0.48f, 0)),
        new("rightFoot", "rightLowerLeg", new(-0.09f, 0.08f, 0)),
        new("rightToes", "rightFoot", new(-0.09f, 0.02f, 0.10f)),
    };

    /// <summary>
    /// A DIVA-like bind pose: shorter (≈1.45 m), arms lowered into an A-pose,
    /// character facing +Z. Real values come from the game; only the shape matters here.
    /// </summary>
    public static readonly Dictionary<string, (string Diva, Vector3 Position)> Diva = new()
    {
        ["hips"] = ("kl_kosi_etc_wj", new(0, 0.82f, 0)),
        ["spine"] = ("j_mune_wj", new(0, 0.92f, 0)),
        ["chest"] = ("kl_mune_b_wj", new(0, 1.06f, 0)),
        ["neck"] = ("kl_kubi", new(0, 1.25f, 0)),
        ["head"] = ("j_kao_wj", new(0, 1.33f, 0)),
        ["leftShoulder"] = ("kl_waki_l_wj", new(0.02f, 1.21f, 0)),
        ["leftUpperArm"] = ("j_kata_l_wj_cu", new(0.12f, 1.21f, 0)),
        ["leftLowerArm"] = ("j_ude_l_wj", new(0.31f, 1.07f, 0)),
        ["leftHand"] = ("kl_te_l_wj", new(0.48f, 0.94f, 0)),
        ["leftMiddleProximal"] = ("nl_naka_l_wj", new(0.53f, 0.90f, 0)),
        ["rightShoulder"] = ("kl_waki_r_wj", new(-0.02f, 1.21f, 0)),
        ["rightUpperArm"] = ("j_kata_r_wj_cu", new(-0.12f, 1.21f, 0)),
        ["rightLowerArm"] = ("j_ude_r_wj", new(-0.31f, 1.07f, 0)),
        ["rightHand"] = ("kl_te_r_wj", new(-0.48f, 0.94f, 0)),
        ["rightMiddleProximal"] = ("nl_naka_r_wj", new(-0.53f, 0.90f, 0)),
        ["leftUpperLeg"] = ("j_momo_l_wj", new(0.08f, 0.78f, 0)),
        ["leftLowerLeg"] = ("j_sune_l_wj", new(0.08f, 0.43f, 0.01f)),
        ["leftFoot"] = ("kl_asi_l_wj_co", new(0.08f, 0.07f, 0)),
        ["leftToes"] = ("kl_toe_l_wj", new(0.08f, 0.01f, 0.09f)),
        ["rightUpperLeg"] = ("j_momo_r_wj", new(-0.08f, 0.78f, 0)),
        ["rightLowerLeg"] = ("j_sune_r_wj", new(-0.08f, 0.43f, 0.01f)),
        ["rightFoot"] = ("kl_asi_r_wj_co", new(-0.08f, 0.07f, 0)),
        ["rightToes"] = ("kl_toe_r_wj", new(-0.08f, 0.01f, 0.09f)),
    };

    /// <summary>
    /// Builds a skinned DIVA object set whose skin carries the reference bind
    /// pose, saved inside a .farc exactly like a stock objset archive.
    /// </summary>
    public static string WriteReferenceFarc(string directory)
    {
        var obj = new DivaObject { Name = "MIKITM001_BODY", Id = 0, Skin = new Skin() };
        uint id = 0;

        foreach (var (_, (name, position)) in Diva)
        {
            // Give each bind matrix an arbitrary orientation: only its translation
            // may be used as the joint position.
            var rotation = Matrix4x4.CreateFromYawPitchRoll(id * 0.3f, id * 0.2f, id * 0.1f);
            var world = rotation * Matrix4x4.CreateTranslation(position);
            Matrix4x4.Invert(world, out var inverse);
            obj.Skin.Bones.Add(new BoneInfo { Name = name, Id = 100 + id++, InverseBindPoseMatrix = inverse });
        }

        // EX bones must be ignored.
        obj.Skin.Bones.Add(new BoneInfo { Name = "n_hiji_l_wj_ex", IsEx = true, InverseBindPoseMatrix = Matrix4x4.Identity });

        obj.Materials.Add(new Material { Name = "body" });
        var mesh = new Mesh
        {
            Name = "body",
            Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            BlendWeights = new[] { Vector4.UnitX, Vector4.UnitX, Vector4.UnitX },
            BlendIndices = new[] { new Vector4Int(0, -1, -1, -1), new Vector4Int(0, -1, -1, -1), new Vector4Int(0, -1, -1, -1) }
        };
        mesh.SubMeshes.Add(new SubMesh
        {
            Indices = new uint[] { 0, 1, 2 },
            BoneIndices = new ushort[] { 0 },
            BonesPerVertex = 4,
            PrimitiveType = PrimitiveType.Triangles,
            IndexFormat = IndexFormat.UInt16
        });
        obj.Meshes.Add(mesh);

        var objectSet = new ObjectSet { Format = BinaryFormat.FT };
        objectSet.Objects.Add(obj);

        var objStream = new MemoryStream();
        objectSet.Save(objStream, leaveOpen: true);
        objStream.Position = 0;

        string path = Path.Combine(directory, "mikitm001.farc");
        using var farc = new FarcArchive { IsCompressed = true, Alignment = 16 };
        farc.Add("mikitm001_obj.bin", objStream, false);
        using var stream = File.Create(path);
        farc.Save(stream, leaveOpen: true);
        return path;
    }
}

/// <summary>Writes small but structurally faithful VRoid-style .vrm files.</summary>
internal sealed class SyntheticVrm
{
    public bool Vrm0 { get; init; }
    public byte[] TextureRgba { get; init; } = SolidTexture(8, 8, 200, 120, 60, 255);
    public int TextureSize { get; init; } = 8;

    public static byte[] SolidTexture(int w, int h, byte r, byte g, byte b, byte a)
    {
        var data = new byte[w * h * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }

        return data;
    }

    /// <summary>
    /// The model: every joint gets a small triangle whose first vertex sits exactly on
    /// the joint, weighted fully to that joint, and two more 1 cm along world +X / +Y.
    /// The extra "hair" bone under the head and an aim-constraint helper under the left
    /// shoulder (neither humanoid) carry a triangle too, followed by probe triangles on
    /// the left elbow skinned to the upper arm and to the aim helper.
    /// Vertex order is joint order.
    /// </summary>
    public byte[] Build()
    {
        var joints = Skeletons.VrmTPose.ToList();
        var hair = new Skeletons.Joint("J_Sec_Hair1_01", "head", new(0, 1.62f, -0.05f));
        joints.Add(hair);

        // VRM 1.0 style aim helper: parented to the shoulder, aimed at the elbow.
        var aim = new Skeletons.Joint("J_Aim_L_TopsUpperArm", "leftShoulder", Skeletons.VrmTPose.First(x => x.Name == "leftUpperArm").Position);
        joints.Add(aim);

        // VRM 0.x models face -Z: rotate the whole model 180° about Y.
        Vector3 Place(Vector3 p) => Vrm0 ? new Vector3(-p.X, p.Y, -p.Z) : p;

        var buffer = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();

        int AddView(byte[] bytes)
        {
            while (buffer.Length % 4 != 0)
                buffer.WriteByte(0);
            bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = buffer.Length, ["byteLength"] = bytes.Length });
            buffer.Write(bytes);
            return bufferViews.Count - 1;
        }

        int AddAccessor(byte[] bytes, int componentType, string type, int count, JsonObject extra = null)
        {
            var accessor = new JsonObject
            {
                ["bufferView"] = AddView(bytes),
                ["componentType"] = componentType,
                ["type"] = type,
                ["count"] = count
            };
            if (extra != null)
                foreach (var (k, v) in extra)
                    accessor[k] = v?.DeepClone();
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        byte[] Floats(IEnumerable<float> values) => values.SelectMany(BitConverter.GetBytes).ToArray();

        // Nodes: 0 = root, 1.. = joints, last = mesh node.
        var nodes = new JsonArray();
        var nodeIndex = new Dictionary<string, int>();
        nodes.Add(new JsonObject { ["name"] = "Root", ["children"] = new JsonArray() });
        foreach (var joint in joints)
        {
            nodeIndex[joint.Name] = nodes.Count;
            nodes.Add(new JsonObject { ["name"] = "J_" + joint.Name });
        }

        foreach (var joint in joints)
        {
            var parentPosition = joint.Parent == null ? Vector3.Zero : Place(joints.First(x => x.Name == joint.Parent).Position);
            var local = Place(joint.Position) - parentPosition;
            var node = nodes[nodeIndex[joint.Name]]!.AsObject();
            node["translation"] = new JsonArray(local.X, local.Y, local.Z);

            var parent = joint.Parent == null ? nodes[0]!.AsObject() : nodes[nodeIndex[joint.Parent]]!.AsObject();
            if (parent["children"] is not JsonArray children)
                parent["children"] = children = new JsonArray();
            children.Add(nodeIndex[joint.Name]);
        }

        nodes[nodeIndex[aim.Name]]!["extensions"] = new JsonObject
        {
            ["VRMC_node_constraint"] = new JsonObject
            {
                ["specVersion"] = "1.0",
                ["constraint"] = new JsonObject
                {
                    ["aim"] = new JsonObject { ["source"] = nodeIndex["leftLowerArm"], ["aimAxis"] = "PositiveX", ["weight"] = 1 }
                }
            }
        };

        int meshNode = nodes.Count;
        nodes.Add(new JsonObject { ["name"] = "Body", ["mesh"] = 0, ["skin"] = 0 });
        nodes[0]!["children"]!.AsArray().Add(meshNode);

        // Geometry.
        var positions = new List<Vector3>();
        var jointsData = new List<ushort>();
        var weights = new List<float>();
        var uvs = new List<float>();

        for (int j = 0; j < joints.Count; j++)
        {
            var p = Place(joints[j].Position);
            positions.Add(p);
            positions.Add(p + new Vector3(0.01f, 0, 0));
            positions.Add(p + new Vector3(0, 0.01f, 0));
            for (int k = 0; k < 3; k++)
            {
                jointsData.AddRange(new ushort[] { (ushort)j, 0, 0, 0 });
                weights.AddRange(new[] { 1f, 0, 0, 0 });
                uvs.AddRange(new[] { 0.25f * k, 0.5f });
            }
        }

        // Probes: triangles sitting on the left elbow, skinned to the upper arm and
        // to the aim helper respectively.
        var elbow = Place(joints.First(x => x.Name == "leftLowerArm").Position);
        foreach (string owner in new[] { "leftUpperArm", aim.Name })
        {
            int joint = joints.FindIndex(x => x.Name == owner);
            foreach (var p in new[] { elbow, elbow + new Vector3(0.01f, 0, 0), elbow + new Vector3(0, 0.01f, 0) })
            {
                positions.Add(p);
                jointsData.AddRange(new ushort[] { (ushort)joint, 0, 0, 0 });
                weights.AddRange(new[] { 1f, 0, 0, 0 });
                uvs.AddRange(new[] { 0f, 0f });
            }
        }

        var min = new Vector3(positions.Min(x => x.X), positions.Min(x => x.Y), positions.Min(x => x.Z));
        var max = new Vector3(positions.Max(x => x.X), positions.Max(x => x.Y), positions.Max(x => x.Z));

        int position = AddAccessor(Floats(positions.SelectMany(p => new[] { p.X, p.Y, p.Z })), 5126, "VEC3", positions.Count,
            new JsonObject { ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z) });
        int normal = AddAccessor(Floats(positions.SelectMany(_ => new[] { 0f, 0f, 1f })), 5126, "VEC3", positions.Count);
        int uv = AddAccessor(Floats(uvs), 5126, "VEC2", positions.Count);
        int jointAccessor = AddAccessor(jointsData.SelectMany(BitConverter.GetBytes).ToArray(), 5123, "VEC4", positions.Count);
        int weightAccessor = AddAccessor(Floats(weights), 5126, "VEC4", positions.Count);
        int indices = AddAccessor(Enumerable.Range(0, positions.Count).SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray(),
            5123, "SCALAR", positions.Count);

        var inverseBind = new List<float>();
        foreach (var joint in joints)
        {
            var world = Matrix4x4.CreateTranslation(Place(joint.Position));
            Matrix4x4.Invert(world, out var inv);
            inverseBind.AddRange(new[]
            {
                inv.M11, inv.M12, inv.M13, inv.M14, inv.M21, inv.M22, inv.M23, inv.M24,
                inv.M31, inv.M32, inv.M33, inv.M34, inv.M41, inv.M42, inv.M43, inv.M44
            });
        }

        int ibm = AddAccessor(Floats(inverseBind), 5126, "MAT4", joints.Count);

        int imageView = AddView(Png.Encode(TextureSize, TextureSize, TextureRgba));

        var humanBones0 = new JsonArray();
        var humanBones1 = new JsonObject();
        foreach (var joint in Skeletons.VrmTPose)
        {
            // VRM 0.x calls the thumb joints differently; the fixture has no thumbs.
            humanBones0.Add(new JsonObject { ["bone"] = joint.Name, ["node"] = nodeIndex[joint.Name] });
            humanBones1[joint.Name] = new JsonObject { ["node"] = nodeIndex[joint.Name] };
        }

        var extensions = Vrm0
            ? new JsonObject
            {
                ["VRM"] = new JsonObject
                {
                    ["specVersion"] = "0.0",
                    ["meta"] = new JsonObject { ["title"] = "Test Girl", ["author"] = "Tester", ["texture"] = 0 },
                    ["humanoid"] = new JsonObject { ["humanBones"] = humanBones0 }
                }
            }
            : new JsonObject
            {
                ["VRMC_vrm"] = new JsonObject
                {
                    ["specVersion"] = "1.0",
                    ["meta"] = new JsonObject { ["name"] = "Test Girl", ["authors"] = new JsonArray("Tester") },
                    ["humanoid"] = new JsonObject { ["humanBones"] = humanBones1 }
                }
            };

        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["extensionsUsed"] = new JsonArray(Vrm0 ? "VRM" : "VRMC_vrm"),
            ["extensions"] = extensions,
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = nodes,
            ["skins"] = new JsonArray(new JsonObject
            {
                ["inverseBindMatrices"] = ibm,
                ["joints"] = new JsonArray(joints.Select(x => (JsonNode)nodeIndex[x.Name]).ToArray())
            }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "Body",
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = position, ["NORMAL"] = normal, ["TEXCOORD_0"] = uv,
                        ["JOINTS_0"] = jointAccessor, ["WEIGHTS_0"] = weightAccessor
                    },
                    ["indices"] = indices,
                    ["material"] = 0
                })
            }),
            ["materials"] = new JsonArray(new JsonObject
            {
                ["name"] = "N00_000_00_Body_00_SKIN (Instance)",
                ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0 } },
                ["alphaMode"] = "OPAQUE",
                ["doubleSided"] = false
            }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }),
            ["images"] = new JsonArray(new JsonObject { ["name"] = "Body_00", ["bufferView"] = imageView, ["mimeType"] = "image/png" }),
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = buffer.Length })
        };

        return Glb(json, buffer.ToArray());
    }

    public static Vector3 VertexOfJoint(string joint)
    {
        int index = Skeletons.VrmTPose.Select(x => x.Name).ToList().IndexOf(joint);
        if (index < 0)
            throw new ArgumentException(joint);
        return Skeletons.VrmTPose[index].Position;
    }

    public static int FirstVertexOfJoint(string joint)
    {
        if (joint == "hair")
            return Skeletons.VrmTPose.Length * 3;
        if (joint == "elbowOnUpperArm")
            return (Skeletons.VrmTPose.Length + 2) * 3;
        if (joint == "elbowOnAimHelper")
            return (Skeletons.VrmTPose.Length + 3) * 3;
        return Skeletons.VrmTPose.Select(x => x.Name).ToList().IndexOf(joint) * 3;
    }

    private static byte[] Glb(JsonObject json, byte[] bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        int jsonPadded = (jsonBytes.Length + 3) & ~3;
        int binPadded = (bin.Length + 3) & ~3;

        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonPadded + 8 + binPadded));
        writer.Write((uint)jsonPadded);
        writer.Write(0x4E4F534Au);
        writer.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonPadded; i++) writer.Write((byte)' ');
        writer.Write((uint)binPadded);
        writer.Write(0x004E4942u);
        writer.Write(bin);
        for (int i = bin.Length; i < binPadded; i++) writer.Write((byte)0);
        return output.ToArray();
    }
}

/// <summary>Minimal RGBA8 PNG encoder for test fixtures.</summary>
internal static class Png
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(output, "IHDR", header);

        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * width * 4, width * 4);
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(data))
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc ^ 0xFFFFFFFF);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vroiddiva-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
