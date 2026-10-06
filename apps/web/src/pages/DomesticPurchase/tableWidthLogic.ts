// 国内采购列表页（国内供应商 / 国内商品）共用的列宽分配与序号逻辑，全部是无副作用的纯函数，便于单测。
//
// 背景：旧写法让「供应商 / 商品」这类列不设宽度，去吃固定列之外的全部剩余宽度；
// 笔记本宽度下刚好合适，但在大屏上这一列会被拉到上千像素，其它列被挤到屏幕右侧之外。
// 新写法：这类列按「容器宽度 - 固定列合计」在 [min, max] 之间取值，封顶之后多出来的宽度不再给它，
// 而是由没有设宽度的「操作」列吸收（按钮靠右对齐），所以大屏上不会再出现一列特别宽的情况。

export interface CappedColumn {
  /** 最窄宽度：1280 视口下也要保证的宽度。 */
  min: number
  /** 最宽宽度：再宽就不再给这一列了。 */
  max: number
  /** 剩余空间在多个弹性列之间的分配权重（同一张表里只有相对大小有意义）。 */
  weight: number
}

/**
 * 把「可分配的宽度」按权重分给若干个有上下限的列，返回每列的宽度（像素，整数）。
 * - 每列至少拿到 min；可分配宽度不够时就都停在 min（由调用方的 scroll.x 保证表格不会被压扁）；
 * - 富余的宽度按权重分配，某列到了 max 就封顶，剩下的继续分给还没封顶的列；
 * - 因取整丢掉的零头（最多几像素）以及全部封顶后的富余，都不分配——交给表格里没设宽度的列吸收。
 */
export function allocateCappedColumns(available: number, columns: readonly CappedColumn[]): number[] {
  const widths = columns.map((column) => column.min)
  let remaining = Math.max(0, Math.floor(available) - widths.reduce((sum, width) => sum + width, 0))
  let open = columns.map((_, index) => index)

  while (remaining > 0 && open.length > 0) {
    const totalWeight = open.reduce((sum, index) => sum + columns[index].weight, 0)
    if (totalWeight <= 0) {
      break
    }
    let used = 0
    const stillOpen: number[] = []
    for (const index of open) {
      const room = columns[index].max - widths[index]
      const share = Math.min(room, Math.floor((remaining * columns[index].weight) / totalWeight))
      widths[index] += share
      used += share
      if (widths[index] < columns[index].max) {
        stillOpen.push(index)
      }
    }
    // 取整后一份都分不出去了，停止，避免死循环。
    if (used === 0) {
      break
    }
    remaining -= used
    open = stillOpen
  }
  return widths
}

/**
 * 列表「序号」：跨页连续编号（第 2 页每页 50 条时，第一行是 51），而不是每页都从 1 开始。
 * 总数很大时（如 2 万多件商品）序号会到 5 位，序号列的宽度要按 5 位数预留。
 */
export function rowSerialNumber(page: number, pageSize: number, index: number): number {
  const safePage = Number.isFinite(page) && page > 0 ? Math.floor(page) : 1
  const safeSize = Number.isFinite(pageSize) && pageSize > 0 ? Math.floor(pageSize) : 0
  return (safePage - 1) * safeSize + index + 1
}
