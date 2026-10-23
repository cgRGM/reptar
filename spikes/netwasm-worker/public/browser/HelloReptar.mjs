import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "06cc29304ab1711f61d1db63a646e634d42e58754facfe255602d945e4fc6185",
  manifestSha256: "e7bc450891c388c7c68e61c6c32528331c162f62ea709037b17daeb3d83b023e",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
