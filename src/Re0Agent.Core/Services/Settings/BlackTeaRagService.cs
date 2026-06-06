using System.Text;
using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Settings;

public sealed class BlackTeaRagService(
    IBlackTeaImporter importer,
    ChapterVariantRenderer chapterVariantRenderer) : IRagService
{
    private const string DefaultRelativePath = "data/settings/REZero_BlackTea_v2.0.0.json";

    private readonly SemaphoreSlim loadLock = new(1, 1);
    private IReadOnlyList<WorldBookEntry>? cachedEntries;

    public async Task<RagContext> QueryAsync(RagQuery query, CancellationToken cancellationToken = default)
    {
        var entries = await LoadEntriesAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return new RagContext();
        }

        var constantMatches = entries
            .Where(entry => entry.Enabled && entry.Constant)
            .Select(entry => CreateMatch(entry, 0, [], query.Chapter))
            .OrderBy(match => match.Entry.InsertionOrder)
            .ThenBy(match => match.Entry.Id)
            .ToList();

        var keywordMatches = entries
            .Where(entry => entry.Enabled && !entry.Constant)
            .Select(entry => TryMatch(entry, query.Text, query.Chapter))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entry.InsertionOrder)
            .ThenBy(match => match.Entry.Id)
            .Take(Math.Max(0, query.MaxNonConstantEntries))
            .ToList();

        return BuildContext(constantMatches, keywordMatches, query.MaxCharacters);
    }

    private async Task<IReadOnlyList<WorldBookEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        if (cachedEntries is not null)
        {
            return cachedEntries;
        }

        await loadLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedEntries is not null)
            {
                return cachedEntries;
            }

            var path = FindDefaultWorldBookPath();
            cachedEntries = path is null
                ? []
                : await importer.ImportAsync(path, cancellationToken);

            return cachedEntries;
        }
        finally
        {
            loadLock.Release();
        }
    }

    private RagMatch? TryMatch(WorldBookEntry entry, string text, int chapter)
    {
        var primaryMatches = MatchKeys(entry.Keys, text, entry.UseRegex);
        if (primaryMatches.Count == 0)
        {
            return null;
        }

        var secondaryMatches = MatchKeys(entry.SecondaryKeys, text, entry.UseRegex);
        if (entry.SecondaryKeys.Count > 0 && secondaryMatches.Count == 0)
        {
            return null;
        }

        var matchedKeys = primaryMatches.Concat(secondaryMatches).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var score = primaryMatches.Count * 10 + secondaryMatches.Count * 4;

        return CreateMatch(entry, score, matchedKeys, chapter);
    }

    private RagMatch CreateMatch(
        WorldBookEntry entry,
        int score,
        IReadOnlyList<string> matchedKeys,
        int chapter)
    {
        return new RagMatch
        {
            Entry = entry,
            Score = score,
            MatchedKeys = matchedKeys,
            RenderedContent = chapterVariantRenderer.Render(entry.Content, chapter).Trim()
        };
    }

    private static List<string> MatchKeys(IReadOnlyList<string> keys, string text, bool useRegex)
    {
        var matches = new List<string>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (MatchesKey(key, text, useRegex))
            {
                matches.Add(key);
            }
        }

        return matches;
    }

    private static bool MatchesKey(string key, string text, bool useRegex)
    {
        if (useRegex)
        {
            try
            {
                return Regex.IsMatch(
                    text,
                    key,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                // Invalid BlackTea regex keys fall back to plain keyword matching.
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return text.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    private static RagContext BuildContext(
        IReadOnlyList<RagMatch> constantMatches,
        IReadOnlyList<RagMatch> keywordMatches,
        int maxCharacters)
    {
        var acceptedMatches = new List<RagMatch>();
        var builder = new StringBuilder();
        var remaining = Math.Max(0, maxCharacters);
        var reservedForKeywords = keywordMatches.Count == 0 ? 0 : maxCharacters / 3;

        foreach (var match in constantMatches)
        {
            if (remaining <= reservedForKeywords)
            {
                break;
            }

            AppendMatch(match, builder, acceptedMatches, ref remaining, remaining - reservedForKeywords);
        }

        foreach (var match in keywordMatches)
        {
            AppendMatch(match, builder, acceptedMatches, ref remaining, remaining);
        }

        return new RagContext
        {
            Matches = acceptedMatches,
            Content = builder.ToString().Trim()
        };
    }

    private static void AppendMatch(
        RagMatch match,
        StringBuilder builder,
        List<RagMatch> acceptedMatches,
        ref int remaining,
        int perCallBudget)
    {
        if (string.IsNullOrWhiteSpace(match.RenderedContent) || remaining <= 0 || perCallBudget <= 0)
        {
            return;
        }

        var block = $"""
        <设定条目 id="{match.Entry.Id}" name="{match.Entry.Comment}">
        {match.RenderedContent}
        </设定条目>

        """;

        var budget = Math.Min(remaining, perCallBudget);
        if (block.Length > budget)
        {
            block = block[..budget];
        }

        builder.Append(block);
        acceptedMatches.Add(match);
        remaining -= block.Length;
    }

    private static string? FindDefaultWorldBookPath()
    {
        foreach (var basePath in CandidateBasePaths())
        {
            var current = new DirectoryInfo(basePath);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, DefaultRelativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateBasePaths()
    {
        yield return AppContext.BaseDirectory;
        yield return Environment.CurrentDirectory;
    }
}
