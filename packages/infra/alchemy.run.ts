import * as Alchemy from "alchemy";
import * as Cloudflare from "alchemy/Cloudflare";
import * as Output from "alchemy/Output";
import * as Effect from "effect/Effect";
import "varlock/auto-load";

export default Alchemy.Stack(
  "reptar",
  {
    providers: Cloudflare.providers(),
    state: Cloudflare.state(),
  },
  Effect.gen(function* reptarStack() {
    const db = yield* Cloudflare.D1.Database("db", {
      migrations: "../../spikes/netwasm-worker/migrations",
    });

    const bucket = yield* Cloudflare.R2.Bucket("bucket");
    const cache = yield* Cloudflare.KV.Namespace("cache");
    const events = yield* Cloudflare.Queues.Queue("events");

    const api = yield* Cloudflare.Worker("api", {
      assets: "../../spikes/netwasm-worker/public",
      compatibility: {
        date: "2026-10-22",
      },
      env: {
        AI: Cloudflare.Workers.AI(),
        APP_NAME: "reptar",
        BUCKET: bucket,
        CORS_ORIGINS:
          "https://reptar-web.rocktown-labs.workers.dev,http://localhost:4321",
        COUNTER: Cloudflare.DurableObject("Counter", {
          className: "Counter",
        }),
        DB: db,
        KV: cache,
        QUEUE: events,
        REALWORLD_JWT_SECRET:
          process.env.REALWORLD_JWT_SECRET ??
          "reptar-realworld-production-secret",
      },
      main: "../../spikes/netwasm-worker/src/index.ts",
      name: "reptar-api",
    });

    const realworldWorker = yield* Cloudflare.Worker("realworld", {
      assets: "../../spikes/realworld-worker/public",
      compatibility: {
        date: "2026-10-22",
      },
      env: {
        CORS_ORIGINS:
          "https://reptar-web.rocktown-labs.workers.dev,http://localhost:4321",
        DB: db,
        REALWORLD_JWT_SECRET:
          process.env.REALWORLD_JWT_SECRET ??
          "reptar-realworld-production-secret",
      },
      main: "../../spikes/realworld-worker/src/index.ts",
      name: "reptar-realworld",
    });

    // Reptar Rooms — the NetWasm WebSocket presence/broadcast hub
    // (examples/realtime-rooms). Build first:
    //   bun scripts/reptar.ts build:worker:rooms
    const roomsDb = yield* Cloudflare.D1.Database("rooms_db", {
      migrations: "../../examples/realtime-rooms/worker/migrations",
    });

    const roomsWorker = yield* Cloudflare.Worker("rooms", {
      assets: "../../examples/realtime-rooms/worker/public",
      compatibility: {
        date: "2026-10-22",
      },
      env: {
        DB: roomsDb,
        // Workers cannot send across another request's socket, so all room
        // fanout runs inside this Durable Object (PartyKit room pattern).
        ROOMS: Cloudflare.DurableObject("RoomsHub", {
          className: "RoomsHub",
        }),
      },
      main: "../../examples/realtime-rooms/worker/src/index.ts",
      name: "reptar-rooms",
    });

    const docsWorker = yield* Cloudflare.Website.StaticSite("docs", {
      // Edge worker in front of the assets: serves per-page Markdown twins
      // via Accept: text/markdown content negotiation (see
      // apps/docs/worker/index.ts) and adds Vary: Accept everywhere.
      assets: {
        runWorkerFirst: true,
      },
      command: "bun run build",
      cwd: "../../apps/docs",
      dev: {
        command: "bun run dev",
        url: "http://localhost:4322",
      },
      main: "../../apps/docs/worker/index.ts",
      name: "reptar-docs",
      outdir: "../../apps/docs/dist",
    });

    const webWorker = yield* Cloudflare.Website.Astro("web", {
      dev: {
        port: 4321,
      },
      env: {
        API: api,
        IMAGES: Cloudflare.Images.Images(),
        // eslint-disable-next-line unicorn/no-array-method-this-argument
        PUBLIC_API_URL: Output.map(api.url, (url) => url ?? ""),
        SESSION: Cloudflare.KV.Namespace("session"),
      },
      name: "reptar-web",
      rootDir: "../../apps/web",
    });

    return {
      api: api.url,
      db: db.databaseName,
      docs: docsWorker.url,
      realworld: realworldWorker.url,
      rooms: roomsWorker.url,
      web: webWorker.url,
    };
  })
);
