using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Re0Agent.Core.Services.Llm;

public static class SseParser
{
    public static async IAsyncEnumerable<string> ReadContentDeltasAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                yield break;
            }

            var delta = TryReadContentDelta(line);
            if (delta is null)
            {
                continue;
            }

            if (delta == "[DONE]")
            {
                yield break;
            }

            yield return delta;
        }
    }

    public static string? TryReadContentDelta(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var payload = line["data:".Length..].Trim();
        if (payload == "[DONE]")
        {
            return payload;
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
        {
            return null;
        }

        if (!delta.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.GetString();
    }
}
