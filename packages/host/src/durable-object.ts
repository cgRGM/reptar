import { createReptarHost } from "./host";
import type {
  ExecutionContextLike,
  ReptarHost,
  ReptarHostOptions,
} from "./host";

/** The subset of DurableObjectState storage the alarm bridge touches. */
interface DurableObjectStorageLike {
  setAlarm?: (time: number) => Promise<void>;
  getAlarm?: () => Promise<number | null>;
  deleteAlarm?: () => Promise<void>;
}

interface DurableObjectStateLike {
  storage?: DurableObjectStorageLike;
  waitUntil?: ExecutionContextLike["waitUntil"];
}

/**
 * Creates a Durable Object class that hosts a NetWasm Reptar app inside
 * object instances.
 *
 * Why a Durable Object? Cloudflare Workers forbid touching one request's
 * I/O objects from another request's context — a plain Worker cannot fan
 * a WebSocket frame from one peer's socket to another's. Cross-socket
 * broadcast (rooms, presence, multiplayer) must run where every socket
 * shares one context: a Durable Object. This is the same shape as
 * PartyKit's "room = DO" model.
 *
 * Sharding: route by room id for production scale —
 *
 *   export const RoomsHub = createReptarDurableObjectHost<Env>(options);
 *   // in the Worker:
 *   const stub = env.ROOMS.get(env.ROOMS.idFromName(roomId));
 *
 * The demo deployment routes everything through idFromName("hub") so REST
 * stats and WebSocket state observe one app instance.
 *
 * Events: the host's opt-in completion contract applies here too — apps
 * that export supportsWsMessageDone() and call wsMessageDone(sessionId)
 * hold WebSocket events open until their async pipelines (including D1
 * writes) complete.
 *
 * Alarms: the object implements the runtime's alarm() handler. The C# app
 * registers `app.OnAlarm(...)`, and this host dispatches a request to the
 * reserved `/_reptar/alarm` path through the normal fetch pipeline — no
 * additional JS exports (Binaryen rejects extra exports from the NetWasm
 * build). To schedule a wake-up, POST the reserved
 * `/_reptar/alarm/schedule` path with `{"runAtMs": <epoch ms>}` — from C#:
 *
 *   await ctx.Durable.FetchAsync("SCHEDULER", "jobs",
 *       "/_reptar/alarm/schedule", "POST", runAtMs.ToString());
 *
 * A throwing handler returns HTTP 500 and alarm() rethrows, so the runtime
 * retries the alarm with its built-in backoff.
 */
export const createReptarDurableObjectHost = <TEnv>(
  options: ReptarHostOptions<TEnv>
) =>
  class ReptarDurableObject {
    private host: ReptarHost<TEnv> | null = null;
    private readonly env: TEnv;
    private readonly state?: DurableObjectStateLike;
    private readonly executionCtx?: ExecutionContextLike;

    constructor(state: unknown, env: TEnv) {
      this.env = env;
      const candidate = state as DurableObjectStateLike;
      this.state = candidate?.storage ? candidate : undefined;
      const waitUntil = candidate?.waitUntil;
      this.executionCtx =
        typeof waitUntil === "function"
          ? { waitUntil: waitUntil.bind(candidate) }
          : undefined;
    }

    async fetch(request: Request): Promise<Response> {
      const url = new URL(request.url);

      // Reserved bridge path: store the wake-up time in DO storage.
      // Handled entirely in TS so the C# app never needs a setAlarm import.
      if (url.pathname === "/_reptar/alarm/schedule") {
        const storage = this.state?.storage;
        if (typeof storage?.setAlarm !== "function") {
          return Response.json(
            {
              error: "This object does not expose Durable Object storage.",
            },
            { status: 501 }
          );
        }

        const body = (await request.json()) as { runAtMs?: number | string };
        const runAtMs =
          typeof body.runAtMs === "string"
            ? Math.trunc(Number(body.runAtMs))
            : body.runAtMs;
        if (
          runAtMs === undefined ||
          !Number.isFinite(runAtMs) ||
          runAtMs < Date.now()
        ) {
          return Response.json(
            { error: "runAtMs must be a future epoch ms." },
            { headers: { "content-type": "application/json" }, status: 400 }
          );
        }

        await storage.setAlarm(runAtMs);
        return Response.json(
          { runAtMs, scheduled: true },
          { headers: { "content-type": "application/json" } }
        );
      }

      // The NetWasm app boots once per object instance (the WASM module is
      // shared; instantiation cost is per-DO cold start).
      this.host ??= createReptarHost<TEnv>(options);
      return this.host.fetch(request, this.env, this.executionCtx);
    }

    async alarm(): Promise<void> {
      this.host ??= createReptarHost<TEnv>(options);
      const response = await this.host.fetch(
        new Request("https://do.local/_reptar/alarm", { method: "POST" }),
        this.env,
        this.executionCtx
      );

      if (response.status >= 500) {
        // Rethrow so the runtime retries the alarm with its own backoff.
        throw new Error(
          `Reptar alarm handler failed: ${await response.text()}`
        );
      }
    }
  };
