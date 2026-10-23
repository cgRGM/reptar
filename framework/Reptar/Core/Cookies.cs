using System.Globalization;

namespace Reptar;

public sealed class CookieJar
{
    private readonly ReptarRequest _request;
    internal List<string> Pending { get; } = new();

    internal CookieJar(ReptarRequest request) => _request = request;

    public string? Get(string name) =>
        _request.Cookies.TryGetValue(name, out var value) ? value : null;

    public void Set(string name, string value, string? expiresIso = null, string? domain = null)
    {
        var parts = new List<string> { $"{name}={value}", "Path=/" };

        if (expiresIso is not null &&
            DateTimeOffset.TryParse(
                expiresIso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expires))
        {
            parts.Add($"Expires={expires.ToString("R", CultureInfo.InvariantCulture)}");
        }

        if (domain is not null)
            parts.Add($"Domain={domain}");

        parts.Add("HttpOnly");
        parts.Add("SameSite=Lax");
        Pending.Add(string.Join("; ", parts));
    }

    public void Delete(string name, string? domain = null) =>
        Set(name, "", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), domain);
}
