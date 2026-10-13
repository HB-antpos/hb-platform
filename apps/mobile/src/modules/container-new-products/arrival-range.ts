import type { ContainerNewProductItem } from "./types";

export type ArrivalRangeFilter = "all" | "past" | "upcoming";

export const ARRIVAL_RANGE_FILTERS: readonly ArrivalRangeFilter[] = ["all", "past", "upcoming"];

// 设备本地日期（YYYY-MM-DD）；旧版后端不返回门店本地今天时兜底使用
export function deviceLocalToday(now = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");
  return `${now.getFullYear()}-${month}-${day}`;
}

type ListDateItem = Pick<ContainerNewProductItem, "warehouseArrivalDate" | "estimatedStoreArrivalDate">;

// 列表排序与货柜面板排序用的日期（YYYY-MM-DD）：优先货柜到仓库日期（已到仓 = 实际到货日期，在途 = 预计到岸日期）。
// 旧版后端不返回到仓库日期时退回预计到店起始日，保证新版 App 遇到旧后端时仍有稳定的排序依据（界面不显示该日期）。
export function resolveListDate(item: ListDateItem): string {
  return item.warehouseArrivalDate ?? item.estimatedStoreArrivalDate.slice(0, 10);
}

// 接口已限定「过去 1 周 ~ 未来 3 周」，这里只按门店本地今天切成两组：
// - 新版后端：按货柜到仓库日期，早于今天算过去，其余（含今天到仓）算未来；
// - 旧版后端没有到仓库日期：保持原来的口径，到店区间结束日早于今天算过去（区间还没结束，今天或以后仍可能到店算未来），没有结束日按起始日判断。
// 日期都是 YYYY-MM-DD，直接按字符串比较即可，避免 Date 解析带来的时区偏移。
export function matchesArrivalRange(item: ListDateItem & Pick<ContainerNewProductItem, "estimatedStoreArrivalDateEnd">, filter: ArrivalRangeFilter, localToday: string): boolean {
  if (filter === "all") return true;
  const lastPossibleDate = item.warehouseArrivalDate ?? item.estimatedStoreArrivalDateEnd ?? item.estimatedStoreArrivalDate;
  const isPast = lastPossibleDate.slice(0, 10) < localToday.slice(0, 10);
  return filter === "past" ? isPast : !isPast;
}

export interface ArrivalDateDisplay {
  /** 起始日 DD/MM */
  start: string;
  /** 结束日 DD/MM；没有结束日或与起始日相同时为 null，只显示单日 */
  end: string | null;
  /** 年份；区间跨年时为「起始年–结束年」 */
  year: string;
}

// 日期（可选区间）拆成 DD/MM 与年份：日期只取前 10 位按 YYYY-MM-DD 拆分，不经过 Date，避免时区偏移。
// 页面只用它显示到仓库日期（单日，end 恒为 null）
export function formatArrivalDateRange(startValue: string, endValue: string | null): ArrivalDateDisplay {
  const start = splitDate(startValue);
  const end = endValue && endValue.slice(0, 10) !== startValue.slice(0, 10) ? splitDate(endValue) : null;
  return {
    start: start.dayMonth,
    end: end?.dayMonth ?? null,
    year: end && end.year && end.year !== start.year ? `${start.year}–${end.year}` : start.year,
  };
}

function splitDate(value: string) {
  const [year, month, day] = value.slice(0, 10).split("-");
  if (!year || !month || !day) return { dayMonth: value, year: "" };
  return { dayMonth: `${day}/${month}`, year };
}
