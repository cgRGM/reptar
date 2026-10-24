import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "ea66158de1247690cd68c11b3ba557cf910764062c2cf4f9587e75c137336558",
  manifestSha256: "8156276ad9f29096d77d639f13f10323941eff5759933abae7eadc083868a6d7",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
