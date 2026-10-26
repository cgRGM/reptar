namespace {{PROJECT_PASCAL}}.Features;

// Concrete response records (never anonymous objects): Native AOT cannot
// serialize compiler-generated anonymous types without reflection metadata.
// Each type is registered in json-context.cs.

/// <summary>Liveness probe payload.</summary>
public sealed record HealthResponse(bool Ok, string Timestamp);

/// <summary>Public profile shape returned by /api/me.</summary>
public sealed record UserProfile(
    string Id,
    string Name,
    string Email,
    string? Image,
    string Role);

/// <summary>Session-aware profile response.</summary>
public sealed record AuthMeResponse(bool Authenticated, UserProfile? User);

/// <summary>Small mutation acknowledgement.</summary>
public sealed record ActionSuccessResponse(bool Success);
