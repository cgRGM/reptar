import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "5ea3e1840b78aa1da4f672de86c7cb467d093bf738b88cb6be5c37978be5e70d",
  manifestSha256: "d45c06dc411d335e3a217eb9aaeb1c6ec9f54cc99c66ee3e1e4cba4685e3adaf",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
