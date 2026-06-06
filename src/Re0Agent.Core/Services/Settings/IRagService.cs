namespace Re0Agent.Core.Services.Settings;

public interface IRagService
{
    Task<RagContext> QueryAsync(RagQuery query, CancellationToken cancellationToken = default);

    /// <summary>返回全部世界书条目（用于前端只读浏览内置设定）。</summary>
    Task<IReadOnlyList<WorldBookEntry>> ListAllEntriesAsync(CancellationToken cancellationToken = default);
}
