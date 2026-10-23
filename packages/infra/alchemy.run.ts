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
  Effect.gen(function* reptarStack() {
    const webWorker = yield* Cloudflare.Website.Astro("web", {
      dev: {
        port: 4321,
      },
      env: {
        IMAGES: Cloudflare.Images.Images(),
        SESSION: Cloudflare.KV.Namespace("session"),
      },
      rootDir: "../../apps/web",
    });

    return {
      web: webWorker.url,
    };
  })
);
