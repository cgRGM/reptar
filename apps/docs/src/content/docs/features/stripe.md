---
title: Payments & Stripe (Reptar.Stripe)
description: Zero-bloat Stripe Checkout sessions and HMAC-SHA256 webhook verification on Cloudflare Workers.
---

## Overview

`Reptar.Stripe` is an optional feature slice in `framework/Reptar/Features/Stripe.cs`. It is not part of the framework core and the reference blog does not mount it. The module exists to prove that Stripe Checkout and webhook verification can run on Workers without `Stripe.net` or a large reflection-heavy SDK.

- **Direct REST calls** to `https://api.stripe.com/v1/checkout/sessions` through `c.Http`.
- **Zero dependency form encoding** using UTF-8 percent encoding.
- **Native HMAC-SHA256** webhook verification through `c.Crypto`.
- **Fail-closed behavior** when Stripe is not configured.

---

## Configuration

```csharp
app.Route("/stripe", Stripe.Routes(ctx => new StripeConfig
{
    ApiKey = ctx.Environment.Get("STRIPE_KEY") ?? "",
    WebhookSecret = ctx.Environment.Get("STRIPE_WEBHOOK_SECRET") ?? "",
    ToleranceSeconds = 300,
}, async (ctx, stripeEvent) =>
{
    // Handle verified events.
    return ctx.Status(200);
}));
```

`StripeConfig` also exposes `ApiUrl` for testing or a proxy.

`c.Environment.Get(...)` reads string env vars/secrets from the Worker host. Mount Stripe routes only in an app that provisions `STRIPE_KEY` and `STRIPE_WEBHOOK_SECRET` in its Alchemy/Cloudflare environment. The reference blog does not mount them.

---

## 1. Creating Checkout Sessions

```csharp
app.Post("/orders/checkout", Auth.RequireUser, async ctx =>
{
    var input = ctx.JsonCodec.Deserialize<CreateCheckoutInput>(
        ctx.Req.BodyText ?? "{}");

    if (input is null)
    {
        return ctx.BadRequest("Invalid payload.");
    }

    var stripeConfig = new StripeConfig
    {
        ApiKey = ctx.Environment.Get("STRIPE_KEY") ?? "",
        WebhookSecret = ctx.Environment.Get("STRIPE_WEBHOOK_SECRET") ?? "",
    };

    var session = await Stripe.CreateCheckoutSessionAsync(
        ctx,
        stripeConfig,
        new CreateCheckoutInput(
            SuccessUrl: "https://my-app.com/orders/success?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl: "https://my-app.com/orders/cancel",
            LineItems:
            [
                new StripeLineItem(
                    Name: "Reptar Pro",
                    UnitAmountInCents: 1900,
                    Currency: "usd",
                    Quantity: 1),
            ],
            ClientReferenceId: ctx.User!.Id,
            CustomerEmail: ctx.User.Email));

    return ctx.Json(session, 201);
});
```

The returned `CheckoutSession` contains `Id`, `Url`, and `Status`.

`StripeLineItem` uses `Name`, `UnitAmountInCents`, `Currency`, and `Quantity`. This module creates ad-hoc prices; it does not look up pre-created Stripe Price objects.

---

## 2. Mounting the Stripe Routes

```csharp
app.Route("/stripe", Stripe.Routes(ctx => new StripeConfig
{
    ApiKey = ctx.Environment.Get("STRIPE_KEY") ?? "",
    WebhookSecret = ctx.Environment.Get("STRIPE_WEBHOOK_SECRET") ?? "",
}, async (ctx, stripeEvent) =>
{
    switch (stripeEvent.Type)
    {
        case "checkout.session.completed":
            // Fulfill the order in D1, enqueue work, etc.
            break;
    }

    return ctx.Status(200);
}));
```

Mounted routes:

- `POST /stripe/checkout` — accepts `CreateCheckoutInput` and returns `CheckoutSession`.
- `POST /stripe/webhook` — validates `Stripe-Signature`, checks the timestamp tolerance, and invokes `onEvent`.

If `ApiKey` or `WebhookSecret` is missing, the corresponding route returns `503 stripe_not_configured` instead of making an unauthenticated request or accepting an unverified webhook.

---

## 3. Webhook Signature Verification

```csharp
app.Post("/webhooks/stripe", async ctx =>
{
    var signature = ctx.Req.Header("stripe-signature") ?? "";
    var body = ctx.Req.BodyText ?? "";

    var valid = await Stripe.VerifyWebhookSignatureAsync(
        ctx,
        stripeConfig,
        body,
        signature);

    if (!valid)
    {
        return ctx.Error(
            400,
            "invalid_signature",
            "Webhook signature verification failed.");
    }

    var stripeEvent = ctx.JsonCodec.Deserialize<StripeEvent>(body);
    // Process the verified event.
    return ctx.Status(200);
});
```

Verification parses `t=` and `v1=` from the `Stripe-Signature` header, rejects missing or malformed timestamps, enforces `ToleranceSeconds`, and compares the expected HMAC against every provided `v1` signature.

## Security notes

- Keep `STRIPE_KEY` and `STRIPE_WEBHOOK_SECRET` out of source control.
- Reject webhooks when the secret is missing; never skip verification.
- The timestamp window limits replay, but production systems should also deduplicate `StripeEvent.Id` values in D1 or KV.
- The reference blog intentionally exposes no `/stripe` routes.
