import type { StorePriceUpdateTask } from "./types";

export type PriceUpdateTranslate = (key: string, params?: Record<string, unknown>) => string;

/** 后端来源代码 → priceUpdates 命名空间 sources.* 文案键。 */
const INITIATOR_SOURCE_KEYS: Record<string, string> = {
  WarehouseProducts: "sources.warehouseProducts",
  MobileWarehouse: "sources.mobileWarehouse",
  BatchUpdate: "sources.batchUpdate",
  WarehouseAutoSync: "sources.warehouseAutoSync",
  StoreSync: "sources.storeSync",
  LocalSupplierInvoice: "sources.localSupplierInvoice",
  DomesticImport: "sources.domesticImport",
  StoreOrderImportPriceVariance: "sources.storeOrderImportPriceVariance",
};

export function resolveInitiatorSourceLabel(
  source: string | null | undefined,
  reference: string | null | undefined,
  t: PriceUpdateTranslate
): string {
  const code = source?.trim() ?? "";
  if (!code) {
    return "";
  }
  if (code === "StoreSync") {
    const storeCode = reference?.trim();
    // 分店同步要带上来源分店代码，店员才知道是哪家店推过来的价格。
    return storeCode ? t("sources.storeSyncFrom", { store: storeCode }) : t("sources.storeSync");
  }
  if (code.startsWith("DataSync")) {
    return t("sources.dataSync");
  }
  const key = INITIATOR_SOURCE_KEYS[code] ?? resolveSourceFamilyKey(code);
  return t(key);
}

/**
 * 仓库改价的审计来源代码有几十种且会继续增加（货柜、国内采购、发票等各有多个变体）。
 * 按前缀归到业务大类；识别不了的归为「其它入口」，不把英文代码直接展示给店员。
 */
function resolveSourceFamilyKey(code: string): string {
  if (code.startsWith("Container") || code.startsWith("YiwuContainer")) return "sources.container";
  if (code.startsWith("Domestic")) return "sources.domesticProduct";
  if (code === "NonDomesticImport") return "sources.nonDomesticImport";
  if (code.startsWith("LocalSupplierInvoice")) return "sources.localSupplierInvoice";
  if (code.startsWith("StoreOrder")) return "sources.storeOrder";
  if (code.startsWith("ProductLegacy") || code.toLowerCase() === "legacy") return "sources.legacyApi";
  return "sources.other";
}

/** 卡片上的分店名：优先分店名称，缺失时回退分店代码（后端列表接口两者都会带）。 */
export function resolveTaskStoreLabel(task: { storeName: string | null; storeCode: string }): string {
  return task.storeName?.trim() || task.storeCode;
}

export function resolveInitiatorName(name: string | null | undefined, t: PriceUpdateTranslate): string {
  const trimmed = name?.trim() ?? "";
  if (!trimmed || trimmed.toLowerCase() === "system") {
    return t("initiator.system");
  }
  return trimmed;
}

function pad(value: number) {
  return String(value).padStart(2, "0");
}

function parseDate(value: string | null | undefined): Date | null {
  if (!value) {
    return null;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** 本地日历日序号，用于判断今天/昨天；不能直接用毫秒差除 24h，夏令时切换日会差一天。 */
function localDayIndex(date: Date) {
  return Math.round(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / 86_400_000);
}

export type RelativeDayKind = "today" | "yesterday" | "date";

export function getRelativeDayKind(date: Date, now: Date): RelativeDayKind {
  const diff = localDayIndex(now) - localDayIndex(date);
  if (diff === 0) return "today";
  if (diff === 1) return "yesterday";
  return "date";
}

export function formatLocalDate(date: Date, now: Date) {
  const monthDay = `${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  return date.getFullYear() === now.getFullYear() ? monthDay : `${date.getFullYear()}-${monthDay}`;
}

/** 今天 09:42 / 昨天 17:05 / 03-18 09:42（跨年带年份）。按设备本地时区展示。 */
export function formatRelativeTime(
  value: string | null | undefined,
  now: Date,
  t: PriceUpdateTranslate
): string {
  const date = parseDate(value);
  if (!date) {
    return "";
  }
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  const kind = getRelativeDayKind(date, now);
  if (kind === "today") return t("time.today", { time });
  if (kind === "yesterday") return t("time.yesterday", { time });
  return `${formatLocalDate(date, now)} ${time}`;
}

export interface CompletedTaskGroup {
  dayKey: string;
  kind: RelativeDayKind;
  /** kind=date 时的日期文本；today/yesterday 由界面翻译。 */
  dateLabel: string;
  items: StorePriceUpdateTask[];
}

/**
 * 已完成列表按完成日（本地时区）分组。保持后端返回的顺序，只在相邻日期变化处切组，
 * 这样分页追加时不会把后续页的条目插回前面的分组造成列表跳动。
 */
export function groupCompletedTasksByDay(
  tasks: readonly StorePriceUpdateTask[],
  now: Date
): CompletedTaskGroup[] {
  const groups: CompletedTaskGroup[] = [];
  for (const task of tasks) {
    const date = parseDate(task.completedAtUtc) ?? parseDate(task.initiatedAtUtc);
    const dayKey = date ? `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` : "unknown";
    const last = groups[groups.length - 1];
    if (last && last.dayKey === dayKey) {
      last.items.push(task);
      continue;
    }
    groups.push({
      dayKey,
      kind: date ? getRelativeDayKind(date, now) : "date",
      dateLabel: date ? formatLocalDate(date, now) : "",
      items: [task],
    });
  }
  return groups;
}

export function formatMoney(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? "--" : `$${value.toFixed(2)}`;
}

function roundMoney(value: number) {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}

function normalizeRate(rate: number | null | undefined) {
  return rate != null && Number.isFinite(rate) && rate > 0 ? Math.min(rate, 1) : 0;
}

export function applyDiscount(price: number | null | undefined, rate: number | null | undefined): number | null {
  if (price == null || !Number.isFinite(price)) {
    return null;
  }
  return roundMoney(price * (1 - normalizeRate(rate)));
}

/** 「减 20%」/「无折扣」。 */
export function formatDiscountLabel(rate: number | null | undefined, t: PriceUpdateTranslate): string {
  const normalized = normalizeRate(rate);
  if (normalized <= 0) {
    return t("discount.none");
  }
  // 最多保留 1 位小数，避免 0.125 显示成 12.5000000001%。
  const percent = Math.round(normalized * 1000) / 10;
  return t("discount.off", { percent: Number.isInteger(percent) ? String(percent) : percent.toFixed(1) });
}

export type PriceDirection = "up" | "down" | "same";

export interface PriceComparison {
  fromPrice: number | null;
  toPrice: number | null;
  fromDiscountRate: number;
  toDiscountRate: number;
  priceChanged: boolean;
  discountChanged: boolean;
  /** 折后价，仅在折扣变化时展示。 */
  fromFinalPrice: number | null;
  toFinalPrice: number | null;
  /** 涨跌额：折扣变化时按折后价计算，否则按零售价。 */
  delta: number | null;
  direction: PriceDirection;
}

/**
 * PriceUpdate：本店现值 → 仓库目标（目标折扣为 null 表示不比较，沿用本店折扣）。
 * LabelOnly：货架标签旧值 → 本店现值。
 */
export function buildPriceComparison(task: StorePriceUpdateTask): PriceComparison {
  const isPriceUpdate = task.kind === "PriceUpdate";
  const fromPrice = isPriceUpdate ? task.storeRetailPrice : task.shelfRetailPrice;
  const toPrice = isPriceUpdate ? task.targetRetailPrice ?? task.storeRetailPrice : task.storeRetailPrice;
  const fromDiscountRate = normalizeRate(isPriceUpdate ? task.storeDiscountRate : task.shelfDiscountRate);
  const toDiscountRate = normalizeRate(
    isPriceUpdate ? task.targetDiscountRate ?? task.storeDiscountRate : task.storeDiscountRate
  );
  const priceChanged = fromPrice != null && toPrice != null && roundMoney(fromPrice) !== roundMoney(toPrice);
  const discountChanged = Math.abs(fromDiscountRate - toDiscountRate) > 0.00005;
  const fromFinalPrice = applyDiscount(fromPrice, fromDiscountRate);
  const toFinalPrice = applyDiscount(toPrice, toDiscountRate);
  const deltaFrom = discountChanged ? fromFinalPrice : fromPrice;
  const deltaTo = discountChanged ? toFinalPrice : toPrice;
  const delta = deltaFrom != null && deltaTo != null ? roundMoney(deltaTo - deltaFrom) : null;

  return {
    fromPrice,
    toPrice,
    fromDiscountRate,
    toDiscountRate,
    priceChanged,
    discountChanged,
    fromFinalPrice,
    toFinalPrice,
    delta,
    direction: delta == null || delta === 0 ? "same" : delta > 0 ? "up" : "down",
  };
}

export type ChangedFieldsKind = "retailPrice" | "discountRate" | "both";

/** 副行「零售价 / 折扣 / 零售价+折扣」；后端未给 changedFields 时按实际对比推断。 */
export function resolveChangedFieldsKind(task: StorePriceUpdateTask): ChangedFieldsKind {
  let price = task.changedFields.includes("retailPrice");
  let discount = task.changedFields.includes("discountRate");
  if (!price && !discount) {
    const comparison = buildPriceComparison(task);
    price = comparison.priceChanged;
    discount = comparison.discountChanged;
  }
  if (price && discount) return "both";
  return discount ? "discountRate" : "retailPrice";
}

export interface LabelPrintPrice {
  retailPrice: number | null;
  discountRate: number;
}

/**
 * 标签上要打印的价格 = 更新后的价格。
 * 仍是 PriceUpdate（改价接口未回传最新任务时的兜底）用仓库目标；LabelOnly 用本店现值。
 */
export function resolveLabelPrintPrice(task: StorePriceUpdateTask): LabelPrintPrice {
  const comparison = buildPriceComparison(task);
  return { retailPrice: comparison.toPrice, discountRate: comparison.toDiscountRate };
}

export type CompletedStatusKey = "updated" | "printed" | "markedReplaced" | "keptStorePrice" | "priceAligned";
export type HqSyncChipKey = "hqSynced" | "hqSyncing" | "hqSyncFailed";

export function resolveCompletedStatusKey(task: StorePriceUpdateTask): CompletedStatusKey {
  switch (task.completionMode) {
    case "Printed":
      return "printed";
    case "MarkedReplaced":
      return "markedReplaced";
    case "KeptStorePrice":
      return "keptStorePrice";
    case "PriceAligned":
      return "priceAligned";
    default:
      return "updated";
  }
}

/** superseded 表示已被更新的同步取代，不属于失败也不再进行中，不展示。 */
export function resolveHqSyncChipKey(task: StorePriceUpdateTask, hqSyncEnabled: boolean): HqSyncChipKey | null {
  if (!hqSyncEnabled || !task.hqSyncStatus) {
    return null;
  }
  if (task.hqSyncStatus === "succeeded") return "hqSynced";
  if (task.hqSyncStatus === "blocked") return "hqSyncFailed";
  if (task.hqSyncStatus === "superseded") return null;
  return "hqSyncing";
}

export interface SelectionSummary {
  /** 需要调用改价接口的任务数（kind=PriceUpdate）。 */
  applyCount: number;
  /** 更新并打印时要出的标签张数（所选全部任务）。 */
  printCount: number;
  /** 所选中的待换标签任务数。 */
  labelOnlyCount: number;
}

export function summarizeSelection(
  tasks: readonly StorePriceUpdateTask[],
  selectedIds: ReadonlySet<number>
): SelectionSummary {
  let applyCount = 0;
  let printCount = 0;
  for (const task of tasks) {
    if (!selectedIds.has(task.id)) continue;
    printCount += 1;
    if (task.kind === "PriceUpdate") applyCount += 1;
  }
  return { applyCount, printCount, labelOnlyCount: printCount - applyCount };
}

/** 分页合并去重：offset 分页在列表变化时可能把同一任务带到下一页。 */
export function mergeUniqueTasks(pages: readonly { items: StorePriceUpdateTask[] }[]): StorePriceUpdateTask[] {
  const seen = new Set<number>();
  const result: StorePriceUpdateTask[] = [];
  for (const page of pages) {
    for (const item of page.items) {
      if (seen.has(item.id)) continue;
      seen.add(item.id);
      result.push(item);
    }
  }
  return result;
}
