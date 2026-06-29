using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Re0Agent.Core.Services.Llm;

/// <summary>SSE 流中的一个事件：内容增量(Content)或末尾的用量统计(Usage)，两者互斥。</summary>
public readonly record struct SseStreamItem(string? Content, LlmUsage? Usage);

public static class SseParser
{
    public static async IAsyncEnumerable<string> ReadContentDeltasAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ReadStreamAsync(reader, cancellationToken))
        {
            if (item.Content is not null)
            {
                yield return item.Content;
            }
        }
    }

    /// <summary>
    /// 读取 SSE 流，既产出内容增量，也产出末尾的 usage 块
    /// （需请求时带 stream_options.include_usage=true；该块的 choices 为空数组）。
    /// </summary>
    public static async IAsyncEnumerable<SseStreamItem> ReadStreamAsync(
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

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                yield break;
            }

            var item = TryReadStreamItem(payload);
            if (item is not null)
            {
                yield return item.Value;
            }
        }
    }

    private static SseStreamItem? TryReadStreamItem(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            // 内容增量：choices[0].delta.content。
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return new SseStreamItem(content.GetString(), null);
            }

            // 末尾用量块：choices 通常为空数组，usage 非空。
            var usage = ParseUsage(root);
            if (usage is not null)
            {
                return new SseStreamItem(null, usage);
            }

            return null;
        }
    }

    private static LlmUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int prompt = ReadInt(usage, "prompt_tokens");
        int completion = ReadInt(usage, "completion_tokens");

        int cached = ReadInt(usage, "prompt_cache_hit_tokens")
            + ReadInt(usage, "cache_read_input_tokens");
        if (cached == 0
            && usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            cached += ReadInt(details, "cached_tokens");
        }

        int cacheWrite = ReadInt(usage, "cache_creation_input_tokens");

        return new LlmUsage(prompt, completion, cached, cacheWrite);
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

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
