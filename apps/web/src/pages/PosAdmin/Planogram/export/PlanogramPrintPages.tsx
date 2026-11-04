import { useMemo, type CSSProperties, type ReactNode } from 'react'
import type { ProductSpecListItemDto } from '../../../../types/planogram'
import { BoardPrintSvg, PRINT_FONT_FAMILY, type BoardPrintLabels } from '../Designer/BoardCanvas'
import { formatNumber } from '../planogramLogic'
import {
  PAGE_MARGIN_TOP,
  PAGE_MARGIN_X,
  bottomPanelHeight,
  SIDE_GAP,
  FOOTER_BOTTOM,
  footerHeight,
  isNarrowFrame,
  PAGE_NO_BOTTOM,
  CHECKLIST_COLUMN_LABEL_KEYS,
  buildHeaderInfo,
  checklistSummaryTop,
  continuedNote,
  displayRowNumber,
  footerTop,
  formatMoney,
  formatMoneyTotal,
  groupTitle,
  hookCellLines,
  makePdfText,
  pageFrameOf,
  printNameFor,
  productCellLines,
  type BoardPagePlan,
  type CellLines,
  type ChecklistColumnKey,
  type ChecklistEntry,
  type ChecklistPagePlan,
  type PdfModel,
  type PdfOptions,
  type PdfPagePlan,
  type PdfPlan,
  type PdfText,
  type PdfTranslate,
} from './sheetLogic'
import './sheetPages.css'

/**
 * 商品陈列图 PDF 页面 DOM（A3 / A4 分页，每页 `.planogram-pdf-page`）：弹窗预览与真正出图是同一份 DOM。
 * 版面图是 BoardCanvas 的打印模式（同一套几何）；执行清单是普通表格；所有文案按导出语言从 planogram.pdf.* 取，不写死。
 * 截图（html2canvas）发生在页面 CSS 之外的独立文档里，所以关键样式都在 sheetPages.css 的 .planogram-pdf-page 作用域内，SVG 的字体 / 颜色写在 SVG 自身。
 */

interface PrintPagesProps {
  model: PdfModel
  plan: PdfPlan
  options: PdfOptions
  /** 商品货号 → 已加载好的实拍图 data URL（没勾选实拍图或加载失败的商品不在里面）。 */
  images: ReadonlyMap<string, string>
  translate: PdfTranslate
  /** 只渲染满足条件的页（弹窗缩略图只要第 1 页）；缺省 = 全部。 */
  pageFilter?: (page: PdfPagePlan) => boolean
}

/** 文字块：单语一行；双语时第二行用小一号的灰色（用在图例 / 要点 / 列名）。 */
function TextLines({ lines, className }: { lines: string[]; className?: string }) {
  return (
    <>
      {lines.map((line, index) => (
        <div key={index} className={`${className ?? ''}${index > 0 ? ' is-secondary' : ''}`.trim() || undefined}>{line}</div>
      ))}
    </>
  )
}

function PageFrame({ plan, page, children, style }: { plan: PdfPlan; page: PdfPagePlan; children: ReactNode; style?: CSSProperties }) {
  // 版面图页与清单页的逻辑尺寸可以不同（A4 的清单页按 A3 横向排版再缩小），但比例都等于所选纸张
  const frame = pageFrameOf(plan.paper, page.kind)
  return (
    <section
      className="planogram-pdf-page"
      data-page-kind={page.kind}
      data-page-no={page.pageNo}
      style={{ width: frame.widthPx, height: frame.heightPx, ...style }}
    >
      {children}
    </section>
  )
}

function PageFooter({ left, text, model, page, plan }: { left: ReactNode; text: PdfText; model: PdfModel; page: PdfPagePlan; plan: PdfPlan }) {
  const { snapshot } = model.source
  const frame = pageFrameOf(plan.paper, page.kind)
  const brand = text.one('planogram.pdf.brand', { name: snapshot.plan.name, version: snapshot.version })
  // 窄页面（A4 纵向版面图页）的英文 / 双语：说明文字占满整行，品牌行挪到底部页码那一行的左边，避免两块挤在一行里互相截断
  const brandOnPageRow = text.lang !== 'zh' && isNarrowFrame(frame)
  return (
    <>
      {/* 英文 / 双语页脚文案更长：允许折成两行（窄页面三行）；中文版仍是单行（高度 24） */}
      <div className={`pdfp-foot${text.lang === 'zh' ? '' : ' is-multiline'}`} style={{ left: PAGE_MARGIN_X, right: PAGE_MARGIN_X, bottom: FOOTER_BOTTOM, height: footerHeight(text.lang, frame) }}>
        <span className="pdfp-foot-left">{left}</span>
        {brandOnPageRow ? null : <span className="pdfp-foot-right">{brand}</span>}
      </div>
      {brandOnPageRow ? <div className="pdfp-brand" style={{ left: PAGE_MARGIN_X, bottom: PAGE_NO_BOTTOM }}>{brand}</div> : null}
      <div className="pdfp-pageno" style={{ right: PAGE_MARGIN_X, bottom: PAGE_NO_BOTTOM }}>
        {text.joined('planogram.pdf.page', { page: page.pageNo, total: plan.pageCount })}
      </div>
    </>
  )
}

// ---------------------------------------------------------------------------
// 版面图页
// ---------------------------------------------------------------------------

interface BoardPageProps extends PrintPagesProps {
  page: BoardPagePlan
  text: PdfText
}

function BoardPage({ model, plan, options, images, translate, page, text }: BoardPageProps) {
  const { snapshot } = model.source
  const shelf = snapshot.shelfModel
  const { paper, boardLayout } = plan
  const frame = paper.board
  // A4 纵向的页头只有 541 px 宽：双语时标题仍双语，货架 / 挂钩 / 版本等说明只写中文，避免两种语言挤成五六行盖住版面图
  const compact = frame.panel === 'bottom'
  const headText = useMemo(() => (compact && text.lang === 'bi' ? makePdfText('zh', translate) : text), [compact, text, translate])
  const header = { ...buildHeaderInfo(model, headText), title: buildHeaderInfo(model, text).title }
  const lang = options.lang

  const labels = useMemo<BoardPrintLabels>(
    () => ({
      bayTitle: (bay, widthMm) => text.joined('planogram.pdf.bayTitle', { bay, width: formatNumber(widthMm) }),
      base: text.joined('planogram.pdf.base', { depth: formatNumber(shelf.baseDepthMm) }),
    }),
    [shelf.baseDepthMm, text],
  )
  const nameOf = useMemo(() => (info: ProductSpecListItemDto | undefined, code: string) => printNameFor(info, code, lang), [lang])
  const rowLabel = useMemo(() => (row: number) => displayRowNumber(shelf, row), [shelf])
  const printOptions = useMemo(() => ({ bubbles: options.bubbles, prices: options.prices, coords: options.coords }), [options.bubbles, options.coords, options.prices])

  const hook = model.mainHook
  const tagText = hook?.hasPriceTagArm ? formatNumber(hook.tagWidthMm) : '—'
  const tagSize = { w: tagText, h: hook?.hasPriceTagArm ? formatNumber(hook.tagHeightMm) : '—' }
  const rowFrom = shelf.rowNumberingFrom === 2 ? 'planogram.pdf.rowFromBase' : 'planogram.pdf.rowFromTop'

  // 校验行：有冲突写明数量（图上已标红），否则写「无越界 · 无重叠 · 间距」并注明是发布版还是保存版
  const validation =
    model.errorCount > 0
      ? text.joined('planogram.pdf.validation.conflicts', { count: model.errorCount })
      : model.warnCount > 0
        ? text.joined('planogram.pdf.validation.warn', { count: model.warnCount })
        : text.joined(model.source.published ? 'planogram.pdf.validation.ok' : 'planogram.pdf.validation.okDraft', {
            gx: snapshot.plan.gapXMm,
            gy: snapshot.plan.gapYMm,
            version: snapshot.version,
          })
  const footNotes = [validation, ...(model.englishFallbackUsed && lang !== 'zh' ? [text.joined('planogram.pdf.checklist.noEnglish')] : [])]

  const bottomPanel = frame.panel === 'bottom'
  const panelLeft = PAGE_MARGIN_X + boardLayout.svgWidth + SIDE_GAP
  const panelWidth = Math.min(260, frame.widthPx - PAGE_MARGIN_X - panelLeft)

  const panel = (
    <div className={`pdfp-panel${bottomPanel ? ' is-bottom' : ''}`}>
      <section>
        <div className="pdfp-panel-title">{text.joined('planogram.pdf.legend.title')}</div>
        <div className="pdfp-legend-line">
          <span className="pdfp-badge">1</span>
          <span><TextLines lines={text.line('planogram.pdf.legend.number')} /></span>
        </div>
        <TextLines lines={text.line('planogram.pdf.legend.holes')} />
        <TextLines lines={text.line('planogram.pdf.legend.tag', tagSize)} />
        <TextLines lines={text.line('planogram.pdf.legend.box')} />
        {model.errorCount > 0 ? <TextLines lines={text.line('planogram.pdf.legend.conflict')} className="is-conflict" /> : null}
      </section>
      <section>
        <div className="pdfp-panel-title">{text.joined('planogram.pdf.totals.title')}</div>
        <TextLines lines={text.line('planogram.pdf.totals.products', { sku: model.stats.skuCount, hooks: model.stats.itemCount })} />
        <TextLines lines={text.line('planogram.pdf.totals.units', { units: model.stats.unitCount, retail: formatMoneyTotal(model.stats.retailValue) })} />
        <TextLines lines={text.line('planogram.pdf.totals.fill', { fill: model.stats.fillPercent })} />
      </section>
      <section>
        <div className="pdfp-panel-title">{text.joined('planogram.pdf.tips.title')}</div>
        <TextLines lines={text.line('planogram.pdf.tips.t1')} />
        <TextLines lines={text.line('planogram.pdf.tips.t2')} />
        <TextLines lines={text.line('planogram.pdf.tips.t3')} />
      </section>
    </div>
  )

  return (
    <PageFrame plan={plan} page={page}>
      <PageHeader
        compact={compact}
        title={header.title}
        meta={header.meta}
        platform={text.one('planogram.pdf.platform')}
        right={[
          headText.joined('planogram.pdf.scale', { ratio: boardLayout.ratio }),
          headText.joined('planogram.pdf.pitch', { px: formatNumber(shelf.holePitchXMm), py: formatNumber(shelf.holePitchYMm), cols: shelf.holeCols, rows: shelf.holeRows }),
          // 「行号自板顶 / 自底板」是嵌进整句的文案片段：双语时两种语言各取各的
          headText.joinedBy('planogram.pdf.numbering', (language) => ({ gx: snapshot.plan.gapXMm, gy: snapshot.plan.gapYMm, from: translate(language, rowFrom) })),
        ]}
        top={PAGE_MARGIN_TOP}
      />
      <div className="pdfp-board" style={{ left: PAGE_MARGIN_X, top: frame.contentTop }}>
        {model.ctx ? (
          <BoardPrintSvg
            ctx={model.ctx}
            items={model.items}
            specs={model.specs}
            conflictsByGuid={model.conflictsByGuid}
            numbers={model.numbers}
            bayFrom={page.bayFrom}
            bayTo={page.bayTo}
            scale={boardLayout.scale}
            options={printOptions}
            images={images}
            lang={lang}
            nameOf={nameOf}
            labels={labels}
            rowLabel={rowLabel}
          />
        ) : null}
      </div>
      {bottomPanel ? (
        <div className="pdfp-panel-wrap is-bottom" style={{ left: PAGE_MARGIN_X, right: PAGE_MARGIN_X, bottom: frame.heightPx - footerTop(frame, lang) + 8, height: bottomPanelHeight(lang) }}>{panel}</div>
      ) : (
        <div className="pdfp-panel-wrap" style={{ left: panelLeft, width: panelWidth, top: frame.contentTop + 22 }}>{panel}</div>
      )}
      <PageFooter left={footNotes.map((note, index) => <span key={index} className="pdfp-foot-note">{note}</span>)} text={text} model={model} page={page} plan={plan} />
    </PageFrame>
  )
}

// ---------------------------------------------------------------------------
// 页头
// ---------------------------------------------------------------------------

function PageHeader({ compact, title, meta, platform, right, top }: { compact: boolean; title: string; meta: string[]; platform: string; right: string[]; top: number }) {
  return (
    <header className={`pdfp-head${compact ? ' is-compact' : ''}`} style={{ left: PAGE_MARGIN_X, right: PAGE_MARGIN_X, top }}>
      <div className="pdfp-head-left">
        <h1>{title}</h1>
        <div className="pdfp-meta">
          {meta.map((part, index) => <span key={index}>{part}</span>)}
        </div>
      </div>
      <div className="pdfp-head-right">
        <strong>{platform}</strong>
        {right.map((line, index) => <div key={index}>{line}</div>)}
      </div>
    </header>
  )
}

// ---------------------------------------------------------------------------
// 执行清单页
// ---------------------------------------------------------------------------

interface ChecklistPageProps extends PrintPagesProps {
  page: ChecklistPagePlan
  text: PdfText
}

function columnHeader(text: PdfText, key: ChecklistColumnKey): string[] {
  return text.line(CHECKLIST_COLUMN_LABEL_KEYS[key])
}

/** 折好行的单元格内容：主行 + 小一号灰色副行（每行单独一个 div，截图时每行各自裁切）。 */
function CellText({ cell }: { cell: CellLines }) {
  return (
    <>
      {cell.lines.map((line, index) => <div key={`p${index}`}>{line}</div>)}
      {cell.secondary.map((line, index) => <div key={`s${index}`} className="is-secondary">{line}</div>)}
    </>
  )
}

function ChecklistTable({
  entries,
  columns,
  columnWidth,
  productWidth,
  columnWidths,
  text,
  headHeight,
  rowHeight,
  groupHeight,
}: {
  entries: ChecklistEntry[]
  columns: ChecklistColumnKey[]
  columnWidth: number
  productWidth: number
  columnWidths: Record<ChecklistColumnKey, number>
  text: PdfText
  headHeight: number
  rowHeight: number
  groupHeight: number
}) {
  const widthOf = (key: ChecklistColumnKey) => (key === 'product' ? productWidth : columnWidths[key])
  const alignCenter: ChecklistColumnKey[] = ['no', 'bay', 'pos', 'perHook', 'total', 'retail', 'checked']
  return (
    <table className={`pdfp-table${text.lang === 'zh' ? '' : ' is-latin-head'}${text.lang === 'en' ? ' is-en' : ''}${text.lang === 'bi' ? ' is-bi' : ''}`} style={{ width: columnWidth }}>
      <colgroup>
        {columns.map((key) => <col key={key} style={{ width: widthOf(key) }} />)}
      </colgroup>
      <thead>
        <tr style={{ height: headHeight }}>
          {columns.map((key) => (
            <th key={key} className={alignCenter.includes(key) ? 'is-c' : undefined}>
              <TextLines lines={columnHeader(text, key)} />
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {entries.map((entry, index) => {
          if (entry.kind === 'group') {
            return (
              <tr key={`g${index}`} className="grp" style={{ height: groupHeight }}>
                <td colSpan={columns.length}>{groupTitle(text, entry.bay, entry.row)}</td>
              </tr>
            )
          }
          const row = entry.row
          return (
            <tr key={row.itemGuid} className={row.hasError ? 'is-error' : undefined} style={{ height: rowHeight }}>
              {columns.map((key) => {
                switch (key) {
                  case 'no':
                    return <td key={key} className="is-c"><span className={`pdfp-badge${row.hasError ? ' is-error' : ''}`}>{row.no}</span></td>
                  case 'bay':
                    return <td key={key} className="is-c">{row.bay}</td>
                  case 'pos':
                    return <td key={key} className="is-c">{`${row.col} / ${row.row}`}</td>
                  case 'code':
                    return <td key={key} className="is-mono">{row.code}</td>
                  case 'barcode':
                    return <td key={key} className="is-mono">{row.barcode}</td>
                  case 'product':
                    // 中文版一行（与以前一致）；英文版最多两行、双语版中文 1 行 + 英文最多 2 行，超出加「…」（截图不支持 text-overflow）
                    return text.lang === 'zh'
                      ? <td key={key}><div>{row.nameZh}</div></td>
                      : <td key={key} className="is-lines"><CellText cell={productCellLines(text.lang, row, productWidth)} /></td>
                  case 'hook':
                    // 挂钩名称里常有臂长等区分信息（「… 200」与「… 250」），截断会看不出差别：最多折两行；双语版再加一行型号编码
                    return <td key={key} className="is-lines is-hook"><CellText cell={hookCellLines(text.lang, row, columnWidths.hook)} /></td>
                  case 'perHook':
                    return <td key={key} className="is-c">{row.perHook}</td>
                  case 'total':
                    return <td key={key} className="is-c"><b>{row.total}</b></td>
                  case 'retail':
                    return <td key={key} className="is-c">{formatMoney(row.price)}</td>
                  case 'checked':
                    return <td key={key} className="is-c"><span className="pdfp-checkbox" /></td>
                  default:
                    return <td key={key} />
                }
              })}
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

function ChecklistPage({ model, plan, options, page, text }: ChecklistPageProps) {
  const { snapshot } = model.source
  const { paper, checklistLayout: layout } = plan
  const frame = paper.checklist
  const lang = options.lang
  const name = snapshot.plan.name
  const nextPage = plan.pages.find((candidate) => candidate.kind === 'checklist' && candidate.pageNo === page.pageNo + 1) as ChecklistPagePlan | undefined
  const lastRowNo = model.rows.length

  // 页眉右侧：孔位说明 + 每钩挂数说明；左侧：排序说明 + 版本
  const header = {
    title: text.joined('planogram.pdf.checklist.title', { name }),
    meta: [text.joined('planogram.pdf.checklist.order'), text.joined('planogram.pdf.checklist.version', { version: snapshot.version })],
    right: [text.joined('planogram.pdf.checklist.position'), text.joined('planogram.pdf.checklist.perHookNote')],
  }
  const footNotes = [text.joined('planogram.pdf.checklist.footNote')]
  if (model.englishFallbackUsed && lang !== 'zh') footNotes.push(text.joined('planogram.pdf.checklist.noEnglish'))

  return (
    <PageFrame plan={plan} page={page}>
      <PageHeader compact={false} title={header.title} meta={header.meta} platform="" right={header.right} top={PAGE_MARGIN_TOP} />
      <div className="pdfp-summary" style={{ left: PAGE_MARGIN_X, right: PAGE_MARGIN_X, top: checklistSummaryTop(frame, lang) }}>
        <span><b>{model.stats.skuCount}</b>{text.joined('planogram.pdf.checklist.products')}</span>
        <span><b>{model.stats.itemCount}</b>{text.joined('planogram.pdf.checklist.hooks')}</span>
        <span><b>{model.stats.unitCount}</b>{text.joined('planogram.pdf.checklist.units')}</span>
        <span><b>{formatMoneyTotal(model.stats.retailValue)}</b>{text.joined('planogram.pdf.checklist.retail')}</span>
        <span className="is-muted">{model.hooksPerBay.map((entry) => text.joined('planogram.pdf.checklist.bayHooks', { bay: entry.bay, count: entry.count })).join(' · ')}</span>
      </div>
      <div className="pdfp-tables" style={{ left: PAGE_MARGIN_X, right: PAGE_MARGIN_X, top: layout.tableTop, gap: 16 }}>
        {page.columns.map((entries, index) => (
          <ChecklistTable
            key={index}
            entries={entries}
            columns={layout.columns}
            columnWidth={layout.columnWidth}
            productWidth={layout.productWidth}
            columnWidths={layout.columnWidths}
            text={text}
            headHeight={layout.headHeight}
            rowHeight={layout.rowHeight}
            groupHeight={layout.groupHeight}
          />
        ))}
      </div>
      {nextPage ? (
        <div className="pdfp-continued" style={{ right: PAGE_MARGIN_X, bottom: frame.heightPx - footerTop(frame, lang) + 4 }}>
          {continuedNote(text, nextPage.pageNo, nextPage.firstNo, lastRowNo)}
        </div>
      ) : null}
      {model.rows.length === 0 ? <div className="pdfp-empty" style={{ left: PAGE_MARGIN_X, top: layout.tableTop + layout.headHeight + 16 }}>{text.one('planogram.pdf.checklist.empty')}</div> : null}
      <PageFooter left={footNotes.map((note, index) => <span key={index} className="pdfp-foot-note">{note}</span>)} text={text} model={model} page={page} plan={plan} />
    </PageFrame>
  )
}

// ---------------------------------------------------------------------------
// 入口
// ---------------------------------------------------------------------------

/** 全部页面（弹窗缩略图传 pageFilter 只取第 1 页）。容器本身没有样式，调用方负责放在屏幕外 / 缩放容器里。 */
export default function PlanogramPrintPages(props: PrintPagesProps) {
  const { plan, options, translate, pageFilter } = props
  const text = useMemo(() => makePdfText(options.lang, translate), [options.lang, translate])
  return (
    <div className="planogram-pdf-root" style={{ fontFamily: PRINT_FONT_FAMILY }}>
      {plan.pages.filter((page) => (pageFilter ? pageFilter(page) : true)).map((page) =>
        page.kind === 'board' ? (
          <BoardPage key={page.pageNo} {...props} page={page} text={text} />
        ) : (
          <ChecklistPage key={page.pageNo} {...props} page={page} text={text} />
        ),
      )}
    </div>
  )
}
