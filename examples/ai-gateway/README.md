# Reptar AI Gateway & Unified Streaming Engine

A [Cloudflare AI Gateway](https://developers.cloudflare.com/ai-gateway/) / [Portkey Gateway](https://github.com/Portkey-AI/gateway)-style universal LLM proxy, built as a single file-based Reptar app. Wire-compatible with the [Vercel AI SDK](https://github.com/vercel/ai) Data Stream Protocol so any React/Next/Vite/Svelte app using `useChat()` or TanStack AI connects with zero client adapters.

## Run it

```bash
bun gateway:dev        # Kestrel on http://localhost:8788 (boots via scripts/reptar.ts)
bun gateway:bench      # 500-connection streaming load benchmark (needs `oha`)
```

## Endpoints

| Route | What it does |
| :-- | :-- |
| `POST /v1/chat/completions` | Universal proxy: OpenAI-compatible in, OpenAI-compatible SSE / Vercel Data Stream / buffered JSON out |
| `GET /v1/models` | Model catalog (provider-prefixed Portkey-style models included) |
| `GET /health` | Liveness |
| `GET /gateway/stats` | Token telemetry aggregates from `gateway_logs` |
| `POST /gateway/reset` | Wipe prompt cache + telemetry (dev helper) |

## Provider routing & failover

Model names route by prefix; `provider/model` syntax (Portkey-style) overrides:

| Pattern | Provider | Auth env |
| :-- | :-- | :-- |
| `gpt-*`, `o1/o3/o4*`, `openai/…` | OpenAI (`/v1/chat/completions`) | `OPENAI_API_KEY` |
| `openrouter/…` | OpenRouter | `OPENROUTER_API_KEY` |
| `claude-*`, `anthropic/…` | Anthropic (`/v1/messages`) | `ANTHROPIC_API_KEY` |
| `gemini-*`, `gemini/…` | Gemini (`:generateContent`) | `GEMINI_API_KEY` |
| `llama*`, `mixtral*`, `qwen*`, `groq/…` | Groq | `GROQ_API_KEY` |
| `ollama/…` | Local Ollama (`/api/chat`) | none (`OLLAMA_BASE_URL`) |
| `workers-ai/…` | Cloudflare Workers AI (NetWasm) | Workers binding |
| `a2a/<agent-url>` | **A2A agents as upstreams**: agent card resolved once, `message/send` JSON-RPC, `tasks/get` polling for long-running tasks | none |
| `mock*` | Built-in mock (benchmarks/demos) | none |

If the primary provider returns `429`/`5xx`/connection errors, the next provider in the chain is retried **within the same HTTP connection**. The chain defaults to `GATEWAY_FALLBACKS=mock` so demos always succeed; set it to `""` for production.
A2A knobs: `A2A_POLL_ATTEMPTS` (default 60), `A2A_POLL_INTERVAL_MS` (default 500).

## Stream resume (continuation tokens)

Every streaming response carries `X-Reptar-Continuation: <token>`, and every SSE
frame carries an incremental `id:`. A client that drops mid-stream can resume from
the cached completion at zero upstream cost:

```
POST /v1/chat/completions?continuation=<token>&last_event=<last-received-id>
```

SSE clients can send the standard `Last-Event-ID` header instead of `last_event`;
data-stream clients use the same frame-index semantics via `last_event`.

## Wire protocols

Selected via `?stream_protocol=data-stream` query or `Accept` header:

1. **Standard SSE** — `data: {"choices":[{"delta":{"content":"…"}}]}` … `data: [DONE]` (`Accept: text/event-stream`)
2. **Vercel AI SDK Data Stream** — `0:"token"` text deltas, `9:{"toolCallId",…}` tool calls, `d:{"finishReason","usage"}` finish (`Accept: text/vnd.ai` / `?stream_protocol=data-stream`)
3. **Buffered JSON** — plain OpenAI `chat.completion` object (`"stream": false`)

## Prompt cache

Keyed on `SHA-256(model + canonical_messages_json)` with TTL (`GATEWAY_CACHE_TTL_SECONDS`, default 120) and bounded entries (`GATEWAY_CACHE_MAX_ENTRIES`, default 4096). Hits replay the cached token stream with `X-Reptar-Cache: HIT` at zero upstream token cost; bypass per-request with `?no_cache=1`.

## Telemetry

Every request logs model, prompt/completion tokens, latency, and cache status into the `gateway_logs` table (`schema.cs`, plain `TableDef`). Inserts are fire-and-forget, flowing through Reptar's SQLite group-commit channel batcher so response latency is decoupled from disk I/O. `bun reptar db plan --db gateway.db` sees the table.

## Env knobs

`OPENAI_API_KEY`, `OPENAI_BASE_URL`, `OPENROUTER_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `GROQ_API_KEY`, `OLLAMA_BASE_URL`, `A2A_POLL_ATTEMPTS`,
`A2A_POLL_INTERVAL_MS`, `GATEWAY_FALLBACKS`, `GATEWAY_DEFAULT_PROVIDER`, `GATEWAY_CACHE_TTL_SECONDS`, `GATEWAY_CACHE_MAX_ENTRIES`, `GATEWAY_MOCK_TOKENS` (default 48), `GATEWAY_TOKEN_DELAY_MS` (stream pacing for demos), `GATEWAY_LOGGING=0` to disable, `PORT` (default 8788).

## Benchmark results (M3 MacBook, dev JIT host, 500 connections)

See `results/` for full JSON. Canonical run (other agents building on this machine concurrently — treat as noisy-floor numbers, not best-case):

| Metric | Value |
| :-- | :-- |
| Uncached throughput (streaming POST, `?no_cache=1`) | **22,044 req/s**, 220k requests, 100% success, p50 22 ms |
| Prompt-cache-hit throughput | **28,499 req/s**, 285k requests, 100% success, p50 17 ms |
| Streaming TTFT (cache miss) | p50 59 ms / p99 69 ms at 500-way burst |
| Streaming TTFT (cache HIT) | p50 36 ms / p99 45 ms |
| Telemetry rows committed during the run | **100,445** (~1.2–1.5k rows/s sustained drain — see below) |

Two honest caveats, both reported in `DX-REPORT.md` (F1, F10):

1. The gateway does **not** true-stream from upstreams: Reptar's `IHttpClient.FetchAsync` buffers whole responses, so the gateway normalizes a buffered upstream completion into token frames. The mock provider generates tokens locally; the benchmark measures the full downstream streaming path.
2. Telemetry inserts are fire-and-forget into Reptar's SQLite group-commit queue, whose pending-write queue is unbounded. Under a 20-second 20k req/s burst the queue backlog grew to ~400k pending writes (RSS ~1.7 GB) and the writer drains at only ~1.2–1.5k rows/s on this machine, so anything still queued is lost if the process exits. For sustained high-rate logging, bound the queue or batch inserts yourself via `DbStatement`/`BatchAsync`.

## Connect a Vercel AI SDK app

```ts
import { useChat } from "@ai-sdk/react";

// Point useChat at the gateway; no adapters needed.
const { messages, input, handleSubmit } = useChat({
  api: "http://localhost:8788/v1/chat/completions",
  streamProtocol: "data", // uses the 0:/9:/d: wire protocol above
  body: { model: "mock-mini" },
});
```
