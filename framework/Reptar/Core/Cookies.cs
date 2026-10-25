using System.Globalization;
#if !NETWASM
using System.Security.Cryptography;
#endif
using System.Text;

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

    public void Set(string name, string value, TimeSpan maxAge, string? domain = null)
    {
        var parts = new List<string>
        {
            $"{name}={value}",
            "Path=/",
            $"Max-Age={Math.Max(0, (long)maxAge.TotalSeconds)}",
        };

        if (domain is not null)
            parts.Add($"Domain={domain}");

        parts.Add("HttpOnly");
        parts.Add("SameSite=Lax");
        Pending.Add(string.Join("; ", parts));
    }

#if !NETWASM
    public void SetSigned(
        string name,
        string value,
        string secret,
        TimeSpan? maxAge = null,
        string? domain = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var signature = ComputeHmacBase64Url(secret, $"{name}:{value}");
        var signedValue = $"{value}.{signature}";

        if (maxAge.HasValue)
        {
            Set(name, signedValue, maxAge.Value, domain);
        }
        else
        {
            Set(name, signedValue, expiresIso: null, domain);
        }
    }

    public string? GetSigned(string name, string secret)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(secret))
            return null;

        var raw = Get(name);
        if (string.IsNullOrEmpty(raw))
            return null;

        var separatorIndex = raw.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
            return null;

        var value = raw[..separatorIndex];
        var providedSignature = raw[(separatorIndex + 1)..];
        var expectedSignature = ComputeHmacBase64Url(secret, $"{name}:{value}");

        var providedBytes = Encoding.ASCII.GetBytes(providedSignature);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedSignature);

        return FixedTimeEquals(providedBytes, expectedBytes)
            ? value
            : null;
    }
#endif

    public async Task SetSignedAsync(
        ICrypto crypto,
        string name,
        string value,
        string secret,
        TimeSpan? maxAge = null,
        string? domain = null)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var signature = await crypto.HmacSha256(secret, $"{name}:{value}");
        var signedValue = $"{value}.{signature}";

        if (maxAge.HasValue)
        {
            Set(name, signedValue, maxAge.Value, domain);
        }
        else
        {
            Set(name, signedValue, expiresIso: null, domain);
        }
    }

    public async Task<string?> GetSignedAsync(ICrypto crypto, string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(secret))
            return null;

        var raw = Get(name);
        if (string.IsNullOrEmpty(raw))
            return null;

        var separatorIndex = raw.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
            return null;

        var value = raw[..separatorIndex];
        var providedSignature = raw[(separatorIndex + 1)..];
        var expectedSignature = await crypto.HmacSha256(secret, $"{name}:{value}");

        var providedBytes = Encoding.ASCII.GetBytes(providedSignature);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedSignature);

        return FixedTimeEquals(providedBytes, expectedBytes)
            ? value
            : null;
    }

    public void Delete(string name, string? domain = null) =>
        Set(name, "", TimeSpan.Zero, domain);

    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }

#if !NETWASM
    private static string ComputeHmacBase64Url(string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, dataBytes);
        return ToBase64Url(hash);
    }
#endif

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
