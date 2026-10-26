import * as Alchemy from "alchemy";
import * as Cloudflare from "alchemy/Cloudflare";
import * as Output from "alchemy/Output";
import * as Config from "effect/Config";
import * as Effect from "effect/Effect";
import * as Redacted from "effect/Redacted";
import "varlock/auto-load";

const zoneName = "rocktownlabs.app";
const corsOrigins =
  "https://reptar.rocktownlabs.app,https://reptar-web.rocktown-labs.workers.dev,http://localhost:4321";

export default Alchemy.Stack(
  "reptar",
  {
    providers: Cloudflare.providers(),
    state: Cloudflare.state(),
  },
  Effect.gen(function* reptarStack() {
    const jwtSecret = yield* Config.Redacted("REALWORLD_JWT_SECRET");
    const signingKey = Redacted.value(jwtSecret);
    if (
      !signingKey ||
      signingKey.length < 32 ||
      signingKey === "reptar-realworld-production-secret"
    ) {
      return yield* Effect.die(
        new Error(
          "Set REALWORLD_JWT_SECRET to a private, random value of at least 32 characters before planning or deploying. No production fallback is allowed."
        )
      );
    }
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
      domain: { name: "reptar-api.rocktownlabs.app", zoneName },
      env: {
        AI: Cloudflare.Workers.AI(),
        APP_NAME: "reptar",
        BUCKET: bucket,
        CORS_ORIGINS: corsOrigins,
        COUNTER: Cloudflare.DurableObject("Counter", {
          className: "Counter",
        }),
        DB: db,
        KV: cache,
        QUEUE: events,
        REALWORLD_JWT_SECRET: jwtSecret,
      },
      main: "../../spikes/netwasm-worker/src/index.ts",
      name: "reptar-api",
      workersDev: true,
    });

    const realworldWorker = yield* Cloudflare.Worker("realworld", {
      assets: "../../spikes/realworld-worker/public",
      compatibility: {
        date: "2026-10-22",
      },
      domain: { name: "reptar-realworld-demo.rocktownlabs.app", zoneName },
      env: {
        CORS_ORIGINS: corsOrigins,
        DB: db,
        REALWORLD_JWT_SECRET: jwtSecret,
      },
      main: "../../spikes/realworld-worker/src/index.ts",
      name: "reptar-realworld",
      workersDev: true,
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
      domain: { name: "reptar-rooms-demo.rocktownlabs.app", zoneName },
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
      workersDev: true,
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
      domain: {
        aliases: ["reptar-bench.rocktownlabs.app"],
        name: "reptar-docs.rocktownlabs.app",
        zoneName,
      },
      main: "../../apps/docs/worker/index.ts",
      name: "reptar-docs",
      outdir: "../../apps/docs/dist",
      workersDev: true,
    });

    const webWorker = yield* Cloudflare.Website.Astro("web", {
      dev: {
        port: 4321,
      },
      domain: { name: "reptar.rocktownlabs.app", zoneName },
      env: {
        API: api,
        IMAGES: Cloudflare.Images.Images(),
        // eslint-disable-next-line unicorn/no-array-method-this-argument
        PUBLIC_API_URL: Output.map(api.url, (url) => url ?? ""),
        SESSION: Cloudflare.KV.Namespace("session"),
      },
      name: "reptar-web",
      rootDir: "../../apps/web",
      workersDev: true,
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
