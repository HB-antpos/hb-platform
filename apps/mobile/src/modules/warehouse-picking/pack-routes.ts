import type { Href } from "expo-router";

// 拣货与装箱联动的页面路由：都在 /warehouse-picking 前缀下，随拣货的全屏作业与设备会话规则，不需要另外登记。

/** 先拣后装页；focus 是要定位的商品编码（核对页「去补装」带过来）。 */
export function packLaterRoute(orderGuid: string, focusProductCode?: string | null): Href {
  const base = `/warehouse-picking/${encodeURIComponent(orderGuid)}/pack`;
  return (focusProductCode ? `${base}?focus=${encodeURIComponent(focusProductCode)}` : base) as Href;
}

/** 提交拣货前核对页。 */
export function packReviewRoute(orderGuid: string): Href {
  return `/warehouse-picking/${encodeURIComponent(orderGuid)}/pack-review` as Href;
}
