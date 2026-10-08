/**
 * 历史页的纯逻辑：把逐行返回的提交记录按批次合并成卡片，以及「表格」视图的年度汇总。
 */
import type {
  SeasonalCardPriceOption,
  SeasonalCardSubmissionRecord,
  SeasonalCardType,
} from "@/modules/seasonal-cards/types";

export interface SeasonalCardHistoryLine {
  submissionGuid: string;
  priceOption: SeasonalCardPriceOption | null;
  priceLabel: string;
  unitPrice: number | null;
  remainingQuantity: number | null;
}

export interface SeasonalCardHistoryEntry {
  /** 批次号；历史单条记录用 submissionGuid 作为键。 */
  key: string;
  /** 是否为批量填报的批次（历史单条记录为 false）。 */
  isBatch: boolean;
  storeCode: string;
  seasonYear: number | null;
  cardType: SeasonalCardType | null;
  cardTypeName: string;
  localSupplierCode: string;
  supplierName: string;
  remark: string;
  submittedByName: string;
  submittedAt: string;
  lines: SeasonalCardHistoryLine[];
  totalQuantity: number;
  totalAmount: number;
  /** 已加载的记录里存在同组合更新的批次（被覆盖）；只在有证据时标记，不会误报。 */
  isSuperseded: boolean;
  /** 历史单条记录用于打开详情。 */
  firstSubmissionGuid: string;
}

function priceSortValue(line: SeasonalCardHistoryLine) {
  if (line.priceOption != null) {
    return line.priceOption;
  }
  const numeric = Number(line.priceLabel.replace(/[^0-9.]/g, ""));
  return Number.isFinite(numeric) && numeric > 0 ? numeric : Number.MAX_SAFE_INTEGER;
}

function comboKeyOf(entry: Pick<
  SeasonalCardHistoryEntry,
  "storeCode" | "seasonYear" | "cardType" | "localSupplierCode"
>) {
  return [
    entry.storeCode,
    entry.seasonYear ?? "",
    entry.cardType ?? "",
    entry.localSupplierCode.toLowerCase(),
  ].join("|");
}

/** 同一 batchGuid 的多行合并成一张卡片；没有批次号的历史行各自独立。保持接口返回的先后顺序。 */
export function groupSeasonalCardHistory(
  records: SeasonalCardSubmissionRecord[]
): SeasonalCardHistoryEntry[] {
  const entries: SeasonalCardHistoryEntry[] = [];
  const byBatch = new Map<string, SeasonalCardHistoryEntry>();

  records.forEach((record) => {
    const batchKey = record.batchGuid.trim().toLowerCase();
    const line: SeasonalCardHistoryLine = {
      submissionGuid: record.submissionGuid,
      priceOption: record.priceOption,
      priceLabel: record.priceLabel,
      unitPrice: record.unitPrice,
      remainingQuantity: record.remainingQuantity,
    };
    const existing = batchKey ? byBatch.get(batchKey) : undefined;
    if (existing) {
      existing.lines.push(line);
      if (record.submittedAt > existing.submittedAt) {
        existing.submittedAt = record.submittedAt;
      }
      return;
    }

    const entry: SeasonalCardHistoryEntry = {
      key: batchKey ? `batch:${batchKey}` : `row:${record.submissionGuid}`,
      isBatch: Boolean(batchKey),
      storeCode: record.storeCode,
      seasonYear: record.seasonYear,
      cardType: record.cardType,
      cardTypeName: record.cardTypeName,
      localSupplierCode: record.localSupplierCode,
      supplierName: record.supplierName,
      remark: record.remark,
      submittedByName: record.submittedByName,
      submittedAt: record.submittedAt,
      lines: [line],
      totalQuantity: 0,
      totalAmount: 0,
      isSuperseded: false,
      firstSubmissionGuid: record.submissionGuid,
    };
    entries.push(entry);
    if (batchKey) {
      byBatch.set(batchKey, entry);
    }
  });

  const latestBatchByCombo = new Map<string, SeasonalCardHistoryEntry>();
  entries.forEach((entry) => {
    entry.lines.sort((left, right) => priceSortValue(left) - priceSortValue(right));
    entry.totalQuantity = entry.lines.reduce(
      (sum, line) => sum + (line.remainingQuantity ?? 0),
      0
    );
    entry.totalAmount =
      Math.round(
        entry.lines.reduce(
          (sum, line) => sum + (line.remainingQuantity ?? 0) * (line.unitPrice ?? 0),
          0
        ) * 100
      ) / 100;

    if (!entry.isBatch) {
      return;
    }
    const comboKey = comboKeyOf(entry);
    const latest = latestBatchByCombo.get(comboKey);
    if (!latest || entry.submittedAt > latest.submittedAt) {
      latestBatchByCombo.set(comboKey, entry);
    }
  });

  entries.forEach((entry) => {
    if (entry.isBatch) {
      entry.isSuperseded = latestBatchByCombo.get(comboKeyOf(entry)) !== entry;
    }
  });

  return entries;
}

export interface SeasonalCardHistoryTableRow {
  priceKey: string;
  priceOption: SeasonalCardPriceOption | null;
  priceLabel: string;
  cells: Map<number, number>;
}

/**
 * 表格视图：每个年度 × 价格的剩余数量，口径与后台统计一致——
 * 同一 年度 + 节日 + 供应商 只取最新一批；历史单条记录按价格各取最新一条；再把各供应商相加。
 */
export function buildSeasonalCardHistoryTable(records: SeasonalCardSubmissionRecord[]) {
  const effective = new Map<string, SeasonalCardSubmissionRecord[]>();
  const latestBatchAt = new Map<string, { batchKey: string; submittedAt: string }>();

  records.forEach((record) => {
    if (typeof record.seasonYear !== "number" || record.remainingQuantity == null) {
      return;
    }
    const supplierKey = record.localSupplierCode.trim().toLowerCase();
    const groupKey = [record.seasonYear, record.cardType ?? "", supplierKey].join("|");
    const batchKey = record.batchGuid.trim().toLowerCase();

    if (!supplierKey || !batchKey) {
      // 历史单条记录：同一价格目录项只认最新一条。
      const legacyKey = `${groupKey}|legacy|${record.catalogGuid || record.priceLabel}`;
      const current = effective.get(legacyKey)?.[0];
      if (!current || record.submittedAt >= current.submittedAt) {
        effective.set(legacyKey, [record]);
      }
      return;
    }

    const latest = latestBatchAt.get(groupKey);
    if (!latest || record.submittedAt > latest.submittedAt) {
      latestBatchAt.set(groupKey, { batchKey, submittedAt: record.submittedAt });
      effective.set(groupKey, [record]);
      return;
    }
    if (latest.batchKey === batchKey) {
      effective.get(groupKey)?.push(record);
    }
  });

  const years = new Set<number>();
  const rows = new Map<string, SeasonalCardHistoryTableRow>();
  effective.forEach((group) => {
    group.forEach((record) => {
      const year = record.seasonYear as number;
      years.add(year);
      const priceKey =
        record.priceOption != null ? `option:${record.priceOption}` : `label:${record.priceLabel}`;
      const row = rows.get(priceKey) ?? {
        priceKey,
        priceOption: record.priceOption,
        priceLabel: record.priceLabel,
        cells: new Map<number, number>(),
      };
      row.cells.set(year, (row.cells.get(year) ?? 0) + (record.remainingQuantity ?? 0));
      rows.set(priceKey, row);
    });
  });

  return {
    years: Array.from(years).sort((left, right) => left - right),
    rows: Array.from(rows.values()).sort((left, right) => {
      const leftValue =
        left.priceOption ?? (Number(left.priceLabel.replace(/[^0-9.]/g, "")) || Number.MAX_SAFE_INTEGER);
      const rightValue =
        right.priceOption ?? (Number(right.priceLabel.replace(/[^0-9.]/g, "")) || Number.MAX_SAFE_INTEGER);
      return leftValue - rightValue || left.priceLabel.localeCompare(right.priceLabel);
    }),
  };
}
