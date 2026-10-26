import { createReptarHost } from "./host";
import type {
  ExecutionContextLike,
  ReptarHost,
  ReptarHostOptions,
} from "./host";

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
 */
export const createReptarDurableObjectHost = <TEnv>(
  options: ReptarHostOptions<TEnv>
) =>
  class ReptarDurableObject {
    private host: ReptarHost<TEnv> | null = null;
    private readonly env: TEnv;
    private readonly executionCtx?: ExecutionContextLike;

    // _state is the runtime's DurableObjectState; the host never touches it
    // directly, so it stays opaque here (the package does not depend on
    // @cloudflare/workers-types).
    constructor(state: unknown, env: TEnv) {
      this.env = env;
      const candidate = state as { waitUntil?: ExecutionContextLike["waitUntil"] };
      this.executionCtx =
        typeof candidate?.waitUntil === "function"
          ? { waitUntil: candidate.waitUntil.bind(candidate) }
          : undefined;
    }

    fetch(request: Request): Promise<Response> {
      // The NetWasm app boots once per object instance (the WASM module is
      // shared; instantiation cost is per-DO cold start).
      this.host ??= createReptarHost<TEnv>(options);
      return this.host.fetch(request, this.env, this.executionCtx);
    }
  };
