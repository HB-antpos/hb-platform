// 弹窗里「点列头排一次」的通用排序。
//
// 为什么不用 antd 自带的实时排序：这类弹窗（套装子项、多码、货柜套装条码）的价格/条码是行内直接编辑的，
// 排序若随数据实时重排，输入过程中行会跳走、焦点丢失。所以只在点列头那一刻按当前值排一次，
// 记下行 id 的顺序；之后编辑不会重排，再点列头才重新排。

export type SnapshotSortOrder = 'ascend' | 'descend'

export interface SnapshotSortState {
  field: string
  order: SnapshotSortOrder
  /** 点列头那一刻排好的行 id 顺序。 */
  ids: string[]
}

const isEmpty = (value: unknown) => value === undefined || value === null || (typeof value === 'string' && value.trim() === '')

/**
 * 返回排好序的行 id。空值无论升降序都排最后；数字/布尔按大小，文本按自然序（-2 排在 -11 前）；
 * 相等时保持原顺序。getValue 可以返回「编辑后的值」，让排序和屏幕上看到的一致。
 */
export function sortItemIds<T>(
  items: readonly T[],
  getId: (item: T) => string,
  getValue: (item: T, field: string) => unknown,
  field: string,
  order: SnapshotSortOrder,
): string[] {
  const direction = order === 'ascend' ? 1 : -1

  return items
    .map((item, index) => ({ id: getId(item), index, value: getValue(item, field) }))
    .sort((left, right) => {
      const leftEmpty = isEmpty(left.value)
      const rightEmpty = isEmpty(right.value)
      if (leftEmpty || rightEmpty) {
        if (leftEmpty && rightEmpty) return left.index - right.index
        return leftEmpty ? 1 : -1
      }
      const bothNumeric = typeof left.value !== 'string' && typeof right.value !== 'string'
      const compared = bothNumeric
        ? Number(left.value) - Number(right.value)
        : String(left.value).localeCompare(String(right.value), undefined, { numeric: true, sensitivity: 'base' })
      return compared !== 0 ? compared * direction : left.index - right.index
    })
    .map((entry) => entry.id)
}

/** 按记录的 id 顺序展示；不在记录里的行（排序后新增的）接在末尾，已删除的 id 自动忽略。 */
export function arrangeByIds<T>(
  items: readonly T[],
  getId: (item: T) => string,
  ids: readonly string[] | null | undefined,
): T[] {
  if (!ids) return items as T[]

  const byId = new Map(items.map((item) => [getId(item), item]))
  const arranged: T[] = []
  ids.forEach((id) => {
    const item = byId.get(id)
    if (item) {
      arranged.push(item)
      byId.delete(id)
    }
  })
  // Map 保持插入顺序，剩下的就是新增行，按原有顺序追加。
  byId.forEach((item) => arranged.push(item))
  return arranged
}
