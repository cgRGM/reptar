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

    const api = yield* Cloudflare.Worker("api", {
      assets: "../../spikes/netwasm-worker/public",
      compatibility: {
        date: "2026-10-22",
      },
      env: {
        BUCKET: bucket,
        DB: db,
      },
      main: "../../spikes/netwasm-worker/src/index.ts",
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
      rootDir: "../../apps/web",
    });

    return {
      api: api.url,
      db: db.databaseName,
      web: webWorker.url,
    };
  })
);
