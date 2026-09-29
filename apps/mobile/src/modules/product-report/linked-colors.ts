/**
 * 三表联动的维度配色，与 Web 独立销售看板一致：分店蓝、供应商紫；商品明细只接收筛选，用中性灰。
 * 选中行统一为浅色底 + 左侧 3pt 色条 + 名称变色，筛选条 chip 与表内选中行同色，便于对应。
 */
export const LINKED_COLORS = {
  branch: "#2563EB",
  branchText: "#1D4ED8",
  branchBackground: "#EFF6FF",
  branchBorder: "#BFDBFE",
  supplier: "#7C3AED",
  supplierText: "#6D28D9",
  supplierBackground: "#F5F3FF",
  supplierBorder: "#DDD6FE",
  product: "#6B7280",
} as const;

/** 局部加载期间旧数据变淡但仍可点按，与 Web 看板的过期栏表现一致。 */
export const LINKED_STALE_OPACITY = 0.5;
