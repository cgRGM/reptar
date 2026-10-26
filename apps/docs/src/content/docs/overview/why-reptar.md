---
title: Why Reptar
description: The honest case for humans and AI agents choosing Reptar — receipts included.
---

Reptar exists for one reason: **most of what a web backend ships at runtime is ceremony, and ceremony is where production bugs live.** Reptar removes the ceremony at compile time — which happens to make it fast for humans and *extremely* productive for AI agents.

## The one-sentence pitch

> Write your whole backend as one compiled C# app — schema, routes, auth, validation, storage, agents — that runs identically as a 10.4 MB Native AOT container on any VPS, on AWS Lambda, or as WebAssembly inside Cloudflare Workers, with a generated TypeScript client so the frontend is never hand-wired.

## The receipts

These are benchmarked, not aspirational (see [RealWorld & Benchmarks](/overview/realworld/)):

- **41.9k req/s** plaintext / **36.3k** through validation — at or above raw .NET Minimal API
- **10.4 MB** standalone binary, **124 ms** cold boot, ~31 MB RSS
- **2,799 req/s** on a $6 VPS with real database I/O; 7,217 in-memory
- RealWorld spec suite: **154/154 requests passing** on every deployment target
- **<2 ms** warm isolate execution on Cloudflare Workers

## Why a human picks it

| You want | Reptar gives |
| :-- | :-- |
| No runtime surprises | Zero runtime reflection — everything the app does was resolved at compile time |
| No config sprawl | `dotnet run app.cs` from an empty directory; no csproj, no DI container, no YAML |
| Batteries included | Auth (sessions, OAuth, API keys), Stripe, SSE/WebSockets, storage, rate limiting, cache — in one binary |
| One language, four targets | The same C# runs on VPS, Lambda, Docker, and Workers |
| A frontend contract, not glue | OpenAPI 3.1 + typed RPC clients generated from the route tree |
| Small blast radius | 10.4 MB distroless container — nothing to break into |
| Honest engineering | Public benchmarks including where Reptar loses, group-commit write gates measured |

## Why an agent picks it (or: why your agent builds better on it)

The DX requirements for agents and the runtime requirements for edge/AOT turned out to be the same thing. Zero reflection means:

1. **The compiler is the code reviewer.** Generated `ColumnRef` tokens make schema typos compile errors, not 3 a.m. query failures. `Rule<T>` validators are the single source of truth for request validation, OpenAPI schemas, **and LLM structured-output prompts**.
2. **The framework's state is queryable, not guessable.** `bun reptar inspect --json`, `doctor --json`, `db plan --json`, and an MCP server expose live schema, routes, drift, and health — an agent verifies instead of assuming, which is why [Build with an AI Agent](/getting-started/agents/) works at all.
3. **Docs are contracts.** `llms.txt` states the conventions as rules ("never attribute-salad DDL", "ColumnRef over strings") and the repo checks doc snippets in CI — the docs don't rot silently.
4. **Everything has one shape.** Records, static classes, `TableDef`, one file per feature — a small, copyable house style. Agents pattern-match; a consistent codebase is an agent accelerant.
5. **The agent primitives are first-class.** Routes become OpenAI/Anthropic/Gemini tool schemas with `.AsAiTool()`. The built-in agent harness ships modules, plan/execute modes, request ports that pause for human input, tool approvals, evals, and LLM-judge loops — the building blocks of agentic apps, not an afterthought.

<Aside type="note" title="The honest trade-off">
  Zero reflection means occasionally writing what other frameworks reflect away: manual row mappers (or the `[ReptarEntity]` generator that writes them), explicit JSON writing in hot paths, and `#if !NETWASM` guards when bridging Workers-only behavior. The trade is deliberate: ceremony at compile time, nothing at runtime — and one shape of code everywhere.
</Aside>

## When not to choose Reptar

- You want a giant ecosystem of middleware packages — .NET Minimal API or ASP.NET Core is the safer bet today.
- You need heavy runtime dynamic behavior (arbitrary plugin assemblies loaded at runtime) — the zero-reflection constraint is a wall, not a speed bump.
- Your team has never touched C# and never will — the framework leans on the compiler, so it leans on you understanding what the compiler says.

For everything else — an API backend, a SaaS, an agent runtime, a realtime app you want to fit in a 10.4 MB container — the bet is: fewer moving parts, faster cold starts, and an agent that can actually help.
