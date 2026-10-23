namespace Reptar;

public sealed record SessionUser(string Id, string Email, string Name);

public sealed record SessionResponse(SessionUser? User);
