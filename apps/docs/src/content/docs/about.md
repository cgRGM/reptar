---
title: About Reptar
description: What Reptar is, who builds it, and where the project lives.
---

Reptar is a high-performance, single-binary C# 13 web framework built for deployment targets that punish heavy runtimes: Cloudflare Workers (compiled to WebAssembly), AWS Lambda (Native AOT), containers, and plain VPS hosts. It enforces one architectural rule — [The One Rule](/overview/the-one-rule/) — zero runtime reflection, with every constraint validated at compile time. That rule is what lets the same application run unchanged across all of those targets.

## Who builds it

Reptar is developed in the open by [Rocktown Labs](https://github.com/RocktownLabs). The framework, its examples, the benchmark suite (the RealWorld spec, run head-to-head against .NET Minimal API and Hono on Bun), and this documentation site all live in the [RocktownLabs/reptar](https://github.com/RocktownLabs/reptar) repository.

## Project status

Reptar is in active, early development — pre-1.0. The API surface is still moving and the docs are updated continuously. Contributions, issue reports, and benchmark reproductions are welcome on GitHub.

## What's on this site

This documentation covers the framework end to end: the [quick start](/getting-started/quickstart/), [core concepts](/core/routing/) like routing and the `Ctx` surface, the [data layer](/data/schema/), [built-in auth](/auth/builtin/), [Cloudflare bindings](/cloudflare/bindings/), and a [generated API reference](/api/reference/) for the blog example app.

If you're building with an AI coding agent, start with [Build with an AI Agent](/getting-started/agents/) — the framework and its documentation are designed to be agent-driven.
