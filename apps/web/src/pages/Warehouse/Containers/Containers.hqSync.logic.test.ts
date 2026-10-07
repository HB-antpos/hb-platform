import { readFileSync } from 'node:fs'
import path from 'node:path'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
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
const pageSource = readFileSync(pageFile, 'utf8')

async function main() {
  const failures: string[] = []

  // HQ 货柜 → HBweb 的「从HQ同步」已于 2026-09-29 停用（后端返回 410），页面只保留推送到 HBSales。
  const hqSyncRemovedFailure = await runTest('货柜页不再提供从HQ同步入口', () => {
    for (const removed of ['syncContainersFromHq', 'handleSync', 'setSyncing', "t('containers.actions.syncFromHq')", 'CloudSyncOutlined']) {
      assert(!pageSource.includes(removed), `页面不应再包含 HQ 同步入口代码：${removed}`)
    }

    const firstPageRequestCount = pageSource.split('await latestRequestFirstPageRef.current()').length - 1
    assertEqual(firstPageRequestCount, 1, '只剩创建成功后通过当前单一入口刷新第一页')
  })
  if (hqSyncRemovedFailure) failures.push(hqSyncRemovedFailure)

  const pushToHbSalesFailure = await runTest('推送到 HBSales 按钮应保留 loading 与选择禁用', () => {
    assert(
      pageSource.includes('loading={pushing}') &&
      pageSource.includes('disabled={!selectedRowKeys.length}') &&
      pageSource.includes('onClick={handlePush}'),
      '推送到 HBSales 应继续保留 loading，并在未勾选时禁用',
    )
  })
  if (pushToHbSalesFailure) failures.push(pushToHbSalesFailure)

  // 重设计：行内下拉「一改就保存」改为「⋯」菜单选目标状态 + 二次确认，权限与回滚逻辑不变。
  const inlineStatusFailure = await runTest('改状态应在 ⋯ 菜单里选择并二次确认后保存', () => {
    assert(
      pageSource.includes('handleContainerStatusChange') &&
      pageSource.includes('updateContainer(record.hguid') &&
      pageSource.includes('{ 状态: nextStatus }'),
      '页面应通过行级 handler 调用 updateContainer 更新当前货柜状态',
    )

    assert(
      pageSource.includes('access.canEditContainer && Boolean(record.hguid) && record.状态 != null && Boolean(containerStatusMeta[record.状态])'),
      '改状态入口应保留原权限与可改条件（有编辑权限、有 GUID、状态已知）',
    )

    const confirmStart = pageSource.indexOf('const confirmContainerStatusChange = (record: ContainerMain, nextStatus: number) => {')
    assert(confirmStart >= 0, '缺少改状态确认函数')
    const confirmSection = pageSource.slice(confirmStart, pageSource.indexOf('const handleStatusTabChange', confirmStart))
    assert(
      confirmSection.includes('Modal.confirm({') && confirmSection.includes('onOk: () => handleContainerStatusChange(record, nextStatus)'),
      '改状态应先二次确认，确认后才调用保存',
    )
    assert(
      pageSource.includes('.filter((value) => value !== record.状态)') &&
      pageSource.includes('onClick: ({ key }) => confirmContainerStatusChange(record, Number(String(key).split(\':\')[1]))'),
      '菜单只列出与当前不同的目标状态，点击后进入确认',
    )
    assert(
      pageSource.includes('disabled={statusUpdatingKeys.includes(recordKey)}'),
      '行级更新进行中应禁用该行的改状态菜单',
    )

    assert(
      pageSource.includes('if (record.状态 === nextStatus || statusUpdatingKeys.includes(recordKey))') &&
      pageSource.includes('setContainers((items) => items.map((item) => (itemKeyOf(item) === recordKey ? { ...item, 状态: previousStatus } : item)))'),
      '状态更新应跳过相同状态和忙碌行，并在失败时回滚原状态',
    )
  })
  if (inlineStatusFailure) failures.push(inlineStatusFailure)

  // 重设计：日期不再按周号哈希上色（颜色只表示状态），周号改为小字显示，由 containersLogic 按 ISO 周计算。
  const weekDateColorFailure = await runTest('日期按 ISO 周显示周号且不再按值上色', () => {
    const logicSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Containers/containersLogic.ts'), 'utf8')
    assert(
      logicSource.includes("import isoWeek from 'dayjs/plugin/isoWeek'") && logicSource.includes('dayjs.extend(isoWeek)'),
      '周号计算应启用 dayjs isoWeek 插件（周一为周首、跨年按 ISO week-year）',
    )
    for (const removed of ['containerDateWeekColors', 'getContainerDateWeekColor', 'renderContainerWeekDate']) {
      assert(!pageSource.includes(removed), `日期不应再按周哈希上色：${removed}`)
    }
    assert(
      pageSource.includes('getIsoWeekNumber(date)') && pageSource.includes('describeEtaHint(record, today)'),
      '装柜日期显示周号，预计到岸显示到岸提示或周号',
    )
    assertEqual(pageSource.split('formatContainerDate(value)').length - 1 >= 3, true, '三列日期都应走统一的日期格式化')
  })
  if (weekDateColorFailure) failures.push(weekDateColorFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('Containers.hqSync.logic.test: ok')
}

await main()
