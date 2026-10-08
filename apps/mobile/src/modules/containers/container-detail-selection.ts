import { getDetailGuid } from "./query";
import { isCreatableNewProduct } from "./container-detail-table-columns";
import type { ContainerDetail } from "./types";

/**
 * 货柜明细表格的勾选 / 定位 / 行点击纯逻辑。
 * 勾选集合只保存明细 GUID（去空白），界面用 Set 做 O(1) 判断，500 行一页也不卡。
 */

export function toSelectedSet(selectedHguids: readonly string[]): Set<string> {
  return new Set(selectedHguids.map((item) => item.trim()).filter(Boolean));
}

export interface PageSelectionState {
  /** 本页有 GUID 的行数 */
  pageCount: number;
  /** 本页已勾选行数 */
  pageSelectedCount: number;
  /** 本页全部勾选（本页为空时为 false） */
  allSelected: boolean;
  /** 本页部分勾选（表头显示「半选」） */
  partiallySelected: boolean;
}

export function getPageSelectionState(
  details: readonly ContainerDetail[],
  selectedSet: ReadonlySet<string>,
): PageSelectionState {
  const guids = details.map((detail) => getDetailGuid(detail).trim()).filter(Boolean);
  const selectedCount = guids.filter((guid) => selectedSet.has(guid)).length;
  return {
    pageCount: guids.length,
    pageSelectedCount: selectedCount,
    allSelected: guids.length > 0 && selectedCount === guids.length,
    partiallySelected: selectedCount > 0 && selectedCount < guids.length,
  };
}

/** 勾选/取消单行；返回新数组，不修改入参。 */
export function toggleRowSelection(selectedHguids: readonly string[], hguid: string): string[] {
  const target = hguid.trim();
  if (!target) return [...selectedHguids];
  return selectedHguids.includes(target)
    ? selectedHguids.filter((item) => item !== target)
    : [...selectedHguids, target];
}

/** 已勾选且仍是未建档新商品的明细（「创建新商品」的候选范围）。 */
export function getSelectedCreatableDetails(
  details: readonly ContainerDetail[],
  selectedSet: ReadonlySet<string>,
): ContainerDetail[] {
  return details.filter((detail) => selectedSet.has(getDetailGuid(detail).trim()) && isCreatableNewProduct(detail));
}

/** 已勾选的明细（按当前页顺序）。 */
export function getSelectedDetails(
  details: readonly ContainerDetail[],
  selectedSet: ReadonlySet<string>,
): ContainerDetail[] {
  return details.filter((detail) => selectedSet.has(getDetailGuid(detail).trim()));
}

export type LocateRowResult =
  | { kind: "found"; index: number }
  /** 该行不在当前页（被筛选/搜索隐藏或在其他页），界面提示用户清除筛选或翻页 */
  | { kind: "not-on-page" };

/** 在当前页明细里按 GUID（忽略大小写与首尾空白）找行下标。 */
export function locateDetailRow(details: readonly ContainerDetail[], hguid: string): LocateRowResult {
  const target = hguid.trim().toUpperCase();
  if (!target) return { kind: "not-on-page" };
  const index = details.findIndex((detail) => getDetailGuid(detail).trim().toUpperCase() === target);
  return index >= 0 ? { kind: "found", index } : { kind: "not-on-page" };
}

/**
 * 滚动到某行时，让它落在可视区偏上位置（0.3），便于同时看到上下文行。
 * 行高固定 + getItemLayout，所以 scrollToIndex 的结果是精确的。
 */
export const LOCATE_ROW_VIEW_POSITION = 0.3;

export type RowPressAction = "edit" | "view";

/** 点击一行：有编辑权限进入编辑，否则只读查看（权限判定与旧版「编辑明细」按钮同一条件）。 */
export function resolveRowPressAction(canEditContainer: boolean): RowPressAction {
  return canEditContainer ? "edit" : "view";
}

/** 定位高亮的持续时间（毫秒）。 */
export const LOCATE_HIGHLIGHT_MS = 2400;
