namespace D4LootBench.Core.Validation;

public enum ValidationSeverity { Info, Warning, Error }

/// <summary>
/// A single validation finding. <see cref="RuleIndex"/> is null for filter-level issues
/// (e.g. rule-count limit); otherwise it points at the offending rule so the UI can
/// jump to it.
/// </summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Message,
    int? RuleIndex = null);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public static readonly ValidationResult Empty = new([]);

    public bool IsValid => Issues.All(i => i.Severity != ValidationSeverity.Error);
    public bool HasIssues => Issues.Count > 0;
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == ValidationSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == ValidationSeverity.Warning);
}
