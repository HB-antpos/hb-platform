// 商品导入页的网格纯逻辑：行状态归类、状态统计条过滤、旧值差异、错误汇总、步骤条、翻译结果合并、货柜冲突。
// 全部是纯函数，页面只负责把结果画出来；这样每条口径都能单测，不必渲染组件。

import type { ProductImportItem } from './types'
import { applyProductImportNameTranslations, containsChineseText } from './utils'

/* ------------------------------------------------------------------ */
/* 行状态                                                              */
/* ------------------------------------------------------------------ */

/**
 * 一行在表格里的展示类别。
 * 把「后端检测结果」「这一行是否参与过检测」「是不是空行」合并成一个结论，行色、状态列、统计条共用同一份口径。
 */
export type ImportRowKind =
  | 'blank' // 完全没填的占位行：不报错、不进任何状态统计（只计入「全部」）
  | 'pending' // 有内容但本轮检测之后才新增 / 尚未检测
  | 'new'
  | 'updated'
  | 'unchanged'
  | 'duplicate'
  | 'dbDuplicate'
  | 'error'

export type ImportStatusFilter = 'all' | 'new' | 'updated' | 'unchanged' | 'duplicate' | 'dbDuplicate' | 'error' | 'sent'

export const IMPORT_STATUS_FILTERS: readonly ImportStatusFilter[] = ['all', 'new', 'updated', 'unchanged', 'duplicate', 'dbDuplicate', 'error', 'sent']

/** 新增行的默认件数；只有件数被改过才算「有内容」。 */
const DEFAULT_QUANTITY = 1

function hasText(value: string | undefined | null): boolean {
  return (value ?? '').trim() !== ''
}

/** 货号是否已填写（按 trim 判断，和重复检测、创建请求保持同一口径）。 */
export function hasImportItemNo(row: ProductImportItem): boolean {
  return hasText(row.newProduct.productCode)
}

/**
 * 空行：所有文本、数值列都没填，件数仍是默认值。
 * 「+10 行」会产生大量占位行，把它们算成「货号为空」错误只会制造噪音，所以空行既不报错也不参与统计。
 */
export function isBlankImportRow(row: ProductImportItem): boolean {
  const product = row.newProduct
  if ([product.productCode, product.barcode, product.productName, product.englishName].some(hasText)) return false
  // 旧代码清空数值框会留下 null，这里 null 与 undefined 一视同仁
  const hasNumber = [product.domesticPrice, product.oemPrice, product.midPackQuantity, product.casePackQuantity, product.volume]
    .some((value) => value !== undefined && value !== null)
  if (hasNumber) return false
  return product.quantity === undefined || product.quantity === null || product.quantity === DEFAULT_QUANTITY
}

/** 逐行错误：规格第一节第 2 条——商品导入里只有「货号为空」是错误，零售价、名称都可以留空。 */
export function getImportRowErrors(row: ProductImportItem): { [field: string]: string } {
  if (isBlankImportRow(row)) return {}
  return hasImportItemNo(row) ? {} : { productCode: '货号不能为空' }
}

/**
 * 归类一行。
 * @param detectedRowIds 最近一次检测覆盖的行 id；null 表示还没检测过。
 */
export function resolveImportRowKind(row: ProductImportItem, detectedRowIds: ReadonlySet<string> | null): ImportRowKind {
  if (isBlankImportRow(row)) return 'blank'
  // 重复是检测前的本地判定（同一货号出现多行），不依赖后端结果，所以先于「是否检测过」判断
  if (row.isDuplicate || row.status === 'duplicate') return 'duplicate'
  // 检测之后才新增的行（如「+10 行」、粘贴补行）还没有检测结果，不能沿用默认的 unchanged
  if (!detectedRowIds || !detectedRowIds.has(row.id)) return 'pending'
  // 货号为空以实时内容为准：补上货号后红色立刻消失，不必等下一次检测
  if (!hasImportItemNo(row)) return 'error'
  switch (row.status) {
    case 'new':
    case 'updated':
    case 'unchanged':
    case 'dbDuplicate':
      return row.status
    // 存量的 error 状态但现在已有货号：需要重新检测才知道结果
    default:
      return 'pending'
  }
}

export interface ImportStatusCounts {
  all: number
  new: number
  updated: number
  unchanged: number
  duplicate: number
  dbDuplicate: number
  error: number
  sent: number
}

export function countImportRows(products: readonly ProductImportItem[], detectedRowIds: ReadonlySet<string> | null): ImportStatusCounts {
  const counts: ImportStatusCounts = { all: products.length, new: 0, updated: 0, unchanged: 0, duplicate: 0, dbDuplicate: 0, error: 0, sent: 0 }
  for (const row of products) {
    if (row.sentToContainer) counts.sent += 1
    const kind = resolveImportRowKind(row, detectedRowIds)
    if (kind === 'new' || kind === 'updated' || kind === 'unchanged' || kind === 'duplicate' || kind === 'dbDuplicate' || kind === 'error') {
      counts[kind] += 1
    }
  }
  return counts
}

export function matchesImportFilter(row: ProductImportItem, filter: ImportStatusFilter, detectedRowIds: ReadonlySet<string> | null): boolean {
  if (filter === 'all') return true
  // 「已发送」与状态正交：一个新商品发送到货柜后同时属于「新」和「已发送」
  if (filter === 'sent') return row.sentToContainer === true
  return resolveImportRowKind(row, detectedRowIds) === filter
}

/** 行 class：每类行一个底色 + 左侧色条（含库内重复）；已发送只在没有其它状态色时才浅蓝，避免盖掉「错误 / 重复」。 */
export function getImportRowClassName(kind: ImportRowKind, sent: boolean): string {
  const classes = ['pi-row']
  if (kind !== 'blank' && kind !== 'pending') classes.push(`pi-row-${kind}`)
  if (sent) classes.push('pi-row-sent')
  return classes.join(' ')
}

/* ------------------------------------------------------------------ */
/* 错误汇总（底部可跳转）与重复行号                                      */
/* ------------------------------------------------------------------ */

export interface ImportRowError {
  rowId: string
  /** 在整张表里的行号（从 1 起），不受状态过滤影响，与「第 N 行」文案一致。 */
  rowNumber: number
  field: 'productCode'
}

export function collectImportErrors(products: readonly ProductImportItem[], detectedRowIds: ReadonlySet<string> | null): ImportRowError[] {
  const errors: ImportRowError[] = []
  products.forEach((row, index) => {
    if (resolveImportRowKind(row, detectedRowIds) === 'error') {
      errors.push({ rowId: row.id, rowNumber: index + 1, field: 'productCode' })
    }
  })
  return errors
}

/** 货号（trim 后）→ 出现该货号的行号列表，用于给重复行标注「与第 N 行」。 */
export function buildDuplicateRowIndex(products: readonly ProductImportItem[]): Map<string, number[]> {
  const index = new Map<string, number[]>()
  products.forEach((row, rowIndex) => {
    const code = row.newProduct.productCode?.trim()
    if (!code) return
    const rows = index.get(code)
    if (rows) rows.push(rowIndex + 1)
    else index.set(code, [rowIndex + 1])
  })
  return index
}

export function getDuplicatePartnerRowNumbers(duplicateIndex: ReadonlyMap<string, number[]>, row: ProductImportItem, rowNumber: number): number[] {
  const code = row.newProduct.productCode?.trim()
  if (!code) return []
  return (duplicateIndex.get(code) ?? []).filter((number) => number !== rowNumber)
}

/* ------------------------------------------------------------------ */
/* 旧值并入单元格                                                       */
/* ------------------------------------------------------------------ */

export type ImportDiffField = 'productName' | 'englishName' | 'barcode' | 'domesticPrice' | 'oemPrice' | 'midPackQuantity' | 'casePackQuantity' | 'volume'

type MatchedProduct = NonNullable<ProductImportItem['matchedProduct']>

/**
 * 新值字段 → 后端 changeList 里的名字 + 旧值字段。
 * 旧代码拿新值字段名（englishName / midPackQuantity / casePackQuantity / volume）直接去 diffFields 里找，
 * 而 diffFields 存的是后端字段名（englishProductName / middlePackQuantity / packingQuantity / unitVolume），
 * 这四列的变化高亮因此从未生效；这里集中维护映射。
 */
const DIFF_FIELD_SOURCES: Record<ImportDiffField, { diffKey: string; matchedKey: keyof MatchedProduct }> = {
  productName: { diffKey: 'productName', matchedKey: 'productName' },
  englishName: { diffKey: 'englishProductName', matchedKey: 'englishProductName' },
  barcode: { diffKey: 'barcode', matchedKey: 'barcode' },
  domesticPrice: { diffKey: 'domesticPrice', matchedKey: 'domesticPrice' },
  oemPrice: { diffKey: 'oemPrice', matchedKey: 'oemPrice' },
  midPackQuantity: { diffKey: 'middlePackQuantity', matchedKey: 'middlePackQuantity' },
  casePackQuantity: { diffKey: 'packingQuantity', matchedKey: 'packingQuantity' },
  volume: { diffKey: 'unitVolume', matchedKey: 'unitVolume' },
}

/** 列顺序：状态列里「更新 + 变化字段」按这个顺序列出。 */
export const IMPORT_DIFF_FIELDS: readonly ImportDiffField[] = ['productName', 'englishName', 'barcode', 'domesticPrice', 'oemPrice', 'midPackQuantity', 'casePackQuantity', 'volume']

export function isImportDiffField(field: string): field is ImportDiffField {
  return (IMPORT_DIFF_FIELDS as readonly string[]).includes(field)
}

export interface ImportCellDiff {
  /** 旧值的展示文本；null 表示旧值为空（库里没填）。 */
  oldDisplay: string | null
}

/** 把库里的旧值格式化成单元格下方的小字：价格带币种、体积保留三位，空值与 ≤0 的数值都视为「空」。 */
export function formatImportOldValue(field: ImportDiffField, raw: unknown): string | null {
  if (raw === undefined || raw === null) return null
  if (typeof raw === 'string') {
    const text = raw.trim()
    return text === '' ? null : text
  }
  // 价格、中包、装箱、体积在库里 0 就是「没填」，显示「原：空」比「原 ¥ 0.00」更接近事实
  if (typeof raw !== 'number' || !Number.isFinite(raw) || raw <= 0) return null
  switch (field) {
    case 'domesticPrice':
      return `¥ ${raw.toFixed(2)}`
    case 'oemPrice':
      return `$ ${raw.toFixed(2)}`
    case 'volume':
      return raw.toFixed(3)
    default:
      return String(raw)
  }
}

/** 该单元格相对库里已有商品是否有变化（以后端检测结果 diffFields 为准）；无变化返回 null。 */
export function getImportCellDiff(row: ProductImportItem, field: ImportDiffField): ImportCellDiff | null {
  const matched = row.matchedProduct
  if (!matched || !row.diffFields || row.diffFields.length === 0) return null
  const source = DIFF_FIELD_SOURCES[field]
  if (!row.diffFields.includes(source.diffKey) && !row.diffFields.includes(field)) return null
  return { oldDisplay: formatImportOldValue(field, matched[source.matchedKey]) }
}

/** 一行里有变化的字段（按列顺序），用于状态列「更新 国内价」。 */
export function getChangedImportFields(row: ProductImportItem): ImportDiffField[] {
  return IMPORT_DIFF_FIELDS.filter((field) => getImportCellDiff(row, field) !== null)
}

/* ------------------------------------------------------------------ */
/* 5 步分段                                                             */
/* ------------------------------------------------------------------ */

export type ImportStepKey = 'supplier' | 'entry' | 'detect' | 'commit' | 'container'
export type ImportStepState = 'done' | 'current' | 'todo'

export type ImportStepSummary =
  | { kind: 'rows'; count: number }
  | { kind: 'detectedAt' }
  | { kind: 'needRedetect' }
  | { kind: 'duplicates'; count: number }
  | { kind: 'pendingCommit'; count: number }
  | { kind: 'committed'; count: number }
  | { kind: 'nothingToCommit' }
  | { kind: 'sent'; count: number }
  | { kind: 'optional' }

export interface ImportStepView {
  key: ImportStepKey
  state: ImportStepState
  /** 需要用户注意（如「有改动，需重新检测」），样式用警示色。 */
  warn: boolean
  summary?: ImportStepSummary
}

export interface ImportStepInput {
  hasSupplier: boolean
  /** 非空行数（空行不算「已录入」）。 */
  filledRowCount: number
  /** 已有检测结果。 */
  detected: boolean
  /** 检测之后又有改动。 */
  needsDetection: boolean
  /** 检测被「重复货号」拦下的组数（0 表示没被拦）。 */
  duplicateGroupCount: number
  /** 当前待入库行数（新 + 更新）。 */
  pendingCommitCount: number
  /** 本页累计已入库条数。 */
  committedCount: number
  /** 已发送到货柜的行数。 */
  sentCount: number
}

/**
 * 计算 5 步的状态。每一步是否完成都依赖上一步（链式），
 * 所以录入之后任何改动都会让「检测匹配」回到当前步、后面各步变回待办——这就是「有改动后步骤回退」。
 */
export function computeImportSteps(input: ImportStepInput): ImportStepView[] {
  const supplierDone = input.hasSupplier
  const entryDone = supplierDone && input.filledRowCount > 0
  const detectDone = entryDone && input.detected && !input.needsDetection && input.duplicateGroupCount === 0
  const commitDone = detectDone && input.pendingCommitCount === 0
  const containerDone = commitDone && input.sentCount > 0

  const done = [supplierDone, entryDone, detectDone, commitDone, containerDone]
  const currentIndex = done.findIndex((value) => !value)
  const stateOf = (index: number): ImportStepState => (done[index] ? 'done' : index === currentIndex ? 'current' : 'todo')

  let detectSummary: ImportStepSummary | undefined
  let detectWarn = false
  if (detectDone) {
    detectSummary = { kind: 'detectedAt' }
  } else if (entryDone && input.duplicateGroupCount > 0) {
    detectSummary = { kind: 'duplicates', count: input.duplicateGroupCount }
    detectWarn = true
  } else if (entryDone && input.detected && input.needsDetection) {
    detectSummary = { kind: 'needRedetect' }
    detectWarn = true
  }

  let commitSummary: ImportStepSummary | undefined
  if (detectDone) {
    commitSummary = input.pendingCommitCount > 0
      ? { kind: 'pendingCommit', count: input.pendingCommitCount }
      : input.committedCount > 0
        ? { kind: 'committed', count: input.committedCount }
        : { kind: 'nothingToCommit' }
  }

  return [
    { key: 'supplier', state: stateOf(0), warn: false },
    { key: 'entry', state: stateOf(1), warn: false, summary: entryDone ? { kind: 'rows', count: input.filledRowCount } : undefined },
    { key: 'detect', state: stateOf(2), warn: detectWarn, summary: detectSummary },
    { key: 'commit', state: stateOf(3), warn: false, summary: commitSummary },
    // 发货柜是可选步骤：已经发送过就显示数量（即使步骤因改动回退，也保留这个事实），否则标「可选」
    { key: 'container', state: stateOf(4), warn: false, summary: input.sentCount > 0 ? { kind: 'sent', count: input.sentCount } : { kind: 'optional' } },
  ]
}

/* ------------------------------------------------------------------ */
/* 翻译结果按 id 合并                                                   */
/* ------------------------------------------------------------------ */

export interface TranslationTarget {
  id: string
  productName: string
  englishName: string
}

/** 点击翻译那一刻的快照：目标行（有选中取选中，否则全部）里含中文的商品名称。 */
export function snapshotTranslationTargets(products: readonly ProductImportItem[], selectedIds: readonly string[]): TranslationTarget[] {
  const selected = new Set(selectedIds)
  const useSelection = selected.size > 0
  return products
    .filter((row) => !useSelection || selected.has(row.id))
    .map((row) => ({ id: row.id, productName: (row.newProduct.productName ?? '').trim(), englishName: (row.newProduct.englishName ?? '').trim() }))
    .filter((target) => target.productName !== '' && containsChineseText(target.productName))
}

export interface MergeTranslationsResult {
  products: ProductImportItem[]
  appliedCount: number
  skippedCount: number
  /** 翻译期间名称或英文名被用户改过、因此没有覆盖的行数。 */
  editedCount: number
}

/**
 * 把翻译结果合并进「当前」的行数组，而不是点击翻译时的旧数组。
 * 旧代码用点击时的旧数组整体覆盖，翻译请求期间用户做的编辑、粘贴、删除都会丢；
 * 这里按 id 逐行合并，并且只覆盖「名称与英文名自点击以来没被改过」的行。
 */
export function mergeTranslationsById(
  currentProducts: ProductImportItem[],
  targets: readonly TranslationTarget[],
  translations: Record<string, string>,
): MergeTranslationsResult {
  const byId = new Map(currentProducts.map((row) => [row.id, row]))
  const eligibleIds: string[] = []
  let editedCount = 0

  for (const target of targets) {
    const row = byId.get(target.id)
    if (!row) continue // 翻译期间被删除的行直接跳过
    const nameNow = (row.newProduct.productName ?? '').trim()
    const englishNow = (row.newProduct.englishName ?? '').trim()
    if (nameNow !== target.productName || englishNow !== target.englishName) {
      editedCount += 1
      continue
    }
    eligibleIds.push(row.id)
  }

  if (eligibleIds.length === 0) return { products: currentProducts, appliedCount: 0, skippedCount: 0, editedCount }
  const result = applyProductImportNameTranslations(currentProducts, translations, eligibleIds)
  return { ...result, editedCount }
}

/* ------------------------------------------------------------------ */
/* 货柜冲突                                                             */
/* ------------------------------------------------------------------ */

export type ConflictResolution = 'increase' | 'override'
export interface ConflictSelection {
  global?: ConflictResolution
  /** 键是本地商品编码（matchedProduct.productCode），不是货号。 */
  perItem?: Record<string, ConflictResolution>
}

export interface ContainerConflictRow {
  /** 本地商品编码：后端冲突检查按它匹配，也是逐项选择的键。 */
  productCode: string
  hbProductNo: string
  productName: string
  existingPieces?: number
  incomingPieces: number
}

/**
 * 把后端返回的冲突项对上本次要发送的行。
 * 后端 check-conflicts 按本地商品编码（ProductCode）匹配；同一本地编码若对应多行（库内重复），本次件数取合计。
 */
export function buildConflictRows(
  products: readonly ProductImportItem[],
  conflicts: ReadonlyArray<{ productCode: string; existingPieces?: number }>,
): ContainerConflictRow[] {
  return conflicts.map((conflict) => {
    const rows = products.filter((row) => row.matchedProduct?.productCode === conflict.productCode)
    const first = rows[0]
    return {
      productCode: conflict.productCode,
      hbProductNo: first?.newProduct.productCode?.trim() || conflict.productCode,
      productName: first?.newProduct.productName?.trim() || first?.matchedProduct?.productName || '',
      existingPieces: typeof conflict.existingPieces === 'number' ? conflict.existingPieces : undefined,
      incomingPieces: rows.reduce((sum, row) => sum + (row.newProduct.quantity || 0), 0),
    }
  })
}

/** 发送后货柜里的件数：增加 = 已有 + 本次；覆盖 = 本次。 */
export function computePiecesAfterSend(row: Pick<ContainerConflictRow, 'existingPieces' | 'incomingPieces'>, resolution: ConflictResolution): number {
  return resolution === 'override' ? row.incomingPieces : (row.existingPieces ?? 0) + row.incomingPieces
}

/**
 * 按用户选择把待发送的行拆成「覆盖」「增加」两批。
 * 逐项选择时按本地商品编码取值——旧代码用货号去查以本地编码为键的选择表，永远查不到，「覆盖」选择从未生效。
 * 没有冲突的行默认走「增加」（后端对不存在的明细就是新建）。
 */
export function splitProductsByResolution(
  products: readonly ProductImportItem[],
  selection: ConflictSelection,
): { override: ProductImportItem[]; increase: ProductImportItem[] } {
  if (selection.global === 'override') return { override: [...products], increase: [] }
  if (selection.global === 'increase') return { override: [], increase: [...products] }
  const perItem = selection.perItem ?? {}
  const override: ProductImportItem[] = []
  const increase: ProductImportItem[] = []
  for (const row of products) {
    const code = row.matchedProduct?.productCode
    if (code && perItem[code] === 'override') override.push(row)
    else increase.push(row)
  }
  return { override, increase }
}
