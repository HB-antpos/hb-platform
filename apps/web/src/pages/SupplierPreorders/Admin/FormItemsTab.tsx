import { App, Button, Input, Segmented, Tag, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  getSupplierPreorderFormItems,
  getSupplierPreorderRefCandidates,
  setSupplierPreorderItemRef,
} from '../../../services/supplierPreorderService'
import type { SupplierPreorderFormDetail, SupplierPreorderFormItem, SupplierPreorderFormItemQuery } from '../../../types/supplierPreorder'

import { formatRefScore, getHistoryVersionKey, getRefCellState, getRefDisplayCode, getRefPriceDiffRate } from './refPicker.logic'
import RefPickerModal, { type RefCandidatesLoader, type RefPickerResult, type RefPickerTarget } from './RefPickerModal'
import './registerSpAdminMessages'
import { ItemThumb } from './SpAdminParts'
import {
  DEFAULT_PAGE_SIZE,
  PAGE_SIZE_OPTIONS,
  formatCbm,
  formatInteger,
  formatMoney,
  formatPriceChangeRate,
  formatSydneyDateTime,
  getErrorText,
  isAbortError,
} from './supplierPreorderAdmin.logic'

type MatchFilter = NonNullable<SupplierPreorderFormItemQuery['match']>

interface FormItemsTabProps {
  formGuid: string
  detail: SupplierPreorderFormDetail
  /** 指定 / 清除参考后通知详情页刷新页头计数 */
  onRefChanged?: () => void
}

/** 订单表商品：按匹配筛选 + 搜索，服务端分页；每行可指定参考商品。 */
export default function FormItemsTab({ formGuid, detail, onRefChanged }: FormItemsTabProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [match, setMatch] = useState<MatchFilter>('all')
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [items, setItems] = useState<SupplierPreorderFormItem[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [picking, setPicking] = useState<SupplierPreorderFormItem | null>(null)
  // 去年历史快照换了一版（排队 → 完成、重新计算）时重拉商品行：去年销量与系统推荐都可能变
  const historyVersion = getHistoryVersionKey(detail.history)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    getSupplierPreorderFormItems(formGuid, { page, pageSize, keyword: keyword || undefined, match }, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return
        setItems(result.items)
        setTotal(result.total)
      })
      .catch((error) => {
        if (controller.signal.aborted || isAbortError(error)) return
        message.error(getErrorText(error, t('spAdmin.common.loadFailed')))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [formGuid, historyVersion, keyword, match, message, page, pageSize, t])

  const pickerTarget = useMemo<RefPickerTarget | null>(
    () =>
      picking
        ? { key: picking.itemGuid, name: picking.name, supplierItemCode: picking.supplierItemCode, price: picking.price, currentRef: picking.ref ?? null }
        : null,
    [picking],
  )

  const pickerLoader = useCallback<RefCandidatesLoader>(
    (searchKeyword, signal) => getSupplierPreorderRefCandidates(formGuid, picking?.itemGuid ?? '', searchKeyword, signal),
    [formGuid, picking],
  )

  // 保存成功后用接口返回的行局部替换，不整页重拉（保留翻页、筛选位置）
  const handlePickerSubmit = useCallback(
    async (result: RefPickerResult) => {
      if (!picking) return
      try {
        const next = await setSupplierPreorderItemRef(
          formGuid,
          picking.itemGuid,
          result.mode === 'Manual' ? { mode: 'Manual', productCode: result.candidate.productCode } : { mode: result.mode, productCode: null },
        )
        setItems((current) => current.map((item) => (item.itemGuid === next.itemGuid ? next : item)))
        setPicking(null)
        message.success(t(result.mode === 'Manual' ? 'spAdmin.ref.savedManual' : result.mode === 'Clear' ? 'spAdmin.ref.savedClear' : 'spAdmin.ref.savedAuto'))
        onRefChanged?.()
      } catch (error) {
        message.error(getErrorText(error, t('spAdmin.common.saveFailed')))
        throw error
      }
    },
    [formGuid, message, onRefChanged, picking, t],
  )

  const columns = useMemo<ColumnsType<SupplierPreorderFormItem>>(
    () => [
      { title: t('spAdmin.import.colSeq'), dataIndex: 'seq', width: 64, className: 'sp-admin-number' },
      { title: t('spAdmin.import.colImage'), dataIndex: 'imageUrl', width: 56, render: (value?: string | null) => <ItemThumb url={value} /> },
      { title: t('spAdmin.import.colItemCode'), dataIndex: 'supplierItemCode', width: 120, className: 'sp-admin-mono' },
      { title: t('spAdmin.import.colBarcode'), dataIndex: 'barcode', width: 130, className: 'sp-admin-mono', render: (value?: string | null) => value || '--' },
      { title: t('spAdmin.import.colName'), dataIndex: 'name', width: 260 },
      { title: t('spAdmin.import.colUnit'), dataIndex: 'unit', width: 64, render: (value?: string | null) => value || '--' },
      { title: t('spAdmin.import.colCartonQty'), dataIndex: 'cartonQty', width: 72, align: 'right', className: 'sp-admin-number' },
      { title: t('spAdmin.import.colCbm'), dataIndex: 'cartonCbm', width: 80, align: 'right', className: 'sp-admin-number', render: formatCbm },
      { title: t('spAdmin.import.colPrice'), dataIndex: 'price', width: 90, align: 'right', className: 'sp-admin-number', render: formatMoney },
      {
        title: t('spAdmin.import.colMatch'),
        key: 'match',
        width: 220,
        render: (_, row) => (
          <div className="sp-admin-cell-stack">
            {row.matchMethod === 'None' ? (
              <Tag color="gold" className="sp-admin-tag">
                {t('spAdmin.common.previewStatus.New')}
              </Tag>
            ) : (
              <Tag color="green" className="sp-admin-tag">
                {t('spAdmin.common.previewStatus.Matched')} · {t(`spAdmin.common.matchMethod.${row.matchMethod}`)}
              </Tag>
            )}
            {row.matchNote ? <span className="sp-admin-muted sp-admin-small">{row.matchNote}</span> : null}
          </div>
        ),
      },
      {
        title: t('spAdmin.items.colTotalCartons'),
        dataIndex: 'totalCartons',
        width: 96,
        align: 'right',
        className: 'sp-admin-number sp-admin-strong',
        render: (value: number) => (value ? formatInteger(value) : <span className="sp-admin-faint">·</span>),
      },
      {
        title: t('spAdmin.ref.colOwnSales'),
        dataIndex: 'ownSalesQty',
        width: 88,
        align: 'right',
        className: 'sp-admin-number',
        render: (value?: number | null) =>
          value === null || value === undefined ? <span className="sp-admin-faint">—</span> : value > 0 ? formatInteger(value) : <span className="sp-admin-muted">0</span>,
      },
      {
        title: t('spAdmin.ref.colRef'),
        key: 'ref',
        width: 280,
        render: (_, row) => <RefCell row={row} />,
      },
      {
        title: '',
        key: 'refAction',
        width: 72,
        fixed: 'right',
        render: (_, row) => (
          <Button size="small" type="link" onClick={() => setPicking(row)}>
            {t('spAdmin.ref.assign')}
          </Button>
        ),
      },
    ],
    [t],
  )

  return (
    <div className="sp-admin-card">
      <div className="sp-admin-toolbar">
        <Segmented<MatchFilter>
          value={match}
          onChange={(value) => {
            setMatch(value)
            setPage(1)
          }}
          options={[
            { value: 'all', label: t('spAdmin.import.filterAll', { count: detail.summary.itemCount }) },
            { value: 'matched', label: t('spAdmin.import.filterMatched', { count: detail.matchedCount }) },
            { value: 'new', label: t('spAdmin.import.filterNew', { count: detail.newCount }) },
            { value: 'warning', label: t('spAdmin.import.filterWarning', { count: detail.warningCount }) },
            { value: 'needRef', label: t('spAdmin.ref.filterNeedRef', { count: detail.needRefCount }) },
            { value: 'ref', label: t('spAdmin.ref.filterRef', { count: detail.refAutoCount + detail.refManualCount }) },
          ]}
        />
        <span className="sp-admin-toolbar-spacer" />
        <Input.Search
          className="sp-admin-search"
          allowClear
          value={keywordInput}
          placeholder={t('spAdmin.items.searchPlaceholder')}
          onChange={(event) => setKeywordInput(event.target.value)}
          onSearch={(value) => {
            setKeyword(value.trim())
            setPage(1)
          }}
        />
      </div>
      <MeasuredTable<SupplierPreorderFormItem>
        metricId="supplier-preorder-form-items"
        className="sp-admin-table"
        rowKey="itemGuid"
        size="small"
        loading={loading}
        columns={columns}
        dataSource={items}
        scroll={{ x: 1740 }}
        pagination={{
          current: page,
          pageSize,
          total,
          showSizeChanger: true,
          pageSizeOptions: PAGE_SIZE_OPTIONS,
          showTotal: (count) => t('spAdmin.items.total', { count }),
          onChange: (nextPage, nextPageSize) => {
            if (nextPageSize !== pageSize) {
              setPageSize(nextPageSize)
              setPage(1)
            } else {
              setPage(nextPage)
            }
          },
        }}
      />
      <RefPickerModal target={pickerTarget} loader={pickerLoader} onSubmit={handlePickerSubmit} onClose={() => setPicking(null)} />
    </div>
  )
}

/** 参考商品列：有参考显示紫色标签 + 名称 + 来源；已清除 / 本品有历史 / 待计算 / 无参考 各有样式。 */
function RefCell({ row }: { row: SupplierPreorderFormItem }) {
  const { t } = useTranslation()
  const state = getRefCellState(row)
  const ref = row.ref
  if ((state === 'auto' || state === 'manual') && ref) {
    const diff = getRefPriceDiffRate(ref.purchasePrice, row.price)
    const tip = (
      <div className="sp-admin-ref-tip">
        <ItemThumb url={ref.imageUrl} size={72} />
        <div className="sp-admin-cell-stack">
          <span>{ref.name || '--'}</span>
          <span className="sp-admin-number">
            {t('spAdmin.ref.priceCompare', { ref: formatMoney(ref.purchasePrice ?? null), self: formatMoney(row.price) })}
            {diff !== null ? ` (${formatPriceChangeRate(diff)})` : ''}
          </span>
          {ref.salesQty !== null && ref.salesQty !== undefined ? (
            <span className="sp-admin-number">{t('spAdmin.ref.candSales', { qty: formatInteger(ref.salesQty) })}</span>
          ) : null}
          {ref.setAtUtc ? <span className="sp-admin-number">{t('spAdmin.ref.setAt', { time: formatSydneyDateTime(ref.setAtUtc) })}</span> : null}
        </div>
      </div>
    )
    return (
      <Tooltip title={tip} placement="left" color="#fff" styles={{ body: { color: '#1f2430' } }}>
        <div className="sp-admin-cell-stack">
          <span className="sp-admin-ref-line">
            <Tag color="purple" className="sp-admin-tag sp-admin-mono">
              {t('spAdmin.ref.tag', { code: getRefDisplayCode(ref) })}
            </Tag>
            <span className="sp-admin-ellipsis sp-admin-small">{ref.name || '--'}</span>
          </span>
          <span className="sp-admin-muted sp-admin-small">
            {state === 'auto' ? t('spAdmin.ref.sourceAuto', { score: formatRefScore(ref.score) }) : t('spAdmin.ref.sourceManual', { by: ref.setBy || '--' })}
          </span>
        </div>
      </Tooltip>
    )
  }
  if (state === 'cleared') return <span className="sp-admin-muted sp-admin-small">{t('spAdmin.ref.cleared')}</span>
  if (state === 'own') return <span className="sp-admin-faint sp-admin-small">{t('spAdmin.ref.ownHistory')}</span>
  if (state === 'pending') return <span className="sp-admin-faint sp-admin-small">{t('spAdmin.ref.pending')}</span>
  return (
    <Tag color="red" bordered={false} className="sp-admin-tag">
      {t('spAdmin.ref.none')}
    </Tag>
  )
}
