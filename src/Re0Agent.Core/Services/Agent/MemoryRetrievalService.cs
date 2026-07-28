using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

public interface IMemoryEmbeddingProvider
{
    string ModelId { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class MemoryRetrievalService(Re0AgentDbContext dbContext)
{
    public async Task<IReadOnlyList<CharacterMemory>> RetrieveAsync(
        string ownerCharacterId,
        int worldEpoch,
        float[]? queryVector = null,
        string? modelId = null,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var allowed = await dbContext.CharacterMemory.AsNoTracking()
            .Where(item => item.OwnerCharacterId == ownerCharacterId
                && (item.WorldEpoch == worldEpoch || item.RetainOnRewind == 1))
            .OrderByDescending(item => item.RowId).Take(128).ToListAsync(cancellationToken);
        allowed = allowed.Where(item => IsVisibleToOwner(item, ownerCharacterId)).ToList();
        if (queryVector is null || string.IsNullOrWhiteSpace(modelId)) return allowed.Take(limit).ToList();
        var ids = allowed.Select(item => item.RowId).ToList();
        var vectors = await dbContext.MemoryEmbeddings.AsNoTracking()
            .Where(item => ids.Contains(item.MemoryRowId) && item.ModelId == modelId && item.Dimensions == queryVector.Length)
            .ToListAsync(cancellationToken);
        if (vectors.Count == 0) return allowed.Take(limit).ToList();
        var byId = allowed.ToDictionary(item => item.RowId);
        return vectors.Select(item => (Memory: byId[item.MemoryRowId], Score: Cosine(queryVector, JsonSerializer.Deserialize<float[]>(item.VectorJson) ?? [])))
            .OrderByDescending(item => item.Score).ThenByDescending(item => item.Memory.RowId)
            .Take(limit).Select(item => item.Memory).ToList();
    }

    public async Task RebuildAsync(IMemoryEmbeddingProvider provider, CancellationToken cancellationToken = default)
    {
        var memories = await dbContext.CharacterMemory.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken);
        var existing = await dbContext.MemoryEmbeddings.Where(item => item.ModelId == provider.ModelId).ToDictionaryAsync(item => item.MemoryRowId, cancellationToken);
        foreach (var memory in memories)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(memory.MemoryText)));
            if (existing.TryGetValue(memory.RowId, out var current) && current.ContentHash == hash && current.WorldEpoch == memory.WorldEpoch) continue;
            var vector = await provider.EmbedAsync(memory.MemoryText, cancellationToken);
            var embedding = existing.GetValueOrDefault(memory.RowId) ?? new MemoryEmbedding
            {
                MemoryRowId = memory.RowId,
                ModelId = provider.ModelId,
                VectorJson = "[]",
                ContentHash = "",
                CreatedAt = ""
            };
            embedding.Dimensions = vector.Length;
            embedding.WorldEpoch = memory.WorldEpoch;
            embedding.VectorJson = JsonSerializer.Serialize(vector);
            embedding.ContentHash = hash;
            embedding.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (embedding.MemoryRowId == memory.RowId && !existing.ContainsKey(memory.RowId)) dbContext.MemoryEmbeddings.Add(embedding);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsVisibleToOwner(CharacterMemory memory, string ownerCharacterId)
    {
        try
        {
            using var json = JsonDocument.Parse(memory.VisibilityScope);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
                return json.RootElement.EnumerateArray().Any(item => item.GetString() == ownerCharacterId);
            return json.RootElement.TryGetProperty("direct", out var direct)
                && direct.EnumerateArray().Any(item => item.GetString() == ownerCharacterId);
        }
        catch (JsonException) { return false; }
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return double.NegativeInfinity;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; leftNorm += left[i] * left[i]; rightNorm += right[i] * right[i]; }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
