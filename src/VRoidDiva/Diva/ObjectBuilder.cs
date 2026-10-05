using System.Numerics;
using MikuMikuLibrary.Numerics;
using MikuMikuLibrary.Hashes;
using MikuMikuLibrary.Materials;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Processing;
using MikuMikuLibrary.Textures;
using VRoidDiva.Retarget;
using VRoidDiva.Vrm;
using DivaObject = MikuMikuLibrary.Objects.Object;

namespace VRoidDiva.Diva;

public sealed class ObjectBuildOptions
{
    public int MaxTextureSize { get; set; } = 2048;

    /// <summary>Use the CLOTH shader for every material (instead of SKIN for skin/face).</summary>
    public bool AllCloth { get; set; }
}

/// <summary>Builds the DIVA object set (model + invisible "hider" object) for a retargeted VRM.</summary>
public static class ObjectBuilder
{
    // Classic (FT / MM+) objects use 16-bit indices; 0xFFFF is the strip restart value.
    private const int MaxVerticesPerMesh = 0xFFFE;

    // Matches MikuMikuLibrary's Splitter limit for bones referenced by one sub-mesh.
    private const int MaxBonesPerSubMesh = 0x40;

    public static ObjectSet Build(VrmModel model, RetargetResult retarget, string objectSetName,
        string bodyObjectName, string dummyObjectName, ObjectBuildOptions options = null)
    {
        options ??= new ObjectBuildOptions();

        var objectSet = new ObjectSet { TextureSet = new TextureSet() };
        var textureCache = new Dictionary<(int Image, Vector4 Tint), Texture>();

        var body = new DivaObject
        {
            Name = bodyObjectName,
            Id = 0,
            Skin = CreateSkin(retarget.Bones)
        };

        var materialIndices = new Dictionary<int, int>();

        foreach (var primitive in retarget.Primitives)
        {
            int sourceMaterial = Math.Clamp(primitive.Source.Material, 0, model.Materials.Count - 1);
            if (!materialIndices.TryGetValue(sourceMaterial, out int materialIndex))
            {
                materialIndex = body.Materials.Count;
                materialIndices[sourceMaterial] = materialIndex;
                body.Materials.Add(CreateMaterial(model, model.Materials[sourceMaterial], objectSetName,
                    objectSet.TextureSet, textureCache, options));
            }

            foreach (var mesh in CreateMeshes(primitive, (uint)materialIndex))
                body.Meshes.Add(mesh);
        }

        if (body.Meshes.Count == 0)
            throw new InvalidOperationException("The VRM contains no triangle geometry.");

        AabbCalculator.Calculate(body);
        objectSet.Objects.Add(body);

        objectSet.Objects.Add(CreateDummyObject(dummyObjectName, retarget.Bones[0]));
        objectSet.TextureIds.AddRange(objectSet.TextureSet.Textures.Select(x => x.Id));

        return objectSet;
    }

    private static Skin CreateSkin(IReadOnlyList<ReferenceBone> bones)
    {
        var skin = new Skin();
        var byName = new Dictionary<string, BoneInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var bone in bones)
        {
            var info = new BoneInfo
            {
                Name = bone.Name,
                Id = bone.Id,
                IsEx = false,
                InverseBindPoseMatrix = bone.InverseBindPose
            };
            skin.Bones.Add(info);
            byName[bone.Name] = info;
        }

        foreach (var bone in bones)
        {
            if (bone.ParentName != null && byName.TryGetValue(bone.ParentName, out var parent))
                byName[bone.Name].Parent = parent;
        }

        return skin;
    }

    /// <summary>
    /// Splits a primitive into DIVA meshes that respect the 16-bit index and
    /// per-sub-mesh bone limits, compacting vertices for each piece.
    /// </summary>
    internal static IEnumerable<Mesh> CreateMeshes(RetargetedPrimitive primitive, uint materialIndex)
    {
        var source = primitive.Source;
        var indices = source.Indices;
        int part = 0;
        int triangle = 0;
        int triangleCount = indices.Length / 3;

        while (triangle < triangleCount)
        {
            var vertexRemap = new Dictionary<uint, ushort>();
            var boneRemap = new Dictionary<int, ushort>();
            var vertexOrder = new List<uint>();
            var boneOrder = new List<int>();
            var meshIndices = new List<uint>();

            for (; triangle < triangleCount; triangle++)
            {
                int newVertices = 0;
                var newBones = new HashSet<int>();

                for (int corner = 0; corner < 3; corner++)
                {
                    uint index = indices[triangle * 3 + corner];
                    if (vertexRemap.ContainsKey(index))
                        continue;
                    newVertices++;
                    for (int k = 0; k < 4; k++)
                    {
                        int bone = primitive.BoneIndices[index * 4 + k];
                        if (bone >= 0 && !boneRemap.ContainsKey(bone))
                            newBones.Add(bone);
                    }
                }

                if (vertexOrder.Count + newVertices > MaxVerticesPerMesh ||
                    boneOrder.Count + newBones.Count > MaxBonesPerSubMesh)
                {
                    break;
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    uint index = indices[triangle * 3 + corner];
                    if (!vertexRemap.TryGetValue(index, out ushort local))
                    {
                        local = (ushort)vertexOrder.Count;
                        vertexRemap[index] = local;
                        vertexOrder.Add(index);

                        for (int k = 0; k < 4; k++)
                        {
                            int bone = primitive.BoneIndices[index * 4 + k];
                            if (bone >= 0 && !boneRemap.ContainsKey(bone))
                            {
                                boneRemap[bone] = (ushort)boneOrder.Count;
                                boneOrder.Add(bone);
                            }
                        }
                    }

                    meshIndices.Add(local);
                }
            }

            if (meshIndices.Count == 0)
                throw new InvalidOperationException("A single triangle exceeds the per-mesh bone limit.");

            int count = vertexOrder.Count;
            var mesh = new Mesh
            {
                Name = part == 0 ? source.Name : $"{source.Name}_{part}",
                Positions = new Vector3[count],
                Normals = new Vector3[count],
                TexCoords0 = new Vector2[count],
                BlendWeights = new Vector4[count],
                BlendIndices = new Vector4Int[count]
            };

            if (source.Colors != null)
                mesh.Colors0 = new Vector4[count];

            for (int i = 0; i < count; i++)
            {
                uint v = vertexOrder[i];
                mesh.Positions[i] = primitive.Positions[v];
                mesh.Normals[i] = primitive.Normals[v];
                mesh.TexCoords0[i] = source.TexCoords?[v] ?? Vector2.Zero;
                if (mesh.Colors0 != null)
                    mesh.Colors0[i] = source.Colors[v];

                var weights = new float[4];
                var bones = new int[4];
                for (int k = 0; k < 4; k++)
                {
                    int bone = primitive.BoneIndices[v * 4 + k];
                    float weight = primitive.BoneWeights[v * 4 + k];
                    bones[k] = bone >= 0 && weight > 0 ? boneRemap[bone] : -1;
                    weights[k] = bones[k] >= 0 ? weight : 0;
                }

                mesh.BlendWeights[i] = new Vector4(weights[0], weights[1], weights[2], weights[3]);
                mesh.BlendIndices[i] = new Vector4Int(bones[0], bones[1], bones[2], bones[3]);
            }

            mesh.SubMeshes.Add(new SubMesh
            {
                MaterialIndex = materialIndex,
                PrimitiveType = PrimitiveType.Triangles,
                IndexFormat = IndexFormat.UInt16,
                Indices = meshIndices.ToArray(),
                BoneIndices = boneOrder.Select(x => (ushort)x).ToArray(),
                BonesPerVertex = 4
            });

            yield return mesh;
            part++;
        }
    }

    private static Material CreateMaterial(VrmModel model, VrmMaterial source, string objectSetName,
        TextureSet textureSet, Dictionary<(int, Vector4), Texture> textureCache, ObjectBuildOptions options)
    {
        string shader = options.AllCloth ? "CLOTH" : ChooseShader(source.Name);

        var material = new Material
        {
            Name = Truncate(source.Name, 63),
            ShaderName = shader,
            Diffuse = new Vector4(1, 1, 1, source.BaseColor.W),
            Ambient = Vector4.One,
            Specular = new Vector4(0, 0, 0, 1),
            Emission = new Vector4(0, 0, 0, 1),
            Shininess = 1,
            DoubleSided = source.DoubleSided,
            CastShadow = true,
            ReceiveShadow = true
        };

        if (source.BaseColorImage >= 0)
        {
            var tint = source.BaseColor;
            if (!textureCache.TryGetValue((source.BaseColorImage, tint), out var texture))
            {
                var (data, _, imageName) = model.Document.GetImage(source.BaseColorImage);
                var image = RgbaImage.Decode(data).Tinted(tint.X, tint.Y, tint.Z, tint.W);

                string textureName = $"{objectSetName}_{SanitizeName(imageName)}_{textureCache.Count:D2}";
                bool alpha = source.AlphaMode != AlphaMode.Opaque && image.HasTransparency();
                texture = TextureBuilder.Build(image, textureName, MurmurHash.Calculate(textureName),
                    options.MaxTextureSize, alpha);

                textureSet.Textures.Add(texture);
                textureCache[(source.BaseColorImage, tint)] = texture;
            }

            // Factor already baked into the texture.
            material.Diffuse = Vector4.One;

            var materialTexture = new MaterialTexture
            {
                Type = MaterialTextureType.Color,
                TextureId = texture.Id,
                RepeatU = true,
                RepeatV = true,
                Blend = 7,
                Filter = 2,
                MipMap = 2,
                TextureCoordinateTranslationType = MaterialTextureCoordinateTranslationType.UV
            };

            material.MaterialTextures[0] = materialTexture;
            material.Flags |= MaterialFlags.Color;

            if (texture.Format == TextureFormat.DXT5)
                material.Flags |= MaterialFlags.ColorAlpha;
        }
        else
        {
            material.Diffuse = source.BaseColor;
        }

        switch (source.AlphaMode)
        {
            case AlphaMode.Mask:
                material.PunchThrough = true;
                break;
            case AlphaMode.Blend:
                material.AlphaTexture = true;
                material.SrcBlendFactor = BlendFactor.SrcAlpha;
                material.DstBlendFactor = BlendFactor.InverseSrcAlpha;
                break;
        }

        material.SortMaterialTextures();
        return material;
    }

    /// <summary>
    /// VRoid material names identify the part they belong to. Skin-like parts use
    /// DIVA's SKIN shader; everything else uses CLOTH. DIVA's HAIR and EYEBALL
    /// shaders need extra maps (normal / special eye textures), so they are avoided.
    /// </summary>
    internal static string ChooseShader(string materialName)
    {
        string name = materialName.ToUpperInvariant();
        string[] skinKeywords = { "SKIN", "FACE_", "_FACE", "BODY_", "_BODY", "EYEWHITE", "FACEMOUTH", "FACEBROW", "FACEEYE" };
        return skinKeywords.Any(name.Contains) && !name.Contains("CLOTH") ? "SKIN" : "CLOTH";
    }

    private static DivaObject CreateDummyObject(string name, ReferenceBone bone)
    {
        // A single degenerate triangle: renders nothing, but is a valid skinned object
        // that can stand in for the stock body parts the module hides.
        var position = bone.Position;
        var obj = new DivaObject
        {
            Name = name,
            Id = 1,
            Skin = CreateSkin(new[] { bone })
        };

        obj.Materials.Add(new Material { Name = "dummy", ShaderName = "BLINN" });

        var mesh = new Mesh
        {
            Name = "dummy",
            Positions = new[] { position, position, position },
            Normals = new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
            TexCoords0 = new Vector2[3],
            BlendWeights = new[] { new Vector4(1, 0, 0, 0), new Vector4(1, 0, 0, 0), new Vector4(1, 0, 0, 0) },
            BlendIndices = new[] { new Vector4Int(0, -1, -1, -1), new Vector4Int(0, -1, -1, -1), new Vector4Int(0, -1, -1, -1) }
        };

        mesh.SubMeshes.Add(new SubMesh
        {
            MaterialIndex = 0,
            PrimitiveType = PrimitiveType.Triangles,
            IndexFormat = IndexFormat.UInt16,
            Indices = new uint[] { 0, 1, 2 },
            BoneIndices = new ushort[] { 0 },
            BonesPerVertex = 4
        });

        obj.Meshes.Add(mesh);
        AabbCalculator.Calculate(obj);
        return obj;
    }

    internal static string SanitizeName(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray();
        string result = new string(chars).Trim('_');
        return string.IsNullOrEmpty(result) ? "TEX" : Truncate(result, 40);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
