import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "ccc68d50169b29ead3895e6fdab538a2139ad76fa8b69d5851e48128caa76ebb",
  manifestSha256: "fa4f26dc95d37687f3189f3a450b95e1ed02866a03f18e86ed51d45a5b90577e",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
