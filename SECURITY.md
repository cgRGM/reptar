# Security Policy

Reptar is a dual-target C# web framework (Cloudflare Workers via NetWasm as the
primary target, Native AOT on Kestrel/AWS Lambda as fallbacks). This document
defines the security contract, what is in scope for reports, and how to
disclose vulnerabilities responsibly.

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Use [GitHub Security Advisories](https://github.com/security/advisories) —
"Report a vulnerability" on the repository — or email the maintainers directly.
Include:

- the affected file(s) and commit SHA,
- a minimal reproduction (a Reptar app or a raw `ReptarRequest` is ideal),
- whether the issue applies to NetWasm, native, or both hosts.

You will get an acknowledgement within 7 days. Reports that land a fix earn
co-authorship on the advisory.

## Scope

**In scope:** the framework under `framework/` — auth/session/OAuth, JWT
handling, the request pipeline, middleware, validation, the typed query layer
and DbClients, storage providers, cookies, and the host shims
(`packages/host/src/`) when the flaw enables a framework-level bypass.

**Out of scope:** applications built *on* Reptar (per-handler authorization
gaps, unsafe app SQL, exposed secrets in app code), the `examples/` and
`spikes/` trees (they are demonstrations, not shipped products), and generic
DoS at the platform level.

## The security contract

Guarantees the framework provides by default:

- **SQL:** every query path binds values as parameters. `ColumnRef<T>` tokens
  are type-safe and compile-time bound; `SqlParamParser` rewrites placeholders
  without touching values. Escape hatches (`SqlFilter.Raw`, raw-clause
  overloads, `SqlOrderBy(string)`) are developer tools — feeding them request
  data is an application vulnerability, and `.semgrep/reptar.yaml` flags it.
- **Sessions:** 32-byte CSPRNG tokens, SHA-256 hashed at rest, expiry checked
  on every lookup. Cookies default `HttpOnly; SameSite=Lax; Secure`.
- **JWT:** HS256 only — the JOSE header is never trusted, so the alg-confusion
  family does not apply. Issuer/audience/expiry enforced; oversized tokens
  fail closed (8 KiB cap).
- **Passwords:** PBKDF2-SHA256, 100k iterations (workerd WebCrypto cap, matched
  across native and Wasm so hashes verify cross-host); inputs > 512 bytes are
  pre-hashed; `FixedTimeEquals` comparison.
- **Body limits:** 10 MiB default with per-route overrides; multipart limits
  (10 files, 100 fields); WebSocket inbound frames capped at 1 MiB.
- **Throttling:** login routes key on the socket peer address; proxy headers
  (`CF-Connecting-IP`, `X-Forwarded-For`) are only trusted behind an explicit
  `TrustProxyHeaders` opt-in. Failure maps are memory-bounded.
- **Headers:** `Ctx.Redirect` and `CookieJar.Set` reject control characters —
  response-splitting and Set-Cookie injection are closed at the framework
  layer. `ctx.File` always sets `X-Content-Type-Options: nosniff`.
- **Middleware on all transports:** the app middleware pipeline gates WebSocket
  upgrades exactly like HTTP requests — auth/rate-limit/origin middlewares
  apply to WS opens on both hosts.
- **Outbound HTTP:** `DefaultHttpClient` and the Workers shim block loopback,
  link-local, private, and metadata networks (opt-in allowlists for
  self-hosted endpoints).

What application authors own: per-route authorization (`Auth.RequireUser`,
policies), input validation (`Rule<T>`), CSRF posture beyond `SameSite=Lax`
(opt-in `Middlewares.Csrf` double-submit tokens), and security headers
(opt-in `UseSecurityHeaders`).

## Known limitations

- Outbound HTTP policy runs before DNS resolution: decimal-IP/hex-octet
  encodings and DNS rebinding are not blocked (true on both hosts).
- Kestrel serves plaintext HTTP by design — terminate TLS at the edge or run
  behind a TLS proxy.
- The Wasm auth slice (`framework/Reptar.Wasm/Auth/`) is a separate
  implementation from the native slice; the CI test suite runs the native
  slice, and the Workers smoke suite (`bun reptar test:realtime`) covers the
  two-host contract.
- GitHub-referenced docs pages (Scalar/Swagger UI) load scripts from public
  CDNs; pin or self-host before exposing them in production.

## Supply chain

- Dependencies are audited in CI (`dotnet list package --vulnerable`,
  `bun audit`) and updated via Dependabot.
- Semgrep + CodeQL run on every push with Reptar-specific rules
  (`.semgrep/`).
- Regression tests for every security fix live in
  `framework/Reptar.Tests/SecurityRegressions.cs`; the full findings register
  and remediation log is `plans/security-deep-dive-2026-10.md`.
