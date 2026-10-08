import {
  buildSeasonalCardBatchPayload,
  buildSeasonalCardComboKey,
  buildSeasonalCardComparison,
  createSeasonalCardDraft,
  formatQuantityDiff,
  getSeasonalCardOptionsForType,
  getSeasonalCardPriceDisplayLabel,
  isSeasonalCardDraftEdited,
  parsePriceInput,
  parseQuantityInput,
  rebaseSeasonalCardDraft,
  resolveSeasonalCardSubmitState,
  sanitizePriceInput,
  sanitizeQuantityInput,
  stepQuantityInput,
  summarizeSeasonalCardDraft,
} from "./submit-draft";
import {
  getSeasonalCardErrorCode,
  getSeasonalCardSubmitErrorKey,
  SEASONAL_CARD_STALE,
} from "./submit-errors";
import type { SeasonalCardBatch, SeasonalCardCatalogItem } from "./types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

function catalogItem(
  catalogGuid: string,
  cardType: 1 | 2,
  priceOption: 1 | 2 | 3 | 4,
  sortOrder: number
): SeasonalCardCatalogItem {
  const isOther = priceOption === 4;
  return {
    catalogGuid,
    cardType,
    cardTypeName: "圣诞节",
    priceOption,
    priceOptionName: isOther ? "其他" : `$${priceOption}`,
    priceLabel: isOther ? "其他" : `$${priceOption}`,
    fixedUnitPrice: isOther ? null : priceOption,
    allowsCustomUnitPrice: isOther,
    isEnabled: true,
    sortOrder,
  };
}

const catalog: SeasonalCardCatalogItem[] = [
  catalogItem("xmas-other", 1, 4, 4),
  catalogItem("xmas-1", 1, 1, 1),
  catalogItem("xmas-3", 1, 3, 3),
  catalogItem("xmas-2", 1, 2, 2),
  catalogItem("val-1", 2, 1, 1),
  { ...catalogItem("xmas-disabled", 1, 1, 0), isEnabled: false },
];

const options = getSeasonalCardOptionsForType(catalog, 1);
assertDeepEqual(
  options.map((item) => item.catalogGuid),
  ["xmas-1", "xmas-2", "xmas-3", "xmas-other"],
  "options are the enabled catalog items of the holiday, ordered by sortOrder"
);

const combo = {
  storeCode: "1013",
  seasonYear: 2026,
  cardType: 1 as const,
  localSupplierCode: "SUP-A01",
};
const comboKey = buildSeasonalCardComboKey(combo);
assertEqual(comboKey, "1013|2026|1|SUP-A01", "combo key joins store, year, holiday and supplier");

function batch(
  batchGuid: string,
  quantities: [number, number, number, number],
  otherUnitPrice = 0,
  remark = ""
): SeasonalCardBatch {
  const prices = [1, 2, 3, otherUnitPrice];
  return {
    batchGuid,
    storeCode: "1013",
    storeName: "Store 1013",
    seasonYear: 2026,
    cardType: 1,
    cardTypeName: "圣诞节",
    localSupplierCode: "SUP-A01",
    supplierName: "示例供应商 A",
    remark,
    submittedByName: "店长 王",
    submittedAt: "2026-10-02T09:10:00Z",
    totalQuantity: quantities.reduce((sum, value) => sum + value, 0),
    totalAmount: 0,
    isCurrent: true,
    lines: (["xmas-1", "xmas-2", "xmas-3", "xmas-other"] as const).map((catalogGuid, index) => ({
      submissionGuid: `${batchGuid}-${index}`,
      catalogGuid,
      priceOption: (index + 1) as 1 | 2 | 3 | 4,
      priceLabel: index === 3 ? "其他" : `$${index + 1}`,
      unitPrice: prices[index],
      remainingQuantity: quantities[index],
    })),
  };
}

// ---- 输入清洗 ----
assertEqual(sanitizeQuantityInput("01a2-3"), "123", "quantity input keeps digits and drops leading zeros");
assertEqual(sanitizeQuantityInput("0"), "0", "a single zero stays");
assertEqual(sanitizeQuantityInput("12345678"), "123456", "quantity input is capped at 6 digits");
assertEqual(sanitizePriceInput("4.567"), "4.56", "price input keeps two decimals");
assertEqual(sanitizePriceInput("4..5x"), "4.5", "price input keeps one decimal point");
assertEqual(parseQuantityInput(""), 0, "empty quantity counts as 0");
assertEqual(parseQuantityInput("1.5"), null, "non-integer quantity is invalid");
assertEqual(parsePriceInput("0"), null, "zero price is not a valid custom price");
assertEqual(parsePriceInput("4.505"), 4.51, "custom price rounds to cents");
assertEqual(stepQuantityInput("", 1), "1", "plus from empty gives 1");
assertEqual(stepQuantityInput("1", -1), "", "minus to zero shows empty (placeholder 0)");
assertEqual(stepQuantityInput("", -1), "", "minus never goes below zero");

// ---- 没填过：空草稿，直接提交 ----
const freshDraft = createSeasonalCardDraft(comboKey, options, null);
assertDeepEqual(
  freshDraft.quantities,
  { "xmas-1": "", "xmas-2": "", "xmas-3": "", "xmas-other": "" },
  "fresh draft starts empty (counted as 0)"
);
const freshSummary = summarizeSeasonalCardDraft(freshDraft, options);
assertEqual(freshSummary.changedCount, 0, "fresh draft has nothing to compare against");
assertEqual(freshSummary.previousTotalQuantity, null, "fresh draft has no previous total");
assertEqual(
  resolveSeasonalCardSubmitState({ ready: true, hasBaseline: false, changedCount: 0 }),
  "submit",
  "never-submitted combo submits directly, even with all zeros"
);
assertEqual(
  resolveSeasonalCardSubmitState({ ready: false, hasBaseline: false, changedCount: 0 }),
  "disabled",
  "without supplier / data the button is disabled"
);

const typedDraft = {
  ...freshDraft,
  quantities: { ...freshDraft.quantities, "xmas-1": "10", "xmas-2": "5", "xmas-other": "2" },
  customUnitPrice: "",
};
const typedSummary = summarizeSeasonalCardDraft(typedDraft, options);
assertEqual(typedSummary.totalQuantity, 17, "total quantity sums every price");
assertEqual(typedSummary.customPriceRequired, true, "other price with quantity needs a unit price");
const pricedSummary = summarizeSeasonalCardDraft(
  { ...typedDraft, customUnitPrice: "4.5" },
  options
);
assertEqual(pricedSummary.customPriceRequired, false, "unit price satisfies the other price");
assertEqual(pricedSummary.totalAmount, 29, "amount = 10×$1 + 5×$2 + 2×$4.50");

assertDeepEqual(
  buildSeasonalCardBatchPayload(combo, { ...typedDraft, customUnitPrice: "4.5", remark: "  破损  " }, options),
  {
    storeCode: "1013",
    seasonYear: 2026,
    cardType: 1,
    localSupplierCode: "SUP-A01",
    expectedPreviousBatchGuid: null,
    remark: "破损",
    items: [
      { catalogGuid: "xmas-1", remainingQuantity: 10 },
      { catalogGuid: "xmas-2", remainingQuantity: 5 },
      { catalogGuid: "xmas-3", remainingQuantity: 0 },
      { catalogGuid: "xmas-other", remainingQuantity: 2, customUnitPrice: 4.5 },
    ],
  },
  "batch payload covers every catalog item; empty counts as 0; only other carries a unit price"
);

assertDeepEqual(
  buildSeasonalCardBatchPayload(combo, freshDraft, options).items.find(
    (item) => item.catalogGuid === "xmas-other"
  ),
  { catalogGuid: "xmas-other", remainingQuantity: 0 },
  "other price with zero quantity and no unit price omits customUnitPrice"
);

// ---- 填过：预填上次数量，未修改禁用 ----
const previous = batch("batch-1", [150, 96, 40, 12], 4.5, "上次备注");
const prefilled = createSeasonalCardDraft(comboKey, options, previous);
assertDeepEqual(
  prefilled.quantities,
  { "xmas-1": "150", "xmas-2": "96", "xmas-3": "40", "xmas-other": "12" },
  "prefilled draft carries last quantities"
);
assertEqual(prefilled.customUnitPrice, "4.50", "prefilled draft carries last other unit price");
assertEqual(prefilled.remark, "上次备注", "prefilled draft carries last remark");
const prefilledSummary = summarizeSeasonalCardDraft(prefilled, options);
assertEqual(prefilledSummary.changedCount, 0, "untouched prefilled draft has no changes");
assertEqual(
  resolveSeasonalCardSubmitState({
    ready: true,
    hasBaseline: true,
    changedCount: prefilledSummary.changedCount,
  }),
  "unchanged",
  "untouched prefilled draft shows the disabled 「未修改」 state"
);
assertEqual(
  buildSeasonalCardBatchPayload(combo, prefilled, options).expectedPreviousBatchGuid,
  "batch-1",
  "payload carries the batch the user saw"
);
assertEqual(
  isSeasonalCardDraftEdited({ ...prefilled, remark: "改了备注" }, options),
  true,
  "remark edits count as user edits"
);
assertEqual(
  summarizeSeasonalCardDraft({ ...prefilled, remark: "改了备注" }, options).changedCount,
  0,
  "remark alone does not enable overwrite (server compares quantities and prices only)"
);

// ---- 修改后：覆盖提交 + 对比 ----
const edited = {
  ...prefilled,
  quantities: { ...prefilled.quantities, "xmas-1": "120", "xmas-2": "80", "xmas-3": "36" },
};
const editedSummary = summarizeSeasonalCardDraft(edited, options);
assertEqual(editedSummary.changedCount, 3, "three prices changed");
assertEqual(
  resolveSeasonalCardSubmitState({
    ready: true,
    hasBaseline: true,
    changedCount: editedSummary.changedCount,
  }),
  "overwrite",
  "edited prefilled draft needs overwrite confirmation"
);
const comparison = buildSeasonalCardComparison(editedSummary);
assertDeepEqual(
  comparison.rows.map((row) => [row.previous, row.current, row.diff]),
  [
    [150, 120, -30],
    [96, 80, -16],
    [40, 36, -4],
    [12, 12, 0],
  ],
  "comparison rows show previous / current / diff per price"
);
assertDeepEqual(
  [comparison.previousTotal, comparison.currentTotal, comparison.diffTotal],
  [298, 248, -50],
  "comparison totals"
);
assertEqual(formatQuantityDiff(-30, "不变"), "−30", "negative diff uses a minus sign");
assertEqual(formatQuantityDiff(5, "不变"), "+5", "positive diff has a plus sign");
assertEqual(formatQuantityDiff(0, "不变"), "不变", "zero diff uses the unchanged label");

const priceOnly = summarizeSeasonalCardDraft({ ...prefilled, customUnitPrice: "5" }, options);
assertEqual(priceOnly.changedCount, 1, "changing only the other unit price is a change");
assertEqual(
  priceOnly.lines.find((line) => line.catalogGuid === "xmas-other")?.priceChanged,
  true,
  "price-only change is flagged on the other row"
);

// ---- 基线变化（抢先提交 / 提交成功后重拉） ----
const newer = batch("batch-2", [100, 90, 30, 0], 0);
const rebasedKeep = rebaseSeasonalCardDraft(edited, newer, options);
assertEqual(rebasedKeep.baseline?.batchGuid, "batch-2", "rebase switches to the newest batch");
assertEqual(rebasedKeep.quantities["xmas-1"], "120", "edited quantities are kept after rebase");
assertEqual(
  summarizeSeasonalCardDraft(rebasedKeep, options).lines[0]?.previousQuantity,
  100,
  "comparison now uses the newest batch"
);
const rebasedFresh = rebaseSeasonalCardDraft(prefilled, newer, options);
assertEqual(rebasedFresh.quantities["xmas-1"], "100", "untouched draft re-prefills from the newest batch");
const rebasedForced = rebaseSeasonalCardDraft(freshDraft, newer, options, true);
assertEqual(rebasedForced.quantities["xmas-1"], "", "preserveEdits keeps the draft as typed");
assertEqual(rebasedForced.baseline?.batchGuid, "batch-2", "preserveEdits still updates the baseline");
assertEqual(
  rebaseSeasonalCardDraft(edited, previous, options),
  edited,
  "same batch keeps the draft object"
);

// ---- 业务错误码 ----
// apiClient 遇到 success=false 抛出的错误：message 可能是 details 字段拼起来的乱串，code 才可靠。
const staleError = Object.assign(new Error("batch-2\n1013\nStore 1013"), {
  apiBusinessError: true,
  code: "SEASONAL_CARD_STALE",
});
assertEqual(getSeasonalCardErrorCode(staleError), SEASONAL_CARD_STALE, "error code is read from error.code");
assertEqual(
  getSeasonalCardSubmitErrorKey(staleError),
  "messages.staleSnackbar",
  "stale error maps to a local message instead of the joined details"
);
assertEqual(
  getSeasonalCardSubmitErrorKey(Object.assign(new Error("x"), { code: "SEASONAL_CARD_NO_CHANGES" })),
  "errors.noChanges",
  "no-changes maps to its message"
);
assertEqual(
  getSeasonalCardSubmitErrorKey(Object.assign(new Error("x"), { code: "BATCH_ITEMS_MISMATCH" })),
  "errors.catalogChanged",
  "catalog mismatch asks for a refresh"
);
assertEqual(
  getSeasonalCardSubmitErrorKey(Object.assign(new Error("x"), { code: "ERR_NETWORK" })),
  null,
  "unknown codes fall back to the generic error handling"
);
assertEqual(getSeasonalCardErrorCode("oops"), null, "non-object errors have no code");

// ---- 价格标签 ----
assertEqual(getSeasonalCardPriceDisplayLabel(4, "其他", "Other"), "Other", "other price uses the localized label");
assertEqual(getSeasonalCardPriceDisplayLabel(2, "$2", "Other"), "$2", "fixed prices use the backend label");
assertEqual(getSeasonalCardPriceDisplayLabel(3, "", "Other"), "$3", "fixed price falls back to the option");
