import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "1f92c406261804c8e4206b8741c75dab8a8964376bb7ed5fdae906fe6cb37d30",
  manifestSha256: "ff246ef92e0c6a6b66968f58a89a8c44c826925937f40035163a6fbaad33d0b9",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
