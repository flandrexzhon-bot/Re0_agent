namespace Re0Agent.Core.Services.Settings;

public interface IBlackTeaImporter
{
    Task<IReadOnlyList<WorldBookEntry>> ImportAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
