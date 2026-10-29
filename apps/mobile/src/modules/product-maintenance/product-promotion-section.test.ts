import assert from "node:assert/strict";
import type { PromotionDetail, PromotionListItem } from "../promotions/types";
import {
  addDraftProduct,
  applyCustomDate,
  applyQuickRange,
  buildProductPromotionCards,
  buildQuickPromotionPayload,
  countActiveProductPromotions,
  createQuickPromotionDraft,
  draftFromPromotionDetail,
  evaluateQuickPromotionDraft,
  isPromotionExclusiveConflictError,
  parseMaxPerOrderText,
  parseMoneyText,
  removeDraftProduct,
  resolveProductPromotionAccess,
  sanitizeMoneyInput,
  toConflictCandidate,
  toLocalDateString,
} from "./product-promotion-section";

function promotion(overrides: Partial<PromotionListItem> = {}): PromotionListItem {
  return {
    id: "p1",
    name: "p1",
    effectiveStart: "2026-10-20T00:00:00",
    effectiveEnd: "2026-10-31T23:59:59",
    isEnabled: true,
    isExclusive: false,
    priority: 0,
    applyQuantity: 2,
    fixedPrice: 25,
    productsCount: 1,
    storesCount: 1,
    products: [],
    stores: [],
    scopeType: "StoreOnly",
    canEditInStoreScope: true,
    canCopyToStore: false,
    ...overrides,
  };
}

const NOW = new Date("2026-10-26T05:00:00Z");
const PRODUCT = { productCode: "A", productName: "Skeleton", retailPrice: 14.99, unitWeight: 1 };

// 权限：没有 View 一律隐藏；只有 View 或离线 / 设备模式只读。
assert.equal(resolveProductPromotionAccess({ canView: false, canEdit: true, interactive: true }), "hidden");
assert.equal(resolveProductPromotionAccess({ canView: true, canEdit: false, interactive: true }), "readonly");
assert.equal(resolveProductPromotionAccess({ canView: true, canEdit: true, interactive: false }), "readonly");
assert.equal(resolveProductPromotionAccess({ canView: true, canEdit: true, interactive: true }), "editable");

// 卡片：过滤未绑定分店与已结束，排序生效中在前，操作按权限与范围给出。
const items = [
  promotion({ id: "upcoming", effectiveStart: "2026-11-01T00:00:00", effectiveEnd: "2026-11-15T23:59:59", scopeType: "MultiStore", storesCount: 12, canEditInStoreScope: false, canCopyToStore: true, applyQuantity: 3, fixedPrice: 30 }),
  promotion({ id: "active" }),
  promotion({ id: "hq", scopeType: "Headquarters" }),
  promotion({ id: "legacy", scopeType: null }),
  promotion({ id: "ended", effectiveEnd: "2026-10-25T00:00:00" }),
  promotion({ id: "disabled", isEnabled: false }),
];
const editableCards = buildProductPromotionCards(items, 14.99, "editable", NOW);
assert.deepEqual(editableCards.map((card) => card.item.id), ["active", "upcoming", "disabled"]);
const [activeCard, upcomingCard, disabledCard] = editableCards;
assert.equal(activeCard.scope, "store");
assert.equal(activeCard.canToggle && activeCard.canEdit && activeCard.canEndNow, true);
assert.equal(activeCard.canCopy, false);
assert.equal(activeCard.dayCount, 6, "10/26 05:00Z 到 10/31 23:59Z 向上取整 6 天");
assert.equal(activeCard.savings?.unitPrice, 12.5);
assert.equal(activeCard.savings?.savingPerUnit, 2.49);
assert.equal(upcomingCard.scope, "multi");
assert.equal(upcomingCard.canEdit || upcomingCard.canToggle || upcomingCard.canEndNow, false, "多店只读");
assert.equal(upcomingCard.canCopy, true);
assert.equal(upcomingCard.dayCount, 6, "未开始显示距开始天数");
assert.equal(disabledCard.canEndNow, false, "已停用不提供提前结束");
assert.equal(disabledCard.canToggle, true);

const readonlyCards = buildProductPromotionCards(items, 14.99, "readonly", NOW);
assert.equal(readonlyCards.length, 3);
assert.ok(readonlyCards.every((card) => !card.canToggle && !card.canEdit && !card.canEndNow && !card.canCopy));
assert.deepEqual(buildProductPromotionCards(items, 14.99, "hidden", NOW), []);
assert.equal(buildProductPromotionCards([promotion()], null, "readonly", NOW)[0].savings, null, "零售价未知不算优惠");
assert.equal(
  buildProductPromotionCards([promotion({ effectiveEnd: "2036-01-01T00:00:00" })], 14.99, "readonly", NOW)[0].longTerm,
  true,
);

// 海报入口只数此刻生效中的促销。
assert.equal(countActiveProductPromotions(items, NOW), 1);

// 服务端排他冲突两种返回形态都要识别。
assert.equal(isPromotionExclusiveConflictError(Object.assign(new Error("排他促销优先级冲突"), { code: "exclusive_conflict" })), true);
assert.equal(isPromotionExclusiveConflictError(new Error("exclusive_conflict")), true);
assert.equal(isPromotionExclusiveConflictError(new Error("other")), false);
assert.equal(isPromotionExclusiveConflictError(null), false);

// 金额与次数输入。
assert.equal(sanitizeMoneyInput("$25.999"), "25.99");
assert.equal(sanitizeMoneyInput("1.2.3"), "1.23");
assert.equal(parseMoneyText("25.00"), 25);
assert.equal(parseMoneyText(""), null);
assert.equal(parseMoneyText("."), null);
assert.equal(parseMaxPerOrderText(""), null);
assert.equal(parseMaxPerOrderText("3"), 3);
assert.equal(parseMaxPerOrderText("0"), undefined);
assert.equal(parseMaxPerOrderText("1.5"), undefined);

// 新建草稿默认：2 件、今天起 7 天、可叠加、启用、优先级 0。
const draft = createQuickPromotionDraft(PRODUCT, NOW);
assert.equal(draft.applyQuantity, 2);
assert.equal(draft.rangeKey, "sevenDays");
assert.equal(draft.isExclusive, false);
assert.equal(draft.priority, 0);
assert.equal(draft.products.length, 1);

// 单品：组合价 ≥ 零售价 × 数量 禁止保存。
const priced = { ...draft, fixedPriceText: "25.00" };
const pricedResult = evaluateQuickPromotionDraft(priced, "A", NOW);
assert.equal(pricedResult.canSave, true);
assert.equal(pricedResult.savings?.savingPerBundle, 4.98);
assert.equal(evaluateQuickPromotionDraft({ ...draft, fixedPriceText: "29.98" }, "A", NOW).issues.includes("notDiscount"), true);
assert.equal(evaluateQuickPromotionDraft({ ...draft, fixedPriceText: "" }, "A", NOW).issues.includes("priceRequired"), true);
assert.equal(evaluateQuickPromotionDraft({ ...priced, applyQuantity: 1 }, "A", NOW).issues.includes("quantityTooSmall"), true);

// 加了同组商品变成混搭：不拦，只给出没有优惠的商品名单。
const mixed = addDraftProduct({ ...draft, fixedPriceText: "29.98" }, { productCode: "B", retailPrice: 20, unitWeight: 1 });
const mixedResult = evaluateQuickPromotionDraft(mixed, "A", NOW);
assert.equal(mixedResult.canSave, true);
assert.deepEqual(mixedResult.nonDiscounted.map((item) => item.productCode), ["A"]);
assert.equal(addDraftProduct(mixed, { productCode: "B", unitWeight: 1 }).products.length, 2, "重复商品不重复加");
assert.equal(removeDraftProduct(mixed, "A", "A").products.length, 2, "入口商品不能移除");
assert.equal(removeDraftProduct(mixed, "B", "A").products.length, 1);

// 有效期：快捷项与自定义日期；结束早于开始或已过都不能保存。
const monthEnd = applyQuickRange(priced, "monthEnd", NOW);
assert.equal(monthEnd.rangeKey, "monthEnd");
const customEnd = applyCustomDate(priced, "end", "2026-11-30");
assert.equal(customEnd.rangeKey, "custom");
assert.equal(toLocalDateString(customEnd.effectiveEnd), "2026-11-30");
const customStart = applyCustomDate(priced, "start", "2026-11-03");
assert.equal(toLocalDateString(customStart.effectiveStart), "2026-11-03");
assert.equal(applyCustomDate(priced, "start", "bad"), priced);
assert.equal(
  evaluateQuickPromotionDraft(applyCustomDate(priced, "start", "2026-12-31"), "A", NOW).issues.includes("rangeInvalid"),
  true,
);
assert.equal(
  evaluateQuickPromotionDraft(
    { ...priced, effectiveStart: "2026-10-01T00:00:00.000Z", effectiveEnd: "2026-10-02T00:00:00.000Z" },
    "A",
    NOW,
  ).issues.includes("rangeEnded"),
  true,
);
assert.equal(evaluateQuickPromotionDraft({ ...priced, maxPerOrderText: "0" }, "A", NOW).issues.includes("maxPerOrderInvalid"), true);

// 提交参数：名称留空用默认名；分店固定为当前店；每单不限传 null。
const payload = buildQuickPromotionPayload(priced, { storeCode: "1033", isEnabled: false, defaultName: "Skeleton 2 件价" });
assert.equal(payload.name, "Skeleton 2 件价");
assert.equal(payload.storeCode, "1033");
assert.equal(payload.isEnabled, false);
assert.equal(payload.fixedPrice, 25);
assert.equal(payload.maxApplicationsPerOrder, null);
assert.deepEqual(payload.products, [{ productCode: "A", unitWeight: 1 }]);
assert.equal(buildQuickPromotionPayload({ ...priced, name: " 自定义 " }, { storeCode: "1033", isEnabled: true, defaultName: "x" }).name, "自定义");

// 编辑草稿：沿用原开关 / 排他 / 优先级 / 商品权重，后端无时区时间按 UTC 归一。
const detail: PromotionDetail = promotion({
  id: "d1",
  name: "Halloween",
  isExclusive: true,
  priority: 5,
  maxApplicationsPerOrder: 2,
  products: [
    { productCode: "A", unitWeight: 1 },
    { productCode: "C", unitWeight: 2 },
  ],
});
const editDraft = draftFromPromotionDetail(detail, PRODUCT);
assert.equal(editDraft.id, "d1");
assert.equal(editDraft.fixedPriceText, "25.00");
assert.equal(editDraft.effectiveStart, "2026-10-20T00:00:00.000Z");
assert.equal(editDraft.priority, 5);
assert.equal(editDraft.maxPerOrderText, "2");
assert.equal(editDraft.products[0].retailPrice, 14.99);
assert.equal(editDraft.products[1].unitWeight, 2);
assert.deepEqual(toConflictCandidate(editDraft, true), {
  id: "d1",
  effectiveStart: editDraft.effectiveStart,
  effectiveEnd: editDraft.effectiveEnd,
  isEnabled: true,
  isExclusive: true,
});

console.log("product-promotion-section.test.ts: ok");
