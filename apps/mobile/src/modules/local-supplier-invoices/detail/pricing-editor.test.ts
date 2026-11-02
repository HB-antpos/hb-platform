import {
  buildPricingEditorChanges,
  buildPricingEditorDraft,
  discountRateToDecimal,
  discountRateToPercent,
  formatDiscountRate,
  formatPricingFloatRate,
  getPricingSummary,
  normalizeDiscountRate,
  type PricingEditorDraft,
} from "./pricing-editor";
import { applyInlineEdit } from "./inline-edit";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

// ---- 折扣率换算 ----
assertEqual(discountRateToDecimal(12.5), 0.125, "12.5% → 0.125");
assertEqual(discountRateToDecimal(10), 0.1, "10% → 0.1");
assertEqual(discountRateToDecimal(0), 0, "0% → 0");
assertEqual(discountRateToDecimal(33.3), 0.333, "33.3% → 0.333（无浮点尾巴）");
assertEqual(discountRateToPercent(0.1), 10, "0.1 → 10（无浮点尾巴）");
assertEqual(discountRateToPercent(0.07), 7, "0.07 → 7");
assertEqual(discountRateToPercent(50), 50, "历史脏数据 >1 视为已是百分数");
assertEqual(discountRateToPercent(null), null, "null 原样返回");
assertEqual(discountRateToPercent(undefined), undefined, "undefined 原样返回");
assertEqual(normalizeDiscountRate(25), 0.25, ">1 归一为小数");
assertEqual(normalizeDiscountRate(1), 1, "恰为 1 不归一（100%）");
assertEqual(normalizeDiscountRate(0.3), 0.3, "小数不变");
assertEqual(formatDiscountRate(0.125), "12.5%", "小数展示为 1 位小数百分数");
assertEqual(formatDiscountRate(0.1), "10.0%", "0.1 → 10.0%");
assertEqual(formatDiscountRate(12.5), "12.5%", ">1 视为百分数");
assertEqual(formatDiscountRate(0), "0.0%", "0 → 0.0%");
assertEqual(formatDiscountRate(null), "-", "空值展示 -");
assertEqual(formatDiscountRate(undefined), "-", "undefined 展示 -");
assertEqual(formatPricingFloatRate(2.5), "2.50", "倍率 2 位小数");
assertEqual(formatPricingFloatRate(null), "--", "倍率空值 --");

// ---- 只返回真正改动的字段（搬自 Web workbenchLogic.test.ts） ----
const pricingDetail = makeItem({
  autoPricing: true,
  pricingFloatRate: 2.5,
  newAutoRetailPrice: 4.5,
  isSpecialProduct: false,
  discountRate: null,
});
const unchangedDraft: PricingEditorDraft = {
  autoPricing: true,
  pricingFloatRate: 2.5,
  newAutoRetailPrice: 4.5,
  isSpecialProduct: false,
  discountPercent: null,
};
assertDeepEqual(buildPricingEditorChanges(pricingDetail, unchangedDraft), [], "未改动时不提交任何字段");
assertDeepEqual(
  buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, autoPricing: false, discountPercent: 10 }),
  [
    { field: "autoPricing", value: false },
    { field: "discountRate", value: 10 },
  ],
  "只返回改动字段，折扣率以百分数交给行内编辑换算"
);
assertDeepEqual(
  buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, pricingFloatRate: null, newAutoRetailPrice: null }),
  [],
  "数字留空表示不修改，不能兜底成 0"
);
assertDeepEqual(
  buildPricingEditorChanges({ ...pricingDetail, discountRate: 0.1 }, { ...unchangedDraft, discountPercent: 10 }),
  [],
  "折扣率按百分数比较，0.1 与 10% 视为未改动"
);
assertDeepEqual(
  buildPricingEditorChanges({ ...pricingDetail, discountRate: 0.1 }, { ...unchangedDraft, discountPercent: 0 }),
  [{ field: "discountRate", value: 0 }],
  "折扣率改成 0% 是有效改动"
);
assertDeepEqual(
  buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, pricingFloatRate: 3, newAutoRetailPrice: 5.5, isSpecialProduct: true }),
  [
    { field: "pricingFloatRate", value: 3 },
    { field: "newAutoRetailPrice", value: 5.5 },
    { field: "isSpecialProduct", value: true },
  ],
  "多字段改动按固定顺序返回"
);
assertDeepEqual(
  buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, pricingFloatRate: 2.50004 }),
  [],
  "差值小于 0.0001 视为未改动"
);
assertDeepEqual(
  buildPricingEditorChanges(makeItem({ autoPricing: null, isSpecialProduct: false }), { ...unchangedDraft, autoPricing: false, pricingFloatRate: null, newAutoRetailPrice: null }),
  [],
  "autoPricing 空值等同 false"
);
assertDeepEqual(
  buildPricingEditorChanges(makeItem({ pricingFloatRate: null }), { ...unchangedDraft, pricingFloatRate: 0, newAutoRetailPrice: null }),
  [{ field: "pricingFloatRate", value: 0 }],
  "原值为空、填 0 是改动"
);

// 草稿初始值 → 无改动；改动能被 applyInlineEdit 落到明细（折扣率百分数 → 小数）
const draft = buildPricingEditorDraft(makeItem({ discountRate: 0.125, isSpecialProduct: true, autoPricing: null, pricingFloatRate: null, newAutoRetailPrice: null }));
assertDeepEqual(
  draft,
  { autoPricing: false, pricingFloatRate: null, newAutoRetailPrice: null, isSpecialProduct: true, discountPercent: 12.5 },
  "初始草稿：折扣率显示为百分数，空值为 null"
);
let applied = pricingDetail;
for (const change of buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, discountPercent: 12.5, isSpecialProduct: true })) {
  const result = applyInlineEdit(applied, change.field, change.value);
  if (!result.ok) throw new Error("改动应能落到明细");
  applied = result.item;
}
assertEqual(applied.discountRate, 0.125, "落到明细的折扣率是小数");
assertEqual(applied.isSpecialProduct, true, "落到明细的特殊商品");

// ---- 定价摘要 ----
const auto = getPricingSummary(makeItem({ autoPricing: true, pricingFloatRate: 2.5, newAutoRetailPrice: 4.5, isSpecialProduct: true, discountRate: 0.125, storeIsAutoPricing: true }));
assertDeepEqual(
  auto,
  {
    mode: "auto",
    floatRate: 2.5,
    floatRateText: "2.50",
    isSpecial: true,
    discount: { percent: 12.5, text: "12.5%" },
    newAutoRetailPrice: 4.5,
    storeStatus: "auto",
  },
  "自动定价摘要"
);
assertEqual(getPricingSummary(makeItem({ autoPricing: true, pricingFloatRate: null })).mode, "autoNoRate", "自动但无倍率");
assertEqual(getPricingSummary(makeItem({ autoPricing: false })).mode, "manual", "手动");
assertEqual(getPricingSummary(makeItem({ autoPricing: null })).mode, "unset", "未设置显示 --");
assertEqual(getPricingSummary(makeItem({ autoPricing: false, pricingFloatRate: 2 })).floatRate, null, "非自动模式不带倍率");
assertEqual(getPricingSummary(makeItem({ discountRate: 0 })).discount, null, "折扣率 0 视为无折扣");
assertEqual(getPricingSummary(makeItem({ discountRate: null })).discount, null, "折扣率空视为无折扣");
assertEqual(getPricingSummary(makeItem({ isSpecialProduct: false })).isSpecial, false, "非特殊");
assertEqual(getPricingSummary(makeItem({ newAutoRetailPrice: null })).newAutoRetailPrice, null, "无新价");
assertEqual(getPricingSummary(makeItem({ storeIsAutoPricing: false })).storeStatus, "manual", "本店手动");
assertEqual(getPricingSummary(makeItem({ storeIsAutoPricing: null })).storeStatus, "none", "本店未建");
assertEqual(getPricingSummary(makeItem({ productCode: "", storeIsAutoPricing: true })).storeStatus, "hidden", "没匹配商品时不显示本店状态");
