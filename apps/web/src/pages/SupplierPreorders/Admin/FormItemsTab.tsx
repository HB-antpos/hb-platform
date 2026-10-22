import { App, Input, Segmented, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getSupplierPreorderFormItems } from '../../../services/supplierPreorderService'
import type { SupplierPreorderFormDetail, SupplierPreorderFormItem, SupplierPreorderFormItemQuery } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { ItemThumb } from './SpAdminParts'
import {
  DEFAULT_PAGE_SIZE,
  PAGE_SIZE_OPTIONS,
  formatCbm,
  formatInteger,
  formatMoney,
  getErrorText,
  isAbortError,
} from './supplierPreorderAdmin.logic'

type MatchFilter = NonNullable<SupplierPreorderFormItemQuery['match']>

interface FormItemsTabProps {
  formGuid: string
  detail: SupplierPreorderFormDetail
}

/** 订单表商品：按匹配筛选 + 搜索，服务端分页。 */
export default function FormItemsTab({ formGuid, detail }: FormItemsTabProps) {
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
  }, [formGuid, keyword, match, message, page, pageSize, t])

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
        scroll={{ x: 1300 }}
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
    </div>
  )
}
