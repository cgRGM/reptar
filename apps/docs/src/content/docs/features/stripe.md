---
title: Payments & Stripe (Reptar.Stripe)
description: Zero-bloat Stripe Checkout sessions and HMAC-SHA256 webhook verification on Cloudflare Workers.
---

## Overview

Reptar provides an optional, built-in payments module (`Reptar.Stripe`) designed specifically for serverless edge execution on Cloudflare Workers.

Traditional .NET applications rely on the official `Stripe.net` NuGet package. While powerful, `Stripe.net` is ~50 MB, contains thousands of reflection-heavy types, and links BCL networking and XML components that fail under WebAssembly ahead-of-time (AOT) compilation.

`Reptar.Stripe` is zero-dependency, AOT-safe, and compiled directly into the framework core:

- **Direct Edge REST Calls**: Invokes Stripe's official REST API (`https://api.stripe.com/v1/checkout/sessions`) via the host's native `fetch` bridge.
- **Lightweight Form Encoding**: Serializes checkout payloads without heavy `System.Uri` or reflection overhead.
- **Hardware-Accelerated Webhook Verification**: Verifies incoming `Stripe-Signature` headers using native WebCrypto **HMAC-SHA256** with replay attack protection.

---

## Configuration

Initialize `StripeConfig` using environment variables passed from Cloudflare Workers or Alchemy:

```csharp
var stripeConfig = new StripeConfig
{
    ApiKey = Environment.GetEnvironmentVariable("STRIPE_KEY") ?? "",
    WebhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") ?? "",
    ToleranceSeconds = 300 // Replay attack protection window (default: 300s)
};
```

> [!IMPORTANT] If `WebhookSecret` is empty or null, webhook signature verification **fails closed** and rejects all incoming webhook requests.

---

## 1. Creating Checkout Sessions

Create a Stripe Checkout session in your route handler:

```csharp
app.Post("/orders/checkout", Auth.RequireUser, async ctx =>
{
    var input = await ctx.Req.JsonAsync<CheckoutInput>();
    if (input is null) return ctx.BadRequest("Invalid payload");

    var session = await Stripe.CreateCheckoutSessionAsync(stripeConfig, new CreateCheckoutInput(
        SuccessUrl: "https://my-app.com/orders/success?session_id={CHECKOUT_SESSION_ID}",
        CancelUrl: "https://my-app.com/orders/cancel",
        LineItems: [
            new StripeLineItem(PriceId: input.PriceId, Quantity: input.Quantity)
        ],
        ClientReferenceId: ctx.User!.Id,
        CustomerEmail: ctx.User.Email
    ));

    return ctx.Json(session, 201);
});
```

The returned `CheckoutSession` record includes `Id` and `Url`, allowing your frontend to redirect the customer directly to Stripe:

```typescript
const res = await api.orders.checkout.$post({
  json: { priceId: "price_1N...", quantity: 1 },
});
if (res.ok) {
  const session = await res.json();
  window.location.href = session.url;
}
```

---

## 2. Mounting the Stripe Routes

You can mount pre-built checkout and webhook routes directly:

```csharp
app.Route("/stripe", Stripe.Routes(stripeConfig, async (stripeEvent, ctx) =>
{
    // Handle verified webhook events
    switch (stripeEvent.Type)
    {
        case "checkout.session.completed":
            Console.WriteLine($"Order completed: {stripeEvent.Id}");
            // Fulfill digital goods, update D1 database, or send queue event
            break;

        case "customer.subscription.deleted":
            // Handle cancellation
            break;
    }
}));
```

Mounting `Stripe.Routes` automatically exposes:

- `POST /stripe/checkout`: Accepts `CreateCheckoutInput` and returns `CheckoutSession`.
- `POST /stripe/webhook`: Validates `Stripe-Signature` via HMAC-SHA256, verifies timestamps, and invokes your `onEvent` delegate.

---

## 3. Webhook Signature Verification

If you prefer to handle webhooks in a custom endpoint, call `Stripe.VerifyWebhookSignatureAsync`:

```csharp
app.Post("/webhooks/stripe", async ctx =>
{
    var signature = ctx.Req.Header("stripe-signature");
    var body = ctx.Req.BodyString();

    var isValid = await Stripe.VerifyWebhookSignatureAsync(
        stripeConfig,
        body,
        signature,
        ctx.Clock
    );

    if (!isValid)
    {
        return ctx.Error(400, "invalid_signature", "Webhook signature verification failed.");
    }

    var evt = ctx.JsonCodec.Deserialize<StripeEvent>(body);
    // Process event safely
    return ctx.Status(200);
});
```

### Security Guarantees

1. **Timing Attack Protection**: Uses constant-time signature comparison.
2. **Replay Attack Protection**: Compares event timestamp `t` with `ctx.Clock.IsoNow()`; timestamps older than `ToleranceSeconds` (default: 5 minutes) are rejected.
3. **Fail-Closed**: If the webhook secret is missing, all requests are rejected immediately.

---

## 4. Production Deployment with Alchemy

Define your Stripe secrets in your `alchemy.run.ts` stack:

```typescript
import * as Alchemy from "alchemy";
import * as Cloudflare from "alchemy/Cloudflare";

export default Alchemy.Stack("reptar", { ... }, Effect.gen(function* () {
  const api = yield* Cloudflare.Worker("api", {
    env: {
      STRIPE_KEY: Alchemy.secret("STRIPE_KEY"),
      STRIPE_WEBHOOK_SECRET: Alchemy.secret("STRIPE_WEBHOOK_SECRET"),
      DB: db,
      BUCKET: bucket,
    },
    main: "../../spikes/netwasm-worker/src/index.ts",
    name: "reptar-api",
  });

  return { api: api.url };
}));
```
