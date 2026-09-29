import assert from "node:assert/strict";
import { getContainerNewProductsErrorCode } from "./errors";

assert.equal(
  getContainerNewProductsErrorCode({
    code: "ERR_BAD_REQUEST",
    response: { status: 400, data: { errorCode: "STORE_STATE_UNKNOWN" } },
  }),
  "STORE_STATE_UNKNOWN",
);
assert.equal(
  getContainerNewProductsErrorCode({ code: "ERR_NETWORK" }),
  "ERR_NETWORK",
);

console.log("container-new-products api tests passed");
