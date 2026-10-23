import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "cf3656af3c6fd2e16f0e9c00fd891f62b93be1c79781a096b6f194d3bf12c88a",
  manifestSha256: "60bfc0fcb44260034741ad8b0fe39fda414d90e41e19a9861b86d392aab99a8d",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
