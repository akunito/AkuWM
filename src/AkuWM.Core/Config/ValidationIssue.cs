namespace AkuWM.Core.Config;

public enum Severity
{
    /// <summary>Worth saying, the configuration still loads.</summary>
    Warning,

    /// <summary>The configuration is refused; the running one stays.</summary>
    Error,
}

/// <summary>One thing wrong with a configuration, and where it is.</summary>
/// <param name="Severity">Whether it refuses the file or only warns.</param>
/// <param name="Path">A JSON-ish path, e.g. <c>rules[3].actions</c>.</param>
/// <param name="Message">What is wrong, in one line.</param>
public readonly record struct ValidationIssue(Severity Severity, string Path, string Message)
{
    public static ValidationIssue Error(string path, string message) =>
        new(Severity.Error, path, message);

    public static ValidationIssue Warning(string path, string message) =>
        new(Severity.Warning, path, message);

    public override string ToString() =>
        $"{(Severity == Severity.Error ? "error" : "warning")}: {Path}: {Message}";
}

/// <summary>The outcome of validating one configuration.</summary>
public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<ValidationIssue> issues) => Issues = issues;

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == Severity.Error);

    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == Severity.Warning);

    /// <summary>True when nothing refuses the file. Warnings do not.</summary>
    public bool Ok => !Errors.Any();
}
