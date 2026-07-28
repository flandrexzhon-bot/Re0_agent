using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Agent;

public sealed partial class FeatureHashMemoryEmbeddingProvider : IMemoryEmbeddingProvider
{
    private const int Dimensions = 256;

    public string ModelId => "feature-hash-v1";

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vector = new float[Dimensions];
        var normalized = Whitespace().Replace(text.Trim().ToLowerInvariant(), " ");
        foreach (var token in Tokenize(normalized))
        {
            var hash = StableHash(token);
            var index = (int)(hash % Dimensions);
            vector[index] += (hash & 1) == 0 ? 1 : -1;
        }
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
        {
            for (var index = 0; index < vector.Length; index++) vector[index] /= (float)norm;
        }
        return Task.FromResult(vector);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in Word().Matches(text)) yield return match.Value;
        for (var index = 0; index + 1 < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]) && !char.IsWhiteSpace(text[index + 1]))
                yield return text.Substring(index, 2);
        }
    }

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("[\\p{L}\\p{N}_]+")]
    private static partial Regex Word();
}
