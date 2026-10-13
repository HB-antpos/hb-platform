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

export interface WarehouseDateText {
  /** 要翻译的文案键（货柜选择面板的整行文字），插值参数为 { date } */
  key: "containerSheet.warehouseDate" | "containerSheet.warehouseDateEstimated";
  /** 日期文本：DD/MM；年份与参考日期不同时补上，写成 DD/MM/YYYY */
  date: string;
}

/**
 * 货柜选择面板的仓库日期行（整行文字）：
 * - basis = actual（货柜已到仓）→「到仓库 {date}」，date 是实际到货日期；
 * - basis = estimated（还在途）→「预计到仓库 {date}」，date 是预计到岸日期兜底；
 * - 没有日期 → null，调用方不渲染这一行。
 *
 * 日期风格与页面其余日期一致：DD/MM，不经过 Date 对象（避免时区偏移）。一行文字里放不下商品卡片那样单独的年份行，
 * 所以只在跨年时（仓库日期的年份与 referenceDate 不同）才补上年份；页面传门店本地今天作为参考，
 * 这样 12 月底、1 月初同时出现两个年份的货柜不会被误读成同一年。
 */
export function getWarehouseDateText(
  basis: ContainerNewProductBasis,
  warehouseArrivalDate: string | null | undefined,
  referenceDate?: string | null,
): WarehouseDateText | null {
  const day = normalizeWarehouseArrivalDate(warehouseArrivalDate);
  if (!day) return null;
  const { start, year } = formatArrivalDateRange(day, null);
  const referenceYear = normalizeWarehouseArrivalDate(referenceDate)?.slice(0, 4);
  return {
    key: basis === "actual" ? "containerSheet.warehouseDate" : "containerSheet.warehouseDateEstimated",
    date: referenceYear && referenceYear !== year ? `${start}/${year}` : start,
  };
}

export interface WarehouseDateColumn {
  /** 右列标签的文案键：已到仓「到仓库」，在途「预计到仓库」 */
  labelKey: "labels.warehouseDateLabel" | "labels.warehouseDateEstimatedLabel";
  /** 大字日期 DD/MM */
  day: string;
  /** 日期下方的年份 YYYY */
  year: string;
}

/**
 * 商品卡片右列（原「预计到店」列的位置）：标签 + 大字日期 + 年份，三行的结构与原来的单日期写法一致。
 * - basis = actual → 标签「到仓库」，日期是实际到货日期；
 * - basis = estimated → 标签「预计到仓库」，日期是预计到岸日期兜底；
 * - 没有日期（旧版后端不返回该字段）→ null，整列不渲染，绝不回退去显示到店日期。
 */
export function getWarehouseDateColumn(
  basis: ContainerNewProductBasis,
  warehouseArrivalDate: string | null | undefined,
): WarehouseDateColumn | null {
  const day = normalizeWarehouseArrivalDate(warehouseArrivalDate);
  if (!day) return null;
  const { start, year } = formatArrivalDateRange(day, null);
  return {
    labelKey: basis === "actual" ? "labels.warehouseDateLabel" : "labels.warehouseDateEstimatedLabel",
    day: start,
    year,
  };
}
