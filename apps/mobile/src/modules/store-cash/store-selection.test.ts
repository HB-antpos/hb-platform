import assert from "node:assert/strict";
import { resolveActiveStore } from "./store-selection";

const store = (storeCode: string) => ({ storeCode, storeName: storeCode, timeZoneId: "Australia/Sydney", storeToday: "2026-10-07" });
const stores = [store("A"), store("B"), store("C")];

assert.equal(resolveActiveStore(stores, "B", "C")?.storeCode, "B", "请求的优先");
assert.equal(resolveActiveStore(stores, "X", "C")?.storeCode, "C", "请求的不在可操作列表时用全局已选");
assert.equal(resolveActiveStore(stores, null, "X")?.storeCode, "A", "都不在时取第一项");
assert.equal(resolveActiveStore(stores, undefined, undefined)?.storeCode, "A");
assert.equal(resolveActiveStore([], "A", "B"), null, "没有可操作分店返回 null");

console.log("store-selection.test.ts: ok");
