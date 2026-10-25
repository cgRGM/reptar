import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "0e5ac920033791bfb6e4dffb11da607fbe3b9824ae6db8c731feeb2291d1f403",
  manifestSha256: "f49c04b5ad2a6c6f056144b1eb09e1ff150833b8398f3b44ff64088c21890186",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
