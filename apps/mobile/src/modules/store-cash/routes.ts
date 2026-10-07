// 现金模块子页面的路径与参数。纯字符串拼装，不依赖 expo-router。
import type { BalanceKind } from "./balance-form";

export const STORE_CASH_ROUTES = {
  home: "/store-cash",
  depositNew: "/store-cash/deposit-new",
  expenseNew: "/store-cash/expense-new",
  balanceNew: "/store-cash/balance-new",
  recordDetail: "/store-cash/record-detail",
} as const;

export type RecordDetailKind = "deposit" | "expense";

function withQuery(path: string, params: Record<string, string>): string {
  const query = Object.entries(params)
    .map(([key, value]) => `${key}=${encodeURIComponent(value)}`)
    .join("&");
  return query ? `${path}?${query}` : path;
}

export function buildDepositNewHref(storeCode: string): string {
  return withQuery(STORE_CASH_ROUTES.depositNew, { storeCode });
}

export function buildExpenseNewHref(storeCode: string): string {
  return withQuery(STORE_CASH_ROUTES.expenseNew, { storeCode });
}

export function buildBalanceNewHref(storeCode: string, kind: BalanceKind): string {
  return withQuery(STORE_CASH_ROUTES.balanceNew, { storeCode, kind });
}

/** created=1 表示刚提交成功，详情页顶部显示「已记录」提示。 */
export function buildRecordDetailHref(
  kind: RecordDetailKind,
  guid: string,
  storeCode: string,
  options: { created?: boolean } = {},
): string {
  return withQuery(STORE_CASH_ROUTES.recordDetail, {
    kind,
    guid,
    storeCode,
    ...(options.created ? { created: "1" } : {}),
  });
}

/** expo-router 的搜索参数可能是 string | string[] | undefined，统一取第一个非空值。 */
export function firstParam(value: string | string[] | undefined): string {
  const candidate = Array.isArray(value) ? value[0] : value;
  return typeof candidate === "string" ? candidate : "";
}

export function parseBalanceKind(value: string | string[] | undefined): BalanceKind {
  return firstParam(value) === "opening" ? "opening" : "count";
}

export function parseRecordDetailKind(value: string | string[] | undefined): RecordDetailKind | null {
  const raw = firstParam(value);
  return raw === "deposit" || raw === "expense" ? raw : null;
}
