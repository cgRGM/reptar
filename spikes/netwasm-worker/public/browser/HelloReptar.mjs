import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "3874f77bba38924635416f9dedf34f2dd2e81daed746ca2b80b7f7cfac3376e4",
  manifestSha256: "63167d75340a3a6f9914767b97558e9b632e22eccfdf5670756f36ee3c1148f2",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
