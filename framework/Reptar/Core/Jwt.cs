using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed class JwtOptions
{
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "reptar";
    public string Audience { get; set; } = "reptar";
    public int LifetimeMinutes { get; set; } = 60;
}

public sealed record JwtPayload(
    string Subject,
    string? Email,
    string? Name,
    string? Role,
    long IssuedAt,
    long ExpiresAt);

public static class Jwt
{
    public static async Task<string> SignAsync(
        ICrypto crypto,
        JwtOptions options,
        JwtPayload payload)
    {
        if (string.IsNullOrWhiteSpace(options.Secret))
            throw new InvalidOperationException("JWT secret is not configured.");

        var header = Base64Url(Encoding.UTF8.GetBytes(
            "{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));

        var body =
            $"{{\"sub\":{Json(payload.Subject)},\"email\":{Json(payload.Email)},\"name\":{Json(payload.Name)},\"role\":{Json(payload.Role)},\"iat\":{payload.IssuedAt},\"exp\":{payload.ExpiresAt},\"iss\":{Json(options.Issuer)},\"aud\":{Json(options.Audience)}}}";

        var unsigned = $"{header}.{Base64Url(Encoding.UTF8.GetBytes(body))}";
        var signature = await crypto.HmacSha256(options.Secret, unsigned);

        return $"{unsigned}.{Base64Url(HexToBytes(signature))}";
    }

    public static async Task<JwtPayload?> VerifyAsync(
        ICrypto crypto,
        JwtOptions options,
        string token)
    {
        if (string.IsNullOrWhiteSpace(options.Secret))
            return null;

        var parts = token.Split('.');

        if (parts.Length != 3)
            return null;

        var unsigned = $"{parts[0]}.{parts[1]}";
        var expected = Base64Url(HexToBytes(
            await crypto.HmacSha256(options.Secret, unsigned)));

        if (!FixedEquals(expected, parts[2]))
            return null;

        try
        {
            using var document = JsonDocument.Parse(
                Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));

            var root = document.RootElement;
            var subject = root.GetProperty("sub").GetString();

            if (string.IsNullOrWhiteSpace(subject))
                return null;

            var issuer = root.TryGetProperty("iss", out var issuerElement)
                ? issuerElement.GetString()
                : null;
            var audience = root.TryGetProperty("aud", out var audienceElement)
                ? audienceElement.GetString()
                : null;

            if (!string.Equals(issuer, options.Issuer, StringComparison.Ordinal) ||
                !string.Equals(audience, options.Audience, StringComparison.Ordinal))
            {
                return null;
            }

            var expiresAt = root.GetProperty("exp").GetInt64();

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt)
                return null;

            return new JwtPayload(
                subject,
                root.TryGetProperty("email", out var email) ? email.GetString() : null,
                root.TryGetProperty("name", out var name) ? name.GetString() : null,
                root.TryGetProperty("role", out var role) ? role.GetString() : null,
                root.GetProperty("iat").GetInt64(),
                expiresAt);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static JwtPayload CreatePayload(
        SessionUser user,
        string? role = null,
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null)
    {
        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();

        return new JwtPayload(
            user.Id,
            user.Email,
            user.Name,
            role,
            issuedAt,
            issuedAt + (long)(lifetime ?? TimeSpan.FromMinutes(60)).TotalSeconds);
    }

    public static string? ExtractToken(ReptarRequest request)
    {
        var header = request.Header("authorization");

        if (string.IsNullOrWhiteSpace(header))
            return null;

        var separator = header.IndexOf(' ');

        if (separator < 0)
            return header.Trim();

        var scheme = header[..separator];
        var token = header[(separator + 1)..].Trim();

        return scheme.Equals("Token", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? token
            : null;
    }

    private static string Json(string? value) =>
        JsonSerializer.Serialize(value, ReptarJson.Options);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;

        if (padding > 0)
            normalized += new string('=', 4 - padding);

        return Convert.FromBase64String(normalized);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
            return [];

        var bytes = new byte[hex.Length / 2];

        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;

        var difference = 0;

        for (var index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }
}
