import { readFileSync } from 'node:fs'
import path from 'node:path'
import { syncLocalSuppliersToHq } from './localSupplierService'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const originalFetch = globalThis.fetch
let capturedUrl = ''
let capturedInit: RequestInit | undefined

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  capturedUrl = String(input)
  capturedInit = init

  return new Response(JSON.stringify({
    success: true,
    data: {
      createdCount: 1,
      updatedCount: 1,
      deactivatedCount: 0,
      skippedCount: 0,
      errors: [],
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  const result = await syncLocalSuppliersToHq(['AUS-001', 'AUS-002'])

  assert(
    capturedUrl === '/api/react/v1/local-suppliers/sync-to-hq',
    '澳洲供应商应请求独立的 HQ 同步端点',
  )
  assert(capturedInit?.method === 'POST', '澳洲供应商 HQ 同步应使用 POST')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { supplierCodes: ['AUS-001', 'AUS-002'] },
    'HQ 同步请求只应提交所选供应商代码',
  )
  assertDeepEqual(
    result,
    {
      createdCount: 1,
      updatedCount: 1,
      deactivatedCount: 0,
      skippedCount: 0,
      errors: [],
    },
    '应返回 HQ 同步统计',
  )
} finally {
  globalThis.fetch = originalFetch
}

const pageFile = path.resolve(
  process.cwd(),
  'src/pages/PosAdmin/SupplierManagement/index.tsx',
)
const pageSource = readFileSync(pageFile, 'utf8')
assert(
  pageSource.includes('rowKey="localSupplierCode"')
    && pageSource.includes('preserveSelectedRowKeys: true'),
  '供应商表格应以供应商代码保留跨页选择',
)
// 「从 HQ 同步」（HQ → HBweb）已于 2026-09-29 停用，写入 HQ 按钮只受编辑权限与选择约束。
assert(
  pageSource.includes('disabled={!canEdit || !selectedRowKeys.length}') && !pageSource.includes('syncingFromHq'),
  '无编辑权限或未选择供应商时应禁用写入 HQ',
)
assert(
  pageSource.includes('const result = await syncLocalSuppliersToHq(selectedRowKeys.map(String))'),
  '页面应只把所选供应商代码同步到 HQ',
)
assert(
  pageSource.includes('Modal.confirm({')
    && pageSource.includes("'posAdmin.suppliers.syncToHqConfirm'"),
  '写入 HQ 前应显示影响范围确认',
)
assert(
  pageSource.includes('const tableRegionRef = useRef<HTMLDivElement>(null)'),
  '供应商表格区域应通过 tableRegionRef 测量实际高度',
)
assert(
  pageSource.includes('ref={tableRegionRef}')
    && pageSource.includes("flex: 1, minHeight: 0, overflow: 'hidden'"),
  '表格区域应占满剩余高度且裁剪溢出，避免固定列进入分页条',
)
assert(
  pageSource.includes("region.querySelector('.ant-table-thead')")
    && pageSource.includes('const tableHeaderHeight = readOuterHeight(tableHeader)'),
  'scroll.y 应扣除表头外部高度（scroll.y 只包含表体）',
)
assert(
  pageSource.includes('tableBody.offsetHeight - tableBody.clientHeight'),
  'scroll.y 应扣除 .ant-table-body 横向滚动条高度',
)
assert(
  pageSource.includes('window.requestAnimationFrame')
    && pageSource.includes("window.addEventListener('resize', scheduleCalc)"),
  '高度测量应通过 requestAnimationFrame 调度并监听窗口尺寸变化',
)
assert(
  pageSource.includes('new ResizeObserver(scheduleCalc)')
    && pageSource.includes('observer.disconnect()'),
  '可用时应使用 ResizeObserver 观察相关区域并在卸载时清理',
)
assert(
  pageSource.includes('Math.abs(current - nextScrollY) > 4'),
  '仅当高度变化超过小阈值时才更新 scroll.y',
)
assert(
  pageSource.includes("position: 'relative'")
    && pageSource.includes('zIndex: 3')
    && pageSource.includes('flexShrink: 0'),
  '分页条应置顶于固定列之上且不可被压缩',
)

console.log('localSupplierService.test: ok')
