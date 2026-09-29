import { readFileSync } from 'node:fs'
import path from 'node:path'

import type { SupplyNoticeInput } from '../../../types/supplyNotice'

// 分店订货明细：行上的上/下架按钮与批量改状态，下架前必须先填写供货说明，说明随下架同一请求提交；上架不需要。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function extractSection(source: string, startText: string, endText: string) {
  const startIndex = source.indexOf(startText)
  assert(startIndex >= 0, `未找到代码片段：${startText}`)
  const endIndex = source.indexOf(endText, startIndex + startText.length)
  assert(endIndex >= 0, `未找到结束片段：${endText}`)
  return source.slice(startIndex, endIndex)
}

function assertBefore(section: string, first: string, second: string, message: string) {
  const firstIndex = section.indexOf(first)
  const secondIndex = section.indexOf(second)
  assert(firstIndex >= 0, `${message}（未找到：${first}）`)
  assert(secondIndex >= 0, `${message}（未找到：${second}）`)
  assert(firstIndex < secondIndex, message)
}

const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/Detail.tsx'), 'utf8')

assert(
  pageSource.includes("import SupplyNoticeModal from '../../../components/SupplyNotice/SupplyNoticeModal'"),
  '订货明细页应复用仓库端的 SupplyNoticeModal',
)
assert(
  pageSource.includes("import { requiresDelistSupplyNotice } from '../../../components/SupplyNotice/delistSupplyNoticeGate'"),
  '订货明细页应复用统一的下架说明判定',
)

// 1. 行上的上/下架按钮。
const toggleSection = extractSection(
  pageSource,
  'const handleToggleLineStatus = async (line: StoreOrderDetailLine, supplyNotice?: SupplyNoticeInput) => {',
  'const handleBatchConfirm',
)
const lineGate = 'if (!supplyNotice && requiresDelistSupplyNotice(nextIsActive, line.isActive)) {'
assert(toggleSection.includes(lineGate), '行开关下架前应判定是否需要先填写供货说明')
assert(
  toggleSection.includes("setSupplyNoticeTarget({ kind: 'line', productCodes: [line.productCode], line })"),
  '行开关下架时应打开供货说明弹窗',
)
assertBefore(toggleSection, lineGate, 'await updateStoreOrderProductStatus({', '供货说明必须在行状态请求之前填写')
assertBefore(toggleSection, lineGate, 'setLineActionLoading(true)', '打开说明弹窗后应直接返回，不进入提交')
assert(
  toggleSection.includes('...(supplyNotice && !nextIsActive ? { supplyNotice } : {}),'),
  '行状态请求应在下架时带上供货说明',
)

// 2. 批量改状态。
const batchSection = extractSection(
  pageSource,
  'const handleBatchConfirm = async (payload: BatchEditPayload, supplyNotice?: SupplyNoticeInput) => {',
  'const handleSupplyNoticeSubmit',
)
const batchGate = "if (payload.type === 'status' && !supplyNotice && requiresDelistSupplyNotice(payload.isActive ?? true)) {"
assert(batchSection.includes(batchGate), '批量改状态为下架时应要求先填写供货说明')
assert(batchSection.includes("kind: 'batch',"), '批量改状态下架时应以 batch 来源打开供货说明弹窗')
assertBefore(batchSection, batchGate, 'await batchUpdateStoreOrderProductStatus({', '供货说明必须在批量状态请求之前填写')
assertBefore(batchSection, batchGate, 'setBatchLoading(true)', '打开说明弹窗后应直接返回，不进入提交')
assert(
  batchSection.includes('...(supplyNotice && !nextIsActive ? { supplyNotice } : {}),'),
  '批量状态请求应在下架时带上供货说明',
)

// 3. 说明弹窗提交：按来源分发，取消不提交。
const noticeSubmitSection = extractSection(
  pageSource,
  'const handleSupplyNoticeSubmit = async (notice: SupplyNoticeInput) => {',
  'const handleResetDetailDefaultSort',
)
assert(
  noticeSubmitSection.includes('await handleToggleLineStatus(supplyNoticeTarget.line, notice)') &&
    noticeSubmitSection.includes('await handleBatchConfirm(supplyNoticeTarget.payload, notice)'),
  '说明弹窗提交应按来源带说明回到行开关或批量改状态',
)
const modalSection = extractSection(pageSource, '<SupplyNoticeModal', '/>')
assert(
  modalSection.includes('open={Boolean(supplyNoticeTarget)}') &&
    modalSection.includes('mode="delist"') &&
    modalSection.includes('onCancel={() => setSupplyNoticeTarget(null)}') &&
    modalSection.includes('onSubmit={handleSupplyNoticeSubmit}'),
  '订货明细页应渲染下架模式的供货说明弹窗，取消即关闭且不提交',
)

// 4. 请求体：两个状态接口都要把说明原样提交给后端。
const notice: SupplyNoticeInput = {
  supplyPlan: 'Discontinued',
  expectedPrecision: 'Unknown',
  storeFacingNote: '厂家停产',
}
const captured: { url: string; body: Record<string, unknown> }[] = []
const originalFetch = globalThis.fetch
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  captured.push({
    url: String(input),
    body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : {},
  })
  return new Response(JSON.stringify({ success: true, data: true }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  const service = await import('../../../services/storeOrderService')

  await service.updateStoreOrderProductStatus({ productCode: 'P-1', isActive: false, supplyNotice: notice })
  await service.batchUpdateStoreOrderProductStatus({ productCodes: ['P-2', 'P-3'], isActive: false, supplyNotice: notice })
  await service.updateStoreOrderProductStatus({ productCode: 'P-4', isActive: true })

  assert(captured.length === 3, `应发出 3 个请求，实际 ${captured.length}`)
  const [lineDelist, batchDelist, lineRelist] = captured
  assert(lineDelist.url.endsWith('/product/status'), '行状态接口路径不正确')
  assert(JSON.stringify(lineDelist.body.supplyNotice) === JSON.stringify(notice), '行状态接口下架时应提交 supplyNotice')
  assert(batchDelist.url.endsWith('/product/batch-status'), '批量状态接口路径不正确')
  assert(JSON.stringify(batchDelist.body.supplyNotice) === JSON.stringify(notice), '批量状态接口下架时应提交 supplyNotice')
  assert(!('supplyNotice' in lineRelist.body), '上架不应提交 supplyNotice')
} finally {
  globalThis.fetch = originalFetch
}

console.log('storeOrderDelistSupplyNotice.uiContract.test: ok')
