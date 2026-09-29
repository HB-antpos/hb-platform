import assert from "node:assert/strict";
import type { StoreSupplyStatus } from "@/modules/supply-notice/types";
import { describeSupplyStatus, mapScanFeedbackToNotice } from "./order-notice";

// 用可读的键名代替真实翻译，直接断言拼装结果。
const t = (key: string, options?: Record<string, unknown>) =>
  options ? `${key}${JSON.stringify(options)}` : key;

const pausedStatus: StoreSupplyStatus = {
  productCode: "P1",
  itemNumber: "HB-31802",
  barcode: "9300675012345",
  productName: "Storage Box",
  productImage: null,
  isOrderable: false,
  supplyPlan: "WillRestock",
  hasNotice: true,
  expectedFrom: "2026-10-01",
  expectedTo: null,
  expectedPrecision: "Month",
  isOverdue: false,
  storeFacingNote: null,
  noticeUpdatedAtUtc: null,
  isWatching: false,
};

for (const status of ["ready", "scanning", "found", "multiple"] as const) {
  assert.equal(mapScanFeedbackToNotice({ status, message: "x" }, t), null, `${status} 不在操作栏闪现`);
}

const added = mapScanFeedbackToNotice(
  { status: "added", message: "扫码枪扫码已加入购物车", productName: "Kitchen Towel", addedQuantity: 6 },
  t,
);
assert.equal(added?.tone, "success", "加购成功为成功色");
assert.equal(added?.title, 'common:orderNotice.addedNamed{"name":"Kitchen Towel"}', "加购提示带商品名");
assert.equal(added?.detail, 'common:orderNotice.addedQuantity{"quantity":6}', "加购提示带数量");

const notFound = mapScanFeedbackToNotice({ status: "not_found", message: "未找到对应商品", barcode: "123" }, t);
assert.deepEqual(notFound, { tone: "warning", title: "未找到对应商品", detail: "123" }, "未找到为琥珀色并带条码");

const paused = mapScanFeedbackToNotice(
  {
    status: "supply_paused",
    message: "该商品暂停供货，查看恢复计划",
    barcode: "9300675012345",
    productName: "Storage Box",
    itemNumber: "HB-31802",
    supplyStatus: pausedStatus,
  },
  t,
);
assert.equal(paused?.tone, "paused", "暂停供货与未找到使用不同色调");
assert.equal(paused?.title, 'supplyNotice:scanPausedNamed{"name":"Storage Box"}', "暂停供货提示带商品名");
assert.match(paused?.detail ?? "", /supplyNotice:storeTitle\.WillRestock/, "暂停供货提示带后续计划");
assert.match(paused?.detail ?? "", /supplyNotice:expectedLabel/, "暂停供货提示带预计恢复时间");

const pausedWithoutStatus = mapScanFeedbackToNotice(
  { status: "supply_paused", message: "x", barcode: "9300675012345", itemNumber: "HB-31802" },
  t,
);
assert.equal(pausedWithoutStatus?.detail, "HB-31802", "缺少供货说明时退回显示货号");

assert.equal(
  describeSupplyStatus({ ...pausedStatus, supplyPlan: "Discontinued" }, t),
  "supplyNotice:storeTitle.Discontinued",
  "不再供应时只给计划，不给预计时间",
);

console.log("order-notice.test.ts: ok");
