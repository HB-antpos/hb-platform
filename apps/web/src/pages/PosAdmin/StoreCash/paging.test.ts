import assert from 'node:assert/strict'
import type { CashPaged } from '../../../types/storeCash'
import { fetchAllPages, PAGE_FETCH_LIMIT, PAGE_FETCH_MAX_ROWS, type FetchAllProgress } from './paging'

// 分页拉全量：limit=200 循环取完、5000 行上限、去重、短页即止、中止。

interface Row { id: string }

function makeSource(total: number) {
  const rows: Row[] = Array.from({ length: total }, (_, index) => ({ id: `r${index}` }))
  const calls: { limit: number; offset: number }[] = []
  const load = async (limit: number, offset: number): Promise<CashPaged<Row>> => {
    calls.push({ limit, offset })
    return { items: rows.slice(offset, offset + limit), total: rows.length }
  }
  return { rows, calls, load }
}

assert.equal(PAGE_FETCH_LIMIT, 200, '服务端单页上限 200')
assert.equal(PAGE_FETCH_MAX_ROWS, 5000, '导出上限 5000 行')

// 1) 正常取完：450 行 = 3 页（200 + 200 + 50），进度逐页回报。
{
  const source = makeSource(450)
  const progress: FetchAllProgress[] = []
  const result = await fetchAllPages(source.load, { onProgress: (value) => progress.push(value), keyOf: (row) => row.id })
  assert.equal(result.status, 'ok')
  if (result.status === 'ok') {
    assert.equal(result.items.length, 450)
    assert.equal(result.total, 450)
    assert.deepEqual(result.items.map((row) => row.id), source.rows.map((row) => row.id), '顺序与服务端一致')
  }
  assert.deepEqual(source.calls, [{ limit: 200, offset: 0 }, { limit: 200, offset: 200 }, { limit: 200, offset: 400 }])
  assert.deepEqual(progress, [{ fetched: 200, total: 450 }, { fetched: 400, total: 450 }, { fetched: 450, total: 450 }])
}

// 2) 恰好整页：400 行取 2 页就够（总数已满足），不多请求一页。
{
  const source = makeSource(400)
  const result = await fetchAllPages(source.load)
  assert.equal(result.status === 'ok' && result.items.length, 400)
  assert.equal(source.calls.length, 2)
}

// 3) 空结果：只请求一次。
{
  const source = makeSource(0)
  const result = await fetchAllPages(source.load)
  assert.deepEqual(result, { status: 'ok', items: [], total: 0 })
  assert.equal(source.calls.length, 1)
}

// 4) 超过上限：第一页拿到总数后立刻停止，不再继续取。
{
  const source = makeSource(5001)
  const result = await fetchAllPages(source.load)
  assert.deepEqual(result, { status: 'tooMany', total: 5001, maxRows: 5000 })
  assert.equal(source.calls.length, 1, '超限时只请求第一页')
  const exact = makeSource(5000)
  const exactResult = await fetchAllPages(exact.load)
  assert.equal(exactResult.status === 'ok' && exactResult.items.length, 5000, '等于上限允许')
  assert.equal(exact.calls.length, 25)
}

// 5) 自定义上限与页大小；页大小超过 200 时按 200 取。
{
  const source = makeSource(30)
  const result = await fetchAllPages(source.load, { pageSize: 1000, maxRows: 20 })
  assert.deepEqual(result, { status: 'tooMany', total: 30, maxRows: 20 })
  assert.equal(source.calls[0].limit, 200)
}

// 6) 取的过程中有人新增记录：偏移错位导致重复，按主键去重；总数变大超限时停止。
{
  let total = 250
  const calls: number[] = []
  const load = async (limit: number, offset: number): Promise<CashPaged<Row>> => {
    calls.push(offset)
    // 第二页时前面插入了 1 条新记录，原第 200 条被挤到第 201 位，第二页开头重复。
    const rows = Array.from({ length: total }, (_, index) => ({ id: `r${offset === 0 ? index : index - 1}` }))
    if (offset > 0) total = 251
    return { items: rows.slice(offset, offset + limit), total }
  }
  const result = await fetchAllPages(load, { keyOf: (row) => row.id })
  assert.equal(result.status, 'ok')
  if (result.status === 'ok') {
    assert.equal(new Set(result.items.map((row) => row.id)).size, result.items.length, '去重后没有重复主键')
  }
}

// 7) 服务端返回的总数比实际多（中途有记录被作废）：短页即止，不死循环；total 以实际取到的为准。
{
  const calls: number[] = []
  const load = async (limit: number, offset: number): Promise<CashPaged<Row>> => {
    calls.push(offset)
    const rows = Array.from({ length: 210 }, (_, index) => ({ id: `r${index}` }))
    return { items: rows.slice(offset, offset + limit), total: 999 }
  }
  const result = await fetchAllPages(load)
  assert.deepEqual(calls, [0, 200])
  assert.equal(result.status === 'ok' && result.total, 210)
}

// 8) 服务端一直返回满页且总数不收敛：页数上限兜底，不会无限请求。
{
  let calls = 0
  const load = async (limit: number): Promise<CashPaged<Row>> => {
    calls += 1
    return { items: Array.from({ length: limit }, (_, index) => ({ id: `${calls}-${index}` })), total: 4000 }
  }
  const result = await fetchAllPages(load, { maxRows: 4000 })
  assert.ok(calls <= 21, `请求次数有上限，实际 ${calls}`)
  assert.equal(result.status, 'ok')
}

// 9) 中止：抛 AbortError，且不再发后续请求。
{
  const controller = new AbortController()
  const source = makeSource(1000)
  const load = async (limit: number, offset: number) => {
    const page = await source.load(limit, offset)
    if (offset === 200) controller.abort()
    return page
  }
  await assert.rejects(() => fetchAllPages(load, { signal: controller.signal }), (error: Error) => error.name === 'AbortError')
  assert.equal(source.calls.length, 2, '中止后不再请求')
  const aborted = new AbortController()
  aborted.abort()
  const untouched = makeSource(10)
  await assert.rejects(() => fetchAllPages(untouched.load, { signal: aborted.signal }), (error: Error) => error.name === 'AbortError')
  assert.equal(untouched.calls.length, 0, '已中止时一次都不请求')
}

console.log('storeCash paging.test: ok')
