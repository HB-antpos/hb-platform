import { Alert, Button, Popover, Skeleton, Tag, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState, type MouseEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import type { BatchSalesPurchaseDetailResult, BatchSalesPurchaseDetailRow } from '../../../types/batchProductSalesAnalysis'
import styles from './index.module.css'

const quantityFormatter = new Intl.NumberFormat('en-AU')
// 与 .purchaseDetail 的宽度 min(620px, 100vw - 48px) 保持一致，用来判断向哪一侧展开。
const POPOVER_WIDTH = 620

export interface PurchaseDetailSummary {
  total: number
  warehouse: number
  supplier: number
}

interface PurchaseDetailPopoverProps {
  /** 同一筛选条件的缓存键；父组件在进货范围变化时清空缓存。 */
  cacheKey: string
  cache: Map<string, BatchSalesPurchaseDetailResult>
  load: (signal: AbortSignal) => Promise<BatchSalesPurchaseDetailResult>
  summary: PurchaseDetailSummary
  range: { startDate: string; endDate: string }
  /** 已限定单个分店时不再显示分店列；已限定单个商品时不再显示货号列。 */
  showBranch: boolean
  showProduct: boolean
  branchLabel: (branchCode: string) => string
  productLabel: (productCode: string) => string
  className?: string
  children: ReactNode
}

/**
 * 进货量的逐笔明细：点击数字打开，首次打开才请求，关闭或卸载时中止请求。
 * 数字常出现在可点击的商品行 / 表格行里，触发器与弹层内容都要阻止点击冒泡，
 * 否则 React 合成事件会穿过 Portal 冒泡到外层，顺带切换商品或分店。
 */
export default function PurchaseDetailPopover({ cacheKey, cache, load, summary, range, showBranch, showProduct, branchLabel, productLabel, className, children }: PurchaseDetailPopoverProps) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [placement, setPlacement] = useState<'bottomLeft' | 'bottomRight'>('bottomRight')
  const [result, setResult] = useState<BatchSalesPurchaseDetailResult | undefined>(() => cache.get(cacheKey))
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const abortRef = useRef<AbortController>()
  // 父组件每次渲染都会传入新的 load；放进 ref，避免无关的重新渲染中止并重发进行中的请求。
  const loadRef = useRef(load)
  loadRef.current = load

  const fetchDetails = useCallback(() => {
    abortRef.current?.abort()
    const cached = cache.get(cacheKey)
    if (cached) {
      setResult(cached)
      setFailed(false)
      return
    }
    const controller = new AbortController()
    abortRef.current = controller
    setResult(undefined)
    setFailed(false)
    setLoading(true)
    loadRef.current(controller.signal)
      .then((next) => {
        if (controller.signal.aborted) return
        cache.set(cacheKey, next)
        setResult(next)
      })
      .catch(() => {
        if (!controller.signal.aborted) setFailed(true)
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
  }, [cache, cacheKey])

  // 打开时加载；筛选条件（cacheKey）在打开状态下变化时重新加载。
  useEffect(() => {
    if (open) fetchDetails()
    else {
      abortRef.current?.abort()
      setLoading(false)
    }
  }, [fetchDetails, open])
  useEffect(() => () => abortRef.current?.abort(), [])

  const stop = (event: MouseEvent) => event.stopPropagation()
  // 触发点左侧放不下整个弹层时改为向右展开（商品列表在页面最左侧），否则右对齐，避免弹层被视口裁掉。
  const onTriggerClick = (event: MouseEvent<HTMLButtonElement>) => {
    event.stopPropagation()
    const width = Math.min(POPOVER_WIDTH, window.innerWidth - 48)
    setPlacement(event.currentTarget.getBoundingClientRect().right >= width + 16 ? 'bottomRight' : 'bottomLeft')
  }
  const number = (value: number) => quantityFormatter.format(value)

  const columns: ColumnsType<BatchSalesPurchaseDetailRow> = [
    {
      title: t('batchProductSalesAnalysis.purchaseDetail.columns.date'),
      width: 112,
      render: (_, row) => (
        <span className={styles.purchaseDetailDate}>
          {row.purchaseDate}
          {row.isShipped ? null : (
            <Tooltip title={t('batchProductSalesAnalysis.purchaseDetail.notShippedHint')}>
              <Tag className={styles.purchaseDetailTag} color="warning">{t('batchProductSalesAnalysis.purchaseDetail.notShipped')}</Tag>
            </Tooltip>
          )}
        </span>
      ),
    },
    {
      title: t('batchProductSalesAnalysis.purchaseDetail.columns.source'),
      width: 92,
      render: (_, row) => (
        <Tag className={styles.purchaseDetailTag} color={row.source === 'Warehouse' ? 'purple' : 'blue'}>
          {t(`batchProductSalesAnalysis.purchaseDetail.source.${row.source}`)}
        </Tag>
      ),
    },
    { title: t('batchProductSalesAnalysis.purchaseDetail.columns.documentNo'), width: 112, ellipsis: true, render: (_, row) => row.documentNo },
    ...(showBranch ? [{ title: t('batchProductSalesAnalysis.purchaseDetail.columns.branch'), width: 120, ellipsis: true, render: (_: unknown, row: BatchSalesPurchaseDetailRow) => branchLabel(row.branchCode) }] : []),
    ...(showProduct ? [{ title: t('batchProductSalesAnalysis.purchaseDetail.columns.itemNumber'), width: 104, ellipsis: true, render: (_: unknown, row: BatchSalesPurchaseDetailRow) => productLabel(row.productCode) }] : []),
    { title: t('batchProductSalesAnalysis.purchaseDetail.columns.quantity'), width: 72, align: 'right', render: (_, row) => number(row.quantity) },
  ]

  const content = (
    // 弹层渲染在 Portal 中，仍需阻止点击冒泡到外层商品行。
    <div className={styles.purchaseDetail} onClick={stop}>
      <div className={styles.purchaseDetailHead}>
        <strong>{t('batchProductSalesAnalysis.purchaseDetail.title')}</strong>
        <span>{t('batchProductSalesAnalysis.purchaseDetail.range', range)}</span>
      </div>
      <p className={styles.purchaseDetailSummary}>
        {t('batchProductSalesAnalysis.purchaseDetail.summary', { total: number(summary.total), warehouse: number(summary.warehouse), supplier: number(summary.supplier) })}
      </p>
      {failed ? (
        <Alert
          type="error"
          showIcon
          message={t('batchProductSalesAnalysis.purchaseDetail.loadFailed')}
          action={<Button size="small" onClick={fetchDetails}>{t('batchProductSalesAnalysis.retry')}</Button>}
        />
      ) : loading || !result ? (
        <Skeleton active title={false} paragraph={{ rows: 3 }} />
      ) : (
        <>
          <MeasuredTable<BatchSalesPurchaseDetailRow>
            metricId="executive-sales-intelligence.batch-product-sales-analysis.purchase-detail"
            size="small"
            rowKey={(row) => `${row.source}|${row.documentNo}|${row.branchCode}|${row.productCode}|${row.purchaseDate}`}
            columns={columns}
            dataSource={result.rows}
            pagination={false}
            scroll={{ x: 'max-content', y: 260 }}
            locale={{ emptyText: t('batchProductSalesAnalysis.purchaseDetail.empty') }}
          />
          <p className={styles.purchaseDetailFoot}>
            {result.truncated
              ? t('batchProductSalesAnalysis.purchaseDetail.truncated', { limit: result.limit })
              : t('batchProductSalesAnalysis.purchaseDetail.count', { count: result.rows.length })}
          </p>
        </>
      )}
    </div>
  )

  return (
    <Popover open={open} onOpenChange={setOpen} trigger="click" placement={placement} content={content} destroyOnHidden>
      <button
        type="button"
        className={`${styles.purchaseTrigger}${className ? ` ${className}` : ''}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        // 用 title 而非 aria-label：读屏仍读出「进货 468」这类可见数字，悬停时提示可查看明细。
        title={t('batchProductSalesAnalysis.purchaseDetail.open')}
        onClick={onTriggerClick}
      >
        {children}
      </button>
    </Popover>
  )
}
