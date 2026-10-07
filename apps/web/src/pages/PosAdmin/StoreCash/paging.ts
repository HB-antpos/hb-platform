import type { CashPaged } from '../../../types/storeCash'

/**
 * 按 limit/offset 分页把当前筛选下的全部记录取完（存款、支出的导出与支出合计用）。
 * - 每页最多 200 行（服务端上限）；总行数超过上限（默认 5000）时不再继续取，直接返回 tooMany，
 *   由界面提示缩小范围，避免一次拉几十页把浏览器和接口都拖慢；
 * - 取的过程中有人新增记录导致偏移错位时，按主键去重；某页不足一页即视为取完；
 * - signal 中止时抛 AbortError，调用方据此静默结束。
 */

export const PAGE_FETCH_LIMIT = 200
export const PAGE_FETCH_MAX_ROWS = 5000

export interface FetchAllProgress {
  fetched: number
  total: number
}

export interface FetchAllOptions<T> {
  pageSize?: number
  maxRows?: number
  signal?: AbortSignal
  onProgress?: (progress: FetchAllProgress) => void
  keyOf?: (item: T) => string
}

export type FetchAllResult<T> =
  | { status: 'ok'; items: T[]; total: number }
  | { status: 'tooMany'; total: number; maxRows: number }

export type PageLoader<T> = (limit: number, offset: number, signal?: AbortSignal) => Promise<CashPaged<T>>

function throwIfAborted(signal?: AbortSignal) {
  if (signal?.aborted) {
    const error = new Error('Aborted')
    error.name = 'AbortError'
    throw error
  }
}

export async function fetchAllPages<T>(loadPage: PageLoader<T>, options: FetchAllOptions<T> = {}): Promise<FetchAllResult<T>> {
  const pageSize = Math.max(1, Math.min(options.pageSize ?? PAGE_FETCH_LIMIT, PAGE_FETCH_LIMIT))
  const maxRows = options.maxRows ?? PAGE_FETCH_MAX_ROWS
  const { signal, onProgress, keyOf } = options
  // 页数上限：正常情况下 total ≤ maxRows 时最多 ceil(maxRows / pageSize) 页，多留一页余量防止死循环。
  const maxPages = Math.ceil(maxRows / pageSize) + 1

  throwIfAborted(signal)
  const first = await loadPage(pageSize, 0, signal)
  throwIfAborted(signal)
  let total = first.total
  if (total > maxRows) return { status: 'tooMany', total, maxRows }

  const items: T[] = []
  const seen = new Set<string>()
  const append = (page: readonly T[]) => {
    for (const item of page) {
      if (keyOf) {
        const key = keyOf(item)
        if (seen.has(key)) continue
        seen.add(key)
      }
      items.push(item)
    }
  }

  append(first.items)
  onProgress?.({ fetched: items.length, total: Math.max(total, items.length) })
  let lastPageSize = first.items.length
  let offset = pageSize
  for (let page = 1; page < maxPages && items.length < total && lastPageSize >= pageSize; page += 1) {
    throwIfAborted(signal)
    const next = await loadPage(pageSize, offset, signal)
    throwIfAborted(signal)
    // 取的过程中总数变了（有人新增或作废），以最新的为准。
    total = next.total
    if (total > maxRows) return { status: 'tooMany', total, maxRows }
    append(next.items)
    lastPageSize = next.items.length
    offset += pageSize
    onProgress?.({ fetched: items.length, total: Math.max(total, items.length) })
  }

  if (items.length > maxRows) return { status: 'tooMany', total: items.length, maxRows }
  // 实际取到的行数才是导出 / 合计的口径（中途有记录被作废时可能少于最初的总数）。
  return { status: 'ok', items, total: items.length }
}
