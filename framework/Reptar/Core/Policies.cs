namespace Reptar;

/// <summary>
/// Zero-reflection authorization manager for declarative resource access policies.
/// Similar to AdonisJS Bouncer and Laravel Gates, policies decouple permission
/// business logic from HTTP handlers.
/// </summary>
public interface IPolicyManager
{
    void Define<TResource>(string action, Func<SessionUser?, TResource, bool> policy);
    bool Can<TResource>(SessionUser? user, string action, TResource resource);
}

public sealed class PolicyManager : IPolicyManager
{
    private readonly Dictionary<string, Delegate> _policies = new(StringComparer.OrdinalIgnoreCase);

    public void Define<TResource>(string action, Func<SessionUser?, TResource, bool> policy)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(policy);
        _policies[$"{typeof(TResource).FullName}:{action.ToLowerInvariant()}"] = policy;
    }

    public bool Can<TResource>(SessionUser? user, string action, TResource resource)
    {
        ArgumentNullException.ThrowIfNull(action);
        var key = $"{typeof(TResource).FullName}:{action.ToLowerInvariant()}";
        if (_policies.TryGetValue(key, out var del) && del is Func<SessionUser?, TResource, bool> policy)
        {
            return policy(user, resource);
        }
        return false;
    }
}
