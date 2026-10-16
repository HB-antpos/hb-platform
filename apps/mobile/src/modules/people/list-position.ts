// 列表里「同一组连续的行共用一张圆角卡片」的分段位置：只有首行有上圆角、末行有下圆角。
// 店员列表、管理员列表、权限列表共用；纯函数，不依赖 React Native。

export type ListRowPosition = "only" | "first" | "middle" | "last";

export function resolveRowPosition(index: number, length: number): ListRowPosition {
  if (length <= 1) return "only";
  if (index === 0) return "first";
  return index === length - 1 ? "last" : "middle";
}
