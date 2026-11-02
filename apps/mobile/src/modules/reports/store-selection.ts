/**
 * 报表分店多选的选择集工具。约定：空数组代表「全部分店」；分店代码比较忽略大小写与首尾空白，
 * 与收银启用白名单（getCashierEnabledStoreCodes）的匹配口径一致。
 */

function normalizeCode(code: string | null | undefined) {
  return (code ?? "").trim().toLocaleLowerCase();
}

/** 选择集里是否包含该分店；空选择集（全部分店）不算「选中」某一家。 */
export function isStoreSelected(selection: readonly string[], code: string | null | undefined): boolean {
  const target = normalizeCode(code);
  return Boolean(target) && selection.some((item) => normalizeCode(item) === target);
}

/** 点选一家：已选则移除，未选则加入。 */
export function toggleStoreSelection(selection: readonly string[], code: string): string[] {
  const target = normalizeCode(code);
  if (!target) return [...selection];
  return isStoreSelected(selection, code)
    ? selection.filter((item) => normalizeCode(item) !== target)
    : [...selection, code.trim()];
}

/**
 * 把选择集收敛到白名单内：只保留仍启用的分店（用白名单里的原始写法、按白名单顺序、去重）；
 * 选满全部启用分店时收敛为空数组，即「全部分店」，与不选的查询范围和显示完全一致。
 */
export function normalizeStoreSelection(
  selection: readonly string[],
  enabledCodes: readonly string[],
): string[] {
  if (selection.length === 0) return [];
  const picked = enabledCodes.filter((code) => isStoreSelected(selection, code));
  const uniqueEnabledCount = new Set(enabledCodes.map(normalizeCode).filter(Boolean)).size;
  return picked.length >= uniqueEnabledCount ? [] : picked;
}

/** 两个选择集是否代表同一组分店（不计顺序与大小写）。 */
export function isSameStoreSelection(left: readonly string[], right: readonly string[]): boolean {
  const leftSet = new Set(left.map(normalizeCode).filter(Boolean));
  const rightSet = new Set(right.map(normalizeCode).filter(Boolean));
  if (leftSet.size !== rightSet.size) return false;
  for (const code of leftSet) {
    if (!rightSet.has(code)) return false;
  }
  return true;
}

/**
 * 选择集的显示文案：未选为「全部分店」，选一家显示店名，选多家显示「已选 N 家分店」。
 * 店名查不到时退回分店代码，保证标签不会是空的。
 */
export function getStoreSelectionLabel(
  selection: readonly string[],
  getStoreName: (code: string) => string | null | undefined,
  labels: { all: string; multiple: (count: number) => string },
): string {
  if (selection.length === 0) return labels.all;
  if (selection.length === 1) return getStoreName(selection[0]) || selection[0];
  return labels.multiple(selection.length);
}
