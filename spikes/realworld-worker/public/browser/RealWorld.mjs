import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "dd28cef0c054713b8d0a5d500ab9df0a8adf0e0a6c54430ab0a15b86ad303833",
  manifestSha256: "ad671eac9b255d5e84b69b1b8c193641d103446073716492af9ab1bd782655cd",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
