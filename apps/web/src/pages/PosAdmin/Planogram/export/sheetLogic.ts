/**
 * 商品陈列图 PDF 的纯逻辑（不依赖 React / 请求 / 浏览器）：数据来源统一成发布快照形状、序号与清单顺序、
 * 版面图的纸张 / 比例 / 每页几节、执行清单的分栏分页、文件名、按语言取词、双语名称。
 * 页面 DOM（PlanogramPrintPages.tsx）与导出弹窗只做接线，规则都在这里，便于单测。
 *
 * 数据来源：已发布版本用 getPlanVersion 的 PlanogramSnapshotDto；「当前已保存版本」用 getPlan 的详情组装成同形状（sourceFromDetail）。
 * 几何一律来自 engine/geometry.ts 与 Designer/BoardCanvas.tsx（与后端校验器同一套公式），这里不另写几何。
 */
import type {
  ConflictDto,
  HookModelDto,
  PlanogramDetailDto,
  PlanogramSnapshotDto,
  PlanogramSnapshotProductDto,
  PlanogramStatsDto,
  ProductSpecListItemDto,
  ShelfModelDto,
} from '../../../../types/planogram'
import { sanitizeFileNamePart } from '../../../Warehouse/StoreOrders/printUtils'
import type { PdfPageFormat } from '../../../Warehouse/StoreOrders/printUtils'
import { formatSydneyIsoDate } from '../../../../utils/sydneyDate'
import { resolvePerHookQty, validateLayout, type LayoutConflict, type LayoutContext } from '../engine/geometry'
import { buildLayoutContext, conflictsByItem, sortChecklistItems, wrapLabel, type DesignerItem } from '../Designer/designerLogic'
import { formatMoney, formatMoneyTotal, formatUpdatedAt } from '../planogramLogic'

export { formatMoney, formatMoneyTotal }

// ---------------------------------------------------------------------------
// 选项
// ---------------------------------------------------------------------------

export type PdfPaper = 'a3-landscape' | 'a4-landscape' | 'a4-portrait'
export type PdfScaleMode = 'auto' | '1:5' | '1:10'
/** 中文 / 英文（给门店）/ 双语。 */
export type PdfLang = 'zh' | 'en' | 'bi'

export interface PdfOptions {
  paper: PdfPaper
  scale: PdfScaleMode
  lang: PdfLang
  /** 版面图。 */
  board: boolean
  /** 执行清单。 */
  checklist: boolean
  /** 序号气泡（与清单行号一一对应）。 */
  bubbles: boolean
  /** 价签上的价格。 */
  prices: boolean
  /** 孔位列号 / 行号。 */
  coords: boolean
  /** 商品实拍图（默认不印）。 */
  images: boolean
  /** 清单加「条码」列（默认不加）。 */
  barcode: boolean
  /** 清单加「已上货 ☐」空白检查栏（默认不加）。 */
  checkColumn: boolean
}

/** 弹窗默认值（设计图 04）：A3 横向、自动适配、版面图 + 清单 + 气泡 + 价格 + 孔位号，中文；实拍图 / 条码 / 检查栏默认不要。 */
export const DEFAULT_PDF_OPTIONS: PdfOptions = {
  paper: 'a3-landscape',
  scale: 'auto',
  lang: 'zh',
  board: true,
  checklist: true,
  bubbles: true,
  prices: true,
  coords: true,
  images: false,
  barcode: false,
  checkColumn: false,
}

/** 至少要有一项内容（版面图或执行清单）才能导出。 */
export function hasPdfContent(options: Pick<PdfOptions, 'board' | 'checklist'>): boolean {
  return options.board || options.checklist
}

/** 页面语言列表：中文 / 英文各取一次文案；双语两种都取。 */
export function pdfLanguages(lang: PdfLang): Array<'zh' | 'en'> {
  return lang === 'bi' ? ['zh', 'en'] : [lang]
}

// ---------------------------------------------------------------------------
// 按语言取词（PDF 文案放在页面消息文件 planogram.pdf.* 里，语言由导出选项决定，与界面语言无关）
// ---------------------------------------------------------------------------

export type MessageTree = { [key: string]: MessageTree | string }

/** 点号路径取文案；路径不存在或不是字符串返回 undefined。 */
export function lookupMessage(tree: MessageTree, key: string): string | undefined {
  let node: MessageTree | string | undefined = tree
  for (const segment of key.split('.')) {
    if (typeof node !== 'object' || node === null) return undefined
    node = node[segment]
  }
  return typeof node === 'string' ? node : undefined
}

/** {{name}} 占位符替换（与 i18next 的写法一致）；缺参数的占位符原样保留，便于发现漏传。 */
export function interpolateMessage(template: string, params?: Record<string, string | number>): string {
  if (!params) return template
  return template.replace(/\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g, (match, name: string) => (name in params ? String(params[name]) : match))
}

export type PdfTranslate = (lang: 'zh' | 'en', key: string, params?: Record<string, string | number>) => string

/** 用 zh / en 两份消息文件造取词函数；缺键时返回键名本身（界面上一眼能看出漏了哪个），不静默回退到另一种语言。 */
export function createPdfTranslator(zh: MessageTree, en: MessageTree): PdfTranslate {
  return (lang, key, params) => {
    const template = lookupMessage(lang === 'zh' ? zh : en, key)
    return template === undefined ? key : interpolateMessage(template, params)
  }
}

/** 按导出语言取词：单语取对应语言；双语时 one() 取中文（主语言），line() 给「中文 + 英文」两行，joined() 用斜杠连成一行。 */
export interface PdfText {
  lang: PdfLang
  one: (key: string, params?: Record<string, string | number>) => string
  /** 单语 → [文案]；双语 → [中文, 英文]（用于图例 / 要点这类「两行」展示）。 */
  line: (key: string, params?: Record<string, string | number>) => string[]
  /** 单语 → 文案；双语 → 「中文 / English」。 */
  joined: (key: string, params?: Record<string, string | number>, separator?: string) => string
  /** 同 joined，但参数按语言分别给（句子里嵌着「板顶 / Top」这类也要随语言变的片段）。 */
  joinedBy: (key: string, paramsFor: (language: 'zh' | 'en') => Record<string, string | number>, separator?: string) => string
}

export function makePdfText(lang: PdfLang, translate: PdfTranslate): PdfText {
  const languages = pdfLanguages(lang)
  return {
    lang,
    one: (key, params) => translate(languages[0], key, params),
    line: (key, params) => languages.map((language) => translate(language, key, params)),
    joined: (key, params, separator = ' / ') => languages.map((language) => translate(language, key, params)).join(separator),
    joinedBy: (key, paramsFor, separator = ' / ') => languages.map((language) => translate(language, key, paramsFor(language))).join(separator),
  }
}

// 中日韩字符范围（用转义写，源码里不出现中文字符）
const CJK_PATTERN = /[\u3000-\u303f\u3400-\u9fff\uff00-\uffef]/

/**
 * 型号名称（货架 / 挂钩）的显示：型号名称是中文自由文本，英文版给中文名没有意义，所以英文版优先用型号编码（语言中性）。
 * 名称本身没有中文（人家就是用英文命名的）就原样显示；双语 = 「名称 (编码)」。
 */
export function localizedModelName(lang: PdfLang, name: string, code: string): string {
  const cleanName = name.trim()
  const cleanCode = code.trim()
  if (lang === 'zh') return cleanName || cleanCode
  const nameHasCjk = CJK_PATTERN.test(cleanName)
  if (lang === 'en') return nameHasCjk ? cleanCode || cleanName : cleanName || cleanCode
  return nameHasCjk && cleanCode ? `${cleanName} (${cleanCode})` : cleanName || cleanCode
}

// ---------------------------------------------------------------------------
// 数据来源：统一成 PlanogramSnapshotDto 形状
// ---------------------------------------------------------------------------

export interface PdfSource {
  snapshot: PlanogramSnapshotDto
  /** published = 某个已发布版本；current = 当前已保存版本（可能还是草稿）。 */
  origin: 'published' | 'current'
  /** 这一版是否已发布（决定页眉写「已发布 / 草稿」与校验行写法）。 */
  published: boolean
  /** 页眉日期来源：已发布 = 发布时间；否则 = 最后保存时间（ISO，Sydney 时区显示）。 */
  date: string | null
  /** 制图人：已发布 = 发布人；否则 = 最后保存人。 */
  by: string | null
}

export function sourceFromSnapshot(snapshot: PlanogramSnapshotDto): PdfSource {
  return { snapshot, origin: 'published', published: true, date: snapshot.publishedAt, by: snapshot.publishedBy }
}

function snapshotProductFromRow(row: ProductSpecListItemDto): PlanogramSnapshotProductDto | null {
  if (!row.spec) return null
  return {
    productCode: row.productCode,
    itemNumber: row.itemNumber ?? '',
    barcode: row.barcode ?? '',
    productName: row.productName,
    englishName: row.englishName ?? null,
    productImage: row.productImage ?? null,
    retailPrice: row.retailPrice,
    spec: row.spec,
  }
}

/**
 * 「当前已保存版本」：用 getPlan 的详情组装成与发布快照同形状的数据（商品的英文名 / 图片 / 零售价 / 规格来自详情里的 productSpecs），
 * 导出代码因此只认 PlanogramSnapshotDto 一种数据来源。挂钩型号只留方案用到的（与发布时固化的口径一致）。
 */
export function sourceFromDetail(detail: PlanogramDetailDto): PdfSource {
  const usedHooks = new Set(detail.items.map((item) => item.hookModelGuid))
  const header = detail.header
  const published = header.status === 1
  const snapshot: PlanogramSnapshotDto = {
    version: header.version,
    publishedAt: published ? header.publishedAt ?? null : null,
    publishedBy: null,
    plan: { guid: header.guid, name: header.name, notes: header.notes ?? null, bayCount: header.bayCount, gapXMm: header.gapXMm, gapYMm: header.gapYMm },
    shelfModel: detail.shelfModel,
    hookModels: detail.hookModels.filter((hook) => usedHooks.has(hook.guid)),
    products: detail.productSpecs.map(snapshotProductFromRow).filter((product): product is PlanogramSnapshotProductDto => product !== null),
    items: detail.items,
    stats: detail.stats,
  }
  return {
    snapshot,
    origin: 'current',
    published,
    date: published ? header.publishedAt ?? header.updatedAt ?? null : header.updatedAt ?? null,
    by: header.updatedBy ?? null,
  }
}

// ---------------------------------------------------------------------------
// 行号 / 序号 / 名称
// ---------------------------------------------------------------------------

/** 内部行号（自板顶）→ 显示行号：货架型号「行号起算 = 自底板」时倒过来数（内部永远存自顶行号）。 */
export function displayRowNumber(shelf: Pick<ShelfModelDto, 'rowNumberingFrom' | 'holeRows'>, row: number): number {
  return shelf.rowNumberingFrom === 2 ? shelf.holeRows - row + 1 : row
}

export interface PdfRow {
  /** 清单序号（节 → 行 → 列排序后的 1..N）。 */
  no: number
  itemGuid: string
  bay: number
  col: number
  /** 显示行号（见 displayRowNumber）。 */
  row: number
  /** 货号（没有货号时用商品编码）。 */
  code: string
  barcode: string
  nameZh: string
  /** 英文名；没有时为 null（英文 / 双语版回退中文名并加 * 号）。 */
  nameEn: string | null
  hookName: string
  hookCode: string
  perHook: number
  /** 合计 = 面位 × 每钩；一钩一行，所以面位恒为 1。 */
  total: number
  price: number
  hasError: boolean
}

export interface PdfRowGroup {
  bay: number
  row: number
  rows: PdfRow[]
}

export interface PdfModel {
  source: PdfSource
  ctx: LayoutContext | null
  items: DesignerItem[]
  /** productCode → 商品行（给画布名称 / 价格 / 货号 / 图片用）。 */
  specs: Record<string, ProductSpecListItemDto>
  /** 条目 guid → 清单序号。 */
  numbers: Map<string, number>
  rows: PdfRow[]
  groups: PdfRowGroup[]
  conflicts: LayoutConflict[]
  conflictsByGuid: Map<string, LayoutConflict[]>
  errorCount: number
  warnCount: number
  stats: PlanogramStatsDto
  /** 每节挂钩数（节号 → 个数）。 */
  hooksPerBay: Array<{ bay: number; count: number }>
  /** 主要挂钩型号（用得最多的）与型号种数，页眉「挂钩 …」用。 */
  mainHook: HookModelDto | null
  hookKinds: number
  /** 英文 / 双语版里有商品没有英文名（回退中文名并加了 * 号）。 */
  englishFallbackUsed: boolean
}

/** 打印用的商品名：返回中文名与英文名（缺英文名时 en = null）。 */
export function productNames(info: Pick<ProductSpecListItemDto, 'productName' | 'englishName'> | undefined, fallbackCode: string): { zh: string; en: string | null } {
  const zh = (info?.productName ?? '').trim() || fallbackCode
  const en = (info?.englishName ?? '').trim()
  return { zh, en: en || null }
}

/**
 * 按导出语言决定画在版面图 / 清单里的商品名：
 * 中文 → 中文名；英文 → 英文名，缺英文名时回退中文名并在后面加 *（页脚说明「* 无英文名」）；双语 → 主行中文名，副行英文名（同样的回退规则）。
 */
export function printNameFor(
  info: Pick<ProductSpecListItemDto, 'productName' | 'englishName'> | undefined,
  fallbackCode: string,
  lang: PdfLang,
): { primary: string; secondary: string | null } {
  const { zh, en } = productNames(info, fallbackCode)
  const english = en ?? `${zh}*`
  if (lang === 'zh') return { primary: zh, secondary: null }
  if (lang === 'en') return { primary: english, secondary: null }
  return { primary: zh, secondary: english }
}

function modelItemFrom(item: PlanogramSnapshotDto['items'][number]): DesignerItem {
  return {
    guid: item.guid,
    bayIndex: item.bayIndex,
    productCode: item.productCode,
    hookModelGuid: item.hookModelGuid,
    holeCol: item.holeCol,
    holeRow: item.holeRow,
    perHookQty: item.perHookQty,
    // 导出的是固化 / 已保存版本，「参数已变」标记对它没有意义
    specChanged: false,
  }
}

function specRowsFrom(snapshot: PlanogramSnapshotDto): Record<string, ProductSpecListItemDto> {
  const specs: Record<string, ProductSpecListItemDto> = {}
  for (const product of snapshot.products) {
    specs[product.productCode] = {
      productCode: product.productCode,
      itemNumber: product.itemNumber,
      barcode: product.barcode,
      productName: product.productName,
      englishName: product.englishName ?? null,
      productImage: product.productImage ?? null,
      retailPrice: product.retailPrice,
      categoryName: null,
      hasSpec: true,
      referencedPlanCount: 0,
      spec: product.spec,
    }
  }
  return specs
}

/**
 * 组装 PDF 数据模型：布局上下文、本地复算冲突（导出会在图上标红）、按「节 → 行 → 列」排序后的清单行与分组、统计。
 * 冲突用设计器 / 后端同一套引擎在快照数据上现算，所以已发布版本（发布时必然无 error 级冲突）与当前保存版本口径一致。
 */
export function buildPdfModel(source: PdfSource): PdfModel {
  const { snapshot } = source
  const items = snapshot.items.map(modelItemFrom)
  const specs = specRowsFrom(snapshot)
  const ctx = buildLayoutContext({
    shelf: snapshot.shelfModel,
    hooks: snapshot.hookModels,
    specs,
    bayCount: snapshot.plan.bayCount,
    gapXMm: snapshot.plan.gapXMm,
    gapYMm: snapshot.plan.gapYMm,
  })
  const conflicts = ctx ? validateLayout(ctx, items).conflicts : []
  const conflictsByGuid = conflictsByItem(conflicts)
  const errorItemGuids = new Set(conflicts.filter((conflict) => conflict.severity === 'error').map((conflict) => conflict.itemGuid))

  const hooksByGuid = new Map(snapshot.hookModels.map((hook) => [hook.guid, hook]))
  const ordered = sortChecklistItems(items)
  const numbers = new Map<string, number>()
  const rows: PdfRow[] = ordered.map((item, index) => {
    const no = index + 1
    numbers.set(item.guid, no)
    const info = specs[item.productCode]
    const hook = hooksByGuid.get(item.hookModelGuid)
    const layoutSpec = ctx?.specs[item.productCode]
    const layoutHook = ctx?.hooks[item.hookModelGuid]
    const perHook = layoutSpec && layoutHook ? resolvePerHookQty(item, layoutHook, layoutSpec) : 0
    const names = productNames(info, item.productCode)
    return {
      no,
      itemGuid: item.guid,
      bay: item.bayIndex,
      col: item.holeCol,
      row: displayRowNumber(snapshot.shelfModel, item.holeRow),
      code: info?.itemNumber || item.productCode,
      barcode: info?.barcode ?? '',
      nameZh: names.zh,
      nameEn: names.en,
      hookName: hook?.name ?? '',
      hookCode: hook?.code ?? '',
      perHook,
      total: perHook,
      price: info?.retailPrice ?? 0,
      hasError: errorItemGuids.has(item.guid),
    }
  })

  // 分组：同一节、同一行挂孔为一组（自上而下上货），组内已按列排序
  const groups: PdfRowGroup[] = []
  for (const row of rows) {
    const last = groups[groups.length - 1]
    if (last && last.bay === row.bay && last.row === row.row) last.rows.push(row)
    else groups.push({ bay: row.bay, row: row.row, rows: [row] })
  }

  const perBay = new Map<number, number>()
  for (const item of items) perBay.set(item.bayIndex, (perBay.get(item.bayIndex) ?? 0) + 1)
  const hooksPerBay = [...perBay.entries()].sort((a, b) => a[0] - b[0]).map(([bay, count]) => ({ bay, count }))

  const hookUse = new Map<string, number>()
  for (const item of items) hookUse.set(item.hookModelGuid, (hookUse.get(item.hookModelGuid) ?? 0) + 1)
  const topHookGuid = [...hookUse.entries()].sort((a, b) => b[1] - a[1])[0]?.[0]

  return {
    source,
    ctx,
    items,
    specs,
    numbers,
    rows,
    groups,
    conflicts,
    conflictsByGuid,
    errorCount: errorItemGuids.size,
    warnCount: new Set(conflicts.filter((conflict) => conflict.severity === 'warn').map((conflict) => conflict.itemGuid)).size,
    stats: snapshot.stats,
    hooksPerBay,
    mainHook: topHookGuid ? hooksByGuid.get(topHookGuid) ?? null : null,
    hookKinds: hookUse.size,
    englishFallbackUsed: rows.some((row) => row.nameEn === null),
  }
}

// ---------------------------------------------------------------------------
// 纸张与版面常量（逻辑像素：1 mm = 2.9 px，A3 横向 = 设计稿 05-pdf.html 的 1218 × 861）
// ---------------------------------------------------------------------------

/** 每页根元素的选择器（printUtils 的分页 PDF 按它逐页截图）。 */
export const PDF_PAGE_SELECTOR = '.planogram-pdf-page'

export const PX_PER_MM = 2.9
export const PAGE_MARGIN_X = 34
export const PAGE_MARGIN_TOP = 28
/** 页脚线距页面底边、页脚高度、页码距底边。 */
export const FOOTER_BOTTOM = 26
export const FOOTER_HEIGHT = 24
/**
 * 英文 / 双语的页脚文案更长（校验行 + 「* 无英文名」说明 + 品牌行），一行放不下会被截断：
 * 页脚允许折成两行，所以高度加到 38；中文版保持 24（单行，版面与以前完全一致）。
 */
export const FOOTER_HEIGHT_LATIN = 38
/**
 * 窄页面（A4 纵向的版面图页，541 px 内容宽）上，英文 / 双语的校验行 + 「* 无英文名」说明要占三行，
 * 所以页脚加到 52，并把品牌行挪到底部页码那一行（见 PlanogramPrintPages 的 PageFooter）。
 */
export const FOOTER_HEIGHT_LATIN_NARROW = 52
export const NARROW_FRAME_MAX_WIDTH = 700

export function isNarrowFrame(frame: Pick<PdfPageFrame, 'widthPx'>): boolean {
  return frame.widthPx < NARROW_FRAME_MAX_WIDTH
}

export function footerHeight(lang: PdfLang, frame?: Pick<PdfPageFrame, 'widthPx'>): number {
  if (lang === 'zh') return FOOTER_HEIGHT
  return frame && isNarrowFrame(frame) ? FOOTER_HEIGHT_LATIN_NARROW : FOOTER_HEIGHT_LATIN
}
export const PAGE_NO_BOTTOM = 8
/** 版面图 SVG 与右侧说明栏之间的空隙、说明栏最小宽度、A4 纵向底部说明栏高度。 */
export const SIDE_GAP = 14
export const SIDE_PANEL_MIN = 150
export const BOTTOM_PANEL_HEIGHT = 112
/** 双语时图例 / 要点每项两行，下方说明栏要更高。 */
export const BOTTOM_PANEL_HEIGHT_BILINGUAL = 196
/** 纯英文时图例「Box: …」「Red box = …」两项在窄页折成两行，内容实测高 128（中文 99），按 136 留余量，否则最后一行会压到页脚分隔线上。 */
export const BOTTOM_PANEL_HEIGHT_EN = 136

export function bottomPanelHeight(lang: PdfLang): number {
  if (lang === 'bi') return BOTTOM_PANEL_HEIGHT_BILINGUAL
  return lang === 'en' ? BOTTOM_PANEL_HEIGHT_EN : BOTTOM_PANEL_HEIGHT
}
/** 版面图 SVG 的标尺与留白（与 BoardCanvas 的 printMetrics 同值）。 */
export const BOARD_RULER_LEFT = 36
export const BOARD_RULER_TOP = 44
export const BOARD_PAD = 10
/** 执行清单：页头下方合计行高度、表头 / 数据行 / 分组行高度、栏间距、续页提示行高度。 */
export const SUMMARY_HEIGHT = 36
/**
 * 窄清单页（A4 纵向，宽 861）的双语页头：标题折 2 行、排序说明折 2 行、右侧孔位说明折 3 行，
 * 真实量得页头底边 y=144，会盖住默认 y=106 的合计行；合计行本身也折成两行（高约 51）。
 * 所以窄页双语把合计行下移到 152（页头底边 + 8 的余量）、合计行占 56，表格随之下移（行数少放几行，分页自动跟随）。
 */
export const NARROW_CHECKLIST_MAX_WIDTH = 1000
export const NARROW_BILINGUAL_SUMMARY_TOP = 152
export const NARROW_BILINGUAL_SUMMARY_HEIGHT = 56

function isNarrowBilingualChecklist(frame: Pick<PdfPageFrame, 'widthPx'>, lang: PdfLang): boolean {
  return lang === 'bi' && frame.widthPx < NARROW_CHECKLIST_MAX_WIDTH
}

/** 清单页合计行的顶边：默认等于页面内容起点，窄页双语因页头折行更高而下移。 */
export function checklistSummaryTop(frame: Pick<PdfPageFrame, 'widthPx' | 'contentTop'>, lang: PdfLang): number {
  return isNarrowBilingualChecklist(frame, lang) ? NARROW_BILINGUAL_SUMMARY_TOP : frame.contentTop
}

/** 清单页合计行（含下方间距）占的高度，表格从「合计行顶边 + 这个高度」开始。 */
export function checklistSummaryHeight(frame: Pick<PdfPageFrame, 'widthPx'>, lang: PdfLang): number {
  return isNarrowBilingualChecklist(frame, lang) ? NARROW_BILINGUAL_SUMMARY_HEIGHT : SUMMARY_HEIGHT
}
export const CHECKLIST_COLUMN_GAP = 16
export const CONTINUED_NOTE_HEIGHT = 20
export const CHECKLIST_ROW_HEIGHT = 27
export const CHECKLIST_ROW_HEIGHT_BILINGUAL = 36
export const CHECKLIST_GROUP_HEIGHT = 24
export const CHECKLIST_HEAD_HEIGHT = 26
export const CHECKLIST_HEAD_HEIGHT_BILINGUAL = 38
/** 清单里商品名一列的最小宽度：两栏版式放不下就改单栏。 */
export const CHECKLIST_PRODUCT_MIN = 96

/** 一类页面（版面图页 / 清单页）的逻辑尺寸与版式。 */
export interface PdfPageFrame {
  widthPx: number
  heightPx: number
  /** 页头下沿 = 版面图 / 清单表格的起点（px）。 */
  contentTop: number
}

export interface PdfPaperSpec {
  key: PdfPaper
  format: PdfPageFormat
  orientation: 'landscape' | 'portrait'
  widthMm: number
  heightMm: number
  /** 版面图页：A3 横向每页 2 节，A4 横向 / 纵向每页 1 节；图例 / 合计 / 要点放在右侧，A4 纵向宽度不够就放在下方。 */
  board: PdfPageFrame & { maxBaysPerPage: number; panel: 'side' | 'bottom' }
  /**
   * 执行清单页：两栏版式需要约 1100 px 以上的宽度，所以 A4 的清单页也按 A3 横向的逻辑尺寸排版（页面比例相同，
   * 出图时整页缩小到 A4，字号约 7.6 pt，一页仍有两栏约 40 行），A4 纵向则用 861 × 1218 的单栏。
   */
  checklist: PdfPageFrame & { columns: 1 | 2 }
}

export const PDF_PAPERS: Record<PdfPaper, PdfPaperSpec> = {
  'a3-landscape': {
    key: 'a3-landscape', format: { paper: 'a3', orientation: 'landscape' }, orientation: 'landscape', widthMm: 420, heightMm: 297,
    board: { widthPx: 1218, heightPx: 861, contentTop: 106, maxBaysPerPage: 2, panel: 'side' },
    checklist: { widthPx: 1218, heightPx: 861, contentTop: 106, columns: 2 },
  },
  'a4-landscape': {
    key: 'a4-landscape', format: { paper: 'a4', orientation: 'landscape' }, orientation: 'landscape', widthMm: 297, heightMm: 210,
    board: { widthPx: 861, heightPx: 609, contentTop: 106, maxBaysPerPage: 1, panel: 'side' },
    checklist: { widthPx: 1218, heightPx: 861, contentTop: 106, columns: 2 },
  },
  'a4-portrait': {
    key: 'a4-portrait', format: { paper: 'a4', orientation: 'portrait' }, orientation: 'portrait', widthMm: 210, heightMm: 297,
    board: { widthPx: 609, heightPx: 861, contentTop: 140, maxBaysPerPage: 1, panel: 'bottom' },
    checklist: { widthPx: 861, heightPx: 1218, contentTop: 106, columns: 1 },
  },
}

/** 页脚线上沿（px）：页面高 − 页脚距底 − 页脚高。 */
export function footerTop(frame: Pick<PdfPageFrame, 'heightPx' | 'widthPx'>, lang: PdfLang = 'zh'): number {
  return frame.heightPx - FOOTER_BOTTOM - footerHeight(lang, frame)
}

// ---------------------------------------------------------------------------
// 版面图：比例与分页
// ---------------------------------------------------------------------------

export interface BoardLayout {
  /** 1 mm 对应的像素数（SVG 里的 scale）。 */
  scale: number
  /** 纸上比例分母（1 : ratio）：纸上 1 mm 对应实物 ratio mm。 */
  ratio: number
  baysPerPage: number
  pages: Array<{ bayFrom: number; bayTo: number }>
  /** 满页（baysPerPage 节）SVG 的尺寸。 */
  svgWidth: number
  svgHeight: number
  /** 固定比例（1:5 / 1:10）放不下纸张高度，已自动缩小到能放下。 */
  adjusted: boolean
}

/** 比例模式 → 纸上 1 mm 对应的像素数（固定比例）；auto 返回 null。 */
export function fixedScaleFor(mode: PdfScaleMode): number | null {
  if (mode === '1:5') return PX_PER_MM / 5
  if (mode === '1:10') return PX_PER_MM / 10
  return null
}

/**
 * 版面图的比例与分页：
 * - 自动适配：每页 maxBaysPerPage 节（A3 横向 2，其余 1），按可用宽 / 高取能放下的最大比例；所有页共用同一个比例；
 * - 固定比例（1:5 / 1:10）：按纸张比例换算，每页能放几节就放几节；高度放不下时自动缩小（adjusted = true，界面会提示）。
 */
export function computeBoardLayout(
  paper: PdfPaperSpec,
  shelf: Pick<ShelfModelDto, 'bayWidthMm' | 'totalHeightMm'>,
  bayCount: number,
  mode: PdfScaleMode,
  lang: PdfLang = 'zh',
): BoardLayout {
  const bays = Math.max(1, bayCount)
  const frame = paper.board
  const regionWidth = frame.widthPx - 2 * PAGE_MARGIN_X - (frame.panel === 'side' ? SIDE_PANEL_MIN + SIDE_GAP : 0)
  const bottomPanel = frame.panel === 'bottom' ? bottomPanelHeight(lang) + 8 : 0
  const regionHeight = footerTop(frame, lang) - 8 - frame.contentTop - bottomPanel
  const availWidth = regionWidth - BOARD_RULER_LEFT - BOARD_PAD
  const byHeight = (regionHeight - BOARD_RULER_TOP - BOARD_PAD) / shelf.totalHeightMm
  const fixed = fixedScaleFor(mode)

  let scale: number
  let baysPerPage: number
  let adjusted = false
  if (fixed === null) {
    baysPerPage = Math.min(frame.maxBaysPerPage, bays)
    scale = Math.min(availWidth / (shelf.bayWidthMm * baysPerPage), byHeight)
  } else {
    scale = Math.min(fixed, byHeight)
    adjusted = scale < fixed - 1e-9
    baysPerPage = Math.max(1, Math.min(bays, Math.floor(availWidth / (shelf.bayWidthMm * scale) + 1e-9)))
    // 一节都放不下宽度：只能再缩（仍然只放一节）
    scale = Math.min(scale, availWidth / shelf.bayWidthMm)
    if (scale < fixed - 1e-9) adjusted = true
  }
  scale = Math.max(0.05, Number.isFinite(scale) ? scale : 0.05)

  const pages: Array<{ bayFrom: number; bayTo: number }> = []
  for (let from = 1; from <= bays; from += baysPerPage) pages.push({ bayFrom: from, bayTo: Math.min(bays, from + baysPerPage - 1) })
  return {
    scale,
    ratio: Math.round((PX_PER_MM / scale) * 10) / 10,
    baysPerPage,
    pages,
    svgWidth: BOARD_RULER_LEFT + shelf.bayWidthMm * baysPerPage * scale + BOARD_PAD,
    svgHeight: BOARD_RULER_TOP + shelf.totalHeightMm * scale + BOARD_PAD,
    adjusted,
  }
}

// ---------------------------------------------------------------------------
// 执行清单：列、分栏、分页
// ---------------------------------------------------------------------------

export type ChecklistColumnKey = 'no' | 'bay' | 'pos' | 'code' | 'barcode' | 'product' | 'hook' | 'perHook' | 'total' | 'retail' | 'checked' | 'notes'

/** 列宽（px）；product 是弹性列（取剩余宽度，这里的值是下限）。条码 / 已上货两列只有勾选时才出现。 */
export const CHECKLIST_COLUMN_WIDTHS: Record<ChecklistColumnKey, number> = {
  no: 28, bay: 24, pos: 52, code: 84, barcode: 104, product: CHECKLIST_PRODUCT_MIN, hook: 100, perHook: 46, total: 40, retail: 46, checked: 52, notes: 46,
}

/**
 * 英文 / 双语的列标题更长（Bay / Col/Row / Per hook）：节、孔位、每钩三列放宽，挂钩列英文只显示型号编码所以可以收窄，
 * 这样两栏版式仍能给商品名留出 ≥ 96 px。条码 / 已上货两列各语言通用。
 */
export function checklistColumnWidths(lang: PdfLang): Record<ChecklistColumnKey, number> {
  // 英文 / 双语：表头（Bay / Col/Row / Per hook）留出 ≥ 15% 余量——字体不同（Windows 的 Arial / Segoe UI 比 macOS 宽）时也放得下；
  // 英文版挂钩列只放型号编码所以收窄；两种语言商品名列都仍 ≥ 96 px，保证 A3 横向两栏。
  if (lang === 'en') return { ...CHECKLIST_COLUMN_WIDTHS, bay: 32, pos: 58, hook: 76, perHook: 58, total: 40, notes: 44 }
  if (lang === 'bi') return { ...CHECKLIST_COLUMN_WIDTHS, bay: 30, pos: 56, code: 72, hook: 100, perHook: 54, total: 38, retail: 44, notes: 36 }
  return CHECKLIST_COLUMN_WIDTHS
}

// ---------------------------------------------------------------------------
// 清单单元格文字：商品名 / 挂钩名按列宽折行（最多 2 行，放不下加省略号）
// ---------------------------------------------------------------------------

/** 单元格左右内边距（与 sheetPages.css 的 td padding 一致）与边框。 */
export const CHECKLIST_CELL_PADDING_X = 5
/** 清单里需要折行的文字字号（px）：商品名主行 / 副行（双语的英文名）/ 挂钩名。 */
export const CHECKLIST_NAME_FONT = 10.5
export const CHECKLIST_SECONDARY_FONT = 9.5
export const CHECKLIST_HOOK_FONT = 10

export interface CellLines {
  /** 主行（中文版 / 英文版整段；双语的中文名）。 */
  lines: string[]
  /** 副行（双语的英文名 / 型号编码），小一号灰字。 */
  secondary: string[]
}

/** 单元格里能放文字的宽度（px）：列宽 − 左右内边距 − 边框。 */
export function cellContentWidth(columnWidth: number): number {
  return Math.max(0, columnWidth - 2 * CHECKLIST_CELL_PADDING_X - 1)
}

/** 折行：宽度估算沿用设计器的 wrapLabel（中日韩字 1 倍字号、拉丁字 0.56 倍）；一个字都放不下时原样返回，不丢内容。 */
function fitLines(text: string, widthPx: number, fontPx: number, maxLines: number): string[] {
  const clean = text.trim()
  if (!clean) return []
  const lines = wrapLabel(clean, widthPx, fontPx, maxLines)
  return lines.length > 0 ? lines : [clean]
}

/**
 * 商品名单元格：中文版一行（与以前一致）；英文版最多 2 行；双语版中文名最多 2 行 + 英文名取剩余（合计最多 3 行，行高 36 px 放得下）。
 * 超出的部分在最后一行加「…」——截图（html2canvas）不支持 CSS 的 text-overflow，必须自己截。
 */
export function productCellLines(lang: PdfLang, row: Pick<PdfRow, 'nameZh' | 'nameEn'>, columnWidth: number): CellLines {
  const width = cellContentWidth(columnWidth)
  const english = row.nameEn ?? `${row.nameZh}*`
  if (lang === 'zh') return { lines: [row.nameZh], secondary: [] }
  if (lang === 'en') return { lines: fitLines(english, width, CHECKLIST_NAME_FONT, 2), secondary: [] }
  // 双语：中文名最多 2 行，英文名取剩下的（总行数 ≤ 3，行高 36 放得下）：中文长名字不再被截成「…横线 8」
  const primary = fitLines(row.nameZh, width, CHECKLIST_NAME_FONT, 2)
  return { lines: primary, secondary: fitLines(english, width, CHECKLIST_SECONDARY_FONT, Math.max(1, 3 - primary.length)) }
}

/** 挂钩单元格：名称里常带臂长（「… 200」与「… 250」），所以最多折 2 行而不是截断；双语在名称下面再加一行型号编码。 */
export function hookCellLines(lang: PdfLang, row: Pick<PdfRow, 'hookName' | 'hookCode'>, columnWidth: number): CellLines {
  const width = cellContentWidth(columnWidth)
  if (lang === 'bi') {
    const name = row.hookName || row.hookCode
    return { lines: fitLines(name, width, CHECKLIST_HOOK_FONT, 2), secondary: row.hookCode && row.hookCode !== name ? [row.hookCode] : [] }
  }
  return { lines: fitLines(localizedModelName(lang, row.hookName, row.hookCode), width, CHECKLIST_HOOK_FONT, 2), secondary: [] }
}

/** 列标题文案键（字面量写在这里：契约测试按字面量核对中英文消息文件里都有）。 */
export const CHECKLIST_COLUMN_LABEL_KEYS: Record<ChecklistColumnKey, string> = {
  no: 'planogram.pdf.col.no',
  bay: 'planogram.pdf.col.bay',
  pos: 'planogram.pdf.col.pos',
  code: 'planogram.pdf.col.code',
  barcode: 'planogram.pdf.col.barcode',
  product: 'planogram.pdf.col.product',
  hook: 'planogram.pdf.col.hook',
  perHook: 'planogram.pdf.col.perHook',
  total: 'planogram.pdf.col.total',
  retail: 'planogram.pdf.col.retail',
  checked: 'planogram.pdf.col.checked',
  notes: 'planogram.pdf.col.notes',
}

export function checklistColumns(options: Pick<PdfOptions, 'barcode' | 'checkColumn'>): ChecklistColumnKey[] {
  const columns: ChecklistColumnKey[] = ['no', 'bay', 'pos', 'code']
  if (options.barcode) columns.push('barcode')
  columns.push('product', 'hook', 'perHook', 'total', 'retail')
  if (options.checkColumn) columns.push('checked')
  columns.push('notes')
  return columns
}

export type ChecklistEntry =
  | { kind: 'group'; bay: number; row: number }
  | { kind: 'row'; row: PdfRow }

export interface ChecklistLayout {
  columns: ChecklistColumnKey[]
  /** 每页分几栏（两栏放不下商品名最小宽度时改单栏）。 */
  columnsPerPage: 1 | 2
  /** 一栏的宽度与各列宽度（px；商品名列取剩余宽度）。 */
  columnWidth: number
  productWidth: number
  columnWidths: Record<ChecklistColumnKey, number>
  /** 表格起点 / 一栏表体可用高度 / 行高 / 分组行高 / 表头高度。 */
  tableTop: number
  bodyHeight: number
  rowHeight: number
  groupHeight: number
  headHeight: number
}

export function computeChecklistLayout(paper: PdfPaperSpec, options: Pick<PdfOptions, 'barcode' | 'checkColumn' | 'lang'>): ChecklistLayout {
  const bilingual = options.lang === 'bi'
  const columns = checklistColumns(options)
  const widths = checklistColumnWidths(options.lang)
  const fixedWidth = columns.filter((key) => key !== 'product').reduce((sum, key) => sum + widths[key], 0)
  const frame = paper.checklist
  const contentWidth = frame.widthPx - 2 * PAGE_MARGIN_X
  const twoColumnWidth = (contentWidth - CHECKLIST_COLUMN_GAP) / 2
  const columnsPerPage: 1 | 2 = frame.columns === 2 && twoColumnWidth - fixedWidth >= CHECKLIST_PRODUCT_MIN ? 2 : 1
  const columnWidth = columnsPerPage === 2 ? twoColumnWidth : contentWidth
  const tableTop = checklistSummaryTop(frame, options.lang) + checklistSummaryHeight(frame, options.lang)
  const headHeight = bilingual ? CHECKLIST_HEAD_HEIGHT_BILINGUAL : CHECKLIST_HEAD_HEIGHT
  const bodyHeight = footerTop(frame, options.lang) - CONTINUED_NOTE_HEIGHT - tableTop - headHeight
  return {
    columns,
    columnsPerPage,
    columnWidth,
    productWidth: Math.floor(columnWidth - fixedWidth),
    columnWidths: widths,
    tableTop,
    bodyHeight,
    rowHeight: bilingual ? CHECKLIST_ROW_HEIGHT_BILINGUAL : CHECKLIST_ROW_HEIGHT,
    groupHeight: CHECKLIST_GROUP_HEIGHT,
    headHeight,
  }
}

export interface ChecklistPage {
  /** 每栏一组条目（分组标题行 + 数据行）。 */
  columns: ChecklistEntry[][]
  firstNo: number
  lastNo: number
}

/**
 * 清单分栏分页：按「节 → 行 → 列」顺序依次填栏、满栏换下一栏、满页换下一页。
 * 一栏里新分组的标题行要和它的第一行一起放得下，否则整组换到下一栏（不留孤零零的标题）；
 * 同一组被栏 / 页切开时，下一栏顶部重复一次分组标题（与设计稿 05 一致）。
 */
export function paginateChecklist(
  groups: readonly PdfRowGroup[],
  layout: Pick<ChecklistLayout, 'columnsPerPage' | 'bodyHeight' | 'rowHeight' | 'groupHeight'>,
): ChecklistPage[] {
  const pages: ChecklistPage[] = []
  // 游标放进对象里：闭包里改写的变量，TypeScript 在调用点会把它收窄成初值（null），用对象属性就不会
  const cursor: { page: ChecklistPage | null; column: ChecklistEntry[]; used: number } = { page: null, column: [], used: 0 }

  const startPage = () => {
    const column: ChecklistEntry[] = []
    const page: ChecklistPage = { columns: [column], firstNo: 0, lastNo: 0 }
    pages.push(page)
    cursor.page = page
    cursor.column = column
    cursor.used = 0
  }
  const nextColumn = () => {
    if (!cursor.page || cursor.page.columns.length >= layout.columnsPerPage) {
      startPage()
      return
    }
    const column: ChecklistEntry[] = []
    cursor.page.columns.push(column)
    cursor.column = column
    cursor.used = 0
  }

  for (const group of groups) {
    let needHeader = true
    for (const row of group.rows) {
      if (!cursor.page) startPage()
      const need = (needHeader ? layout.groupHeight : 0) + layout.rowHeight
      // 空栏里也放不下（行高超过栏高的极端情况）就硬放一行，避免死循环
      if (cursor.used + need > layout.bodyHeight && cursor.column.length > 0) {
        nextColumn()
        needHeader = true
      }
      if (needHeader) {
        cursor.column.push({ kind: 'group', bay: group.bay, row: group.row })
        cursor.used += layout.groupHeight
        needHeader = false
      }
      cursor.column.push({ kind: 'row', row })
      cursor.used += layout.rowHeight
      const current = cursor.page as ChecklistPage
      if (current.firstNo === 0) current.firstNo = row.no
      current.lastNo = row.no
    }
  }
  return pages
}

// ---------------------------------------------------------------------------
// 整体分页
// ---------------------------------------------------------------------------

export interface BoardPagePlan {
  kind: 'board'
  pageNo: number
  bayFrom: number
  bayTo: number
}

export interface ChecklistPagePlan extends ChecklistPage {
  kind: 'checklist'
  pageNo: number
}

export type PdfPagePlan = BoardPagePlan | ChecklistPagePlan

export interface PdfPlan {
  paper: PdfPaperSpec
  boardLayout: BoardLayout
  checklistLayout: ChecklistLayout
  pages: PdfPagePlan[]
  pageCount: number
  boardPageCount: number
  checklistPageCount: number
  /** 清单总行数。 */
  rowCount: number
}

/** 某一页的逻辑尺寸与版式（版面图页与清单页可以不同）。 */
export function pageFrameOf(paper: PdfPaperSpec, kind: PdfPagePlan['kind']): PdfPageFrame {
  return kind === 'board' ? paper.board : paper.checklist
}

/** 先版面图页（每页 baysPerPage 节，超过一页自动续页），再执行清单页；页码一路连续。 */
export function planPdfPages(model: PdfModel, options: PdfOptions): PdfPlan {
  const paper = PDF_PAPERS[options.paper]
  const snapshot = model.source.snapshot
  const boardLayout = computeBoardLayout(paper, snapshot.shelfModel, snapshot.plan.bayCount, options.scale, options.lang)
  const checklistLayout = computeChecklistLayout(paper, options)
  const pages: PdfPagePlan[] = []
  if (options.board) {
    for (const span of boardLayout.pages) pages.push({ kind: 'board', pageNo: pages.length + 1, bayFrom: span.bayFrom, bayTo: span.bayTo })
  }
  const boardPageCount = pages.length
  if (options.checklist) {
    for (const page of paginateChecklist(model.groups, checklistLayout)) pages.push({ kind: 'checklist', pageNo: pages.length + 1, ...page })
    // 没有任何明细时也要有一页（空清单页：只有表头与合计），否则勾了「执行清单」却什么都没有
    if (pages.length === boardPageCount) {
      pages.push({ kind: 'checklist', pageNo: pages.length + 1, columns: [[]], firstNo: 0, lastNo: 0 })
    }
  }
  return {
    paper,
    boardLayout,
    checklistLayout,
    pages,
    pageCount: pages.length,
    boardPageCount,
    checklistPageCount: pages.length - boardPageCount,
    rowCount: model.rows.length,
  }
}

// ---------------------------------------------------------------------------
// 页眉 / 文件名 / 展示格式
// ---------------------------------------------------------------------------

/** 页眉里的日期：悉尼时区的 YYYY-MM-DD；没有日期显示 —。 */
export function formatPdfDate(value: string | null | undefined): string {
  const text = formatUpdatedAt(value ?? null, true)
  return text === '—' ? text : text.slice(0, 10)
}

export interface PdfHeaderInfo {
  title: string
  /** 页眉第二行的几段（货架 / 挂钩 / 版本日期 / 制图人）。 */
  meta: string[]
}

/** 页眉文案（版面图第 1 页的页眉，也是弹窗「页眉文案预览」的来源）。 */
export function buildHeaderInfo(model: PdfModel, text: PdfText): PdfHeaderInfo {
  const { snapshot } = model.source
  const shelfName = localizedModelName(text.lang, snapshot.shelfModel.name, snapshot.shelfModel.code)
  const hookName = model.mainHook ? localizedModelName(text.lang, model.mainHook.name, model.mainHook.code) : '—'
  const version = snapshot.version
  const date = formatPdfDate(model.source.date)
  const versionText = model.source.published
    ? text.joined('planogram.pdf.metaVersionPublished', { version, date })
    : text.joined('planogram.pdf.metaVersionDraft', { version, date })
  const meta = [
    text.joined('planogram.pdf.metaShelf', { shelf: shelfName, bays: snapshot.plan.bayCount }),
    model.hookKinds > 1
      ? text.joined('planogram.pdf.metaHookMany', { hook: hookName, count: model.hookKinds })
      : text.joined('planogram.pdf.metaHook', { hook: hookName }),
    versionText,
  ]
  if (model.source.by) meta.push(text.joined('planogram.pdf.metaBy', { by: model.source.by }))
  return { title: text.joined('planogram.pdf.title', { name: snapshot.plan.name }), meta }
}

/** 导出弹窗里的页眉文案预览：用「 · 」连起来的一行（方案名 · 货架 · 间距 · 版本 · 日期 · 制图 · 页码）。 */
export function buildHeaderPreviewLine(model: PdfModel, text: PdfText): string {
  const { snapshot } = model.source
  const parts = [
    snapshot.plan.name,
    localizedModelName(text.lang, snapshot.shelfModel.name, snapshot.shelfModel.code),
    text.joined('planogram.pdf.previewGap', { gx: snapshot.plan.gapXMm, gy: snapshot.plan.gapYMm }),
    `v${snapshot.version}`,
    formatPdfDate(model.source.date),
    ...(model.source.by ? [text.joined('planogram.pdf.metaBy', { by: model.source.by })] : []),
    text.joined('planogram.pdf.previewPageNo'),
  ]
  return parts.join(' · ')
}

/** 文件名：Planogram_<方案名>_v<版本>_<zh|en|bi>_<yyyymmdd>.pdf（方案名里的非法字符换成 _；日期取悉尼当天）。 */
export function buildPdfFileName(args: { planName: string; version: number; lang: PdfLang; date?: Date }): string {
  const day = formatSydneyIsoDate(args.date ?? new Date()).replace(/-/g, '')
  const name = sanitizeFileNamePart(args.planName) || 'plan'
  return `Planogram_${name}_v${args.version}_${args.lang}_${day}.pdf`
}

/** 用户改过的文件名：补 .pdf 后缀、去掉路径分隔符等非法字符；空名回退到默认名。 */
export function normalizePdfFileName(input: string, fallback: string): string {
  const base = sanitizeFileNamePart(input.trim().replace(/\.pdf$/i, ''))
  return base ? `${base}.pdf` : fallback
}

/** 条目冲突 → 是否整体有 error 级冲突（导出弹窗底部「导出会在图上标红」用）。 */
export function countErrorItems(conflicts: readonly Pick<ConflictDto, 'itemGuid' | 'severity'>[]): number {
  return new Set(conflicts.filter((conflict) => conflict.severity === 'error').map((conflict) => conflict.itemGuid)).size
}

/** 清单里「节 / 行」分组标题文案。 */
export function groupTitle(text: PdfText, bay: number, row: number): string {
  return text.joined('planogram.pdf.checklist.group', { bay, row })
}

/** 清单「续」页提示：下一页的页码与序号范围。 */
export function continuedNote(text: PdfText, nextPageNo: number, from: number, to: number): string {
  return text.joined('planogram.pdf.checklist.continued', { page: nextPageNo, from, to })
}
