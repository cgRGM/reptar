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
  // The DO fetches only small host metadata (deployment/interop manifests).
  // The application wasm is compiled from the imported module, so it is not
  // fetched through ASSETS (see @reptar/host patchNetWasmHost).
  assets: () => ({
    fetch: (request: Request) => fetch(request),
  }),
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
    const url = new URL(request.url);

    // Serve host metadata artifacts from the Worker before entering the DO.
    // The DO host's `assets` hook routes its /browser/* requests back here.
    if (url.pathname.startsWith("/browser/")) {
      return env.ASSETS.fetch(request);
    }

    // Route everything through the hub DO so REST stats and WebSocket
    // fanout observe the same room state. Production sharding would use
    // idFromName(roomId) per room instead of a single "hub" instance.
    const stub = env.ROOMS.get(env.ROOMS.idFromName("hub"));
    return stub.fetch(request);
  },
};
