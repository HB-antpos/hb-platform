import { useCallback, useMemo, useState } from 'react'
import type { TableProps } from 'antd'
import { arrangeByIds, sortItemIds } from '../utils/snapshotSort'
import type { SnapshotSortOrder, SnapshotSortState } from '../utils/snapshotSort'

/**
 * antd 表格列头排序的「点一次排一次」版本，见 utils/snapshotSort.ts 的说明。
 * 用法：列上写 `sorter: true, sortOrder: sortOrderOf('字段名')`，表格传 `dataSource={displayItems}` 与 `onChange={onTableChange}`。
 * getId / getValue 每次渲染都可以是新函数：点击时用的是最新一次渲染的闭包。
 */
export function useSnapshotSort<T>({
  items,
  getId,
  getValue = (item, field) => (item as Record<string, unknown>)[field],
}: {
  items: readonly T[]
  getId: (item: T) => string
  getValue?: (item: T, field: string) => unknown
}) {
  const [sort, setSort] = useState<SnapshotSortState | null>(null)

  const displayItems = useMemo(() => arrangeByIds(items, getId, sort?.ids), [items, sort]) // eslint-disable-line react-hooks/exhaustive-deps

  const sortOrderOf = useCallback(
    (field: string): SnapshotSortOrder | null => (sort?.field === field ? sort.order : null),
    [sort],
  )

  const onTableChange: NonNullable<TableProps<T>['onChange']> = (_pagination, _filters, sorter) => {
    const current = Array.isArray(sorter) ? sorter[0] : sorter
    if (!current?.order || typeof current.field !== 'string') {
      setSort(null)
      return
    }
    // 以当前展示的顺序为基准排序，相等的行保持用户眼前看到的相对顺序。
    setSort({
      field: current.field,
      order: current.order,
      ids: sortItemIds(displayItems, getId, getValue, current.field, current.order),
    })
  }

  const resetSort = useCallback(() => setSort(null), [])

  return { displayItems, sortOrderOf, onTableChange, resetSort }
}
