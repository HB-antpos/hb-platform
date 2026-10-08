import { buildSeasonalCardHistoryTable, groupSeasonalCardHistory } from "./history";
import type { SeasonalCardSubmissionRecord } from "./types";

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

function row(
  submissionGuid: string,
  overrides: Partial<SeasonalCardSubmissionRecord>
): SeasonalCardSubmissionRecord {
  return {
    submissionGuid,
    storeCode: "1013",
    catalogGuid: `catalog-${overrides.priceOption ?? 1}`,
    cardType: 1,
    cardTypeName: "圣诞节",
    seasonYear: 2026,
    unitPrice: overrides.priceOption ?? 1,
    priceLabel: `$${overrides.priceOption ?? 1}`,
    remainingQuantity: 1,
    remark: "",
    submittedByName: "店长 王",
    submittedAt: "2026-10-02T09:10:00Z",
    priceOption: 1,
    localSupplierCode: "SUP-A",
    supplierName: "供应商 A",
    batchGuid: "",
    ...overrides,
  };
}

const records: SeasonalCardSubmissionRecord[] = [
  // 最新一批（供应商 A），接口按提交时间倒序，同批 4 行连续
  row("n-2", { batchGuid: "B2", priceOption: 2, remainingQuantity: 80, submittedAt: "2026-10-05T01:00:00Z" }),
  row("n-1", { batchGuid: "B2", priceOption: 1, remainingQuantity: 120, submittedAt: "2026-10-05T01:00:00Z" }),
  row("n-3", { batchGuid: "B2", priceOption: 3, remainingQuantity: 36, submittedAt: "2026-10-05T01:00:00Z" }),
  row("n-4", { batchGuid: "B2", priceOption: 4, unitPrice: 4.5, priceLabel: "其他", remainingQuantity: 2, submittedAt: "2026-10-05T01:00:00Z" }),
  // 供应商 B 的一批
  row("b-1", { batchGuid: "C1", localSupplierCode: "SUP-B", supplierName: "供应商 B", priceOption: 1, remainingQuantity: 7, submittedAt: "2026-10-04T01:00:00Z" }),
  // 被覆盖的旧批次（供应商 A）
  row("o-1", { batchGuid: "B1", priceOption: 1, remainingQuantity: 150, submittedAt: "2026-10-02T01:00:00Z" }),
  row("o-2", { batchGuid: "B1", priceOption: 2, remainingQuantity: 96, submittedAt: "2026-10-02T01:00:00Z" }),
  // 批量填报之前的历史单条记录：没有供应商、没有批次
  row("legacy-1", { batchGuid: "", localSupplierCode: "", supplierName: "", priceOption: 2, remainingQuantity: 5, submittedAt: "2026-05-27T01:00:00Z" }),
  row("legacy-2", { batchGuid: "", localSupplierCode: "", supplierName: "", priceOption: 2, remainingQuantity: 9, submittedAt: "2026-05-27T01:01:00Z" }),
];

const entries = groupSeasonalCardHistory(records);
assertDeepEqual(
  entries.map((entry) => entry.key),
  ["batch:b2", "batch:c1", "batch:b1", "row:legacy-1", "row:legacy-2"],
  "rows of one batch merge into one card; legacy rows stay separate; order follows the API"
);
assertDeepEqual(
  entries[0]?.lines.map((line) => line.priceOption),
  [1, 2, 3, 4],
  "batch lines are sorted by price"
);
assertEqual(entries[0]?.totalQuantity, 238, "batch total quantity");
assertEqual(entries[0]?.totalAmount, 120 + 160 + 108 + 9, "batch total amount uses each unit price");
assertEqual(entries[0]?.isSuperseded, false, "the newest batch of a combo is not superseded");
assertEqual(entries[1]?.isSuperseded, false, "another supplier's batch is independent");
assertEqual(entries[2]?.isSuperseded, true, "an older batch of the same combo is superseded");
assertEqual(entries[3]?.isBatch, false, "legacy rows are not batches");
assertEqual(entries[3]?.firstSubmissionGuid, "legacy-1", "legacy rows keep their submission id for detail");

const table = buildSeasonalCardHistoryTable(records);
assertDeepEqual(table.years, [2026], "table years");
assertDeepEqual(
  table.rows.map((tableRow) => [tableRow.priceOption, tableRow.cells.get(2026)]),
  [
    [1, 127],
    [2, 89],
    [3, 36],
    [4, 2],
  ],
  "table sums each supplier's latest batch plus the latest legacy row per price (A 120 + B 7; A 80 + legacy 9)"
);
