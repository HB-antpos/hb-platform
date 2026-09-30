import type { ContainerNewProductItem } from "./types";

// 品种数按商品去重：同一商品分在两个货柜里时列表有两张卡片，但只算一个品种（工作台角标与页面合计共用）
export function countNewProductKinds(items: readonly Pick<ContainerNewProductItem, "productCode">[]): number {
  return new Set(items.map((item) => item.productCode.toUpperCase())).size;
}
