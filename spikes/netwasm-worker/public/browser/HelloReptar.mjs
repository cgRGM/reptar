import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "fcec17ea8e9915cc1c53e440a436300ac304d650cbc9d1664f0ebff2d8d16336",
  manifestSha256: "11d9c00234bafc759e8a204f6e206a959e861e28acb555a9126ef2cc533d2aeb",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}
