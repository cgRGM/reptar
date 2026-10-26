# Reptar Security Deep Dive — October 2026

**Scope:** dual-host threat model (NetWasm/Cloudflare Workers + Native AOT Kestrel/Lambda, weighted equally),
open-source-product trust model (app authors are the framework's users), six surfaces: SQLi, XSS/encoding,
binding/deserialization, session/JWT, CSRF/CORS/headers, plus uploads/storage, BetterAuth+OAuth, realtime, AI dispatch.

**Method:** five evidence sources — direct code verification (auth/crypto/pipeline core), four parallel survey
agents (data layer, request pipeline, uploads/BetterAuth/OAuth, realtime/AI), plus external research
(OWASP Top 10:2026 & API Top 10:2023, ASVS 4.0, Microsoft guidance, GitHub advisories: CVE-2018-8171,
CVE-2025-55315, Fastify CVE-2022-41919, FastEndpoints #508, JWT alg-confusion family CVE-2024-54150 /
CVE-2023-48223 / CVE-2023-48238, Semgrep C# docs, Roslyn analyzer landscape 2026).

**Reference frameworks:** OWASP Top 10:2026 (A01 Broken Access Control, A02 Crypto Failures, A05 Misconfig,
A08 Software Supply Chain, new improper-error-handling class), OWASP API Security Top 10:2023
(API1 BOLA, API2 Broken Auth, API4 Unrestricted Resource Consumption, API8 Security Misconfig, API10 Unsafe
Consumption of APIs), OWASP ASVS 4.0 (V2 Auth, V3 Session, V4 Access Control, V5 Validation & Encoding,
V12 Data Protection).

---

## Threat model (trust boundaries)

```
Browser ──HTTP/WS──▶ Host (Kestrel plaintext-only | API Gateway+Lambda | Cloudflare edge→workerd)
                        │  envelope → C# core (ReptarApp.ExecuteAsync → router → middleware* → validator → handler)
                        │  *middleware does NOT run on WebSocket opens
                        ├──▶ DB (SQLite / D1 / Postgres / MySQL) — always parameterized values
                        ├──▶ Storage (LocalStorage / S3 / R2-Wasm)
                        ├──▶ JS shim (crypto, http, ws, durable, r2) — full trust boundary cross on NetWasm
                        └──▶ Outbound HTTP (DefaultHttpClient native: UNGUARDED | Workers: https-only + private-IP block)
```

Trust boundary violations observed: request headers treated as identity (`Host`, `Origin`, `X-Forwarded-For`,
`CF-Connecting-IP` off-platform, `X-Forwarded-Proto/Host`); JS shim trusted for all crypto and storage ops;
LLM providers treated as trusted destinations for server-held credentials.

---

## Findings register

Severity: C = Critical (when mounted), H = High, M = Medium, L = Low, P = Positive control confirmed.
Latent = requires an app author to make the mistake; the framework's job is to prevent that class.

### A. Auth, session, OAuth

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-01 | H | Built-in OAuth `state` generated but never validated on callback (BetterAuthRoutes is the only site in repo that checks state) | `Auth/Auth.cs:285,299` vs `:303-381`; `BetterAuthRoutes.cs:284-301` |
| F-02 | H | `redirect_uri` derived from attacker-influenceable `Origin`/`Host`/`X-Forwarded-Proto`/`X-Forwarded-Host`; no trusted-origin pinning | `Auth/Auth.cs:287-289,324-326`; `BetterAuthRoutes.cs:408-413` |
| F-03 | H | Open redirect post-OAuth: user-supplied `callbackUrl`/`destination` round-trips cookie→redirect; `Ctx.Redirect` does zero validation | `Auth/Auth.cs:294,322,380`; `BetterAuthRoutes.cs:268,304,376`; `Core/Ctx.cs:178-183` |
| F-04 | H | Login throttle keyed on spoofable headers (native trusts `CF-Connecting-IP`+`XFF`; off-platform both are client-set) — CVE-2018-8171 precedent | `Auth/Auth.cs:160-163`; `Reptar.Wasm/Auth/Auth.cs:98-101`; cf. correct pattern `Core/Middleware.cs:329-355` |
| F-05 | M | `_loginAttempts` unbounded `ConcurrentDictionary` — memory exhaustion under spoofed-IP flood | `Auth/Auth.cs:121`; Wasm slice uses plain `Dictionary` + lock `Wasm/Auth/Auth.cs:42-43` |
| F-06 | M | No CSRF token machinery anywhere (repo grep `csrf` = 0); defense is SameSite=Lax only; login CSRF feasible on all three auth sets (Set-Cookie applies on cross-site POST) | `BetterAuthRoutes.cs:70-162`; `Auth/Auth.cs`; cookie attrs `Core/Cookies.cs:41-42` |
| F-07 | H | OAuth account linking by email match ignoring IdP `email_verified` → account takeover via provider account with victim's email | `Auth/Auth.cs:338-357`; `BetterAuthRoutes.cs:329-353`; `Auth/OAuth.cs:110-114,186-193` |
| F-08 | M | Email enumeration: signup 409 "Email already in use" | `Auth/Auth.cs:174-175` |
| F-09 | M | BetterAuth route set has zero rate limiting (drift vs builtin + Wasm slices) | `BetterAuthRoutes.cs` entire file |
| F-10 | L | Session token returned in JSON body on BetterAuth wire (protocol design; weakens HttpOnly) | `BetterAuthRoutes.cs:106,161,208` |
| F-11 | M | BetterAuth `/sign-in/email` has no validators → unbounded password reaches PBKDF2 | `BetterAuthRoutes.cs:70`; cf. `Auth.cs:110-112` |
| F-12 | H | `VerifyPassword` stackalloc sized by attacker-controlled password byte length → uncatchable `StackOverflowException` | `Data/Services.cs:280-282` |
| F-13 | H | `Jwt.VerifyAsync`/`Base64UrlDecode` stackalloc sized by attacker-controlled token length → same | `Core/Jwt.cs:64-69,190` |
| F-14 | L | PBKDF2 100k iterations below OWASP 600k guidance (workerd WebCrypto cap; parity hash format) | `Data/Services.cs:251`; `packages/host/src/crypto.ts:66-74` |
| F-15 | L | JWT secret used raw as HMAC key (no KDF) | `Core/Jwt.cs:39`; `JwtOptions` |
| F-16 | P | Session tokens: 32-byte CSPRNG, SHA-256 hashed at rest, expiry-checked lookup | `Auth/Auth.cs:664-682,597-625` |
| F-17 | P | JWT: HS256-only, header never trusted (immune to alg-confusion family CVE-2024-54150/CVE-2023-48223/CVE-2023-48238); iss/aud/exp enforced; fixed-time compare | `Core/Jwt.cs:32-41,44-85,106-115,235-248` |
| F-18 | P | Cookies default `HttpOnly; SameSite=Lax; Secure` (AuthOptions default true) | `Core/Cookies.cs:41-46`; `Core/ReptarApi.cs:36-40` |

### B. Cookies & headers

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-19 | M | `CookieJar.Set` raw `$"{name}={value}"` interpolation — no RFC 6265 token/octet validation; user input reaches values via PKCE cookie | `Core/Cookies.cs:26,55` |
| F-20 | L | `SetSigned` name:value concatenation ambiguity (`a`+`b:c` ≡ `a:b`+`c`) | `Core/Cookies.cs:84,112` |
| F-21 | L | SSE `data: {message}` framing without newline sanitization (event/data injection) | `Core/Ctx.cs:225` |
| F-22 | L | `ServerTiming` desc unescaped into quoted header value | `Core/Ctx.cs:116-131` |
| F-23 | M | Security headers opt-in (`UseSecurityHeaders`), NOT default; template/examples never enable | `Core/Middleware.cs:383-414`; `Core/ProductionExtensions.cs:58-79` |

### C. Data layer

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-24 | P | Values are parameterized end-to-end (fluent API, raw APIs, SqlParamParser never interpolates values; LIMIT/OFFSET are ints) | `ColumnRef.cs:113-135`; `SqlParamParser.cs:7-225`; all DbClients |
| F-25 | M (latent) | Dictionary/JSON **keys** interpolated as identifiers in InsertAsync/UpdateAsync when they match no column | `SqliteDbClient.cs:1083-1089,1187-1196`; `MySqlDbClient.cs:267-269,371-375`; `PostgresDbClient.cs:180-190,250-277`; `D1DbClient.cs:111,151-152` |
| F-26 | M (latent) | Bypass surface of the typed-token system: raw-clause overloads (`ListAsync/CountAsync/DeleteWhereAsync/ExistsAsync`), `SqlOrderBy(string)` implicit, `SqlFilter.Raw` | `Data/Services.cs:41-54,99-115`; `ColumnRef.cs:44,61-62,165-175` |
| F-27 | M | Naive identifier quoting, no escaping; `ColumnRef` double-quotes break on MySQL (no ANSI_QUOTES) | `SqlDialect.cs:24,44,64`; `ColumnRef.cs:17-35` |
| F-28 | M | Postgres `GenerateCreateTableSql` interpolates `DefaultValue` unescaped (inconsistent with other dialects' `FormatDefault`) | `PostgresDbClient.cs:341` |
| F-29 | L | `PostgresDbClient.QueryAsync<T>` lacks `[DynamicallyAccessedMembers]` while reaching reflection RowMapper (AOT hazard); fallback mapper reflects per row | `PostgresDbClient.cs:20`; `RowMapper.cs:24,68,78` |
| F-30 | L | `SqlFilter.Raw` treats literal `@` as placeholder → silent null binding corruption | `ColumnRef.cs:119-132` |
| F-31 | P | Env-var PRAGMAs allowlisted; migrations dev-file-driven, bookkeeping parameterized | `SqliteDbClient.cs:110-119`; `Migrations.cs:128-137` |

### D. Binding, validation, pipeline

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-32 | P | System.Text.Json only; zero polymorphic deserialization; source-gen on Wasm/AOT; malformed JSON → 400 | repo grep; `ReptarApp.cs:1050-1072` |
| F-33 | M | Validation opt-in; `Regex` rule silently no-ops on NETWasm (`#if !NETWASM`) → host-dependent validation drift | `Validation/Validator.cs:174-176,254-262` |
| F-34 | L | `IsEmail` heuristic accepts `<`, `>`, `"` etc. | `Validation/Validator.cs:327-339` |
| F-35 | M | Lambda host: no own body limit (relies on API GW) + stack trace posted to runtime API | `Reptar.Lambda/LambdaHost.cs:152-162,67-78` |
| F-36 | M | Kestrel WS receive loop: unbounded `MemoryStream` accumulation per message | `Reptar.Kestrel/KestrelHost.cs:104-125` |
| F-37 | M | Workers host buffers entire request body before app-level limit runs (pre-auth memory surface) | `packages/host/src/host.ts:873` |
| F-38 | M | No native SSRF guard on `DefaultHttpClient` (Workers has https-only + private-IP blocklist) | `Data/Services.cs:585-670`; cf. `host.ts:444-533` |
| F-39 | L | Kestrel serves plaintext HTTP only (no TLS config in framework) | `Reptar.Kestrel/KestrelHost.cs:60-79` |
| F-40 | L | Routing: case-insensitive match; no `..` collapse or percent-decode at routing layer (host-dependent normalization) | `ReptarApp.cs:1219-1284` |
| F-41 | P | Body-size gate: 10 MiB default, enforced mid-stream on Kestrel, per-route overrides | `ReptarApp.cs:825-840`; `KestrelHost.cs:290-357` |
| F-42 | P | Debug-gated stack traces in 500s; production returns generic body | `ReptarApp.cs:909-918` |
| F-43 | L | OpenAPI docs pages load third-party CDN scripts (supply-chain / CSP consideration) | `Core/OpenApi.cs:91,109-116` |

### E. Uploads & storage

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-44 | P | Multipart parser: O(n) monotonic scan, terminator required, sane defaults (10MB/10 files/100 fields, boundary ≤70) | `Core/Multipart.cs:16-22,54-161` |
| F-45 | P | `StorageKey.TryNormalize`: centralized traversal/control-char/segment-length validation; LocalStorage `GetFullPath` containment; S3 normalizes everywhere | `StorageModels.cs:263-336`; `LocalStorage.cs:242-264`; `S3Storage.cs:60-370` |
| F-46 | P | `ctx.File` always sets `X-Content-Type-Options: nosniff`; Content-Disposition builder strips CRLF/quotes/controls | `StorageFile.cs:263`; `StorageModels.cs:410-450` |
| F-47 | M | Wasm `R2Client` skips `StorageKey.Normalize` entirely — and is the NetWasm default storage | `Reptar.Wasm/Data/R2Client.cs:54-105`; `ReptarApp.cs:1326-1333` |
| F-48 | L | `FormFile.FileName` retains `\r`, quotes, control chars, unicode after path-strip; apps can leak it into headers | `Multipart.cs:163-167` |
| F-49 | L | `ctx.Req.Form` not tunable (defaults only); baas example serves files via `ctx.Stream` (no nosniff/disposition) | `ReptarRequest.cs:43-44`; `examples/baas/app.cs:307-327` |

### F. Realtime

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-50 | H | WS upgrade: zero auth, zero Origin check, on both hosts; **framework middleware never runs on WS opens** (CSWSH; entire auth/ratelimit/CORS surface bypassed) | `KestrelHost.cs:84-97`; `host.ts:1116-1118,996-999`; `ReptarApp.cs:640-676` |
| F-51 | H | ActionCable: `AuthorizeSubscription` null ⇒ auto-approve; `signed_stream_name` accepted unsigned; guessable `user_{id}` streams | `ActionCableServer.cs:25,94-112`; `ActionCableProtocol.cs:146-156`; `CampfireRealtime.cs:34-36` |
| F-52 | M | DO bridge: name-addressed (`idFromName(id)`) with no ACL; production pattern uses user-derived room names → IDOR by name | `host.ts:551-578`; `durable-object.ts:19-26` |
| F-53 | M | SSE: unbounded channel, no per-user cap, no timeout once started on Workers | `Sse.cs:99-103`; `host.ts:1244-1250` |
| F-54 | L | SSE `BodyStream` silently dropped on Workers (stream JS imports orphaned) — fails empty, not closed | `WorkerHost.cs:130-142` |
| F-55 | L | Campfire example: raw `{body}` interpolation into turbo-stream HTML; room name spliced into JSON | `examples/campfire/app.cs:157-161,69` |

### G. AI dispatch

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| F-56 | C* | Workspace tools mounted as HTTP routes with no auth: `read_file`, `write_file`, `edit_file`, `run_command` (`/bin/bash -c`) — RCE by design when exposed; only path-escape guard + optional prefix allowlist | `Reptar.AI/WorkspaceTools.cs:185,292,327,371,556-631`; `examples/agent-cli/app.cs:47-60` |
| F-57 | C* | Unauthenticated gateway: `model: "a2a/<url>"` → full SSRF with response reflection on native (no URL guard); Workers restricted to public https; remote-controlled `url`/`sendUrl` honored from agent cards | `AiGateway.cs:102,685,1241-1407`; `Data/Services.cs:585-622`; cf. `host.ts:580-669` |
| F-58 | H* | Cost amplification: unauthenticated completions; client picks model + `max_tokens` against server-held keys; ModelCatalog never enforced on completions path | `AiGateway.cs:102,574-577,678-704,913` |
| F-59 | H* | Example chain: unauthenticated `/api/providers/connect` overwrites provider credential with attacker `baseUrl` → server key sent to attacker host (`Authorization: Bearer $KEY`) | `examples/agent-cli/features/chat.cs:242-276`; `LlmTypes.cs:212-258`; `LlmOpenAi.cs:22-48` |
| F-60 | M | AI tool dispatch builds synthetic request; `Cookies` dict never populated → cookie session lost (protected tools always 401; default harness overload passes no identity at all; no per-tool authz model) | `AiTools.cs:382-395`; `AgentHarness.cs:79` |
| F-61 | L | Gateway `FlattenPrompt` merges system+user into one string (role-boundary injection surface for Workers-AI/A2A) | `AiGateway.cs:641-642` |
| F-62 | L | Public tool-schema listing route; unauth `/gateway/reset` + `/gateway/stats` | `AiTools.cs:278-291`; `AiGateway.cs:104-134` |
| F-63 | P | Secrets never serialized in framework responses; on-disk store chmod 600; redaction filter exists (opt-in) | `LlmAuth.cs:150-164,256-304`; `AgentSafety.cs:24-74` |

`*` = severity when the surface is mounted/exposed; several are dev-tools by intent — the finding is that the
framework ships them default-open with no auth gate or prominent deployment warning.

### H. Structural (cross-cutting)

| ID | Sev | Finding | Evidence |
|---|---|---|---|
| S-1 | H | **Dual-implementation drift**: 3 auth slices (native builtin, Wasm, BetterAuth) with divergent protections; 2 crypto providers; 2 JWT verify paths (`#if NETWASM`); R2 normalization drift; Regex validation drift; SSE/BodyStream host drift | see register |
| S-2 | H | **Opt-in security posture**: no default auth step; opt-in headers/validation/rate-limiting; null ⇒ allow subscription hook. Precedent: FastEndpoints antiforgery #508 | `ReptarApp.cs:923-959`; `ActionCableServer.cs:25` |
| S-3 | H | **Header-trust boundary violations** (F-01/02/04, BuildRedirectUri) | see register |
| S-4 | H | **Unbounded memory primitives** (F-05/12/13/36/37/53) | see register |
| S-5 | C* | **AI control plane default-open** (F-56→F-62) | see register |
| S-6 | H | **Realtime has no security model** (F-50→F-52) | see register |
| S-7 | H | **Identity linking weaknesses** (F-06/07/08/10) | see register |
| S-8 | M | **XSS posture**: JSON-first; `ctx.Html` raw by design with no shipped escaper; no default CSP; extension map can serve stored .html/.svg (nosniff always on) | `Ctx.cs:173-174`; `Storage.cs:143-149` |
| S-9 | M | **Data-layer escape hatches** safe-defaults-but-wide-bypass (F-25→F-30) | see register |
| S-10 | M | **Host divergence as threat multiplier**: plaintext Kestrel, Lambda body/stacktrace, Workers per-isolate state + no client IP in envelope (`RemoteIp` always null → header keying only), buffering before limits | see register |

---

## Phase 2 — Adversarial code scenarios (vulnerable vs. secure)

### Scenario 1 — OAuth state CSRF → login CSRF / account binding hijack (F-01, OWASP A01, ASVS V2.5.3)

```csharp
// VULNERABLE — Auth/Auth.cs today: state is generated, sent, and never checked
var state = ctx.Crypto.RandomToken(16);
// cookie: "{verifier}|{callbackUrl}"  ← state NOT persisted
ctx.Cookies.Set($"reptar_oauth_pkce_{p}", $"{verifier}|{input.CallbackUrl}", expires, ...);
var authUrl = provider.BuildAuthorizationUrl(state, challenge, redirectUri);

// GET /callback/:provider — reads only `code`; a callback forged by any party holding
// the victim's pkce cookie (SameSite=Lax IS sent on top-level GET navigation!) exchanges
// an attacker-controlled code against the victim's verifier and signs the victim in.
```

Attack: attacker initiates `/social` on their own browser, extracts `code` before it's consumed
(GitHub: code valid ~10 min), then gets a logged-in victim's browser to top-navigate to
`https://victim.app/auth/callback/github?code=ATTACKER_CODE`. The Lax pkce cookie rides along,
the victim is now signed into the attacker's OAuth identity → account-linking hijack.

```csharp
// SECURE — bind state, verify with Ordinal compare, single-use (mirror BetterAuthRoutes.cs:284-301)
var state = ctx.Crypto.RandomToken(16);
// HMAC the cookie contents: verifier|callback are chosen by us; state binds the round-trip
ctx.Cookies.Set($"reptar_oauth_pkce_{p}", $"{state}|{verifier}|{SafeRelative(callbackUrl)}", ...);

// callback:
var state = ctx.Req.QueryValue("state");
var parts = pkceCookie.Split('|', 3);
if (state is null || parts.Length < 3 ||
    !FixedTimeEquals(state, parts[0]))            // constant-time, Ordinal
    return ctx.BadRequest("Invalid OAuth state.");
var verifier = parts[1];
// ... exchange code, then delete the state cookie (single-use) before redirecting
```

### Scenario 2 — Attacker-sized stackalloc → uncatchable process crash (F-12/13, OWASP API4)

```csharp
// VULNERABLE — Core/Jwt.cs VerifyAsync (native path)
Span<byte> keyBytes = stackalloc byte[Encoding.UTF8.GetByteCount(options.Secret)];
Span<byte> unsignedBytes = stackalloc byte[Encoding.UTF8.GetByteCount(unsignedSpan)];
// unsignedSpan = token header.payload — 100 % attacker-controlled length.
// A 4 MB JWT in the Authorization header → stack overflow → StackOverflowException,
// which CANNOT be caught (not an SEHException path) → Kestrel process crash. One request.

// Data/Services.cs VerifyPassword — same class of bug:
Span<byte> pwdBytes = stackalloc byte[Encoding.UTF8.GetByteCount(password)];
// password comes from ChangePasswordInput.CurrentPassword (validator: Min(1), NO Max)
// and BetterAuthRoutes /sign-in/email (NO validator at all) → same crash primitive.
```

```csharp
// SECURE — bound before allocate; normalize passwords by pre-hashing long inputs
private const int MaxTokenChars = 8 * 1024;
if (token.Length > MaxTokenChars) return null;          // fail closed, not crash
if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes)   // 512
    password = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))); // PBKDF2 pre-hash, RFC 9106-style
Span<byte> pwdBytes = stackalloc byte[Encoding.UTF8.GetByteCount(password)]; // now ≤ 512
```

### Scenario 3 — Header-spoofed rate-limit keying → infinite brute force (F-04; CVE-2018-8171 precedent)

```csharp
// VULNERABLE — Auth/Auth.cs GetClientIp (used by native login limiter on Kestrel/Lambda,
// where BOTH headers are client-set):
ctx.Req.Header("CF-Connecting-IP") ?? ctx.Req.Header("X-Forwarded-For")?.Split(',')[0].Trim() ?? "127.0.0.1";
// Every request with a fresh spoofed XFF gets a fresh throttle bucket → the limiter
// is decorative. Exactly the ASP.NET Core CVE-2018-8171 failure mode.

// SECURE — mirror the framework's own correct middleware (Middleware.cs:329-355):
#if NETWASM
    // Workers: CF-Connecting-IP is platform-injected; RemoteIp is absent from the envelope
    ctx.Req.Header("CF-Connecting-IP") ?? "loopback";
#else
    // Kestrel/Lambda: RemoteIp = actual TCP peer (KestrelHost.cs:584) / event source IP
    ctx.Req.RemoteIp ?? "loopback";   // never trust XFF unless TrustProxyHeaders is explicit
#endif
// plus: cap the map (sweep expired windows; stop recording beyond N entries)
```

### Scenario 4 — Cookie-value injection via OAuth callback URL (F-03 + F-19)

```csharp
// VULNERABLE — Core/Cookies.cs Set: raw interpolation into Set-Cookie
var parts = new List<string> { $"{name}={value}", "Path=/" , ... };
// Auth/Auth.cs /social writes user input into `value`:
$"{verifier}|{input.CallbackUrl}"   // CallbackUrl = "\r\nSet-Cookie: reptar_session=attacker" ?
// → header injection (Kestrel rejects → 500 DoS; laxer intermediaries may split responses).
// And after callback: ctx.Redirect(callbackUrl) with "https://evil.example" → open redirect.

// SECURE — validate at ingestion + sanitize at emission
// ingestion (Auth/BetterAuth): site-relative only
static bool IsSafeRedirect(string? url) =>
    url is not null && url.Length <= 512 &&
    url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") &&
    !url.AsSpan().ContainsAny('\r', '\n', '\0');
// emission (CookieJar): RFC 6265 token names, cookie-octet values
ArgumentException.ThrowIfNullOrEmpty(name);
if (name.AsSpan().IndexOfAny("\r\n;,= \t") >= 0) throw new ArgumentException("Invalid cookie name.");
value = value.Replace("\r", "").Replace("\n", "");   // or reject outright
if (value.AsSpan().ContainsAny('\r', '\n', ';', '\0')) throw new ArgumentException("Invalid cookie value.");
```

### Scenario 5 — Unverified-email OAuth account linking → takeover (F-07, OWASP A07/API2)

```csharp
// VULNERABLE — Auth/Auth.cs:338-357 (same in BetterAuthRoutes.cs:329-353)
var email = userInfo.Email.Trim().ToLowerInvariant();
var user = await ctx.Db.QuerySingleOrDefaultAsync<UserRow>(
    "SELECT * FROM users WHERE email = @email", new { email });
if (user is null) { /* create */ }
// else: silently attaches the OAuth identity to the existing account and signs it in.
// Google's email_verified is discarded at OAuth.cs:110-114. An attacker with a Google
// account that merely *claims* victim@example.com (unverified) signs in as the victim.
```

```csharp
// SECURE — gate linking on provider attestation; never mark verified without it
var verified = userInfo is { EmailVerified: true, Email: not null };
var user = await FindByEmail(ctx, email);
if (user is not null && !verified)
    return ctx.Conflict("Provider email is not verified. Sign in with your password to link this provider.");
if (user is null)
    user = await CreateOAuthUser(ctx, email, verified: verified); // EmailVerified = verified ? 1 : 0
```

### Scenario 6 — Dictionary-key identifier injection (F-25, the latent SQLi class)

```csharp
// VULNERABLE — BaaS-style flexible write (app-side pattern the framework currently permits):
app.Post("/api/docs/:collection", async (ctx, input) => {
    var doc = await ctx.JsonCodec.Deserialize<Dictionary<string, object?>>(ctx.Req.Body);
    await ctx.Db.InsertAsync(Schema.Docs, doc);
    // SqliteDbClient.cs:1083-1089: keys matching no column are spliced raw into
    // INSERT INTO docs ("{key}") ... — user-controlled IDENTIFIER position.
    // `"x") VALUES (1); DROP TABLE users;--"` as a key becomes DDL/DML injection.
});
```

```csharp
// SECURE — framework-side: reject unknown keys instead of interpolating them
if (!table.Columns.Contains(sqlName, StringComparer.Ordinal))
    throw new InvalidOperationException($"Unknown column '{sqlName}' for table '{table.Name}'.");
// + add SqlFilter/OrderBy analyzer rules (Phase 3) so raw-string escape hatches
//   taint-check any non-constant input.
```

### Scenario 7 — WebSocket upgrade bypasses the entire middleware pipeline (F-50, CSWSH)

```csharp
// VULNERABLE — ReptarApp.cs:640-676: WS opens never run _middlewares; KestrelHost.cs:89-97
// accepts any upgrade with no Origin check. A malicious page on evil.example opens
// ws://victim.app/rooms/1 in the victim's browser; SameSite=Lax cookies ARE sent on the
// WS handshake (it's a GET upgrade); Auth.RequireUser never runs; the app trusts ctx.User-less
// handlers and full room access follows.

// SECURE — run the pipeline on upgrade, enforce Origin at the transport
// ReptarApp.HandleWebSocketOpenAsync:
foreach (var middleware in _middlewares) { var gate = await middleware(ctx, () => Task.FromResult<ReptarResponse?>((ReptarResponse?)null)); if (gate is { } blocked) return; }
// KestrelHost: before AcceptWebSocketAsync —
var origin = http.Headers.Origin;
if (app.AllowedWsOrigins is { Count: > 0 } allow && (origin is null || !allow.Contains(origin)))
    { http.Response.StatusCode = 403; return; }
// + per-connection inbound frame cap (KestrelHost.cs:104-125): if (total > MaxFrameBytes) abort;
```

### Scenario 8 — Gateway `a2a/<url>` SSRF with response reflection (F-57, OWASP API10/A10)

```csharp
// VULNERABLE — AiGateway.cs:685,1241-1407 + Data/Services.cs:585-622 (native DefaultHttpClient:
// no scheme/IP validation, redirects followed):
POST /v1/chat/completions   // unauthenticated (AiGateway.cs:102)
{ "model": "a2a/http://169.254.169.254/latest/meta-data/iam/security-credentials/",
  "messages": [...] }
// ResolveA2aSendUrlAsync even honors a remote-controlled `url` field from the fetched
// agent card, and A2aAwaitTaskAsync polls a remote-supplied sendUrl up to 60×,
// returning response text in the completion → internal read primitive with disclosure.

// SECURE — port the Workers guard (host.ts:580-669) to native; blocklist private ranges,
// metadata IPs, non-https by default; allowlist mode for known A2A peers:
if (!HttpUrlPolicy.IsAllowed(url, options))   // scheme, DNS-resolved IP, redirect re-check
    return ctx.Error(400, "bad_request", "Upstream URL is not permitted.");
// + require auth on the gateway by default + enforce ModelCatalog on completions.
```

---

## Phase 3 — Automated validation strategy

### 3.1 Static analysis in CI (additions to `.github/workflows/ci.yml`)

```yaml
  security:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.401' }
      # SAST — GitHub CodeQL (C# fully supported; free for OSS)
      - uses: github/codeql-action/init@v3
        with: { languages: csharp, 'javascript-typescript' }
      - uses: github/codeql-action/analyze@v3
      # SAST — Semgrep with Reptar-specific rules (CE-compatible, no taint engine needed)
      - uses: returntocorp/semgrep-action@v1        # or: pip install semgrep && semgrep --config .semgrep
        with: { config: '.semgrep' }
      # SCA — NuGet vulnerability audit (lockfile-friendly, no token needed)
      - run: dotnet list framework/Reptar/Reptar.csproj package --vulnerable --include-transitive
      - uses: actions/dependency-review-action@v4    # + Dependabot config (already Bun+NuGet)
```

Roslyn analyzers via `Directory.Build.props` (Security Code Scan for classic vuln patterns,
Meziantou for crypto/design hygiene, **BannedApiAnalyzers** to ban Reptar's own escape hatches in *app* code):

```xml
<ItemGroup>
  <PackageReference Include="SecurityCodeScan.VS2019" Version="5.6.7" PrivateAssets="all" />
  <PackageReference Include="Meziantou.Analyzer" Version="*" PrivateAssets="all" />
  <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="*" PrivateAssets="all" />
  <AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />
</ItemGroup>
```

`BannedSymbols.txt` (enforced for apps that reference the framework, opt-out documented):

```
T:Reptar.SqlFilter; Reptar: use typed ColumnRef filters; SqlFilter.Raw is audit-gated.
M:Reptar.TableQuery`1.OrderBy(System.String); Reptar: use ColumnRef.Desc()/Asc() — string ORDER BY is SQL-injection-prone.
P:Reptar.Core.Ctx.Html(System.String); Reptar: raw HTML — use an Html.Encode helper; audit-gated.
```

### 3.2 Semgrep rules — `.semgrep/reptar.yaml` (framework-specific, CE-safe patterns)

```yaml
rules:
  - id: reptar-raw-sql-tainted-where
    languages: [csharp]
    severity: ERROR
    message: "Raw SQL clause built from request data — use typed ColumnRef filters or parameters."
    patterns:
      - pattern-either:
          - pattern: ctx.Db.ListAsync(..., $X + ...)
          - pattern: ctx.Db.DeleteWhereAsync(..., $X + ...)
          - pattern: ctx.Db.ExistsAsync(..., $X + ...)
          - pattern: ctx.Db.QueryAsync<T>(... + ...)
      - metavariable-pattern: { metavariable: $X, pattern-either: [ctx.Req.Param(...), ctx.Req.QueryValue(...), ctx.Req.Header(...)] }
  - id: reptar-open-redirect-tainted
    languages: [csharp]
    severity: ERROR
    message: "ctx.Redirect with non-relative/unvalidated URL — enforce site-relative targets."
    pattern-either:
      - pattern: ctx.Redirect(ctx.Req.QueryValue(...))
      - pattern: ctx.Redirect(input.$FIELD)
  - id: reptar-cookie-value-from-user
    languages: [csharp]
    severity: WARNING
    message: "User input flows into a Set-Cookie value — validate/encode before CookieJar.Set."
    pattern-either:
      - pattern: ctx.Cookies.Set(..., input.$FIELD, ...)
      - pattern: ctx.Cookies.Set(..., ctx.Req.$ANY(...), ...)
  - id: reptar-orderby-string-tainted
    languages: [csharp]
    severity: ERROR
    message: "SqlOrderBy from string defeats typed tokens — never with request data."
    pattern-either:
      - pattern: $Q.OrderBy(ctx.Req.$ANY(...))
      - pattern: $Q.OrderBy($X + ...)
```

### 3.3 Security regression tests (`framework/Reptar.Tests/SecurityRegressions.cs`)

Mirror the established `SecurityAndCacheTests` style (`app.HandleAsync` + raw `ReptarRequest`):

1. `OAuth_Callback_RejectsMissingOrWrongState` — POST /social → GET callback with `code` only / wrong `state` ⇒ 400; correct `state` ⇒ redirects.
2. `Jwt_VerifyRejectsOversizedTokenWithoutCrashing` — 2 MB forged token ⇒ `VerifyAsync` returns null (no stack overflow).
3. `VerifyPassword_SurvivesMultipartMegabytePassword` — 1 MB password string on native `DevCrypto` ⇒ false, no crash.
4. `LoginThrottle_IgnoresSpoofedForwardedHeaders` — native slice: rotate `X-Forwarded-For`/`CF-Connecting-IP` across 20 failed sign-ins from one `RemoteIp` ⇒ 429 (mirror SecurityAndCacheTests.cs:76-113 but through `Auth.Routes()`).
5. `LoginThrottle_DictionaryIsBounded` — flood 20k distinct RemoteIps ⇒ map size ≤ cap, no OOM.
6. `CookieJar_RejectsCrlfAndSeparatorValues` + `CookieJar_RejectsInvalidNames` ⇒ `ArgumentException`, and `Set` output never contains raw `\r`/`\n`.
7. `Redirect_RejectsControlCharLocations` ⇒ `ArgumentException`.
8. `OAuth_CallbackUrl_MustBeSiteRelative` — `/social` with `https://evil.example` / `//evil` ⇒ 400.
9. `OAuth_DoesNotLinkUnverifiedProviderEmail` — Google `email_verified=false` + existing local user ⇒ no link/409.
10. `Ws_Upgrade_RunsAuthMiddleware_AndOriginGate` — upgrade request without origin/with cross origin + without session ⇒ 403 (after S-6 patch).
11. `Multipart_FuzzCorpus` — deterministic mini-fuzz: 50 malformed bodies (truncated, boundary-colliding, null-byte, oversize field count) ⇒ always null/413, never throw.
12. **Dual-host parity gate**: run the auth + validation tests under both `net10.0` and a workerd-backed `test:realtime`-style harness (extend `bun reptar test:realtime` pattern to `test:security`) — catches S-1 drift before release.

### 3.4 Runtime & dynamic

- **`dotnet-counters monitor --counters System.Runtime,Microsoft.AspNetCore.Hosting`** during a k6/bombardier mix including oversized-JWT and WS-flood profiles — watch allocation rate + gen2 for the unbounded primitives (F-05/36/53).
- **`dotnet-dump collect/analyze`** on Kestrel after 10k concurrent WS frames → confirm frame cap; `threadpool` queue depth for SSE producer storms.
- **Fuzzing**: SharpFuzz/libFuzzer over the NetWasm module targeting `MultipartForm.TryParse` and `JsonCodec` inputs; AFL-style corpus from StorageAndUploadTests seeds.
- **`bun reptar check:security`** — new CLI step chaining semgrep + analyzers + the regression tests, wired into `bun reptar self-check`.

### 3.5 Process (OSS-release readiness)

- `SECURITY.md` + private advisory workflow (GitHub Security Advisories → GHSA/CVE) — required before the open-source launch, given the framework ships auth primitives.
- Threat-model doc (this file) kept in `plans/`, refreshed per release; ASVS 4.0 V2/V3/V5 checklist attached to PRs touching `Auth/`, `Core/Jwt.cs`, `Core/Cookies.cs`.
- Dependency pinning already exists (bun.lock, NuGet lockfile via `packages.lock.json` for Semgrep SCA); add `dependabot.yml` for both ecosystems.
- Document the security contract: which guarantees the framework gives by default (parameterized values, cookie attributes, body caps, signed cookies) vs. what app authors own (per-route auth, validation, per-tool authz).

---

## Patch plan (top findings — pre-approved scope)

| # | Patch | Files | Findings |
|---|---|---|---|
| P1 | OAuth `state` round-trip validation on `/social` + `/callback/:provider` (mirror BetterAuth cookie pattern) | `framework/Reptar/Auth/Auth.cs` | F-01 |
| P2 | Site-relative `callbackUrl`/`destination` enforcement at ingestion + CRLF guard in `Ctx.Redirect` | `Auth/Auth.cs`, `Auth/BetterAuthRoutes.cs`, `Core/Ctx.cs` | F-03 |
| P3 | Host-aware throttle keying (RemoteIp native / CF-Connecting-IP Wasm; drop raw XFF) + bounded failure maps | `Auth/Auth.cs`, `Reptar.Wasm/Auth/Auth.cs` | F-04, F-05 |
| P4 | Token/password length bounds before stackalloc (JWT max 8KB; password pre-hash >512B) | `Core/Jwt.cs`, `Data/Services.cs` | F-12, F-13, F-11 |
| P5 | `CookieJar` RFC 6265 name/value sanitization | `Core/Cookies.cs` | F-19 |
| P6 | Shared login throttle wired into BetterAuth `sign-in/email` + password cap | `Auth/BetterAuthRoutes.cs` (+ shared helper) | F-09, F-11 |
| P7 | Google `email_verified` honored on linking; verified-only link | `Auth/OAuth.cs`, `Auth/Auth.cs`, `Auth/BetterAuthRoutes.cs` | F-07 |

Deferred (report-only, larger design work): WS auth/origin model (F-50/51), native SSRF guard (F-38/57),
workspace-tools auth gate + gateway auth/model allowlist (F-56/58), Wasm R2 normalization (F-47),
security-headers-default-on debate (S-2), CSRF token middleware (F-06) — each scoped in this doc for follow-up.
