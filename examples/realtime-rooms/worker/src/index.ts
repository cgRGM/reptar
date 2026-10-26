import { createReptarDurableObjectHost } from "@reptar/host";
import type {
  D1DatabaseLike,
  ReptarBindings,
  ReptarWasmApp,
  ReptarHostOptions,
} from "@reptar/host";

import wasmModule from "../public/browser/RoomsWorker.wasm";

interface Env {
  ASSETS: { fetch: (request: Request) => Promise<Response> };
  DB: D1Database;
  ROOMS: DurableObjectNamespace;
}

// Cloudflare Workers forbid touching one request's I/O objects from
// another request's context — a plain Worker cannot fan a frame from one
// peer's socket to another's. Cross-socket room fanout therefore runs
// inside a Durable Object (the PartyKit "room = DO" pattern), where every
// socket and request shares one context. The NetWasm hub boots once
// inside the DO and owns all room state.
const hostOptions: ReptarHostOptions<Env> = {
  // The Wasm app boots by fetching /browser/deployment.json through the
  // static-assets binding, so it must be handed to the host.
  assets: (env) => env.ASSETS,
  bindings: (env) =>
    ({
      db: env.DB as unknown as D1DatabaseLike,
    }) as ReptarBindings,
  open: async () => {
    const module = await import("../public/browser/RoomsWorker.mjs");
    return (await module.open()) as ReptarWasmApp;
  },
  wasm: wasmModule,
};

export const RoomsHub = createReptarDurableObjectHost<Env>(hostOptions);

export default {
  fetch: (request: Request, env: Env): Promise<Response> => {
    // Route everything through the hub DO so REST stats and WebSocket
    // fanout observe the same room state. Production sharding would use
    // idFromName(roomId) per room instead of a single "hub" instance.
    const stub = env.ROOMS.get(env.ROOMS.idFromName("hub"));
    return stub.fetch(request);
  },
};
