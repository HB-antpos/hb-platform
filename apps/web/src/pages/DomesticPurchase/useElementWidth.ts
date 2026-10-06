import { useLayoutEffect, useState } from 'react'
import type { RefObject } from 'react'

/**
 * 读取某个元素的内容宽度，并在它变化（窗口缩放、侧栏折叠）时更新。
 * 列表页用它拿「表格可用宽度」，据此给供应商 / 商品这类弹性列分配有上限的宽度（见 tableWidthLogic.ts）。
 * - 首次渲染拿不到真实宽度（还没挂载 / 服务端渲染）时返回 fallback；
 * - 宽度为 0（元素被隐藏，如 keepAlive 缓存的标签页）时保留上一次的值，避免列宽被算成最小宽度后又跳回来。
 */
export function useElementWidth(ref: RefObject<HTMLElement | null>, fallback: number): number {
  const [width, setWidth] = useState(fallback)

  useLayoutEffect(() => {
    const element = ref.current
    if (!element) {
      return undefined
    }
    const update = () => {
      const next = Math.floor(element.getBoundingClientRect().width)
      if (next > 0) {
        setWidth(next)
      }
    }
    update()
    if (typeof ResizeObserver === 'undefined') {
      return undefined
    }
    const observer = new ResizeObserver(update)
    observer.observe(element)
    return () => observer.disconnect()
  }, [ref])

  return width
}
