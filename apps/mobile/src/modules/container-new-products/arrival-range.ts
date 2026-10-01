import type { ContainerNewProductItem } from "./types";

export type ArrivalRangeFilter = "all" | "past" | "upcoming";

export const ARRIVAL_RANGE_FILTERS: readonly ArrivalRangeFilter[] = ["all", "past", "upcoming"];

// 设备本地日期（YYYY-MM-DD）；旧版后端不返回门店本地今天时兜底使用
export function deviceLocalToday(now = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, "0");
  const day = String(now.getDate()).padStart(2, "0");
  return `${now.getFullYear()}-${month}-${day}`;
}

// 接口已限定「过去 1 周 ~ 未来 2 周」，这里只按今天切成两组：到店日早于今天算过去，今天及以后算未来。
// 日期都是 YYYY-MM-DD，直接按字符串比较即可，避免 Date 解析带来的时区偏移。
export function matchesArrivalRange(item: Pick<ContainerNewProductItem, "estimatedStoreArrivalDate">, filter: ArrivalRangeFilter, localToday: string): boolean {
  if (filter === "all") return true;
  const isPast = item.estimatedStoreArrivalDate.slice(0, 10) < localToday.slice(0, 10);
  return filter === "past" ? isPast : !isPast;
}
