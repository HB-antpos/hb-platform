import { PrinterOutlined, RollbackOutlined } from '@ant-design/icons'
import { Button, Empty, Space, Spin, message } from 'antd'
import dayjs from 'dayjs'
import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'

import { getPickingSlips, type PickingSlip, type PickingSlipLine, type PickingSlips } from '../../../../services/warehousePickingAssignmentService'
import { buildBarcodeSvgPath, encodeBarcodeModules } from '../../../../utils/barcode'
import { printElementPagesAsPdf } from '../printUtils'

import { buildSlipRows, paginateSlipRows, type SlipRow } from './pickingAssignmentLogic'
import '../print.css'
import './messages'
import './pickingSlips.css'

// 1px 模块 ≈ 0.26mm，常见扫描枪可读；24 个字符的分单条码约 300px，放得进 90mm 条码区。
const SLIP_BARCODE_MODULE_WIDTH = 1
const SLIP_BARCODE_HEIGHT = 56
/** 第一页有页头、条码和汇总，放 24 行；之后每页 34 行。 */
const FIRST_PAGE_ROWS = 24
const NEXT_PAGE_ROWS = 34

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
  const { orderGuids, segmentNo } = useMemo(() => parsePickingSlipsQuery(location.search), [location.search])
  const [orders, setOrders] = useState<PickingSlips[]>([])
  const [failures, setFailures] = useState<string[]>([])
  const [loading, setLoading] = useState(false)
  const [printing, setPrinting] = useState(false)

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
        setFailures(failed)
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [orderGuids, segmentNo])

  const pages = useMemo<SlipPage[]>(
    () =>
      orders.flatMap((order) =>
        order.slips.flatMap((slip) => {
          const chunks = paginateSlipRows(buildSlipRows(slip.lines), FIRST_PAGE_ROWS, NEXT_PAGE_ROWS, (row) => row.kind !== 'line')
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
    [orders],
  )

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

  const renderRow = (row: SlipRow<PickingSlipLine>, index: number) => {
    if (row.kind === 'turn') {
      return (
        <tr key={`turn-${index}`} className="picking-slip-turn">
          <td colSpan={8}>
            {row.first
              ? t('storeOrders.pickingSlips.startRow', '{{zone}} 区 {{row}} 排 · 列号从小到大', { zone: row.zone, row: row.rowLabel })
              : t('storeOrders.pickingSlips.turnRow', '折返 · 转入 {{zone}} 区 {{row}} 排', { zone: row.zone, row: row.rowLabel })}
          </td>
        </tr>
      )
    }
    if (row.kind === 'unlocated') {
      return (
        <tr key={`unlocated-${index}`} className="picking-slip-turn">
          <td colSpan={8}>{t('storeOrders.pickingSlips.unlocatedRow', '无货位 / 编码不规范 · 按商品找货')}</td>
        </tr>
      )
    }
    const { line } = row
    return (
      <tr key={line.detailGuid}>
        <td>{row.index}</td>
        <td className="picking-slip-mono">{line.locationCode || '—'}</td>
        <td>{line.itemNumber || '—'}</td>
        <td>{line.productName || '—'}</td>
        <td className="picking-slip-mono">{line.barcode || '—'}</td>
        <td className="picking-slip-number">{line.orderedQuantity}</td>
        <td className="picking-slip-number">{line.minOrderQuantity || '—'}</td>
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
        <div ref={pagesRootRef} className="picking-slip-pages">
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
                        {t('storeOrders.pickingSlips.orderLine', '订单 {{orderNo}} · {{store}} · 订货 {{date}}', {
                          orderNo: page.order.orderNo ?? '—',
                          store: page.order.storeName || page.order.storeCode || '—',
                          date: page.order.orderDate ? dayjs(page.order.orderDate).format('YYYY-MM-DD') : '—',
                        })}
                        <br />
                        {t('storeOrders.pickingSlips.assignedLine', '{{name}} 分配于 {{assignedAt}} · 打印 {{printedAt}}', {
                          name: page.order.assignedByName ?? '—',
                          assignedAt: page.order.assignedAtUtc ? dayjs(page.order.assignedAtUtc).format('MM-DD HH:mm') : '—',
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
                    <div><span>{t('storeOrders.pickingSlips.lines', '品种')}</span><b>{page.slip.lineCount}</b></div>
                    <div><span>{t('storeOrders.pickingSlips.pieces', '件数')}</span><b>{page.slip.pieces}</b></div>
                    <div className="is-wide">
                      <span>{t('storeOrders.pickingSlips.route', '路线（M 型走位）')}</span>
                      <b className="picking-slip-mono">
                        {page.slip.firstLocation ? `${page.slip.firstLocation} → ${page.slip.lastLocation}` : t('storeOrders.pickingSlips.noLocation', '无货位')}
                      </b>
                    </div>
                    <div>
                      <span>{t('storeOrders.pickingSlips.others', '同单其他段')}</span>
                      <b className="is-small">{page.slip.otherPickerNames.join(' · ') || '—'}</b>
                    </div>
                  </div>
                </Fragment>
              ) : (
                <div className="picking-slip-continued">
                  {t('storeOrders.pickingSlips.continued', '{{orderNo}} · 第 {{no}} 段 {{name}}（续）', {
                    orderNo: page.order.orderNo ?? '—',
                    no: page.slip.segmentNo,
                    name: page.slip.pickerName ?? '',
                  })}
                </div>
              )}
              <table className="picking-slip-table">
                <thead>
                  <tr>
                    <th style={{ width: 28 }}>#</th>
                    <th style={{ width: 104 }}>{t('storeOrders.pickingSlips.location', '货位')}</th>
                    <th style={{ width: 76 }}>{t('storeOrders.pickingSlips.itemNumber', '货号')}</th>
                    <th>{t('storeOrders.pickingSlips.product', '商品')}</th>
                    <th style={{ width: 112 }}>{t('storeOrders.pickingSlips.barcode', '条码')}</th>
                    <th style={{ width: 44 }} className="picking-slip-number">{t('storeOrders.pickingSlips.ordered', '订货')}</th>
                    <th style={{ width: 40 }} className="picking-slip-number">{t('storeOrders.pickingSlips.pack', '中包')}</th>
                    <th style={{ width: 56 }}>{t('storeOrders.pickingSlips.picked', '实拣')}</th>
                  </tr>
                </thead>
                <tbody>{page.rows.map(renderRow)}</tbody>
              </table>
              <div className="picking-slip-footer">
                {page.pageNo === page.pageCount ? (
                  <div className="picking-slip-sign">
                    <span>{t('storeOrders.pickingSlips.signPicker', '拣货人签字')} ____________________</span>
                    <span>{t('storeOrders.pickingSlips.signTime', '完成时间')} ______________</span>
                    <span>{t('storeOrders.pickingSlips.signCheck', '复核')} ______________</span>
                  </div>
                ) : null}
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
