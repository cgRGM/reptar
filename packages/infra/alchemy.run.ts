import * as Alchemy from "alchemy";
import * as Cloudflare from "alchemy/Cloudflare";
import * as Effect from "effect/Effect";
import "varlock/auto-load";

export default Alchemy.Stack(
  "reptar",
  {
    providers: Cloudflare.providers(),
    state: Cloudflare.state(),
  },
  Effect.gen(function* () {
    const webWorker = yield* Cloudflare.Website.Astro("web", {
      rootDir: "../../apps/web",
      env: {
        SESSION: Cloudflare.KV.Namespace("session"),
        IMAGES: Cloudflare.Images.Images(),
      },
      dev: {
        port: 4321,
      },
    });

    return {
      web: webWorker.url,
    };
  }),
);
