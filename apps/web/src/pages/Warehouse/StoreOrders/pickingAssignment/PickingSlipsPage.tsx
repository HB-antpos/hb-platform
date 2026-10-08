import { PrinterOutlined, RollbackOutlined } from '@ant-design/icons'
import { Button, Empty, Space, Spin, message } from 'antd'
import dayjs from 'dayjs'
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'

import { getPickingSlips, type PickingSlip, type PickingSlipLine, type PickingSlips } from '../../../../services/warehousePickingAssignmentService'
import { buildBarcodeSvgPath, encodeBarcodeModules } from '../../../../utils/barcode'
import { formatInnerPackCount } from '../pickingListLogic'
import { printElementPagesAsPdf } from '../printUtils'

import {
  SLIP_LINE_ROW_MM,
  SLIP_MARKER_ROW_MM,
  SLIP_NEXT_PAGE_BODY_MM,
  buildSlipRows,
  formatUtcShort,
  otherSegmentLabels,
  paginateSlipRowsByHeight,
  resolveSlipsSearch,
  slipFirstPageBodyMm,
  type SlipRow,
} from './pickingAssignmentLogic'
import '../print.css'
import './messages'
import './pickingSlips.css'

// 1px 模块 ≈ 0.26mm，常见扫描枪可读；24 个字符的分单条码约 300px，放得进 90mm 条码区。
const SLIP_BARCODE_MODULE_WIDTH = 1
const SLIP_BARCODE_HEIGHT = 56
/** 页签只记路径、丢了 ?orders=，从别的页签点回来时用它找回上次打开的订单。 */
const REMEMBERED_SEARCH_KEY = 'hb.pickingSlips.lastSearch'
/** 实测溢出时最多收紧这么多行（每次一行），防止异常内容让页面反复重排。 */
const MAX_SHRINK_ROWS = 6

function readRememberedSearch(): string | null {
  try {
    return window.sessionStorage.getItem(REMEMBERED_SEARCH_KEY)
  } catch {
    return null
  }
}

function writeRememberedSearch(search: string) {
  try {
    window.sessionStorage.setItem(REMEMBERED_SEARCH_KEY, search)
  } catch {
    // 隐私模式等拿不到 sessionStorage 时只是不记，不影响打印。
  }
}

interface SlipPage {
  key: string
  order: PickingSlips
  slip: PickingSlip
  rows: SlipRow<PickingSlipLine>[]
  pageNo: number
  pageCount: number
}

/** 从 ?orders=a,b&segment=2 解析要打印的订单与段（segment 只在单张订单时生效）。 */
export function parsePickingSlipsQuery(search: string) {
  const params = new URLSearchParams(search)
  const orderGuids = (params.get('orders') ?? '')
    .split(',')
    .map((guid) => guid.trim())
    .filter(Boolean)
  const segment = Number.parseInt(params.get('segment') ?? '', 10)
  return {
    orderGuids: Array.from(new Set(orderGuids)),
    segmentNo: orderGuids.length === 1 && Number.isInteger(segment) && segment > 0 ? segment : null,
  }
}

function SlipBarcode({ value }: { value: string }) {
  const modules = encodeBarcodeModules(value, 'CODE128')
  if (!modules) return <div className="picking-slip-code">{value}</div>
  const width = modules.length * SLIP_BARCODE_MODULE_WIDTH
  return (
    <svg
      width={width}
      height={SLIP_BARCODE_HEIGHT}
      viewBox={`0 0 ${width} ${SLIP_BARCODE_HEIGHT}`}
      shapeRendering="crispEdges"
      role="img"
      aria-label={value}
    >
      <path d={buildBarcodeSvgPath(modules, SLIP_BARCODE_MODULE_WIDTH, SLIP_BARCODE_HEIGHT)} fill="#000" />
    </svg>
  )
}

/**
 * 分单拣货单（A4，每人一页，长段自动续页）：页头印段号、负责人与 Code128 分单条码，
 * 员工在 PDA 扫码即进入自己那一段；明细按 M 型走位顺序，换排处有提示行，留实拣方框手写核对。
 */
export default function PickingSlipsPage() {
  const { t } = useTranslation()
  const location = useLocation()
  const navigate = useNavigate()
  const pagesRootRef = useRef<HTMLDivElement | null>(null)
  // 地址里没有订单时（从订货明细等页签点回来）改用上次的查询串，并把地址补回去。
  const effectiveSearch = useMemo(() => resolveSlipsSearch(location.search, readRememberedSearch()), [location.search])
  const { orderGuids, segmentNo } = useMemo(() => parsePickingSlipsQuery(effectiveSearch ?? ''), [effectiveSearch])
  const [orders, setOrders] = useState<PickingSlips[]>([])
  // 渲染后实测某页溢出时逐行收紧每页容量（见下面的 useLayoutEffect）。
  const [shrinkMm, setShrinkMm] = useState(0)
  const [failures, setFailures] = useState<string[]>([])
  const [loading, setLoading] = useState(false)
  const [printing, setPrinting] = useState(false)

  useEffect(() => {
    if (!effectiveSearch) return
    if (effectiveSearch !== location.search) navigate({ pathname: location.pathname, search: effectiveSearch }, { replace: true })
    else writeRememberedSearch(effectiveSearch)
  }, [effectiveSearch, location.pathname, location.search, navigate])

  useEffect(() => {
    if (orderGuids.length === 0) return
    const controller = new AbortController()
    setLoading(true)
    // 逐张取打印数据：某张单没有分配（或已撤销）时跳过并提示，不影响其它单。
    Promise.allSettled(orderGuids.map((guid) => getPickingSlips(guid, segmentNo, controller.signal)))
      .then((results) => {
        if (controller.signal.aborted) return
        const loaded: PickingSlips[] = []
        const failed: string[] = []
        results.forEach((result, index) => {
          if (result.status === 'fulfilled' && result.value) loaded.push(result.value)
          else failed.push(orderGuids[index])
        })
        setOrders(loaded)
        setShrinkMm(0)
        setFailures(failed)
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [orderGuids, segmentNo])

  const unclaimedLabel = useCallback((no: number) => t('storeOrders.pickingSlips.otherUnclaimed', '第 {{no}} 段待领取', { no }), [t])

  // 按真实行高（毫米）分页：整行放得下才放，绝不让一行被页边裁掉；“其他段”文字变长时首页相应少放。
  const pages = useMemo<SlipPage[]>(
    () =>
      orders.flatMap((order) =>
        order.slips.flatMap((slip) => {
          const firstPageMm = slipFirstPageBodyMm(otherSegmentLabels(slip, unclaimedLabel).join(' · '), shrinkMm)
          const chunks = paginateSlipRowsByHeight(
            buildSlipRows(slip.lines),
            firstPageMm,
            SLIP_NEXT_PAGE_BODY_MM - shrinkMm,
            (row) => (row.kind === 'line' ? SLIP_LINE_ROW_MM : SLIP_MARKER_ROW_MM),
            (row) => row.kind !== 'line',
          )
          return chunks.map((rows, index) => ({
            key: `${order.orderGuid}-${slip.segmentNo}-${index}`,
            order,
            slip,
            rows,
            pageNo: index + 1,
            pageCount: chunks.length,
          }))
        }),
      ),
    [orders, shrinkMm, unclaimedLabel],
  )

  // 兜底：估算的页头高度与真实渲染总会有出入，渲染后量一次，哪页内容超出 A4 就整体少放一行再排，直到都放得下。
  useLayoutEffect(() => {
    const root = pagesRootRef.current
    if (!root || loading) return
    const overflowed = Array.from(root.querySelectorAll<HTMLElement>('.picking-slip-page')).some((page) => page.scrollHeight > page.clientHeight + 1)
    if (overflowed && shrinkMm < SLIP_LINE_ROW_MM * MAX_SHRINK_ROWS) setShrinkMm((value) => value + SLIP_LINE_ROW_MM)
  }, [pages, loading, shrinkMm])

  const print = async () => {
    if (!pagesRootRef.current || pages.length === 0) return
    setPrinting(true)
    try {
      await printElementPagesAsPdf(pagesRootRef.current, {
        pageSelector: '.picking-slip-page',
        createCanvasContextErrorMessage: t('warehouse.pickingList.createPdfCanvasFailed'),
        layoutNotReadyErrorMessage: t('warehouse.pickingList.layoutNotReady'),
      })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('warehouse.pickingList.printFailed'))
    } finally {
      setPrinting(false)
    }
  }

  // 订单号与门店加粗，每页（含续页）页头都要有；订货日期用常规字重跟在后面。
  const orderTitle = (page: SlipPage) =>
    t('storeOrders.pickingSlips.orderTitle', '订单 {{orderNo}} · {{store}}', {
      orderNo: page.order.orderNo ?? '—',
      store: page.order.storeName || page.order.storeCode || '—',
    })
  const orderedOn = (page: SlipPage) =>
    t('storeOrders.pickingSlips.orderedOn', '订货 {{date}}', {
      date: page.order.orderDate ? dayjs(page.order.orderDate).format('YYYY-MM-DD') : '—',
    })

  const renderRow = (row: SlipRow<PickingSlipLine>, index: number) => {
    if (row.kind === 'start') {
      return (
        <tr key={`start-${index}`} className="picking-slip-turn">
          <td colSpan={7}>{t('storeOrders.pickingSlips.startRow', '{{zone}} 区 {{row}} 排 · 列号从小到大', { zone: row.zone, row: row.rowLabel })}</td>
        </tr>
      )
    }
    if (row.kind === 'unlocated') {
      return (
        <tr key={`unlocated-${index}`} className="picking-slip-turn">
          <td colSpan={7}>{t('storeOrders.pickingSlips.unlocatedRow', '无货位 / 编码不规范 · 按商品找货')}</td>
        </tr>
      )
    }
    const { line } = row
    return (
      <tr key={line.detailGuid}>
        <td>{row.index}</td>
        <td className="picking-slip-mono">{line.itemNumber || '—'}</td>
        <td className="picking-slip-mono picking-slip-location">{line.locationCode || '—'}</td>
        <td>{line.productName || '—'}</td>
        {/* INNER Pack 与配货单同口径：订货数 ÷ 每包数量，每包数量不大于 1 时留空。 */}
        <td className="picking-slip-number">{formatInnerPackCount(line.orderedQuantity, null, line.minOrderQuantity ?? undefined)}</td>
        <td className="picking-slip-number">{line.orderedQuantity}</td>
        <td><span className="picking-slip-box" /></td>
      </tr>
    )
  }

  if (orderGuids.length === 0) {
    return <Empty description={t('storeOrders.pickingSlips.noOrders', '没有要打印的订单')} style={{ marginTop: 120 }} />
  }

  return (
    <div className="store-order-print-page">
      <div className="store-order-print-toolbar no-print">
        <Space wrap>
          <Button icon={<RollbackOutlined />} onClick={() => navigate(-1)}>
            {t('common.back')}
          </Button>
          <Button type="primary" icon={<PrinterOutlined />} loading={printing} disabled={pages.length === 0} onClick={() => void print()}>
            {t('storeOrders.pickingSlips.print', '打印分单（{{count}} 页）', { count: pages.length })}
          </Button>
        </Space>
      </div>
      {failures.length > 0 ? (
        <div className="picking-slip-failures no-print">
          {t('storeOrders.pickingSlips.skipped', '{{count}} 张订单没有拣货分配，已跳过', { count: failures.length })}
        </div>
      ) : null}
      {loading ? (
        <Spin size="large" style={{ display: 'block', margin: '120px auto' }} />
      ) : pages.length === 0 ? (
        <Empty description={t('storeOrders.pickingSlips.empty', '没有可打印的分单')} style={{ marginTop: 120 }} />
      ) : (
        <div
          ref={pagesRootRef}
          className="picking-slip-pages"
          style={{ '--slip-row-h': `${SLIP_LINE_ROW_MM}mm`, '--slip-marker-h': `${SLIP_MARKER_ROW_MM}mm` } as CSSProperties}
        >
          {pages.map((page) => (
            <div key={page.key} className="store-order-pdf-page picking-slip-page">
              {page.pageNo === 1 ? (
                <Fragment>
                  <div className="picking-slip-header">
                    <div className="picking-slip-heading">
                      <div className="picking-slip-title">{t('storeOrders.pickingSlips.title', '分单拣货单')}</div>
                      <div className="picking-slip-owner">
                        <span className="picking-slip-segment">
                          {t('storeOrders.pickingSlips.segment', '第 {{no}} / {{count}} 段', { no: page.slip.segmentNo, count: page.slip.segmentCount })}
                        </span>
                        {page.slip.pickerName || (
                          <span className="picking-slip-claim">
                            {t('storeOrders.pickingSlips.claimLine', '领取人 ____________（扫码领取）')}
                          </span>
                        )}
                      </div>
                      <div className="picking-slip-meta">
                        <b>{orderTitle(page)}</b> · {orderedOn(page)}
                        <br />
                        {t('storeOrders.pickingSlips.assignedLine', '{{name}} 分配于 {{assignedAt}} · 打印 {{printedAt}}', {
                          name: page.order.assignedByName ?? '—',
                          assignedAt: formatUtcShort(page.order.assignedAtUtc),
                          printedAt: dayjs().format('MM-DD HH:mm'),
                        })}
                      </div>
                    </div>
                    <div className="picking-slip-barcode">
                      <SlipBarcode value={page.slip.slipCode} />
                      <div className="picking-slip-code">{page.slip.slipCode}</div>
                      <div className="picking-slip-scan-hint">
                        {page.slip.pickerName
                          ? t('storeOrders.pickingSlips.scanHint', '用 PDA 扫此条码，进入你负责的这一段')
                          : t('storeOrders.pickingSlips.claimHint', '用 PDA 扫此条码领取这一段，领取后记在你名下')}
                      </div>
                    </div>
                  </div>
                  <div className="picking-slip-facts">
                    <div className="picking-slip-fact"><span>{t('storeOrders.pickingSlips.lines', '品种')}</span><b>{page.slip.lineCount}</b></div>
                    <div className="picking-slip-fact"><span>{t('storeOrders.pickingSlips.pieces', '件数')}</span><b>{page.slip.pieces}</b></div>
                    <div className="picking-slip-fact-stack">
                      <div className="picking-slip-fact-line">
                        <span>{t('storeOrders.pickingSlips.route', '路线（M 型走位）')}</span>
                        <b className="picking-slip-mono">
                          {page.slip.firstLocation ? `${page.slip.firstLocation} → ${page.slip.lastLocation}` : t('storeOrders.pickingSlips.noLocation', '无货位')}
                        </b>
                      </div>
                      <div className="picking-slip-fact-line">
                        <span>{t('storeOrders.pickingSlips.others', '同单其他段')}</span>
                        <b className="is-small">{otherSegmentLabels(page.slip, unclaimedLabel).join(' · ') || '—'}</b>
                      </div>
                    </div>
                  </div>
                </Fragment>
              ) : (
                <div className="picking-slip-continued">
                  <span>
                    <b>{orderTitle(page)}</b> · {orderedOn(page)}
                  </span>
                  <span className="picking-slip-segment">
                    {t('storeOrders.pickingSlips.segmentCont', '第 {{no}} / {{count}} 段（续）', { no: page.slip.segmentNo, count: page.slip.segmentCount })}
                  </span>
                </div>
              )}
              <table className="picking-slip-table">
                <colgroup>
                  {/* 行号列要放得下 4 位数，长单行号过百不能被截成 1… */}
                  <col style={{ width: 40 }} />
                  <col style={{ width: 120 }} />
                  <col style={{ width: 108 }} />
                  <col />
                  <col style={{ width: 86 }} />
                  <col style={{ width: 68 }} />
                  <col style={{ width: 58 }} />
                </colgroup>
                <thead>
                  <tr>
                    <th>#</th>
                    <th>{t('storeOrders.pickingSlips.itemNumber', '货号')}</th>
                    <th>{t('storeOrders.pickingSlips.location', '货位')}</th>
                    <th>{t('storeOrders.pickingSlips.product', '商品')}</th>
                    <th className="picking-slip-number">{t('warehouse.pickingList.innerPackShort')}</th>
                    <th className="picking-slip-number">{t('storeOrders.pickingSlips.ordered', '订货')}</th>
                    <th>{t('storeOrders.pickingSlips.picked', '实拣')}</th>
                  </tr>
                </thead>
                <tbody>{page.rows.map(renderRow)}</tbody>
              </table>
              <div className="picking-slip-footer">
                <div className="picking-slip-note">
                  {t('storeOrders.pickingSlips.note', '实拣数量以 PDA 扫码记录为准。订单重新分配后本单条码失效，请以新打印的分单为准。')}
                  {' '}
                  {t('storeOrders.pickingSlips.pageNo', '第 {{page}} 页 / 共 {{count}} 页', { page: page.pageNo, count: page.pageCount })}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
