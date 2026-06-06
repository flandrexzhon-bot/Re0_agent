namespace Re0Agent.Core.Services.Database;

public sealed record SqlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static SqlValidationResult Valid { get; } = new(true, null);
    public static SqlValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}

public sealed record SqlExecutionResult(int StatementsExecuted, IReadOnlyList<string> ExecutedStatements);
