import assert from "node:assert/strict";
import {
  buildQuickRange,
  canCopyPromotionToStore,
  canEditPromotionInStore,
  canEndPromotionNow,
  findExclusiveConflicts,
  formatPercent,
  getBundleSavings,
  getDaysRemaining,
  getElapsedRatio,
  getNonDiscountedProducts,
  getPromotionStatus,
  getUnitAveragePrice,
  isScopeVisible,
  parsePromotionTime,
  sortProductPromotions,
  suggestEndBeforeConflicts,
  validatePromotionRule,
} from "./promotion-logic";
import type { PromotionListItem } from "./types";

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

// 不带时区的后端时间按 UTC 解析；带 Z / 偏移的保持原意。
assert.equal(parsePromotionTime("2026-10-20T00:00:00")?.toISOString(), "2026-10-20T00:00:00.000Z");
assert.equal(parsePromotionTime("2026-10-20T00:00:00Z")?.toISOString(), "2026-10-20T00:00:00.000Z");
assert.equal(parsePromotionTime("2026-10-20T11:00:00+11:00")?.toISOString(), "2026-10-20T00:00:00.000Z");
assert.equal(parsePromotionTime(""), null);
assert.equal(parsePromotionTime("not-a-date"), null);

// 状态四段互斥，口径与后端一致：已结束优先于停用。
assert.equal(getPromotionStatus(promotion(), NOW), "active");
assert.equal(getPromotionStatus(promotion({ effectiveStart: "2026-11-01T00:00:00" }), NOW), "upcoming");
assert.equal(getPromotionStatus(promotion({ isEnabled: false }), NOW), "disabled");
assert.equal(getPromotionStatus(promotion({ effectiveEnd: "2026-10-25T00:00:00" }), NOW), "ended");
assert.equal(
  getPromotionStatus(promotion({ isEnabled: false, effectiveEnd: "2026-10-25T00:00:00" }), NOW),
  "ended",
  "已结束且停用的算已结束，不重复出现在已停用",
);

assert.equal(getDaysRemaining("2026-10-31T23:59:59", NOW), 6);
assert.equal(getDaysRemaining("2026-10-25T00:00:00", NOW), 0);
assert.equal(getDaysRemaining(undefined, NOW), null);
const ratio = getElapsedRatio(promotion({ effectiveStart: "2026-10-20T05:00:00", effectiveEnd: "2026-11-01T05:00:00" }), NOW);
assert.equal(ratio, 0.5);
assert.equal(getElapsedRatio(promotion({ effectiveStart: "2026-11-01T00:00:00" }), NOW), 0);

// 均价与优惠力度：设计稿样例 2 件 $25，零售 $14.99。
assert.equal(getUnitAveragePrice(2, 25), 12.5);
assert.equal(getUnitAveragePrice(3, 10), 3.33);
assert.equal(getUnitAveragePrice(0, 10), null);
const savings = getBundleSavings(14.99, 2, 25)!;
assert.equal(savings.unitPrice, 12.5);
assert.equal(savings.savingPerUnit, 2.49);
assert.equal(savings.savingPerBundle, 4.98);
assert.equal(formatPercent(savings.discountRate), "16.6%");
assert.equal(savings.isDiscount, true);
// 刚好等于原价不算优惠（分整数比较，不受 14.99×2 浮点误差影响）。
assert.equal(getBundleSavings(14.99, 2, 29.98)!.isDiscount, false);
assert.equal(getBundleSavings(11.99, 2, 25)!.isDiscount, false);
assert.equal(getBundleSavings(null, 2, 25), null);

// 规则校验：单品不优惠禁止保存，混搭只提醒。
assert.deepEqual(validatePromotionRule({ applyQuantity: 2, fixedPrice: 25, productCount: 1, singleProductRetailPrice: 14.99 }), []);
assert.deepEqual(
  validatePromotionRule({ applyQuantity: 2, fixedPrice: 30, productCount: 1, singleProductRetailPrice: 14.99 }),
  ["notDiscount"],
);
assert.deepEqual(
  validatePromotionRule({ applyQuantity: 2, fixedPrice: 30, productCount: 4, singleProductRetailPrice: 14.99 }),
  [],
  "混搭促销不因个别商品不优惠而拦截",
);
assert.deepEqual(validatePromotionRule({ applyQuantity: 1, fixedPrice: 0, productCount: 1 }), ["quantityTooSmall", "priceRequired"]);
assert.deepEqual(
  validatePromotionRule({ applyQuantity: 2, fixedPrice: 30, productCount: 1, singleProductRetailPrice: null }),
  [],
  "零售价未知时不拦",
);

const nonDiscounted = getNonDiscountedProducts(
  [
    { productCode: "A", retailPrice: 14.99 },
    { productCode: "B", retailPrice: 11.99 },
    { productCode: "C", retailPrice: null },
  ],
  2,
  25,
);
assert.deepEqual(nonDiscounted.map((item) => item.productCode), ["B"]);

// 冲突预检：时间重叠 + 任一方排他 + 对方启用。
const exclusiveLater = promotion({
  id: "hq",
  scopeType: "MultiStore",
  isExclusive: true,
  effectiveStart: "2026-11-01T00:00:00",
  effectiveEnd: "2026-11-15T23:59:59",
});
const candidate = {
  effectiveStart: "2026-10-26T00:00:00Z",
  effectiveEnd: "2026-11-30T23:59:59Z",
  isEnabled: true,
  isExclusive: false,
};
assert.deepEqual(findExclusiveConflicts(candidate, [exclusiveLater, promotion({ id: "plain" })]).map((item) => item.id), ["hq"]);
assert.deepEqual(findExclusiveConflicts({ ...candidate, isEnabled: false }, [exclusiveLater]), [], "本条停用不提醒");
assert.deepEqual(findExclusiveConflicts(candidate, [{ ...exclusiveLater, isEnabled: false }]), [], "对方停用不提醒");
assert.deepEqual(
  findExclusiveConflicts({ ...candidate, effectiveEnd: "2026-10-31T23:59:59Z" }, [exclusiveLater]),
  [],
  "不重叠不提醒",
);
assert.deepEqual(
  findExclusiveConflicts({ ...candidate, id: "hq" }, [exclusiveLater]),
  [],
  "编辑时不和自己比",
);
assert.equal(
  suggestEndBeforeConflicts(candidate.effectiveStart, [exclusiveLater]),
  "2026-10-31T23:59:00.000Z",
);
assert.equal(
  suggestEndBeforeConflicts("2026-11-02T00:00:00Z", [exclusiveLater]),
  null,
  "整段都被覆盖时不给建议",
);

// 商品查询页排序：生效中 → 未开始 → 已停用，同组先结束的在前。
const sorted = sortProductPromotions(
  [
    promotion({ id: "disabled", isEnabled: false }),
    promotion({ id: "upcoming", effectiveStart: "2026-11-01T00:00:00" }),
    promotion({ id: "active-late", effectiveEnd: "2026-12-31T00:00:00" }),
    promotion({ id: "active-soon", effectiveEnd: "2026-10-28T00:00:00" }),
  ],
  NOW,
);
assert.deepEqual(sorted.map((item) => item.id), ["active-soon", "active-late", "upcoming", "disabled"]);

// 权限边界：只有本店独享可改；多店只能复制；未绑定分店不显示。
assert.equal(canEditPromotionInStore(promotion()), true);
assert.equal(canEditPromotionInStore(promotion({ scopeType: "MultiStore", canEditInStoreScope: false })), false);
assert.equal(canCopyPromotionToStore(promotion({ scopeType: "MultiStore", canCopyToStore: true })), true);
assert.equal(canCopyPromotionToStore(promotion({ scopeType: "Headquarters", canCopyToStore: true })), false);
assert.equal(isScopeVisible("Headquarters"), false);
assert.equal(isScopeVisible(null), false);
assert.equal(isScopeVisible("MultiStore"), true);
assert.equal(canEndPromotionNow(promotion(), NOW), true);
assert.equal(canEndPromotionNow(promotion({ effectiveStart: "2026-11-01T00:00:00" }), NOW), false, "未开始不提供提前结束");
assert.equal(canEndPromotionNow(promotion({ scopeType: "MultiStore", canEditInStoreScope: false }), NOW), false);

// 快捷有效期：本地整天，结束为第 N 天 23:59:59。
const localNow = new Date(2026, 9, 26, 14, 30);
const seven = buildQuickRange("sevenDays", localNow);
assert.equal(new Date(seven.effectiveStart).getTime(), new Date(2026, 9, 26, 0, 0, 0).getTime());
assert.equal(new Date(seven.effectiveEnd).getTime(), new Date(2026, 10, 1, 23, 59, 59).getTime());
const monthEnd = buildQuickRange("monthEnd", localNow);
assert.equal(new Date(monthEnd.effectiveEnd).getTime(), new Date(2026, 9, 31, 23, 59, 59).getTime());
const thirty = buildQuickRange("thirtyDays", localNow);
assert.equal(new Date(thirty.effectiveEnd).getTime(), new Date(2026, 10, 24, 23, 59, 59).getTime());

console.log("promotion-logic tests passed");
