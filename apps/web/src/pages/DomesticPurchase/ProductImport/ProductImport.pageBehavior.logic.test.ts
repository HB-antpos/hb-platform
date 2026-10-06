import { existsSync, readdirSync, readFileSync } from 'node:fs'

// 商品导入页重设计后新增的行为保护：这些行为都写在组件里，无法像纯函数一样直接调用，
// 因此沿用本目录其它测试的做法，对源码关键片段做静态断言，防止后续改动悄悄丢掉。

const directory = 'src/pages/DomesticPurchase/ProductImport'
const pageSource = readFileSync(`${directory}/index.tsx`, 'utf8')
const conflictSource = readFileSync(`${directory}/ConflictResolutionDialog.tsx`, 'utf8')
const duplicateSource = readFileSync(`${directory}/DuplicateDialog.tsx`, 'utf8')

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

function sliceBetween(source: string, startMarker: string, endMarker: string): string {
  const start = source.indexOf(startMarker)
  if (start < 0) throw new Error(`找不到片段起点: ${startMarker}`)
  const end = source.indexOf(endMarker, start + startMarker.length)
  if (end < 0) throw new Error(`找不到片段终点: ${endMarker}`)
  return source.slice(start, end)
}

// 1) keepAlive：document 级粘贴监听只在页面处于前台时注册，并且只处理本页表格里的粘贴
const pasteEffectSource = sliceBetween(pageSource, "document.addEventListener('paste', handlePaste)", 'const handleHeaderClick')
const pasteHandlerSource = sliceBetween(pageSource, 'const handlePaste = useCallback((e: ClipboardEvent) => {', 'useEffect(() => {')
assertDeepEqual(
  [
    pageSource.includes("import { useKeepAliveContext } from 'keepalive-for-react'"),
    pageSource.includes('const { active } = useKeepAliveContext()'),
    // 注册监听之前必须先判断 active
    pageSource.slice(pageSource.lastIndexOf('useEffect(() => {', pageSource.indexOf("document.addEventListener('paste', handlePaste)")), pageSource.indexOf("document.addEventListener('paste', handlePaste)")).includes('if (!active) return'),
    pasteEffectSource.includes('[active, handlePaste]'),
    pasteHandlerSource.includes('productImportTableRef.current?.contains(tableContainer)'),
  ],
  [true, true, true, true, true],
  'keepAlive 下粘贴监听只在页面激活时注册，且只处理本页表格内的粘贴',
)

// 2) 粘贴定位：按 id 查行，不能再对 row_ 开头的 id 做 parseInt（会得到 NaN）
assertDeepEqual(
  [
    !pageSource.includes('parseInt(tr.getAttribute'),
    pasteHandlerSource.includes("findRowIndexById(state.products, tr.getAttribute('data-row-key'))"),
    // 过滤状态下可见行只是整表的一部分：整列粘贴 / 清列必须先回到「全部」
    pasteHandlerSource.includes("statusFilter !== 'all'"),
    sliceBetween(pageSource, 'const handleClearColumn = useCallback(() => {', 'const handlePaste').includes("statusFilter !== 'all'"),
  ],
  [true, true, true, true],
  '粘贴按 id 定位行；筛选状态下禁止整列粘贴与清列',
)

// 3) 翻译结果按 id 合并进当前行，而不是用点击时的旧数组整体覆盖
const translateSource = sliceBetween(pageSource, 'const handleBatchTranslate = useCallback(', 'const handleUpdateHbwebProductNames')
assertDeepEqual(
  [
    translateSource.includes('snapshotTranslationTargets(stateRef.current.products, stateRef.current.selectedIds)'),
    translateSource.includes('mergeTranslationsById(prev.products, targets, translations).products'),
    !translateSource.includes('applyProductImportNameTranslations'),
  ],
  [true, true, true],
  '翻译结果应在 setState 更新函数里按 id 合并到最新的行',
)

// 4) 检测 / 入库 / 发送 / 同步 / 发总部：串行执行 + loading + 表格锁定
assertDeepEqual(
  [
    pageSource.includes('const busyRef = useRef(false)'),
    pageSource.includes('if (busyRef.current) return undefined'),
    pageSource.includes("runExclusive('detect'"),
    pageSource.includes("runExclusive('translate'"),
    pageSource.includes("runExclusive('create'"),
    pageSource.includes("runExclusive('update'"),
    pageSource.includes("runExclusive('names'"),
    pageSource.includes("runExclusive('send'"),
    pageSource.includes("runExclusive('hbsales'"),
    pageSource.includes("runExclusive('hq'"),
    pageSource.includes("const tableLocked = busyAction !== null && busyAction !== 'translate'"),
    pageSource.includes('disabled={tableLocked}'),
    pageSource.includes("loading={busyAction === 'detect'}"),
    pageSource.includes("loading={busyAction === 'send'}"),
    pageSource.includes("loading={busyAction === 'create'}"),
    pageSource.includes("loading={busyAction === 'update'}"),
    pageSource.includes("confirming={busyAction === 'send'}"),
    pageSource.includes('detectGuardRef.current.isLatest(requestId)'),
  ],
  Array(18).fill(true),
  '会改数据或发请求的动作必须串行、带 loading，检测期间锁定表格，迟到的检测结果被丢弃',
)

// 5) 检测后只有一列「状态」，旧值并入单元格；旧的 11 个检测后专属列不应再出现
assertDeepEqual(
  [
    pageSource.includes("key: 'status'"),
    pageSource.includes("key: 'actions'"),
    !pageSource.includes("key: 'matchedProductCode'"),
    !pageSource.includes("key: 'matchedBarcode'"),
    !pageSource.includes("key: 'matchedOemPrice'"),
    !pageSource.includes("key: 'diffFields'"),
    !pageSource.includes("key: 'matchStatus'"),
  ],
  [true, true, true, true, true, true, true],
  '检测后表格从 24 列收敛为 13 列：旧值并入单元格，状态合并为一列',
)

// 6) 货柜冲突：逐项选择以本地商品编码为键；两个弹窗都不允许点遮罩关闭
assertDeepEqual(
  [
    pageSource.includes('splitProductsByResolution(products, selection)'),
    !pageSource.includes('perItem[p.newProduct.productCode]'),
    conflictSource.includes('maskClosable={false}'),
    duplicateSource.includes('maskClosable={false}'),
    // 确认发送进行中不能关闭，避免重复提交
    conflictSource.includes('keyboard={!confirming}'),
    conflictSource.includes('closable={!confirming}'),
  ],
  [true, true, true, true, true, true],
  '货柜冲突逐项选择按本地商品编码生效；弹窗不可点遮罩关闭、发送中不可关闭',
)

// 7) 规格第一节第 2 条：商品导入里只有「货号为空」是错误
const gridLogicSource = readFileSync(`${directory}/importGridLogic.ts`, 'utf8')
assertDeepEqual(
  [
    gridLogicSource.includes("return hasImportItemNo(row) ? {} : { productCode: '货号不能为空' }"),
    // 页面不再调用会把「商品名称为空」也算作错误的 validateProduct('import')
    !pageSource.includes('validateProduct('),
  ],
  [true, true],
  '只有货号为空是错误，零售价、名称留空都不报错（行为见 gridRules 测试）',
)

// 8) 死代码清理：ImagePreviewDialog 没有任何引用，整个 ImageCell.tsx 已删除
const sourceFilesInDirectory = readdirSync(directory).filter((name) => /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name))
assertDeepEqual(
  [
    existsSync(`${directory}/ImageCell.tsx`),
    sourceFilesInDirectory.some((name) => readFileSync(`${directory}/${name}`, 'utf8').includes('ImagePreviewDialog')),
    existsSync(`${directory}/styles.css`),
  ],
  [false, false, false],
  '死代码 ImagePreviewDialog（ImageCell.tsx）与旧 styles.css 应已删除',
)

console.log('ProductImport.pageBehavior.logic.test: ok')
