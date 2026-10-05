using System.Numerics;
using System.Text.Json.Nodes;

namespace VRoidDiva.Vrm;

public enum ConstraintKind
{
    None,
    Roll,
    Aim,
    Rotation
}

public sealed class VrmNode
{
    public int Index;
    public string Name;
    public int Parent = -1;
    public List<int> Children = new();
    public Matrix4x4 Local = Matrix4x4.Identity;
    public Matrix4x4 World = Matrix4x4.Identity;

    /// <summary>VRMC_node_constraint (VRM 1.0) driving this node, if any.</summary>
    public ConstraintKind Constraint;

    public int ConstraintSource = -1;
}

public enum AlphaMode
{
    Opaque,
    Mask,
    Blend
}

public sealed class VrmMaterial
{
    public string Name;
    public Vector4 BaseColor = Vector4.One;
    public int BaseColorImage = -1;
    public AlphaMode AlphaMode;
    public float AlphaCutoff = 0.5f;
    public bool DoubleSided;
}

/// <summary>One glTF primitive, already converted to its rest (bind) pose in world space.</summary>
public sealed class VrmPrimitive
{
    public string Name;
    public int Material;
    public Vector3[] Positions;
    public Vector3[] Normals;
    public Vector2[] TexCoords;
    public Vector4[] Colors;

    /// <summary>Up to four influencing nodes per vertex (node indices, -1 = unused).</summary>
    public int[] JointNodes;

    public float[] JointWeights;
    public uint[] Indices;
}

/// <summary>
/// A VRM (0.x or 1.0) humanoid reduced to what the converter needs: node
/// hierarchy, humanoid bone assignments, rest-pose geometry and materials.
/// </summary>
public sealed class VrmModel
{
    public GltfDocument Document { get; private set; }
    public string SpecVersion { get; private set; }
    public string Title { get; private set; }
    public string Author { get; private set; }
    public int ThumbnailImage { get; private set; } = -1;

    public List<VrmNode> Nodes { get; } = new();
    public Dictionary<string, int> HumanBones { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VrmMaterial> Materials { get; } = new();
    public List<VrmPrimitive> Primitives { get; } = new();

    public bool IsVrm0 => SpecVersion.StartsWith("0", StringComparison.Ordinal);

    public static VrmModel Load(string filePath) => Load(GltfDocument.Load(filePath));

    public static VrmModel Load(GltfDocument document)
    {
        var model = new VrmModel { Document = document };
        model.ReadNodes();
        model.ReadVrmExtension();
        model.ReadMaterials();
        model.ReadMeshes();
        return model;
    }

    private void ReadNodes()
    {
        var nodes = Document.GetArray("nodes");
        for (int i = 0; i < nodes.Count; i++)
        {
            var json = nodes[i]!.AsObject();
            var node = new VrmNode
            {
                Index = i,
                Name = json["name"]?.GetValue<string>() ?? $"node{i}",
                Local = ReadLocalTransform(json)
            };

            if (json["children"] is JsonArray children)
                node.Children.AddRange(children.Select(x => x!.GetValue<int>()));

            if (json["extensions"]?["VRMC_node_constraint"]?["constraint"] is JsonObject constraint)
            {
                foreach (var (kind, name) in new[]
                         {
                             (ConstraintKind.Roll, "roll"), (ConstraintKind.Aim, "aim"), (ConstraintKind.Rotation, "rotation")
                         })
                {
                    if (constraint[name]?["source"] is JsonNode source)
                    {
                        node.Constraint = kind;
                        node.ConstraintSource = source.GetValue<int>();
                        break;
                    }
                }
            }

            Nodes.Add(node);
        }

        foreach (var node in Nodes)
        foreach (int child in node.Children)
            Nodes[child].Parent = node.Index;

        foreach (var node in Nodes.Where(x => x.Parent < 0))
            UpdateWorld(node, Matrix4x4.Identity);
    }

    private void UpdateWorld(VrmNode node, Matrix4x4 parentWorld)
    {
        node.World = node.Local * parentWorld;
        foreach (int child in node.Children)
            UpdateWorld(Nodes[child], node.World);
    }

    private static Matrix4x4 ReadLocalTransform(JsonObject json)
    {
        if (json["matrix"] is JsonArray m)
        {
            var values = m.Select(x => x!.GetValue<float>()).ToArray();
            return GltfDocument.ReadMatrix(values, 0);
        }

        var t = json["translation"] is JsonArray ta ? new Vector3(F(ta, 0), F(ta, 1), F(ta, 2)) : Vector3.Zero;
        var r = json["rotation"] is JsonArray ra ? new Quaternion(F(ra, 0), F(ra, 1), F(ra, 2), F(ra, 3)) : Quaternion.Identity;
        var s = json["scale"] is JsonArray sa ? new Vector3(F(sa, 0), F(sa, 1), F(sa, 2)) : Vector3.One;

        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) *
               Matrix4x4.CreateTranslation(t);

        static float F(JsonArray a, int i) => a[i]!.GetValue<float>();
    }

    private void ReadVrmExtension()
    {
        var extensions = Document.Json["extensions"] as JsonObject;

        if (extensions?["VRMC_vrm"] is JsonObject vrm1)
        {
            SpecVersion = vrm1["specVersion"]?.GetValue<string>() ?? "1.0";

            if (vrm1["humanoid"]?["humanBones"] is JsonObject bones)
            {
                foreach (var (boneName, value) in bones)
                {
                    if (value?["node"] is JsonNode n)
                        HumanBones[boneName] = n.GetValue<int>();
                }
            }

            if (vrm1["meta"] is JsonObject meta)
            {
                Title = meta["name"]?.GetValue<string>();
                if (meta["authors"] is JsonArray authors && authors.Count > 0)
                    Author = string.Join(", ", authors.Select(x => x!.GetValue<string>()));
                ThumbnailImage = meta["thumbnailImage"]?.GetValue<int>() ?? -1;
            }
        }
        else if (extensions?["VRM"] is JsonObject vrm0)
        {
            SpecVersion = vrm0["specVersion"]?.GetValue<string>() ?? "0.0";

            if (vrm0["humanoid"]?["humanBones"] is JsonArray bones)
            {
                foreach (var bone in bones)
                {
                    string boneName = bone?["bone"]?.GetValue<string>();
                    if (boneName != null && bone["node"] is JsonNode n && n.GetValue<int>() >= 0)
                        HumanBones[boneName] = n.GetValue<int>();
                }
            }

            if (vrm0["meta"] is JsonObject meta)
            {
                Title = meta["title"]?.GetValue<string>();
                Author = meta["author"]?.GetValue<string>();

                // VRM 0.x stores a *texture* index; resolve it to the image.
                int texture = meta["texture"]?.GetValue<int>() ?? -1;
                if (texture >= 0 && texture < Document.GetArray("textures").Count)
                    ThumbnailImage = Document.GetArray("textures")[texture]?["source"]?.GetValue<int>() ?? -1;
            }
        }
        else
        {
            throw new InvalidDataException(
                "This file has no VRM extension (VRM or VRMC_vrm). Export the model from VRoid Studio as .vrm.");
        }

        if (IsVrm0)
            CanonicalizeVrm0ThumbNames();

        if (!HumanBones.ContainsKey("hips"))
            throw new InvalidDataException("The VRM humanoid has no 'hips' bone.");
    }

    /// <summary>
    /// VRM 0.x names the thumb joints Proximal/Intermediate/Distal, VRM 1.0 names
    /// the same joints Metacarpal/Proximal/Distal. Bone maps use the 1.0 names.
    /// </summary>
    private void CanonicalizeVrm0ThumbNames()
    {
        foreach (string side in new[] { "left", "right" })
        {
            bool hasProximal = HumanBones.Remove($"{side}ThumbProximal", out int proximal);
            bool hasIntermediate = HumanBones.Remove($"{side}ThumbIntermediate", out int intermediate);

            if (hasProximal)
                HumanBones[$"{side}ThumbMetacarpal"] = proximal;
            if (hasIntermediate)
                HumanBones[$"{side}ThumbProximal"] = intermediate;
        }
    }

    private void ReadMaterials()
    {
        var textures = Document.GetArray("textures");
        var materials = Document.GetArray("materials");

        for (int i = 0; i < materials.Count; i++)
        {
            var json = materials[i]!.AsObject();
            var material = new VrmMaterial
            {
                Name = json["name"]?.GetValue<string>() ?? $"material{i}",
                DoubleSided = json["doubleSided"]?.GetValue<bool>() ?? false,
                AlphaCutoff = json["alphaCutoff"]?.GetValue<float>() ?? 0.5f,
                AlphaMode = (json["alphaMode"]?.GetValue<string>() ?? "OPAQUE") switch
                {
                    "MASK" => AlphaMode.Mask,
                    "BLEND" => AlphaMode.Blend,
                    _ => AlphaMode.Opaque
                }
            };

            if (json["pbrMetallicRoughness"] is JsonObject pbr)
            {
                if (pbr["baseColorFactor"] is JsonArray c)
                {
                    material.BaseColor = new Vector4(c[0]!.GetValue<float>(), c[1]!.GetValue<float>(),
                        c[2]!.GetValue<float>(), c[3]!.GetValue<float>());
                }

                int texture = pbr["baseColorTexture"]?["index"]?.GetValue<int>() ?? -1;
                if (texture >= 0 && texture < textures.Count)
                    material.BaseColorImage = textures[texture]?["source"]?.GetValue<int>() ?? -1;
            }

            Materials.Add(material);
        }

        if (Materials.Count == 0)
            Materials.Add(new VrmMaterial { Name = "default" });
    }

    private void ReadMeshes()
    {
        var meshes = Document.GetArray("meshes");
        var skins = Document.GetArray("skins");

        foreach (var node in Nodes)
        {
            var nodeJson = Document.GetArray("nodes")[node.Index]!.AsObject();
            if (nodeJson["mesh"] == null)
                continue;

            var meshJson = meshes[nodeJson["mesh"]!.GetValue<int>()]!.AsObject();
            string meshName = meshJson["name"]?.GetValue<string>() ?? node.Name;

            int[] skinJoints = null;
            Matrix4x4[] skinMatrices = null;

            if (nodeJson["skin"] != null)
            {
                var skin = skins[nodeJson["skin"]!.GetValue<int>()]!.AsObject();
                skinJoints = skin["joints"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();

                // Per-joint matrix mapping mesh-space bind positions to the current (rest) world pose.
                var inverseBindMatrices = new Matrix4x4[skinJoints.Length];
                if (skin["inverseBindMatrices"] != null)
                {
                    var values = Document.ReadFloats(skin["inverseBindMatrices"]!.GetValue<int>(), out _);
                    for (int j = 0; j < skinJoints.Length; j++)
                        inverseBindMatrices[j] = GltfDocument.ReadMatrix(values, j);
                }
                else
                {
                    Array.Fill(inverseBindMatrices, Matrix4x4.Identity);
                }

                skinMatrices = new Matrix4x4[skinJoints.Length];
                for (int j = 0; j < skinJoints.Length; j++)
                    skinMatrices[j] = inverseBindMatrices[j] * Nodes[skinJoints[j]].World;
            }

            int primitiveIndex = 0;
            foreach (var primitiveJson in meshJson["primitives"]!.AsArray())
            {
                var primitive = ReadPrimitive(primitiveJson!.AsObject(), node, skinJoints, skinMatrices);
                if (primitive != null)
                {
                    primitive.Name = meshJson["primitives"]!.AsArray().Count > 1
                        ? $"{meshName}_{primitiveIndex}"
                        : meshName;
                    Primitives.Add(primitive);
                }

                primitiveIndex++;
            }
        }
    }

    private VrmPrimitive ReadPrimitive(JsonObject json, VrmNode node, int[] skinJoints, Matrix4x4[] skinMatrices)
    {
        int mode = json["mode"]?.GetValue<int>() ?? 4;
        if (mode != 4)
            return null; // Only triangle lists are used by VRoid.

        var attributes = json["attributes"]!.AsObject();
        if (attributes["POSITION"] == null)
            return null;

        var positions = ToVector3(Document.ReadFloats(attributes["POSITION"]!.GetValue<int>(), out _));
        int vertexCount = positions.Length;

        var normals = attributes["NORMAL"] != null
            ? ToVector3(Document.ReadFloats(attributes["NORMAL"]!.GetValue<int>(), out _))
            : new Vector3[vertexCount];

        Vector2[] texCoords = null;
        if (attributes["TEXCOORD_0"] != null)
        {
            var uv = Document.ReadFloats(attributes["TEXCOORD_0"]!.GetValue<int>(), out _);
            texCoords = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                texCoords[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
        }

        Vector4[] colors = null;
        if (attributes["COLOR_0"] != null)
        {
            var c = Document.ReadFloats(attributes["COLOR_0"]!.GetValue<int>(), out int cc);
            colors = new Vector4[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                colors[i] = new Vector4(c[i * cc], c[i * cc + 1], c[i * cc + 2], cc == 4 ? c[i * cc + 3] : 1f);
        }

        var jointNodes = new int[vertexCount * 4];
        var jointWeights = new float[vertexCount * 4];
        Array.Fill(jointNodes, -1);

        bool skinned = skinJoints != null && attributes["JOINTS_0"] != null && attributes["WEIGHTS_0"] != null;

        if (skinned)
        {
            var joints = Document.ReadInts(attributes["JOINTS_0"]!.GetValue<int>(), out _);
            var weights = Document.ReadFloats(attributes["WEIGHTS_0"]!.GetValue<int>(), out _);

            for (int v = 0; v < vertexCount; v++)
            {
                var position = Vector3.Zero;
                var normal = Vector3.Zero;
                float total = 0;

                for (int k = 0; k < 4; k++)
                {
                    float w = weights[v * 4 + k];
                    if (w <= 0)
                        continue;

                    int joint = joints[v * 4 + k];
                    jointNodes[v * 4 + k] = skinJoints[joint];
                    jointWeights[v * 4 + k] = w;
                    total += w;

                    position += Vector3.Transform(positions[v], skinMatrices[joint]) * w;
                    normal += Vector3.TransformNormal(normals[v], skinMatrices[joint]) * w;
                }

                if (total > 0)
                {
                    positions[v] = position / total;
                    normals[v] = SafeNormalize(normal);
                }
                else
                {
                    // Unweighted vertex: treat as rigidly attached to the mesh node.
                    positions[v] = Vector3.Transform(positions[v], node.World);
                    normals[v] = SafeNormalize(Vector3.TransformNormal(normals[v], node.World));
                    jointNodes[v * 4] = node.Index;
                    jointWeights[v * 4] = 1;
                }
            }
        }
        else
        {
            for (int v = 0; v < vertexCount; v++)
            {
                positions[v] = Vector3.Transform(positions[v], node.World);
                normals[v] = SafeNormalize(Vector3.TransformNormal(normals[v], node.World));
                jointNodes[v * 4] = node.Index;
                jointWeights[v * 4] = 1;
            }
        }

        uint[] indices;
        if (json["indices"] != null)
        {
            var ints = Document.ReadInts(json["indices"]!.GetValue<int>(), out _);
            indices = ints.Select(x => (uint)x).ToArray();
        }
        else
        {
            indices = Enumerable.Range(0, vertexCount).Select(x => (uint)x).ToArray();
        }

        return new VrmPrimitive
        {
            Material = json["material"]?.GetValue<int>() ?? 0,
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Colors = colors,
            JointNodes = jointNodes,
            JointWeights = jointWeights,
            Indices = indices
        };
    }

    private static Vector3[] ToVector3(float[] values)
    {
        var result = new Vector3[values.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
        return result;
    }

    private static Vector3 SafeNormalize(Vector3 v)
    {
        float length = v.Length();
        return length > 1e-8f ? v / length : Vector3.UnitY;
    }
}
