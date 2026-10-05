using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace VRoidDiva.Vrm;

/// <summary>
/// Minimal binary glTF 2.0 (.glb / .vrm) reader. Only the parts needed to
/// pull a skinned humanoid out of a VRoid export are implemented.
/// </summary>
public sealed class GltfDocument
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBin = 0x004E4942; // "BIN\0"

    public JsonObject Json { get; }
    public byte[] BinaryChunk { get; }

    private GltfDocument(JsonObject json, byte[] binaryChunk)
    {
        Json = json;
        BinaryChunk = binaryChunk;
    }

    public static GltfDocument Load(string filePath) => Load(File.ReadAllBytes(filePath));

    public static GltfDocument Load(byte[] data)
    {
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != GlbMagic)
            throw new InvalidDataException("Not a binary glTF/VRM file (missing 'glTF' magic).");

        uint version = BitConverter.ToUInt32(data, 4);
        if (version != 2)
            throw new InvalidDataException($"Unsupported glTF version {version} (expected 2).");

        int totalLength = (int)Math.Min(BitConverter.ToUInt32(data, 8), (uint)data.Length);

        JsonObject json = null;
        byte[] bin = null;
        int offset = 12;

        while (offset + 8 <= totalLength)
        {
            int chunkLength = (int)BitConverter.ToUInt32(data, offset);
            uint chunkType = BitConverter.ToUInt32(data, offset + 4);
            offset += 8;

            if (offset + chunkLength > totalLength)
                throw new InvalidDataException("Truncated glTF chunk.");

            if (chunkType == ChunkJson && json == null)
                json = JsonNode.Parse(Encoding.UTF8.GetString(data, offset, chunkLength))!.AsObject();
            else if (chunkType == ChunkBin && bin == null)
                bin = data.AsSpan(offset, chunkLength).ToArray();

            offset += (chunkLength + 3) & ~3;
        }

        if (json == null)
            throw new InvalidDataException("glTF file has no JSON chunk.");

        return new GltfDocument(json, bin ?? Array.Empty<byte>());
    }

    public JsonArray GetArray(string name) => Json[name] as JsonArray ?? new JsonArray();

    public ReadOnlySpan<byte> GetBufferViewBytes(int bufferViewIndex)
    {
        var view = GetArray("bufferViews")[bufferViewIndex]!.AsObject();
        int buffer = view["buffer"]?.GetValue<int>() ?? 0;
        if (buffer != 0)
            throw new NotSupportedException("Only the embedded GLB buffer (buffer 0) is supported.");

        int byteOffset = view["byteOffset"]?.GetValue<int>() ?? 0;
        int byteLength = view["byteLength"]!.GetValue<int>();
        return BinaryChunk.AsSpan(byteOffset, byteLength);
    }

    private static int ComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => throw new InvalidDataException($"Unknown accessor type '{type}'.")
    };

    private static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new InvalidDataException($"Unknown accessor component type {componentType}.")
    };

    /// <summary>
    /// Reads an accessor as a flat float array (count * components). Normalized
    /// integer data is converted to [0,1] / [-1,1]; other integers are converted as-is.
    /// </summary>
    public float[] ReadFloats(int accessorIndex, out int components) =>
        ReadAccessor(accessorIndex, out components, allowNormalize: true);

    /// <summary>Reads integer data (indices, joints); normalization is never applied.</summary>
    public int[] ReadInts(int accessorIndex, out int components)
    {
        var floats = ReadAccessor(accessorIndex, out components, allowNormalize: false);
        var ints = new int[floats.Length];
        for (int i = 0; i < floats.Length; i++)
            ints[i] = (int)floats[i];
        return ints;
    }

    private float[] ReadAccessor(int accessorIndex, out int components, bool allowNormalize)
    {
        var accessor = GetArray("accessors")[accessorIndex]!.AsObject();
        int count = accessor["count"]!.GetValue<int>();
        int componentType = accessor["componentType"]!.GetValue<int>();
        components = ComponentCount(accessor["type"]!.GetValue<string>());
        bool normalized = allowNormalize && (accessor["normalized"]?.GetValue<bool>() ?? false);

        var result = new float[count * components];

        if (accessor["bufferView"] != null)
        {
            int viewIndex = accessor["bufferView"]!.GetValue<int>();
            var view = GetArray("bufferViews")[viewIndex]!.AsObject();
            int componentSize = ComponentSize(componentType);
            int stride = view["byteStride"]?.GetValue<int>() ?? componentSize * components;
            int accessorOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
            var bytes = GetBufferViewBytes(viewIndex);

            for (int i = 0; i < count; i++)
            {
                int elementOffset = accessorOffset + i * stride;
                for (int c = 0; c < components; c++)
                {
                    result[i * components + c] = ReadComponent(bytes, elementOffset + c * componentSize,
                        componentType, normalized);
                }
            }
        }

        if (accessor["sparse"] is JsonObject sparse)
            ApplySparse(sparse, componentType, result, components, normalized);

        return result;
    }

    private void ApplySparse(JsonObject sparse, int valueType, float[] result, int components, bool normalized)
    {
        int count = sparse["count"]!.GetValue<int>();
        var indicesInfo = sparse["indices"]!.AsObject();
        var valuesInfo = sparse["values"]!.AsObject();

        var indexBytes = GetBufferViewBytes(indicesInfo["bufferView"]!.GetValue<int>());
        int indexOffset = indicesInfo["byteOffset"]?.GetValue<int>() ?? 0;
        int indexType = indicesInfo["componentType"]!.GetValue<int>();
        int indexSize = ComponentSize(indexType);

        var valueBytes = GetBufferViewBytes(valuesInfo["bufferView"]!.GetValue<int>());
        int valueOffset = valuesInfo["byteOffset"]?.GetValue<int>() ?? 0;
        int valueSize = ComponentSize(valueType);

        for (int i = 0; i < count; i++)
        {
            int target = (int)ReadComponent(indexBytes, indexOffset + i * indexSize, indexType, false);
            for (int c = 0; c < components; c++)
            {
                result[target * components + c] = ReadComponent(valueBytes,
                    valueOffset + (i * components + c) * valueSize, valueType, normalized);
            }
        }
    }

    private static float ReadComponent(ReadOnlySpan<byte> bytes, int offset, int componentType, bool normalized)
    {
        switch (componentType)
        {
            case 5120:
            {
                sbyte v = (sbyte)bytes[offset];
                return normalized ? Math.Max(v / 127f, -1f) : v;
            }
            case 5121:
            {
                byte v = bytes[offset];
                return normalized ? v / 255f : v;
            }
            case 5122:
            {
                short v = BitConverter.ToInt16(bytes.Slice(offset, 2));
                return normalized ? Math.Max(v / 32767f, -1f) : v;
            }
            case 5123:
            {
                ushort v = BitConverter.ToUInt16(bytes.Slice(offset, 2));
                return normalized ? v / 65535f : v;
            }
            case 5125:
                return BitConverter.ToUInt32(bytes.Slice(offset, 4));
            case 5126:
                return BitConverter.ToSingle(bytes.Slice(offset, 4));
            default:
                throw new InvalidDataException($"Unknown accessor component type {componentType}.");
        }
    }

    public static Matrix4x4 ReadMatrix(float[] values, int index)
    {
        // glTF matrices are column-major for column vectors, which is exactly the
        // row-major layout System.Numerics uses for row vectors.
        int o = index * 16;
        return new Matrix4x4(
            values[o + 0], values[o + 1], values[o + 2], values[o + 3],
            values[o + 4], values[o + 5], values[o + 6], values[o + 7],
            values[o + 8], values[o + 9], values[o + 10], values[o + 11],
            values[o + 12], values[o + 13], values[o + 14], values[o + 15]);
    }

    /// <summary>Returns the encoded bytes and MIME type of an image.</summary>
    public (byte[] Data, string MimeType, string Name) GetImage(int imageIndex)
    {
        var image = GetArray("images")[imageIndex]!.AsObject();
        string name = image["name"]?.GetValue<string>() ?? $"image{imageIndex}";
        string mime = image["mimeType"]?.GetValue<string>() ?? "image/png";

        if (image["bufferView"] != null)
            return (GetBufferViewBytes(image["bufferView"]!.GetValue<int>()).ToArray(), mime, name);

        string uri = image["uri"]?.GetValue<string>();
        if (uri != null && uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = uri.IndexOf(',');
            return (Convert.FromBase64String(uri[(comma + 1)..]), mime, name);
        }

        throw new NotSupportedException($"Image {imageIndex} uses an external URI, which is not supported for .vrm files.");
    }
}
