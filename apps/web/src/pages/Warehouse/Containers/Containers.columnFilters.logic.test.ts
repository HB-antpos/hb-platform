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
    assert(
      pageSource.includes('const activeColumnFilters = options.columnFilters ?? columnFilters') &&
        pageSource.includes('...activeColumnFilters') &&
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

  const columnFailure = await runTest('全部业务列应配置列头过滤控件', () => {
    const expectedMarkers = [
      "...textFilterProps('containerNumberFilter'",
      "...dateRangeFilterProps('loadingDateStart', 'loadingDateEnd')",
      "...dateRangeFilterProps('estimatedArrivalDateStart', 'estimatedArrivalDateEnd')",
      "...dateRangeFilterProps('actualArrivalDateStart', 'actualArrivalDateEnd')",
      "...numberRangeFilterProps('totalPiecesMin', 'totalPiecesMax')",
      "...numberRangeFilterProps('totalAmountMin', 'totalAmountMax')",
      "...numberRangeFilterProps('totalVolumeMin', 'totalVolumeMax')",
      'filterDropdown: makeStatusFilterDropdown',
      'filtered: Boolean(columnFilters.statuses?.length)',
    ]
    expectedMarkers.forEach((marker) => assert(pageSource.includes(marker), `业务列缺少过滤配置：${marker}`))
  })
  if (columnFailure) failures.push(columnFailure)

  const remarkColumnFailure = await runTest('货柜列表应显示备注列', () => {
    const columnsStart = normalizedPageSource.indexOf('const columns: ColumnsType<ContainerMain> = [')
    const columnsEnd = normalizedPageSource.indexOf(']\n\n  return', columnsStart)
    assert(columnsStart >= 0 && columnsEnd > columnsStart, '无法定位货柜列表 columns 定义')

    const columnsSource = normalizedPageSource.slice(columnsStart, columnsEnd)
    assert(columnsSource.includes("title: t('containers.fields.remark')"), '货柜列表列定义缺少备注标题')
    assert(columnsSource.includes("dataIndex: '备注'"), '货柜列表列定义缺少备注字段')
  })
  if (remarkColumnFailure) failures.push(remarkColumnFailure)

  const resetFailure = await runTest('顶部重置应同步清空列头过滤', () => {
    assert(
      pageSource.includes('setColumnFilters({})') &&
        pageSource.includes('columnFilters: {}') &&
        pageSource.includes('顶部重置同时清空列头过滤'),
      '顶部重置应清空列头状态，并用空 columnFilters 立即刷新服务端列表',
    )
  })
  if (resetFailure) failures.push(resetFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('Containers.columnFilters.logic.test: ok')
}

await main()
