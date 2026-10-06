import assert from 'node:assert/strict'
import { createPrefixCode, updatePrefixCode } from '../../../services/productPrefixCodeService'
import { buildPrefixPayload, buildStatusTogglePayload } from './prefixCodeRules'

// 服务层不再把 sortOrder 补成 0：表单提交的排序号要原样出现在请求体里，未填时请求体里不应有该键。
const originalFetch = globalThis.fetch
const bodies: Array<Record<string, unknown>> = []

globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
  bodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>)
  return new Response(JSON.stringify({ success: true, data: { prefixCode: 'P1', supplierCode: 'S1', prefixName: 'BX', isActive: true } }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  await createPrefixCode(buildPrefixPayload({ prefixName: 'bx', sortOrder: 10, isActive: true }, 'S1'))
  assert.equal(bodies[0].sortOrder, 10, '新增：表单里的排序号必须随请求提交')
  assert.equal(bodies[0].prefixName, 'BX', '新增：前缀保存时转大写')
  assert.equal(bodies[0].supplierCode, 'S1')

  await createPrefixCode(buildPrefixPayload({ prefixName: 'bx', sortOrder: null }, 'S1'))
  assert.equal('sortOrder' in bodies[1], false, '新增：排序未填时不能被补成 0')

  await updatePrefixCode('P1', buildPrefixPayload({ prefixName: 'bx', sortOrder: 0 }))
  assert.equal(bodies[2].sortOrder, 0, '编辑：用户明确填 0 时应提交 0')

  // 列表内切换状态：后端排序为空的前缀，切换后仍保持为空。
  await updatePrefixCode('P1', buildStatusTogglePayload({ prefixName: 'BX', prefixDescription: undefined, sortOrder: undefined }, false))
  assert.equal('sortOrder' in bodies[3], false, '切换状态不能把空排序重置为 0')
  assert.equal(bodies[3].isActive, false)
  assert.equal(bodies[3].prefixName, 'BX')
  assert.equal('supplierCode' in bodies[3], false, '编辑接口不带供应商')
} finally {
  globalThis.fetch = originalFetch
}

console.log('prefixCodePayload.test.ts: ok')
