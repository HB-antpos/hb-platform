import type { RecordsFilter } from "./records";

// react-query 键：所有现金数据都挂在 ["store-cash"] 下，写操作成功后可整体失效。
export const storeCashKeys = {
  all: ["store-cash"] as const,
  context: () => ["store-cash", "context"] as const,
  summary: (storeCode: string) => ["store-cash", "summary", storeCode] as const,
  daily: (storeCode: string, from: string, to: string) => ["store-cash", "daily", storeCode, from, to] as const,
  deposits: (storeCode: string, filter: RecordsFilter) => ["store-cash", "deposits", storeCode, filter] as const,
  expenses: (storeCode: string, filter: RecordsFilter) => ["store-cash", "expenses", storeCode, filter] as const,
  entries: (storeCode: string, includeVoided: boolean) => ["store-cash", "entries", storeCode, includeVoided] as const,
  depositDetail: (guid: string) => ["store-cash", "deposit", guid] as const,
  expenseDetail: (guid: string) => ["store-cash", "expense", guid] as const,
};
