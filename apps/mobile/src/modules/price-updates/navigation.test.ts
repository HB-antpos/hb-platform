import assert from "node:assert/strict";
import { PRICE_UPDATES_PATH, buildPriceUpdatesRoute, isPriceUpdatesPath, parsePriceUpdatesParams } from "./navigation";

assert.equal(PRICE_UPDATES_PATH, "/(shell)/price-updates");

assert.deepEqual(buildPriceUpdatesRoute(), { pathname: "/(shell)/price-updates", params: {} });
assert.deepEqual(buildPriceUpdatesRoute({ storeCode: "1004", overdueOnly: true }), {
  pathname: "/(shell)/price-updates",
  params: { storeCode: "1004", filter: "overdue" },
});
assert.deepEqual(buildPriceUpdatesRoute({ storeCode: null, overdueOnly: false }).params, {}, "空值不写进参数");

// 构造与解析互为逆运算
const route = buildPriceUpdatesRoute({ storeCode: "1004", overdueOnly: true });
assert.deepEqual(parsePriceUpdatesParams(route.params), { storeCode: "1004", overdueOnly: true });
assert.deepEqual(parsePriceUpdatesParams({}), { storeCode: null, overdueOnly: false });
assert.deepEqual(parsePriceUpdatesParams({ storeCode: ["1005", "1006"], filter: ["overdue"] }), {
  storeCode: "1005",
  overdueOnly: true,
}, "expo-router 的参数可能是数组，取第一个");
assert.deepEqual(parsePriceUpdatesParams({ storeCode: "  ", filter: "other" }), { storeCode: null, overdueOnly: false });

assert.equal(isPriceUpdatesPath("/price-updates"), true);
assert.equal(isPriceUpdatesPath("/(shell)/price-updates"), true);
assert.equal(isPriceUpdatesPath("/price-updates/"), true);
assert.equal(isPriceUpdatesPath("/reports"), false);
assert.equal(isPriceUpdatesPath("/price-updates-old"), false);
assert.equal(isPriceUpdatesPath(null), false);

console.log("price-updates/navigation.test.ts: ok");
