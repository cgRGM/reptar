import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "0c022ba77f52b4b6d56917d02ef70e23d370c072a72c39533ed5a15940f10207",
  manifestSha256: "e371bf6c007e969e1f0ae36d0a0ef2425b9be3123cc03f95dd343d7ff8bf758a",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
