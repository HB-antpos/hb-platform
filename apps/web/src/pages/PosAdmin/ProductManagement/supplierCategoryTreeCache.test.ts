import assert from 'node:assert/strict'
import type { LocalSupplierCategoryNode } from '../../../types/localSupplierCategory'
import { createSupplierCategoryTreeCache } from './supplierCategoryTreeCache'

function node(categoryGuid: string, children: LocalSupplierCategoryNode[] = []): LocalSupplierCategoryNode {
  return { categoryGuid, name: categoryGuid, depth: 0, isPromotional: false, isActive: true, productCount: 0, children }
}

type Deferred = { resolve: (nodes: LocalSupplierCategoryNode[]) => void; reject: (error: unknown) => void }

function createControlledFetcher() {
  const calls: string[] = []
  const pending: Deferred[] = []
  const fetcher = (supplierCode: string) => {
    calls.push(supplierCode)
    return new Promise<LocalSupplierCategoryNode[]>((resolve, reject) => {
      pending.push({ resolve, reject })
    })
  }
  return { calls, pending, fetcher }
}

async function flush() {
  await new Promise((resolve) => setTimeout(resolve, 0))
}

// 并发 ensure 合并成一个请求；完成后再 ensure 直接用缓存
{
  const { calls, pending, fetcher } = createControlledFetcher()
  const cache = createSupplierCategoryTreeCache(fetcher)
  let notifications = 0
  cache.subscribe(() => { notifications += 1 })
  const first = cache.ensure(' 240 ')
  const second = cache.ensure('240')
  assert.deepEqual(calls, ['240'], '供应商编码归一化后只发一次请求')
  assert.equal(cache.get('240')?.status, 'loading')
  assert.equal(cache.get('240')?.loaded, false)
  pending[0].resolve([node('a')])
  assert.deepEqual((await first).map((item) => item.categoryGuid), ['a'])
  assert.deepEqual((await second).map((item) => item.categoryGuid), ['a'])
  assert.equal(cache.get('240')?.status, 'ready')
  assert.equal(cache.get('240')?.loaded, true)
  await cache.ensure('240')
  assert.equal(calls.length, 1, '已加载不重复请求')
  assert.ok(notifications >= 2, '加载开始与完成都应通知订阅者')
  assert.equal(cache.get(undefined), undefined)
  assert.deepEqual(await cache.ensure(''), [], '空编码不请求')
}

// 失败后记为 error，再次 ensure 会重新请求；刷新期间保留旧数据
{
  const { calls, pending, fetcher } = createControlledFetcher()
  const cache = createSupplierCategoryTreeCache(fetcher)
  const failed = cache.ensure('201')
  pending[0].reject(new Error('boom'))
  await assert.rejects(failed, /boom/)
  assert.equal(cache.get('201')?.status, 'error')
  assert.equal(cache.get('201')?.loaded, false)

  const retry = cache.ensure('201')
  assert.equal(calls.length, 2)
  pending[1].resolve([node('x')])
  await retry

  const refresh = cache.reload('201')
  assert.equal(cache.get('201')?.status, 'loading')
  assert.equal(cache.get('201')?.loaded, true, '刷新期间仍视为已加载')
  assert.deepEqual(cache.get('201')?.nodes.map((item) => item.categoryGuid), ['x'])
  pending[2].reject(new Error('refresh failed'))
  await assert.rejects(refresh)
  assert.equal(cache.get('201')?.status, 'error')
  assert.deepEqual(cache.get('201')?.nodes.map((item) => item.categoryGuid), ['x'], '刷新失败保留旧数据')
}

// 过期响应被丢弃：reload 取代在途请求、invalidate 让在途结果作废
{
  const { pending, fetcher } = createControlledFetcher()
  const cache = createSupplierCategoryTreeCache(fetcher)
  const stale = cache.ensure('227')
  const fresh = cache.reload('227')
  pending[1].resolve([node('fresh')])
  await fresh
  pending[0].resolve([node('stale')])
  await stale
  await flush()
  assert.deepEqual(cache.get('227')?.nodes.map((item) => item.categoryGuid), ['fresh'], '先发后至的旧响应不能覆盖新数据')

  const inflight = cache.reload('227')
  cache.invalidate('227')
  assert.equal(cache.get('227'), undefined)
  pending[2].resolve([node('late')])
  await inflight
  assert.equal(cache.get('227'), undefined, 'invalidate 之后到达的响应应丢弃')

  cache.ensure('243')
  cache.invalidate()
  assert.equal(cache.get('243'), undefined, '无参 invalidate 清空全部')
}

// patchNode 乐观更新：不区分 GUID 大小写，只替换命中的分支
{
  const { pending, fetcher } = createControlledFetcher()
  const cache = createSupplierCategoryTreeCache(fetcher)
  const loading = cache.ensure('240')
  const untouched = node('other')
  pending[0].resolve([node('ROOT', [node('Leaf')]), untouched])
  await loading
  cache.patchNode('240', 'leaf', { isPromotional: true, promotionalSource: 'manual' })
  const nodes = cache.get('240')?.nodes ?? []
  assert.equal(nodes[0].children[0].isPromotional, true)
  assert.equal(nodes[0].children[0].promotionalSource, 'manual')
  assert.equal(nodes[1], untouched, '未命中的分支保持引用不变')
  const before = cache.get('240')
  cache.patchNode('240', 'missing', { isPromotional: true })
  assert.equal(cache.get('240'), before, '未命中时不产生新快照')
  cache.patchNode('999', 'leaf', { isPromotional: true })
  assert.equal(cache.get('999'), undefined)
}

// 取消订阅后不再通知
{
  const { fetcher } = createControlledFetcher()
  const cache = createSupplierCategoryTreeCache(fetcher)
  let count = 0
  const unsubscribe = cache.subscribe(() => { count += 1 })
  unsubscribe()
  cache.ensure('240')
  assert.equal(count, 0)
}

console.log('supplierCategoryTreeCache.test: ok')
