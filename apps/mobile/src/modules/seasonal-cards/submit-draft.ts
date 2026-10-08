/**
 * 节日贺卡整组填报的纯逻辑：由总览的当前批次生成预填草稿、比较「上次 → 本次」、算合计、
 * 组装批量提交请求、判定底部按钮三态。不依赖 React / 原生模块，便于用 tsx 直接测试。
 */
import { isSeasonalCardCustomPriceOption } from "@/modules/seasonal-cards/form";
import type {
  SeasonalCardBatch,
  SeasonalCardBatchLine,
  SeasonalCardBatchPayload,
  SeasonalCardCatalogItem,
  SeasonalCardPriceOption,
  SeasonalCardType,
} from "@/modules/seasonal-cards/types";

/** 数量输入最多 6 位，避免误触长按 + 后溢出；剩余张数实际远小于此。 */
const MAX_QUANTITY_DIGITS = 6;
const MAX_QUANTITY = 999999;

export interface SeasonalCardCombo {
  storeCode: string;
  seasonYear: number;
  cardType: SeasonalCardType;
  localSupplierCode: string;
}

export interface SeasonalCardDraft {
  /** 分店 + 年份 + 节日 + 供应商；切换任一项都要重新预填。 */
  comboKey: string;
  /** 预填时看到的当前生效批次；没填过为 null。提交时作为 expectedPreviousBatchGuid。 */
  baseline: SeasonalCardBatch | null;
  /** catalogGuid → 数量输入文本；空字符串按 0 记录。 */
  quantities: Record<string, string>;
  /** 「其他」价格的实际单价输入文本。 */
  customUnitPrice: string;
  remark: string;
}

export interface SeasonalCardDraftLine {
  catalogGuid: string;
  priceOption: SeasonalCardPriceOption | null;
  isCustomPrice: boolean;
  /** 本次数量；输入非法时为 null。 */
  quantity: number | null;
  /** 上次数量；没填过（无基线）或基线缺这一项时为 null。 */
  previousQuantity: number | null;
  /** 本次单价：固定价取目录价；「其他」取输入值，未填为 null。 */
  unitPrice: number | null;
  previousUnitPrice: number | null;
  /** 与上次相比是否有变化（只在有基线时可能为 true）。 */
  changed: boolean;
  /** 只有单价变了、数量没变（仅「其他」行可能出现）。 */
  priceChanged: boolean;
  subtotal: number;
}

export interface SeasonalCardDraftSummary {
  lines: SeasonalCardDraftLine[];
  totalQuantity: number;
  totalAmount: number;
  previousTotalQuantity: number | null;
  changedCount: number;
  /** 「其他」有数量但没填大于 0 的实际单价。 */
  customPriceRequired: boolean;
  /** 存在非法数量（理论上输入已过滤，兜底）。 */
  hasInvalidQuantity: boolean;
}

export type SeasonalCardSubmitState =
  /** 条件不全（未选供应商、目录/总览未加载）：禁用。 */
  | "disabled"
  /** 没填过：直接提交。 */
  | "submit"
  /** 填过且有改动：弹覆盖确认。 */
  | "overwrite"
  /** 填过但没改：禁用并显示「未修改」。 */
  | "unchanged";

export function buildSeasonalCardComboKey(combo: SeasonalCardCombo) {
  return [
    combo.storeCode.trim(),
    String(combo.seasonYear),
    String(combo.cardType),
    combo.localSupplierCode.trim(),
  ].join("|");
}

function roundMoney(value: number) {
  return Math.round(value * 100) / 100;
}

/** 只保留数字，去掉前导 0，限制位数。 */
export function sanitizeQuantityInput(text: string) {
  const digits = text.replace(/\D/g, "").slice(0, MAX_QUANTITY_DIGITS);
  const trimmed = digits.replace(/^0+(?=\d)/, "");
  return trimmed;
}

/** 只保留数字和一个小数点，最多两位小数。 */
export function sanitizePriceInput(text: string) {
  const cleaned = text.replace(/[^\d.]/g, "");
  const [integerPart, ...rest] = cleaned.split(".");
  if (rest.length === 0) {
    return integerPart.slice(0, 6);
  }
  return `${integerPart.slice(0, 6)}.${rest.join("").slice(0, 2)}`;
}

/** 空字符串按 0；非整数或负数返回 null。 */
export function parseQuantityInput(text: string): number | null {
  const trimmed = text.trim();
  if (!trimmed) {
    return 0;
  }
  if (!/^\d+$/.test(trimmed)) {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

/** 大于 0 的单价（保留两位小数）；空、0 或非法返回 null。 */
export function parsePriceInput(text: string): number | null {
  const trimmed = text.trim();
  if (!trimmed) {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) && parsed > 0 ? roundMoney(parsed) : null;
}

/** 加减按钮：结果不小于 0；0 显示为空（输入框占位符是 0）。 */
export function stepQuantityInput(text: string, delta: number) {
  const current = parseQuantityInput(text) ?? 0;
  const next = Math.min(MAX_QUANTITY, Math.max(0, current + delta));
  return next === 0 ? "" : String(next);
}

function formatQuantityInput(quantity: number) {
  return quantity > 0 ? String(quantity) : "";
}

function formatPriceInputValue(price: number) {
  return price > 0 ? roundMoney(price).toFixed(2) : "";
}

/** 按 catalogGuid 找基线行；目录重建导致 guid 对不上时退回按价格类型匹配。 */
export function findBaselineLine(
  baseline: SeasonalCardBatch | null,
  option: SeasonalCardCatalogItem
): SeasonalCardBatchLine | null {
  if (!baseline) {
    return null;
  }
  const byGuid = baseline.lines.find(
    (line) => line.catalogGuid.toLowerCase() === option.catalogGuid.toLowerCase()
  );
  if (byGuid) {
    return byGuid;
  }
  return option.priceOption == null
    ? null
    : baseline.lines.find((line) => line.priceOption === option.priceOption) ?? null;
}

/** 由当前生效批次生成预填草稿；没填过则全部为空（按 0 记录）。 */
export function createSeasonalCardDraft(
  comboKey: string,
  options: SeasonalCardCatalogItem[],
  baseline: SeasonalCardBatch | null
): SeasonalCardDraft {
  const quantities: Record<string, string> = {};
  let customUnitPrice = "";
  options.forEach((option) => {
    const line = findBaselineLine(baseline, option);
    quantities[option.catalogGuid] = formatQuantityInput(line?.remainingQuantity ?? 0);
    if (isSeasonalCardCustomPriceOption(option) && line && line.unitPrice > 0) {
      customUnitPrice = formatPriceInputValue(line.unitPrice);
    }
  });

  return {
    comboKey,
    baseline,
    quantities,
    customUnitPrice,
    remark: baseline?.remark ?? "",
  };
}

/**
 * 「其他」行实际会写入的单价，与服务端口径一致：
 * 有数量时用输入的单价；数量为 0 时填了有效单价就照传，否则不传（服务端记 0）。
 */
function resolveCustomEffectiveUnitPrice(quantity: number | null, customPrice: number | null) {
  if (customPrice != null) {
    return customPrice;
  }
  return quantity === 0 ? 0 : null;
}

export function summarizeSeasonalCardDraft(
  draft: SeasonalCardDraft,
  options: SeasonalCardCatalogItem[]
): SeasonalCardDraftSummary {
  const customPrice = parsePriceInput(draft.customUnitPrice);
  const hasBaseline = draft.baseline != null;
  let totalQuantity = 0;
  let totalAmount = 0;
  let previousTotalQuantity = 0;
  let changedCount = 0;
  let customPriceRequired = false;
  let hasInvalidQuantity = false;

  const lines = options.map<SeasonalCardDraftLine>((option) => {
    const isCustomPrice = isSeasonalCardCustomPriceOption(option);
    const quantity = parseQuantityInput(draft.quantities[option.catalogGuid] ?? "");
    const baselineLine = findBaselineLine(draft.baseline, option);
    const previousQuantity = baselineLine ? baselineLine.remainingQuantity : null;
    const previousUnitPrice = baselineLine ? roundMoney(baselineLine.unitPrice) : null;
    const unitPrice = isCustomPrice
      ? resolveCustomEffectiveUnitPrice(quantity, customPrice)
      : option.fixedUnitPrice;

    if (quantity == null) {
      hasInvalidQuantity = true;
    }
    if (isCustomPrice && (quantity ?? 0) > 0 && customPrice == null) {
      customPriceRequired = true;
    }

    const quantityChanged = hasBaseline && quantity !== previousQuantity;
    // 只有「其他」行的单价可能变；固定价行以目录价为准，不参与比较。
    const priceChanged =
      hasBaseline &&
      isCustomPrice &&
      !quantityChanged &&
      baselineLine != null &&
      (unitPrice == null || roundMoney(unitPrice) !== previousUnitPrice);
    const changed = quantityChanged || priceChanged;

    const subtotal = roundMoney((quantity ?? 0) * (unitPrice ?? 0));
    totalQuantity += quantity ?? 0;
    totalAmount += subtotal;
    previousTotalQuantity += previousQuantity ?? 0;
    if (changed) {
      changedCount += 1;
    }

    return {
      catalogGuid: option.catalogGuid,
      priceOption: option.priceOption,
      isCustomPrice,
      quantity,
      previousQuantity,
      unitPrice,
      previousUnitPrice,
      changed,
      priceChanged,
      subtotal,
    };
  });

  return {
    lines,
    totalQuantity,
    totalAmount: roundMoney(totalAmount),
    previousTotalQuantity: hasBaseline ? previousTotalQuantity : null,
    changedCount,
    customPriceRequired,
    hasInvalidQuantity,
  };
}

/** 草稿是否被用户改过（含备注），用于决定基线变化时是否保留输入。 */
export function isSeasonalCardDraftEdited(
  draft: SeasonalCardDraft,
  options: SeasonalCardCatalogItem[]
) {
  const pristine = createSeasonalCardDraft(draft.comboKey, options, draft.baseline);
  const sameQuantities = options.every(
    (option) =>
      (parseQuantityInput(draft.quantities[option.catalogGuid] ?? "") ?? -1) ===
      (parseQuantityInput(pristine.quantities[option.catalogGuid] ?? "") ?? -1)
  );
  return (
    !sameQuantities ||
    parsePriceInput(draft.customUnitPrice) !== parsePriceInput(pristine.customUnitPrice) ||
    draft.remark.trim() !== pristine.remark.trim()
  );
}

/**
 * 服务端的当前批次变了（别人抢先提交、或自己提交成功后重拉）：
 * - 用户改过或明确要求保留（抢先提交被拒）→ 保留输入，只换基线，重新显示「上次 → 本次」；
 * - 没改过 → 直接用新批次重新预填。
 */
export function rebaseSeasonalCardDraft(
  draft: SeasonalCardDraft,
  nextBaseline: SeasonalCardBatch | null,
  options: SeasonalCardCatalogItem[],
  preserveEdits = false
): SeasonalCardDraft {
  const currentGuid = draft.baseline?.batchGuid ?? "";
  const nextGuid = nextBaseline?.batchGuid ?? "";
  if (currentGuid.toLowerCase() === nextGuid.toLowerCase()) {
    return draft.baseline === nextBaseline ? draft : { ...draft, baseline: nextBaseline };
  }
  if (preserveEdits || isSeasonalCardDraftEdited(draft, options)) {
    return { ...draft, baseline: nextBaseline };
  }
  return createSeasonalCardDraft(draft.comboKey, options, nextBaseline);
}

export function resolveSeasonalCardSubmitState(input: {
  ready: boolean;
  hasBaseline: boolean;
  changedCount: number;
}): SeasonalCardSubmitState {
  if (!input.ready) {
    return "disabled";
  }
  if (!input.hasBaseline) {
    return "submit";
  }
  return input.changedCount > 0 ? "overwrite" : "unchanged";
}

/** 组装批量提交请求：必须覆盖该节日全部启用目录项，空着的数量按 0。 */
export function buildSeasonalCardBatchPayload(
  combo: SeasonalCardCombo,
  draft: SeasonalCardDraft,
  options: SeasonalCardCatalogItem[]
): SeasonalCardBatchPayload {
  const customPrice = parsePriceInput(draft.customUnitPrice);
  const remark = draft.remark.trim();
  return {
    storeCode: combo.storeCode.trim(),
    seasonYear: combo.seasonYear,
    cardType: combo.cardType,
    localSupplierCode: combo.localSupplierCode.trim(),
    expectedPreviousBatchGuid: draft.baseline?.batchGuid || null,
    ...(remark ? { remark } : {}),
    items: options.map((option) => {
      const quantity = parseQuantityInput(draft.quantities[option.catalogGuid] ?? "") ?? 0;
      // 固定价不能带单价（服务端会拒绝）；「其他」只在填了有效单价时才传。
      const shouldSendPrice = isSeasonalCardCustomPriceOption(option) && customPrice != null;
      return {
        catalogGuid: option.catalogGuid,
        remainingQuantity: quantity,
        ...(shouldSendPrice ? { customUnitPrice: customPrice } : {}),
      };
    }),
  };
}

export interface SeasonalCardComparisonRow {
  catalogGuid: string;
  previous: number;
  current: number;
  diff: number;
  priceChanged: boolean;
}

/** 覆盖确认弹窗的对比表：每个价格的上次 / 本次 / 变化，以及合计。 */
export function buildSeasonalCardComparison(summary: SeasonalCardDraftSummary) {
  const rows = summary.lines.map<SeasonalCardComparisonRow>((line) => {
    const previous = line.previousQuantity ?? 0;
    const current = line.quantity ?? 0;
    return {
      catalogGuid: line.catalogGuid,
      previous,
      current,
      diff: current - previous,
      priceChanged: line.priceChanged,
    };
  });
  const previousTotal = rows.reduce((sum, row) => sum + row.previous, 0);
  const currentTotal = rows.reduce((sum, row) => sum + row.current, 0);
  return {
    rows,
    previousTotal,
    currentTotal,
    diffTotal: currentTotal - previousTotal,
  };
}

/** 变化量文本：0 用调用方给的「不变」，正数带 +，负数用全角减号与设计稿一致。 */
export function formatQuantityDiff(diff: number, unchangedLabel: string) {
  if (diff === 0) {
    return unchangedLabel;
  }
  return diff > 0 ? `+${diff}` : `−${Math.abs(diff)}`;
}

/** 价格行的显示标签：「其他」用本地化文案（后端标签是中文），固定价优先用后端 $1/$2/$3。 */
export function getSeasonalCardPriceDisplayLabel(
  priceOption: SeasonalCardPriceOption | null,
  priceLabel: string,
  otherLabel: string,
  unitPrice?: number | null
) {
  if (priceOption === 4) {
    return otherLabel;
  }
  const trimmed = priceLabel.trim();
  if (trimmed) {
    return trimmed;
  }
  if (priceOption != null) {
    return `$${priceOption}`;
  }
  return unitPrice != null && unitPrice > 0 ? `$${roundMoney(unitPrice).toFixed(2)}` : otherLabel;
}

/** 选好分店、年份、节日、供应商后，挑出该节日启用的价格目录项（按排序）。 */
export function getSeasonalCardOptionsForType(
  catalog: SeasonalCardCatalogItem[],
  cardType: SeasonalCardType
) {
  return catalog
    .filter((item) => item.isEnabled && item.cardType === cardType)
    .slice()
    .sort((left, right) => {
      const leftOrder = left.sortOrder ?? left.priceOption ?? Number.MAX_SAFE_INTEGER;
      const rightOrder = right.sortOrder ?? right.priceOption ?? Number.MAX_SAFE_INTEGER;
      return leftOrder - rightOrder || left.catalogGuid.localeCompare(right.catalogGuid);
    });
}
