// 分店列拖拽状态：表头的 ⋮⋮ 把手与「分店」下拉里「已显示」每行的 ⋮⋮ 共用同一份（原生 HTML5 拖放，不引入依赖）。
// 状态同时放 ref（拖放事件里读最新值，不受渲染延迟影响）和 state（驱动落点线 / 淡化等渲染）。
import { useCallback, useRef, useState } from 'react'

export interface ColumnDrag {
  /** 正在拖的分店；没在拖为 null */
  guid: string | null
  /** 落点：在显示列的第几列之前插入（0..列数）；还没移到有效位置为 null */
  insertIndex: number | null
  begin: (guid: string) => void
  /** 拖到某个落点上方（null = 离开有效区域） */
  hover: (insertIndex: number | null) => void
  /** 松手：按当前落点提交；返回是否触发了提交 */
  drop: () => boolean
  /** 拖拽结束（松手后或被取消）：清状态 */
  end: () => void
}

export function useColumnDrag(onCommit: (guid: string, insertIndex: number) => void): ColumnDrag {
  const [state, setState] = useState<{ guid: string | null; insertIndex: number | null }>({ guid: null, insertIndex: null })
  const latest = useRef(state)
  const commitRef = useRef(onCommit)
  commitRef.current = onCommit

  const update = useCallback((next: { guid: string | null; insertIndex: number | null }) => {
    latest.current = next
    setState((current) => (current.guid === next.guid && current.insertIndex === next.insertIndex ? current : next))
  }, [])

  const begin = useCallback((guid: string) => update({ guid, insertIndex: null }), [update])
  const hover = useCallback(
    (insertIndex: number | null) => {
      if (latest.current.guid === null) return
      update({ guid: latest.current.guid, insertIndex })
    },
    [update],
  )
  const end = useCallback(() => update({ guid: null, insertIndex: null }), [update])
  const drop = useCallback(() => {
    const { guid, insertIndex } = latest.current
    update({ guid: null, insertIndex: null })
    if (guid === null || insertIndex === null) return false
    commitRef.current(guid, insertIndex)
    return true
  }, [update])

  return { guid: state.guid, insertIndex: state.insertIndex, begin, hover, drop, end }
}
