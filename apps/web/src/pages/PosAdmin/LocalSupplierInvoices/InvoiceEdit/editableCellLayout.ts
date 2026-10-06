/**
 * 行内编辑单元格布局参数。
 */

export const COMPACT_NUMBER_INPUT_WIDTH = 58

export function resolveEditableNumberInputWidth({
  addonAfter,
  inputWidth,
}: {
  addonAfter?: unknown
  inputWidth?: number
}) {
  // 默认宽度保持旧行为；窄列可显式传入 inputWidth 避免编辑框撑开表格。
  return inputWidth ?? (addonAfter ? 110 : 90)
}

export function shouldSelectEditableNumberTextOnFocus(selectTextOnFocus?: boolean) {
  // 数字格进入编辑态默认全选，方便用户直接覆盖；保留显式 false 给特殊列关闭。
  return selectTextOnFocus !== false
}
