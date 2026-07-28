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
        await dbContext.MemoryEmbeddings.ExecuteDeleteAsync(cancellationToken);
        var memories = await dbContext.CharacterMemory.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken);
        foreach (var memory in memories)
        {
            var vector = await provider.EmbedAsync(memory.MemoryText, cancellationToken);
            dbContext.MemoryEmbeddings.Add(new MemoryEmbedding
            {
                MemoryRowId = memory.RowId,
                ModelId = provider.ModelId,
                Dimensions = vector.Length,
                VectorJson = JsonSerializer.Serialize(vector),
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(memory.MemoryText))),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length) return double.NegativeInfinity;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; leftNorm += left[i] * left[i]; rightNorm += right[i] * right[i]; }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
