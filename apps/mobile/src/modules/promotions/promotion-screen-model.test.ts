import assert from "node:assert/strict";
import {
  addDraftProduct,
  buildCopyName,
  buildFormValuesFromDraft,
  combineLocalDateTime,
  countPromotionsByStatus,
  createEmptyDraft,
  draftFromDetail,
  filterProductPromotions,
  getDraftFingerprint,
  getDraftNonDiscountedProducts,
  getListSortModel,
  getPromotionErrorKind,
  getRemainingKind,
  getSegmentCount,
  parseMoneyInput,
  parsePositiveIntInput,
  parseTimeInput,
  runWithConcurrency,
  splitLocalDateTime,
  stepApplyQuantity,
  stepUnitWeight,
  validateDraft,
  type PromotionDraft,
} from "./promotion-screen-model";
import type { PromotionListItem } from "./types";

const NOW = new Date("2026-10-26T05:00:00Z");

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

function draft(overrides: Partial<PromotionDraft> = {}): PromotionDraft {
  return {
    ...createEmptyDraft(NOW),
    name: "Halloween",
    fixedPriceText: "25",
    products: [
      { productCode: "A", unitWeight: 1, retailPrice: 14.99, lookupState: "ready" },
      { productCode: "B", unitWeight: 1, retailPrice: 11.99, lookupState: "ready" },
    ],
    ...overrides,
  };
}

// 排序口径：生效中 / 未开始按结束升序，已结束按结束降序，其余走后端默认。
assert.deepEqual(getListSortModel("active"), [{ colId: "effectiveEnd", sort: "asc" }]);
assert.deepEqual(getListSortModel("upcoming"), [{ colId: "effectiveEnd", sort: "asc" }]);
assert.deepEqual(getListSortModel("ended"), [{ colId: "effectiveEnd", sort: "desc" }]);
assert.equal(getListSortModel("disabled"), undefined);
assert.equal(getListSortModel("all"), undefined);

// 商品模式：过滤未绑定分店、按分段过滤、按结束时间排序，并在前端计数。
{
  const items = [
    promotion({ id: "late", effectiveEnd: "2026-12-31T00:00:00" }),
    promotion({ id: "soon", effectiveEnd: "2026-10-28T00:00:00" }),
    promotion({ id: "hq", scopeType: "Headquarters" }),
    promotion({ id: "off", isEnabled: false }),
    promotion({ id: "future", effectiveStart: "2026-11-01T00:00:00", effectiveEnd: "2026-11-30T00:00:00" }),
  ];
  assert.deepEqual(
    filterProductPromotions(items, "active", NOW).map((item) => item.id),
    ["soon", "late"],
  );
  assert.deepEqual(filterProductPromotions(items, "disabled", NOW).map((item) => item.id), ["off"]);
  assert.equal(filterProductPromotions(items, "all", NOW).length, 4);
  const counts = countPromotionsByStatus(items, NOW);
  assert.deepEqual(counts, { active: 2, upcoming: 1, disabled: 1, ended: 0, total: 4 });
  assert.equal(getSegmentCount(counts, "all"), 4);
  assert.equal(getSegmentCount(null, "active"), null);
}

// 错误归类：errorCode 在 code 上，启停接口把 exclusive_conflict 放在 message 里。
assert.equal(getPromotionErrorKind(Object.assign(new Error("排他促销优先级冲突"), { code: "exclusive_conflict" })), "exclusiveConflict");
assert.equal(getPromotionErrorKind(new Error("exclusive_conflict")), "exclusiveConflict");
assert.equal(getPromotionErrorKind(Object.assign(new Error("无权访问当前分店"), { code: "STORE_FORBIDDEN" })), "storeForbidden");
assert.equal(getPromotionErrorKind(new Error("network")), null);
assert.equal(getPromotionErrorKind(null), null);

// 输入解析
assert.equal(parseMoneyInput("25"), 25);
assert.equal(parseMoneyInput("$12.5"), 12.5);
assert.equal(parseMoneyInput("12."), 12);
assert.equal(parseMoneyInput("12.345"), null);
assert.equal(parseMoneyInput("abc"), null);
assert.equal(parsePositiveIntInput(""), null);
assert.equal(parsePositiveIntInput("3"), 3);
assert.ok(Number.isNaN(parsePositiveIntInput("0") as number));
assert.ok(Number.isNaN(parsePositiveIntInput("1.5") as number));
assert.equal(stepApplyQuantity(2, -1), 2);
assert.equal(stepApplyQuantity(2, 1), 3);
assert.equal(stepApplyQuantity(99, 1), 99);
assert.equal(stepUnitWeight(1, -1), 1);
assert.deepEqual(parseTimeInput("9:05"), { hours: 9, minutes: 5 });
assert.deepEqual(parseTimeInput("0930"), { hours: 9, minutes: 30 });
assert.deepEqual(parseTimeInput("23"), { hours: 23, minutes: 0 });
assert.equal(parseTimeInput("24:00"), null);
assert.equal(parseTimeInput("ab"), null);

// 本地日期时间互转：结束时间带 59 秒。
{
  const iso = combineLocalDateTime("2026-10-31", { hours: 23, minutes: 59 }, true)!;
  const local = new Date(iso);
  assert.equal(local.getSeconds(), 59);
  assert.deepEqual(splitLocalDateTime(iso), { date: "2026-10-31", time: "23:59" });
  assert.equal(combineLocalDateTime("bad", { hours: 0, minutes: 0 }, false), null);
}

// 新建草稿：本店独享默认值，买满数量 2，优先级 0。
{
  const empty = createEmptyDraft(NOW);
  assert.equal(empty.applyQuantity, 2);
  assert.equal(empty.priority, 0);
  assert.equal(empty.isEnabled, true);
  assert.deepEqual(validateDraft(empty).sort(), ["nameRequired", "priceRequired", "productsRequired"].sort());
}

// 加商品：去重，新行在最前面。
{
  const first = addDraftProduct([], { productCode: " A " });
  assert.equal(first.products[0].productCode, "A");
  assert.equal(first.products[0].unitWeight, 1);
  assert.equal(first.products[0].lookupState, "idle");
  const second = addDraftProduct(first.products, { productCode: "B", productName: "Bee" });
  assert.deepEqual(second.products.map((row) => row.productCode), ["B", "A"]);
  assert.equal(second.products[0].lookupState, "ready");
  const dup = addDraftProduct(second.products, { productCode: "A" });
  assert.equal(dup.duplicated, true);
  assert.equal(dup.products.length, 2);
}

// 校验口径：混搭里个别不优惠只提醒，不阻断；单商品不优惠阻断。
{
  const mixed = draft();
  assert.deepEqual(validateDraft(mixed), []);
  assert.deepEqual(getDraftNonDiscountedProducts(mixed).map((row) => row.productCode), ["B"]);

  const single = draft({ products: [{ productCode: "B", unitWeight: 1, retailPrice: 11.99, lookupState: "ready" }] });
  assert.ok(validateDraft(single).includes("notDiscount"));
  // 单商品时不重复出提醒名单（由阻断提示负责）。
  assert.deepEqual(getDraftNonDiscountedProducts(single), []);

  const badRange = draft({ effectiveStart: "2026-10-31T00:00:00Z", effectiveEnd: "2026-10-30T00:00:00Z" });
  assert.ok(validateDraft(badRange).includes("rangeInvalid"));
  assert.ok(validateDraft(draft({ maxApplicationsText: "0" })).includes("maxApplicationsInvalid"));
  assert.ok(validateDraft(draft({ applyQuantity: 1 })).includes("quantityTooSmall"));
}

// 保存请求：新建优先级固定 0，编辑保持原值；商品按加入顺序提交；空次数为 null。
{
  const created = buildFormValuesFromDraft(draft({ priority: 7 }), "1033");
  assert.equal(created.priority, 0);
  assert.equal(created.storeCode, "1033");
  assert.equal(created.fixedPrice, 25);
  assert.equal(created.maxApplicationsPerOrder, null);
  assert.deepEqual(created.products?.map((row) => row.productCode), ["B", "A"]);

  const edited = buildFormValuesFromDraft(draft({ id: "x", priority: 7, maxApplicationsText: "2" }), "1033");
  assert.equal(edited.priority, 7);
  assert.equal(edited.maxApplicationsPerOrder, 2);
}

// 详情 → 草稿：时间补 UTC 时区、商品保留顺序与权重、改动指纹稳定。
{
  const detail = promotion({
    id: "d1",
    maxApplicationsPerOrder: 3,
    products: [
      { productCode: "A", unitWeight: 1 },
      { productCode: "B", unitWeight: 2 },
    ],
  });
  const fromDetail = draftFromDetail(detail);
  assert.equal(fromDetail.effectiveStart, "2026-10-20T00:00:00.000Z");
  assert.equal(fromDetail.fixedPriceText, "25.00");
  assert.equal(fromDetail.maxApplicationsText, "3");
  assert.deepEqual(fromDetail.products.map((row) => row.productCode), ["B", "A"]);
  assert.equal(fromDetail.products[0].unitWeight, 2);
  assert.equal(getDraftFingerprint(fromDetail), getDraftFingerprint(draftFromDetail(detail)));
  assert.notEqual(getDraftFingerprint(fromDetail), getDraftFingerprint({ ...fromDetail, name: "changed" }));
  // 回写时商品顺序与原详情一致（草稿里新行在前，所以提交时要反转）。
  const values = buildFormValuesFromDraft(fromDetail, "1033");
  assert.deepEqual(values.products?.map((row) => row.productCode), ["A", "B"]);
}

// 复制默认名称
assert.equal(buildCopyName("全场贺卡 2 张价", "Top Ryde"), "全场贺卡 2 张价（Top Ryde）");
assert.equal(buildCopyName("全场贺卡 2 张价（Top Ryde）", "Top Ryde"), "全场贺卡 2 张价（Top Ryde）");
assert.equal(buildCopyName(" A ", ""), "A");

// 剩余时间描述
assert.deepEqual(getRemainingKind(promotion(), NOW), { kind: "daysLeft", days: 6 });
assert.deepEqual(getRemainingKind(promotion({ effectiveEnd: "2099-01-01T00:00:00" }), NOW), { kind: "longTerm" });
assert.deepEqual(
  getRemainingKind(promotion({ effectiveStart: "2026-10-28T05:00:00", effectiveEnd: "2026-11-30T00:00:00" }), NOW),
  { kind: "startsIn", days: 2 },
);
assert.deepEqual(getRemainingKind(promotion({ effectiveEnd: "2026-10-01T00:00:00" }), NOW), { kind: "ended" });

// 并发控制：同时最多 limit 个任务，全部执行完。
void (async () => {
  let running = 0;
  let peak = 0;
  const done: number[] = [];
  await runWithConcurrency([1, 2, 3, 4, 5, 6, 7], 3, async (value) => {
    running += 1;
    peak = Math.max(peak, running);
    await new Promise((resolve) => setTimeout(resolve, 5));
    done.push(value);
    running -= 1;
  });
  assert.equal(peak, 3);
  assert.equal(done.length, 7);
  await runWithConcurrency([], 3, async () => {
    throw new Error("should not run");
  });
  console.log("promotion-screen-model tests passed");
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
