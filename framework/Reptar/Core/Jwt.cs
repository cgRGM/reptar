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
    // Bounded so no stack allocation is sized by attacker-controlled token
    // length; a real HS256 JWT is a few hundred bytes.
    private const int MaxTokenLength = 8 * 1024;
    private const int MaxSecretBytes = 1024;

    public static async Task<string> SignAsync(
        ICrypto crypto,
        JwtOptions options,
        JwtPayload payload)
    {
        if (string.IsNullOrWhiteSpace(options.Secret))
            throw new InvalidOperationException("JWT secret is not configured.");

        if (Encoding.UTF8.GetByteCount(options.Secret) > MaxSecretBytes)
            throw new InvalidOperationException(
                $"JWT secret must not exceed {MaxSecretBytes} bytes.");

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
        if (string.IsNullOrWhiteSpace(options.Secret) || string.IsNullOrEmpty(token))
            return null;

        // Fail closed on oversized tokens before any buffer is sized by token
        // length (the verify path stackallocs from these values).
        if (token.Length > MaxTokenLength)
            return null;

        var firstDot = token.IndexOf('.');
        if (firstDot <= 0)
            return null;

        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot <= 0 || secondDot == token.Length - 1)
            return null;

        var unsignedSpan = token.AsSpan(0, secondDot);
        var sigSpan = token.AsSpan(secondDot + 1);

#if !NETWASM
        Span<byte> expectedSig = stackalloc byte[32];
        Span<byte> keyBytes = stackalloc byte[Encoding.UTF8.GetByteCount(options.Secret)];
        Encoding.UTF8.GetBytes(options.Secret, keyBytes);

        Span<byte> unsignedBytes = stackalloc byte[Encoding.UTF8.GetByteCount(unsignedSpan)];
        Encoding.UTF8.GetBytes(unsignedSpan, unsignedBytes);

        System.Security.Cryptography.HMACSHA256.HashData(keyBytes, unsignedBytes, expectedSig);
        var expectedBase64 = Base64Url(expectedSig.ToArray());
        if (!FixedEquals(expectedBase64, sigSpan.ToString()))
        {
            return null;
        }
#else
        var unsigned = unsignedSpan.ToString();
        var signature = sigSpan.ToString();
        var expected = Base64Url(HexToBytes(
            await crypto.HmacSha256(options.Secret, unsigned)));

        if (!FixedEquals(expected, signature))
            return null;
#endif

        try
        {
            var payloadSpan = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);
            var payloadBytes = Base64UrlDecode(payloadSpan);
            using var document = JsonDocument.Parse(payloadBytes);

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

    private static byte[] Base64UrlDecode(string value) => Base64UrlDecode(value.AsSpan());

    private static byte[] Base64UrlDecode(ReadOnlySpan<char> value)
    {
        Span<char> buffer = stackalloc char[value.Length + 4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = c == '-' ? '+' : c == '_' ? '/' : c;
        }

        var padding = value.Length % 4;
        var totalLen = value.Length;
        if (padding > 0)
        {
            var padCount = 4 - padding;
            for (var p = 0; p < padCount; p++)
            {
                buffer[totalLen++] = '=';
            }
        }

        var byteCount = (totalLen * 3) / 4;
        var bytes = new byte[byteCount];
        if (Convert.TryFromBase64Chars(buffer.Slice(0, totalLen), bytes, out var written))
        {
            if (written == bytes.Length) return bytes;
            Array.Resize(ref bytes, written);
            return bytes;
        }

        return Convert.FromBase64String(buffer.Slice(0, totalLen).ToString());
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
