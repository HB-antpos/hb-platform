import { formatArrivalDateRange } from "./arrival-range";
import type { ContainerNewProductBasis } from "./types";

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;

/**
 * 接口里的货柜到仓库日期（可能带时间部分）→ YYYY-MM-DD。
 * 旧版后端不返回、或格式不对一律当作没有（null），页面此时不显示仓库日期行，
 * 绝不回退去显示到店日期。
 */
export function normalizeWarehouseArrivalDate(value: string | null | undefined): string | null {
  const day = value?.trim().slice(0, 10);
  return day && DATE_ONLY.test(day) ? day : null;
}

/** 仓库日期文案所在的 locale 分组：商品卡片用 labels，货柜选择面板用 containerSheet */
export type WarehouseDateScope = "labels" | "containerSheet";

export interface WarehouseDateText {
  /** 要翻译的文案键，插值参数为 { date } */
  key: `${WarehouseDateScope}.warehouseDate` | `${WarehouseDateScope}.warehouseDateEstimated`;
  /** 日期文本：DD/MM；年份与参考日期不同时补上，写成 DD/MM/YYYY */
  date: string;
}

/**
 * 决定仓库日期行显示什么：
 * - basis = actual（货柜已到仓）→「到仓库 {date}」，date 是实际到货日期；
 * - basis = estimated（还在途）→「预计到仓库 {date}」，date 是预计到岸日期兜底；
 * - 没有日期 → null，调用方不渲染这一行。
 *
 * 日期风格与页面其余日期一致：DD/MM，不经过 Date 对象（避免时区偏移）。页面里年份单独显示在到店日期下方，
 * 一行文字里放不下独立的年份，所以只在跨年时（仓库日期的年份与 referenceDate，即该货柜到店起始日的年份不同）
 * 才补上年份，避免 12 月到仓、次年 1 月到店时被误读成同一年。
 */
export function getWarehouseDateText(
  scope: WarehouseDateScope,
  basis: ContainerNewProductBasis,
  warehouseArrivalDate: string | null | undefined,
  referenceDate?: string | null,
): WarehouseDateText | null {
  const day = normalizeWarehouseArrivalDate(warehouseArrivalDate);
  if (!day) return null;
  const { start, year } = formatArrivalDateRange(day, null);
  const referenceYear = normalizeWarehouseArrivalDate(referenceDate)?.slice(0, 4);
  return {
    key: basis === "actual" ? `${scope}.warehouseDate` : `${scope}.warehouseDateEstimated`,
    date: referenceYear && referenceYear !== year ? `${start}/${year}` : start,
  };
}
