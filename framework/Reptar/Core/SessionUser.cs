namespace Reptar;

public sealed record SessionUser(
    string Id,
    string Email,
    string Name,
    string Role = "user",
    string? Image = null,
    int EmailVerified = 0);

public sealed record SessionResponse(SessionUser? User);
