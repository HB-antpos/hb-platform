import { readFileSync } from 'node:fs'
import path from 'node:path'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

async function runTest(name: string, execute: () => void | Promise<void>): Promise<string | null> {
  try {
    await execute()
    console.log(`ok - ${name}`)
    return null
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    console.error(`not ok - ${name}`)
    console.error(reason)
    return `${name}: ${reason}`
  }
}

const pageFile = path.resolve(process.cwd(), 'src/pages/Warehouse/Containers/index.tsx')
const serviceFile = path.resolve(process.cwd(), 'src/services/containerService.ts')
const typeFile = path.resolve(process.cwd(), 'src/types/container.ts')

const pageSource = readFileSync(pageFile, 'utf8')
const serviceSource = readFileSync(serviceFile, 'utf8')
const typeSource = readFileSync(typeFile, 'utf8')
// 统一换行，避免 Windows CRLF 让源码片段定位失效。
const normalizedPageSource = pageSource.replace(/\r\n/g, '\n')

async function main() {
  const failures: string[] = []

  const stateFailure = await runTest('页面应维护列头过滤状态并通过服务端查询应用', () => {
    assert(
      pageSource.includes('const [columnFilters, setColumnFilters] = useState<ContainerColumnFilters>({})'),
      '页面应维护受控 columnFilters 状态',
    )
    // 重设计后原列头条件收进「更多筛选」，经 buildContainerListQuery 展开后随请求发到服务端。
    assert(
      pageSource.includes('const activeColumnFilters = options.columnFilters ?? columnFilters') &&
        pageSource.includes('columnFilters: activeColumnFilters') &&
        pageSource.includes('...filters.columnFilters') &&
        pageSource.includes('void requestFirstPage({ columnFilters: nextFilters })'),
      '列头过滤应随 getContainerList 请求发送到服务端，而不是只过滤当前页 dataSource',
    )
  })
  if (stateFailure) failures.push(stateFailure)

  const dateTypeFailure = await runTest('顶部日期类型应提供装柜日期且不被异步选项覆盖', () => {
    assert(
      pageSource.includes("const dateTypeOptions = ['预计到岸日期', '实际到货日期', '装柜日期']"),
      '顶部日期类型缺少装柜日期',
    )
    assert(pageSource.includes('options={dateTypeOptions.map('), '日期类型下拉应使用固定选项')
    assert(!pageSource.includes('getDateFilterOptions'), '异步选项不应覆盖日期类型下拉')
  })
  if (dateTypeFailure) failures.push(dateTypeFailure)

  const requestMappingFailure = await runTest('前端请求类型和服务映射应包含全部列头过滤字段', () => {
    const requiredTypeFields = [
      'containerNumberFilter?: string',
      'loadingDateStart?: string',
      'estimatedArrivalDateEnd?: string',
      'actualArrivalDateEnd?: string',
      'totalPiecesMin?: number',
      'totalAmountMax?: number',
      'totalVolumeMax?: number',
      'statuses?: number[]',
    ]
    requiredTypeFields.forEach((field) => assert(typeSource.includes(field), `ContainerQueryRequest 缺少 ${field}`))

    const requiredRequestFields = [
      'ContainerNumberFilter: query.containerNumberFilter',
      'LoadingDateStart: query.loadingDateStart',
      'EstimatedArrivalDateEnd: query.estimatedArrivalDateEnd',
      'ActualArrivalDateEnd: query.actualArrivalDateEnd',
      'TotalPiecesMin: query.totalPiecesMin',
      'TotalAmountMax: query.totalAmountMax',
      'TotalVolumeMax: query.totalVolumeMax',
      'Statuses: query.statuses',
    ]
    requiredRequestFields.forEach((field) => assert(serviceSource.includes(field), `getContainerList 请求体缺少 ${field}`))
  })
  if (requestMappingFailure) failures.push(requestMappingFailure)

  // 重设计：原列头放大镜里的条件全部收进「更多筛选」弹层，状态多选改由状态页签承担。
  const columnFailure = await runTest('原列头条件应全部保留在更多筛选里，状态由页签过滤', () => {
    const expectedMarkers = [
      'value={moreDraft.containerNumberFilter ?? \'\'}',
      "{ startKey: 'loadingDateStart', endKey: 'loadingDateEnd'",
      "{ startKey: 'estimatedArrivalDateStart', endKey: 'estimatedArrivalDateEnd'",
      "{ startKey: 'actualArrivalDateStart', endKey: 'actualArrivalDateEnd'",
      "{ minKey: 'totalPiecesMin', maxKey: 'totalPiecesMax'",
      "{ minKey: 'totalAmountMin', maxKey: 'totalAmountMax'",
      "{ minKey: 'totalVolumeMin', maxKey: 'totalVolumeMax'",
      'DATE_RANGE_FILTERS.map(({ startKey, endKey, labelKey }) => renderMoreFilterDateRange(',
      'NUMBER_RANGE_FILTERS.map(({ minKey, maxKey, labelKey }) => renderMoreFilterRange(',
      'statuses: CONTAINER_STATUS_TAB_STATUSES[activeStatusTab]',
    ]
    expectedMarkers.forEach((marker) => assert(pageSource.includes(marker), `更多筛选或状态页签缺少条件：${marker}`))
    assert(pageSource.includes('<ActiveFilterBar items={activeFilterItems} onClearAll={clearAllFilters} />'), '生效条件应集中显示且可逐个移除')
  })
  if (columnFailure) failures.push(columnFailure)

  const remarkColumnFailure = await runTest('货柜列表应显示备注（并入货柜编号第二行）', () => {
    const columnsStart = normalizedPageSource.indexOf('const columns: ColumnsType<ContainerMain> = [')
    const columnsEnd = normalizedPageSource.indexOf('\n  ]\n', columnsStart)
    assert(columnsStart >= 0 && columnsEnd > columnsStart, '无法定位货柜列表 columns 定义')

    const columnsSource = normalizedPageSource.slice(columnsStart, columnsEnd)
    assert(columnsSource.includes("title: t('containers.fields.containerNumber')"), '货柜列表列定义缺少货柜编号列')
    assert(columnsSource.includes('const remark = record.备注?.trim()'), '货柜编号列应读取备注字段')
    assert(columnsSource.includes('{remark ? <div className="wh-containers-sub" title={remark}>{remark}</div> : null}'), '备注应显示在货柜编号第二行')
  })
  if (remarkColumnFailure) failures.push(remarkColumnFailure)

  const resetFailure = await runTest('清空全部应同步清空更多筛选里的原列头条件', () => {
    assert(
      pageSource.includes('setColumnFilters({})') &&
        pageSource.includes('columnFilters: {}') &&
        pageSource.includes('清空全部同时清掉「更多筛选」里的原列头条件'),
      '清空全部应清空列头条件状态，并用空 columnFilters 立即刷新服务端列表',
    )
  })
  if (resetFailure) failures.push(resetFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('Containers.columnFilters.logic.test: ok')
}

await main()
