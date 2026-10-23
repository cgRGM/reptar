namespace Reptar;

public sealed record ErrorBody(string Code, string Message, string? RequestId);

public sealed record ValidationIssue(string Message, string[]? Path);

public sealed record ValidationError(
    string Code,
    string Message,
    string? RequestId,
    IReadOnlyList<ValidationIssue> Issues);
