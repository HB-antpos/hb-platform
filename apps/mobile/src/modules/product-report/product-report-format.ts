import type { ProductReportCostStatus } from "@/modules/product-report/api";
import { formatMoney, formatWholeMoney } from "@/modules/reports/format";

/**
 * 商品报告各处（主页面、供应商表、供应商弹窗）共用的数值格式化，
 * 放在同一处避免主表和弹窗对同一个指标出现两种写法。
 */

export function formatCount(value: number) {
  return Math.round(value).toLocaleString("en-AU");
}

export function formatNullableMoney(value: number | null) {
  return value === null ? "—" : formatMoney(value);
}

export function formatNullableWholeMoney(value: number | null) {
  return value === null ? "—" : formatWholeMoney(value);
}

export function formatGrossMarginRate(
  value: number | null,
  costStatus: ProductReportCostStatus,
  costPendingLabel: string,
  noActivityLabel: string,
) {
  // 后端统一返回 0-1 比率（例如 0.4 = 40%），展示层再换算为百分比。
  if (costStatus === "NoActivity") return noActivityLabel;
  if (costStatus === "Missing") return costPendingLabel;
  // Complete 但销售额为零时毛利率按数学定义为空值，不代表成本缺失。
  return value === null ? "—" : `${(value * 100).toFixed(1)}%`;
}

export function formatShare(value: number, denominator: number) {
  if (!Number.isFinite(value) || !Number.isFinite(denominator) || denominator <= 0) {
    return "--";
  }
  return `${((value / denominator) * 100).toFixed(1)}%`;
}
