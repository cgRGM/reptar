using System.Text;
using System.Text.Json;

namespace Reptar;

public sealed class StripeConfig
{
    public required string ApiKey { get; init; }
    public string? WebhookSecret { get; init; }
    public string ApiUrl { get; init; } = "https://api.stripe.com/v1";
    public int ToleranceSeconds { get; init; } = 300;
}

public sealed record StripeLineItem(
    string Name,
    long UnitAmountInCents,
    string Currency = "usd",
    int Quantity = 1);

public sealed record CreateCheckoutInput(
    string SuccessUrl,
    string CancelUrl,
    List<StripeLineItem> LineItems,
    string Mode = "payment",
    string? CustomerEmail = null,
    string? ClientReferenceId = null);

public sealed record CheckoutSession(
    string Id,
    string Url,
    string Status);

public sealed record StripeEvent(
    string Id,
    string Type,
    string RawData);

public static class Stripe
{
    private static string UrlEncode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sb = new StringBuilder();

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;

            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }

    public static async Task<CheckoutSession> CreateCheckoutSessionAsync(
        Ctx ctx,
        StripeConfig config,
        CreateCheckoutInput input)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                "Stripe API key is not configured.");
        }

        var form = new List<string>
        {
            $"success_url={UrlEncode(input.SuccessUrl)}",
            $"cancel_url={UrlEncode(input.CancelUrl)}",
            $"mode={UrlEncode(input.Mode)}",
        };

        if (!string.IsNullOrEmpty(input.CustomerEmail))
        {
            form.Add($"customer_email={UrlEncode(input.CustomerEmail)}");
        }

        if (!string.IsNullOrEmpty(input.ClientReferenceId))
        {
            form.Add($"client_reference_id={UrlEncode(input.ClientReferenceId)}");
        }

        if (input.LineItems != null)
        {
            for (var i = 0; i < input.LineItems.Count; i++)
            {
                var item = input.LineItems[i];
                form.Add($"line_items[{i}][price_data][currency]={UrlEncode(item.Currency)}");
                form.Add($"line_items[{i}][price_data][unit_amount]={item.UnitAmountInCents}");
                form.Add($"line_items[{i}][price_data][product_data][name]={UrlEncode(item.Name)}");
                form.Add($"line_items[{i}][quantity]={item.Quantity}");
            }
        }

        var formBody = string.Join("&", form);
        var headers = new Dictionary<string, string>
        {
            ["authorization"] = $"Bearer {config.ApiKey}",
            ["content-type"] = "application/x-www-form-urlencoded",
        };

        var res = await ctx.Http.FetchAsync(
            $"{config.ApiUrl}/checkout/sessions",
            "POST",
            headers,
            formBody);

        if (res.Status >= 400)
        {
            throw new InvalidOperationException(
                $"Stripe checkout creation failed ({res.Status}): {res.Body}");
        }

        using var doc = JsonDocument.Parse(res.Body);
        var id = doc.RootElement.GetProperty("id").GetString() ?? "";
        var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "open" : "open";

        return new CheckoutSession(id, url, status);
    }

    public static async Task<bool> VerifyWebhookSignatureAsync(
        Ctx ctx,
        StripeConfig config,
        string payload,
        string signatureHeader)
    {
        var webhookSecret = config.WebhookSecret;

        if (string.IsNullOrEmpty(webhookSecret) ||
            string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        string? timestamp = null;
        var signatures = new List<string>();

        foreach (var item in signatureHeader.Split(','))
        {
            var parts = item.Trim().Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0] == "t")
            {
                timestamp = parts[1];
            }
            else if (parts[0] == "v1")
            {
                signatures.Add(parts[1]);
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            return false;
        }

        if (!long.TryParse(timestamp, out var t))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tolerance = config.ToleranceSeconds > 0
            ? config.ToleranceSeconds
            : 300;

        if (Math.Abs(now - t) > tolerance)
        {
            return false;
        }

        var signedPayload = $"{timestamp}.{payload}";
        var expectedSig = await ctx.Crypto.HmacSha256(webhookSecret, signedPayload);

        foreach (var sig in signatures)
        {
            if (CryptographicEquals(expectedSig, sig))
            {
                return true;
            }
        }

        return false;
    }

    public static ReptarApp Routes(
        StripeConfig config,
        Func<Ctx, StripeEvent, Task<ReptarResponse>>? onEvent = null) =>
        Routes(_ => config, onEvent);

    public static ReptarApp Routes(
        Func<Ctx, StripeConfig> configFactory,
        Func<Ctx, StripeEvent, Task<ReptarResponse>>? onEvent = null)
    {
        var app = ReptarApp.Create();

        app.Post("/checkout", async ctx =>
        {
            var config = configFactory(ctx);

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                return ctx.Error(
                    503,
                    "stripe_not_configured",
                    "Stripe API key is not configured.");
            }

            var bodyText = ctx.Req.Body is null
                ? "{}"
                : Encoding.UTF8.GetString(ctx.Req.Body);

            var input = ctx.JsonCodec.Deserialize<CreateCheckoutInput>(bodyText);
            if (input is null ||
                string.IsNullOrWhiteSpace(input.SuccessUrl) ||
                string.IsNullOrWhiteSpace(input.CancelUrl))
            {
                return ctx.BadRequest("Missing required successUrl or cancelUrl.");
            }

            var session = await CreateCheckoutSessionAsync(ctx, config, input);
            return ctx.Json(session, 201);
        });

        app.Post("/webhook", async ctx =>
        {
            var config = configFactory(ctx);

            if (string.IsNullOrWhiteSpace(config.WebhookSecret))
            {
                return ctx.Error(
                    503,
                    "stripe_not_configured",
                    "Stripe webhook secret is not configured.");
            }

            var sigHeader = ctx.Req.Header("stripe-signature");
            var bodyText = ctx.Req.Body is null
                ? ""
                : Encoding.UTF8.GetString(ctx.Req.Body);

            var valid = await VerifyWebhookSignatureAsync(
                ctx,
                config,
                bodyText,
                sigHeader ?? "");

            if (!valid)
            {
                return ctx.BadRequest("Invalid Stripe webhook signature.");
            }

            if (onEvent is not null && !string.IsNullOrWhiteSpace(bodyText))
            {
                using var doc = JsonDocument.Parse(bodyText);
                var id = doc.RootElement.TryGetProperty("id", out var idProp)
                    ? idProp.GetString() ?? ""
                    : "";
                var type = doc.RootElement.TryGetProperty("type", out var typeProp)
                    ? typeProp.GetString() ?? ""
                    : "";

                return await onEvent(ctx, new StripeEvent(id, type, bodyText));
            }

            return ctx.Status(200);
        });

        return app;
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}
