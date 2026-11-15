using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClinicalRag.Api.Services;

public sealed record EmbeddingBatch(string Model, int Dimensions, IReadOnlyList<byte[]> Vectors);

public sealed class EmbeddingModelMismatchException : InvalidOperationException
{
    public EmbeddingModelMismatchException() : base("Embedding model or dimensions do not match. Re-embed the workspace with a single model.") { }
}

/// <summary>Validated, unit-length float32 vectors in little-endian binary form.</summary>
public static class EmbeddingVectors
{
    public static bool ValidModel(string? model) => !string.IsNullOrWhiteSpace(model)
        && model.Length <= 256 && model == model.Trim() && !model.Any(char.IsControl);

    public static EmbeddingBatch Parse(JsonElement response, int expectedCount)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("model", out var modelElement) || modelElement.ValueKind != JsonValueKind.String
            || !ValidModel(modelElement.GetString())
            || !response.TryGetProperty("embeddings", out var vectors) || vectors.ValueKind != JsonValueKind.Array
            || vectors.GetArrayLength() != expectedCount || expectedCount < 1)
            throw new InvalidDataException("Missing model identity or unexpected embedding count.");
        var result = new List<byte[]>(expectedCount);
        int? dimensions = null;
        foreach (var vector in vectors.EnumerateArray())
        {
            if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() < 1
                || vector.GetArrayLength() > RetrievalLimits.MaxEmbeddingDimensions
                || (dimensions.HasValue && vector.GetArrayLength() != dimensions.Value))
                throw new InvalidDataException("Invalid embedding dimensions.");
            dimensions = vector.GetArrayLength();
            var values = new float[dimensions.Value];
            var i = 0;
            foreach (var value in vector.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
                    throw new InvalidDataException("Invalid float32 embedding number.");
                values[i++] = number;
            }
            result.Add(EncodeNormalized(values));
        }
        if (!response.TryGetProperty("dimensions", out var declared) || declared.ValueKind != JsonValueKind.Number || !declared.TryGetInt32(out var declaredDimensions)
            || declaredDimensions != dimensions)
            throw new InvalidDataException("Missing or mismatched embedding dimensions.");
        return new(response.GetProperty("model").GetString()!, dimensions!.Value, result);
    }

    public static byte[] EncodeNormalized(ReadOnlySpan<float> values)
    {
        if (values.Length < 1 || values.Length > RetrievalLimits.MaxEmbeddingDimensions)
            throw new InvalidDataException("Invalid embedding dimensions.");
        // Accumulate in double so finite float32 extremes do not overflow/underflow
        // while normalising. Only the stored/query representation is float32.
        double normSquared = 0;
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite embedding number.");
            normSquared += (double)value * value;
        }
        if (!double.IsFinite(normSquared) || normSquared <= 0) throw new InvalidDataException("Invalid embedding norm.");
        var norm = Math.Sqrt(normSquared);
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), (float)(values[i] / norm));
        return bytes;
    }

    public static float[] ReadNormalized(byte[] bytes) => TryReadNormalized(bytes, out var vector)
        ? vector : throw new InvalidDataException("Invalid stored float32 embedding.");

    public static bool TryReadNormalized(byte[] bytes, out float[] vector)
    {
        vector = [];
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0 || bytes.Length / sizeof(float) > RetrievalLimits.MaxEmbeddingDimensions)
            return false;
        var values = new float[bytes.Length / sizeof(float)];
        if (BitConverter.IsLittleEndian) MemoryMarshal.Cast<byte, float>(bytes).CopyTo(values);
        else for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        if (values.Any(value => !float.IsFinite(value))) return false;
        var normSquared = TensorPrimitives.Dot(values, values);
        if (!float.IsFinite(normSquared) || Math.Abs(normSquared - 1) > .001f) return false;
        vector = values;
        return true;
    }
}
