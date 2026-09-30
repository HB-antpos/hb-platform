import type { ContainerNewProductItem } from "./types";

// 与后端一致：到店日升序 → HB 货号（忽略大小写，没有货号的排在该日末尾）→ ProductCode 兜底保证顺序稳定
export function compareByArrivalThenProductNo(a: ContainerNewProductItem, b: ContainerNewProductItem): number {
  const byDate = compareOrdinal(a.estimatedStoreArrivalDate, b.estimatedStoreArrivalDate);
  if (byDate !== 0) return byDate;
  if (a.hbProductNo && b.hbProductNo) {
    const byNo = compareOrdinal(a.hbProductNo.toUpperCase(), b.hbProductNo.toUpperCase());
    if (byNo !== 0) return byNo;
  } else if (a.hbProductNo || b.hbProductNo) {
    return a.hbProductNo ? -1 : 1;
  }
  return compareOrdinal(a.productCode, b.productCode);
}

// 按码点比较，不用 localeCompare，避免设备语言影响顺序、与后端 Ordinal 比较对不上
function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
