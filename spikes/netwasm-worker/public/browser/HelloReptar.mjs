import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "55facdcb0ddb609c3119d71149f9a389622c50629288d905b3f88ad8a1a22c2b",
  manifestSha256: "0e17b65691da993d8afae18d3470174680f686c2c3e66560a5c9dd6e1cd49cd1",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
