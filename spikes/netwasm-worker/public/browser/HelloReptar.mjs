import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "2b57c37ef7bde6df0ccfef5f02fa16258466cc1dbc7befbc3335874ba13336ff",
  manifestSha256: "c658ac9b0e54f9a219dc3c9fd59ae8078bb59939b4e3401af9fac405806a52b1",
});
const exports = Object.freeze(["createPost","listPosts","uploadImage"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
