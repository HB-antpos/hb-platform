import { readFileSync } from 'node:fs'
import path from 'node:path'

import type { SupplyNoticeInput } from '../../../types/supplyNotice'

// 仓库商品管理：编辑弹窗「是否上架」与批量修改「是否上架」改为下架时，必须先填写供货说明，
// 说明随 full-update / batch-update(jobs) 同一请求提交（与批量上下架一致）。

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

const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Products/index.tsx'), 'utf8')

assert(
  pageSource.includes("import { requiresDelistSupplyNotice } from '../../../components/SupplyNotice/delistSupplyNoticeGate'"),
  '仓库商品页应复用统一的下架说明判定',
)

// 0. 说明弹窗提交必须按下架来源分发：行上开关只下架被点击的行，不能按勾选数量推断
// （回归：只勾选了 Y 却点 X 的开关下架时，旧逻辑会把 Y 下架、X 不动）。
const submitSection = extractSection(pageSource, 'const handleSupplyNoticeSubmit = async (notice: SupplyNoticeInput) => {', 'const handleOpenSetItems')
assert(!submitSection.includes('selectedRowKeys.length'), '说明弹窗提交不得按勾选数量推断下架对象')
assert(submitSection.includes("origin === 'rowToggle'"), '说明弹窗提交应识别行上开关来源')
assertBefore(submitSection, "origin === 'rowToggle'", 'handleToggleSingleActive(record, false, notice)', '行上开关来源应只下架被点击的那一行')
assert(pageSource.includes("productCodes: [record.productCode], origin: 'rowToggle' }"), '行上开关打开说明弹窗时应记录来源 rowToggle')
assert(pageSource.includes("productCodes: selectedRowKeys.map(String), origin: 'batchToggle' }"), '批量上下架打开说明弹窗时应记录来源 batchToggle')

// 1. 编辑弹窗：从上架改为下架才弹说明；弹窗在提交 full-update 之前拦截。
const handleSaveSection = extractSection(pageSource, 'const handleSave = async (supplyNotice?: SupplyNoticeInput) => {', 'const getInlineCellKey')
const editGate = "if (!supplyNotice && requiresDelistSupplyNotice(values.isActive, editingItem.isActive)) {"
assert(handleSaveSection.includes(editGate), '编辑弹窗保存应按「原状态 → 新状态」判定是否需要先填写供货说明')
assert(
  handleSaveSection.includes("setSupplyNoticeTarget({ mode: 'delist', productCodes: [editingItem.productCode], origin: 'editModal' });"),
  '编辑弹窗改为下架时应以 editModal 来源打开供货说明弹窗',
)
assertBefore(handleSaveSection, editGate, 'await updateWarehouseProductFull(', '供货说明必须在 full-update 请求之前填写')
assertBefore(handleSaveSection, "origin: 'editModal' });", 'setSaving(true);', '打开说明弹窗后应直接返回，不进入保存')
assert(
  handleSaveSection.includes('...(supplyNotice && !values.isActive ? { supplyNotice } : {}),'),
  'full-update 请求应在下架时带上供货说明',
)

// 2. 批量修改：把「是否上架」设为下架时先弹说明，再二次确认并提交后台任务。
const batchEditSaveSection = extractSection(pageSource, 'const handleBatchEditSave = async (supplyNotice?: SupplyNoticeInput) => {', 'const handleToggleSingleActive')
const batchGate = 'if (!supplyNotice && requiresDelistSupplyNotice(values.isActive)) {'
assert(batchEditSaveSection.includes(batchGate), '批量修改应在「是否上架」设为下架时要求先填写供货说明')
assert(
  batchEditSaveSection.includes("setSupplyNoticeTarget({ mode: 'delist', productCodes: selectedRowKeys.map(String), origin: 'batchEdit' });"),
  '批量修改设为下架时应以 batchEdit 来源打开供货说明弹窗',
)
assertBefore(batchEditSaveSection, batchGate, 'Modal.confirm({', '供货说明必须在批量修改二次确认之前填写')
assert(
  batchEditSaveSection.includes('...(supplyNotice && values.isActive === false ? { supplyNotice } : {}),'),
  '批量修改提交选项应在设为下架时带上供货说明',
)
const submitBatchEditSection = extractSection(pageSource, 'const submitBatchEdit = async (', 'const submitBatchSuggestedDiscountOnly')
assert(submitBatchEditSection.includes('supplyNotice?: SupplyNoticeInput;'), 'submitBatchEdit 的选项类型应包含供货说明')
assert(
  submitBatchEditSection.includes('await createWarehouseProductBatchUpdateJob(items, options)'),
  '批量修改应把含供货说明的选项整体交给后台任务接口',
)

// 3. 说明弹窗提交：按来源分发回原保存流程。
const noticeSubmitSection = extractSection(pageSource, 'const handleSupplyNoticeSubmit = async (notice: SupplyNoticeInput) => {', 'const handleOpenSetItems')
assert(
  noticeSubmitSection.includes("if (mode === 'delist' && origin === 'editModal') {") && noticeSubmitSection.includes('await handleSave(notice);'),
  '编辑弹窗来源的说明应带回 handleSave 继续保存',
)
assert(
  noticeSubmitSection.includes("else if (mode === 'delist' && origin === 'batchEdit') {") && noticeSubmitSection.includes('await handleBatchEditSave(notice);'),
  '批量修改来源的说明应带回 handleBatchEditSave 继续提交',
)
assertBefore(noticeSubmitSection, "origin === 'editModal'", "origin === 'rowToggle'", '编辑弹窗、批量修改的来源分发必须先于行上开关与批量上下架')

// 4. 请求体：三个接口都要把说明原样提交给后端。
const notice: SupplyNoticeInput = {
  supplyPlan: 'WillRestock',
  expectedPrecision: 'Range',
  expectedFrom: '2099-10-05',
  expectedTo: '2099-10-10',
  storeFacingNote: '运输途中',
}
const captured: { url: string; method?: string; body: Record<string, unknown> }[] = []
const originalFetch = globalThis.fetch
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  captured.push({
    url: String(input),
    method: init?.method,
    body: typeof init?.body === 'string' ? JSON.parse(init.body) as Record<string, unknown> : {},
  })
  return new Response(JSON.stringify({ success: true, data: { jobId: 'job-1', status: 'Queued' } }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  const service = await import('../../../services/warehouseProductService')

  await service.updateWarehouseProductFull('P-1', { isActive: false, supplyNotice: notice })
  await service.updateWarehouseProductFull('P-2', { isActive: true })
  await service.batchUpdateWarehouseProducts([{ ProductCode: 'P-3', IsActive: false }], { supplyNotice: notice })
  await service.batchUpdateWarehouseProducts([{ ProductCode: 'P-4', IsActive: true }])
  await service.createWarehouseProductBatchUpdateJob([{ ProductCode: 'P-5', IsActive: false }], { supplyNotice: notice })
  await service.createWarehouseProductBatchUpdateJob([{ ProductCode: 'P-6', MinOrderQuantity: 6 }])

  assert(captured.length === 6, `应发出 6 个请求，实际 ${captured.length}`)
  const [fullDelist, fullRelist, syncDelist, syncOther, jobDelist, jobOther] = captured
  assert(fullDelist.method === 'PUT' && fullDelist.url.endsWith('/api/react/v1/product-warehouse/P-1/full-update'), 'full-update 路径或方法不正确')
  assert(JSON.stringify(fullDelist.body.SupplyNotice) === JSON.stringify(notice), 'full-update 下架时应提交 SupplyNotice')
  assert(!('SupplyNotice' in fullRelist.body), '未带说明的 full-update 不应出现 SupplyNotice 字段')
  assert(syncDelist.url.endsWith('/api/react/v1/product-warehouse/batch-update'), '同步批量修改路径不正确')
  assert(JSON.stringify(syncDelist.body.SupplyNotice) === JSON.stringify(notice), '同步批量修改应提交 SupplyNotice')
  assert(!('SupplyNotice' in syncOther.body), '未带说明的同步批量修改不应出现 SupplyNotice 字段')
  assert(jobDelist.url.endsWith('/api/react/v1/product-warehouse/batch-update/jobs'), '批量修改后台任务路径不正确')
  assert(JSON.stringify(jobDelist.body.SupplyNotice) === JSON.stringify(notice), '批量修改后台任务应提交 SupplyNotice')
  assert(!('SupplyNotice' in jobOther.body), '未带说明的批量修改后台任务不应出现 SupplyNotice 字段')
} finally {
  globalThis.fetch = originalFetch
}

console.log('WarehouseProducts.delistSupplyNotice.uiContract.test: ok')
