using System.Reflection;
using System.Text.Json;
using ClinicalRag.Api.Models;

namespace ClinicalRag.Tests;

internal static class EmbeddingTestData
{
    public const string Model = "synthetic-model";
    private static readonly PropertyInfo Property = typeof(Chunk).GetProperty(nameof(Chunk.Embedding))!;

    // Allows the behavioural red tests to run before the storage migration.
    public static void Set(Chunk chunk, params float[] values)
    {
        var norm = Math.Sqrt(values.Sum(value => (double)value * value));
        var vector = values.Select(value => (float)(value / norm)).ToArray();
        if (Property.PropertyType == typeof(byte[]))
        {
            var bytes = new byte[vector.Length * sizeof(float)];
            for (var i = 0; i < vector.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), vector[i]);
            Property.SetValue(chunk, bytes);
        }
        else Property.SetValue(chunk, JsonSerializer.Serialize(vector));
        chunk.EmbeddingModel = Model;
    }

    public static JsonElement Result(int count, string model = Model, params float[] vector) =>
        JsonSerializer.SerializeToElement(new { model, dimensions = vector.Length, embeddings = Enumerable.Range(0, count).Select(_ => vector) });
}
