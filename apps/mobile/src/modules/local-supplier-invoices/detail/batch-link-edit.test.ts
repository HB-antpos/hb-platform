import { buildBatchEditFields, createEmptyBatchEditDraft } from "./batch-edit";
import { mapWithConcurrency, pickBatchLinkCandidates, resolveSingleMatchedProductCode } from "./batch-link";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

// ---- 批量选用：口径搬自 Web batchLinkMatchedProducts.test.ts ----
const eligible = makeItem({ detailGuid: "e1", existingProductCount: 0, barcodeStatus: 2, barcodeMatchCount: 1 });
const noBarcode = makeItem({ detailGuid: "e2", existingProductCount: 0, barcode: "  ", barcodeMatchCount: 1 });
const noMatch = makeItem({ detailGuid: "e3", existingProductCount: 0, barcodeMatchCount: 0 });
const exists = makeItem({ detailGuid: "e4", existingProductCount: 1 });
const createdHere = makeItem({ detailGuid: "e5", existingProductCount: 0, isCreatedByThisInvoice: true, barcodeMatchCount: 1 });
const unselected = makeItem({ detailGuid: "e6", existingProductCount: 0, barcodeMatchCount: 1 });
const { candidates, ineligibleCount } = pickBatchLinkCandidates(
  ["e1", "e2", "e3", "e4", "e5"],
  [eligible, noBarcode, noMatch, exists, createdHere, unselected]
);
assertDeepEqual(candidates.map((row) => row.detailGuid), ["e1"], "只有主档不存在 + 有条码 + 匹配到过商品的勾选行入选");
assertEqual(ineligibleCount, 4, "其余勾选行计入不符合；未勾选的不计");

assertEqual(resolveSingleMatchedProductCode([{ productCode: "P1" }, { productCode: "p1 " }]), "P1", "同一商品多条匹配记录去重后算一个，用第一次的写法");
assertEqual(resolveSingleMatchedProductCode([{ productCode: "P1" }, { productCode: "P2" }]), null, "多个商品交给人工");
assertEqual(resolveSingleMatchedProductCode([]), null, "没有匹配");
assertEqual(resolveSingleMatchedProductCode(undefined), null, "查询无结果");

void (async () => {
  let running = 0;
  let peak = 0;
  const results = await mapWithConcurrency([1, 2, 3, 4, 5, 6], 2, async (n) => {
    running += 1;
    peak = Math.max(peak, running);
    await new Promise((resolve) => setTimeout(resolve, 5));
    running -= 1;
    if (n === 3) throw new Error("boom");
    return n * 10;
  });
  assertEqual(peak, 2, "并发不超过上限");
  assertDeepEqual(results.map((r) => r.status), ["fulfilled", "fulfilled", "rejected", "fulfilled", "fulfilled", "fulfilled"], "单个失败不影响其它且顺序不变");
  assertEqual((results[5] as PromiseFulfilledResult<number>).value, 60, "结果按输入顺序");
  console.log("batch-link async tests passed");
})().catch((error) => {
  console.error(error);
  process.exit(1);
});

// ---- 批量编辑 ----
const empty = createEmptyBatchEditDraft();
assertDeepEqual(buildBatchEditFields(empty), { ok: false, error: "noField" }, "一项都没开 = 提示至少选一项");
assertDeepEqual(
  buildBatchEditFields({ ...empty, updatePurchasePrice: true, purchasePrice: "abc" }),
  { ok: false, error: "invalidPurchasePrice" },
  "进货价不是数字"
);
assertDeepEqual(
  buildBatchEditFields({ ...empty, updateRetailPrice: true, retailPrice: "-1" }),
  { ok: false, error: "invalidRetailPrice" },
  "零售价不能为负"
);
assertDeepEqual(
  buildBatchEditFields({ ...empty, updateDiscountRate: true, discountPercent: "120" }),
  { ok: false, error: "invalidDiscount" },
  "折扣超过 100%"
);
const ok = buildBatchEditFields({
  ...empty,
  updatePurchasePrice: true,
  purchasePrice: " 1.25 ",
  updateIsAutoPricing: true,
  isAutoPricing: false,
  updateDiscountRate: true,
  discountPercent: "12.3",
});
assertDeepEqual(
  ok,
  {
    ok: true,
    fields: {
      updatePurchasePrice: true,
      updateRetailPrice: false,
      updateIsAutoPricing: true,
      updateIsSpecialProduct: false,
      updateDiscountRate: true,
      updateAction: false,
      purchasePrice: 1.25,
      isAutoPricing: false,
      discountRate: 0.123,
    },
  },
  "只带开了的字段；折扣百分数换成小数且没有浮点尾巴"
);

console.log("batch-link-edit tests passed");
