import { batchAssignProducts, deleteWarehouseCategory, updateWarehouseCategory } from './warehouseCategoryService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

async function assertRejects(execute: () => Promise<unknown>, expectedMessage: string, label: string) {
  try {
    await execute()
  } catch (error) {
    assertEqual(error instanceof Error ? error.message : String(error), expectedMessage, label)
    return
  }

  throw new Error(`${label}。Expected promise to reject`)
}

const originalFetch = globalThis.fetch

try {
  // 编辑分类：后端 DTO 要求请求体带 CategoryGUID（模型校验先于控制器执行），缺失会直接 400。
  let capturedUpdate: { url: string; method?: string; body: Record<string, unknown> } | null = null
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUpdate = { url: String(input), method: init?.method, body: JSON.parse(String(init?.body ?? '{}')) }
    return new Response(JSON.stringify({
      success: true,
      data: { categoryGUID: 'cat-guid-9', categoryName: 'Renamed', isActive: true },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  await updateWarehouseCategory('cat-guid-9', { categoryName: 'Renamed', chineseName: '改名', isActive: true })
  const update = capturedUpdate as { url: string; method?: string; body: Record<string, unknown> } | null
  assertEqual(update?.method, 'PUT', '编辑分类应使用 PUT')
  assertEqual(update?.url.endsWith('/api/react/v1/warehouse-categories/cat-guid-9'), true, '编辑分类的路由应带分类 GUID')
  assertEqual(update?.body.categoryGuid, 'cat-guid-9', '编辑分类的请求体必须带 categoryGuid，否则后端模型校验返回 400')
  assertEqual(update?.body.categoryName, 'Renamed', '编辑分类的请求体应保留分类名称')

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: { affected: 3 },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  const affected = await batchAssignProducts('cat-guid-1', ['product-1', 'product-2', 'product-3'])
  assertEqual(affected, 3, '批量分配接口应返回后端实际影响商品数')

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: false,
    message: '后端拒绝批量更新',
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  await assertRejects(
    () => batchAssignProducts('cat-guid-1', ['product-1']),
    '后端拒绝批量更新',
    '批量分配接口应抛出后端业务失败消息',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    isSuccess: false,
    message: '后端返回 isSuccess 失败',
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  await assertRejects(
    () => batchAssignProducts('cat-guid-1', ['product-1']),
    '后端返回 isSuccess 失败',
    '批量分配接口应兼容 isSuccess=false 的业务失败消息',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  assertEqual(
    await deleteWarehouseCategory('cat-guid-1'),
    true,
    '删除接口成功响应没有 data 时也应返回成功',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: false,
    message: '该分类下存在关联商品',
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  await assertRejects(
    () => deleteWarehouseCategory('cat-guid-1'),
    '该分类下存在关联商品',
    '删除接口应抛出后端业务失败消息',
  )
} finally {
  globalThis.fetch = originalFetch
}

console.log('warehouseCategoryService.batchAssign.test: ok')
