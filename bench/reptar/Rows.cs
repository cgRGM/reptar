using Reptar;

namespace ReptarBench;

[ReptarEntity]
public partial record AuthUserRow(string Id, string Email, string Name, string CreatedAt);

[ReptarEntity]
public partial record AuthUserWithPasswordRow(
    string Id,
    string Email,
    string Name,
    string PasswordHash,
    string CreatedAt);

[ReptarEntity]
public partial record TaskDetailRow(
    string Id,
    string ProjectId,
    string Title,
    string Description,
    string Status,
    int Priority,
    string AuthorId,
    string AuthorName,
    string? AssigneeId,
    string? AssigneeName,
    string CreatedAt,
    string UpdatedAt);

[ReptarEntity]
public partial record CommentRow(
    string Id,
    string TaskId,
    string Body,
    string CreatedAt,
    string AuthorId,
    string AuthorName);
