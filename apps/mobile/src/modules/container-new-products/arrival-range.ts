import type { ContainerNewProductItem } from "./types";

export type ArrivalRangeFilter = "all" | "past" | "upcoming";

export const ARRIVAL_RANGE_FILTERS: readonly ArrivalRangeFilter[] = ["all", "past", "upcoming"];

// 设备本地日期（YYYY-MM-DD）；旧版后端不返回门店本地今天时兜底使用
export function deviceLocalToday(now = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");
  return `${now.getFullYear()}-${month}-${day}`;
}

// 接口已限定「过去 1 周 ~ 未来 2 周」，这里只按今天切成两组：到店区间结束日早于今天算过去，
// 区间还没结束（今天或以后仍可能到店）算未来；旧版后端没有结束日时按起始日判断。
// 日期都是 YYYY-MM-DD，直接按字符串比较即可，避免 Date 解析带来的时区偏移。
export function matchesArrivalRange(item: Pick<ContainerNewProductItem, "estimatedStoreArrivalDate" | "estimatedStoreArrivalDateEnd">, filter: ArrivalRangeFilter, localToday: string): boolean {
  if (filter === "all") return true;
  const lastPossibleDate = item.estimatedStoreArrivalDateEnd ?? item.estimatedStoreArrivalDate;
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

// 到店日期（区间）显示：日期只取前 10 位按 YYYY-MM-DD 拆分，不经过 Date，避免时区偏移
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
