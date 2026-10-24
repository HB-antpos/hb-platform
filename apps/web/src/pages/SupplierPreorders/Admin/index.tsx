import { PlusOutlined, TeamOutlined } from '@ant-design/icons'
import { App, Button, Input, Segmented } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { getSupplierPreorderForms } from '../../../services/supplierPreorderService'
import type { SupplierPreorderFormStatus, SupplierPreorderFormSummary } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { DeadlineText, FormStatusPill } from './SpAdminParts'
import {
  DEFAULT_PAGE_SIZE,
  PAGE_SIZE_OPTIONS,
  formatCbm,
  formatInteger,
  formatMoney,
  getErrorText,
} from './supplierPreorderAdmin.logic'
import './styles.css'

type StatusFilter = SupplierPreorderFormStatus | ''

/** 供应商节日预订单 · 订单表列表（/supplier-preorders）。 */
export default function SupplierPreorderFormsPage() {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const navigate = useNavigate()
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [status, setStatus] = useState<StatusFilter>('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [items, setItems] = useState<SupplierPreorderFormSummary[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [loadedAtMs, setLoadedAtMs] = useState(() => Date.now())
  const requestSeqRef = useRef(0)

  useEffect(() => {
    // 只认最后一次请求的结果，避免快速翻页 / 筛选时旧响应覆盖新结果
    const seq = ++requestSeqRef.current
    setLoading(true)
    getSupplierPreorderForms({ page, pageSize, keyword: keyword || undefined, status })
      .then((result) => {
        if (seq !== requestSeqRef.current) return
        setItems(result.items)
        setTotal(result.total)
        setLoadedAtMs(Date.now())
      })
      .catch((error) => {
        if (seq !== requestSeqRef.current) return
        message.error(getErrorText(error, t('spAdmin.common.loadFailed')))
      })
      .finally(() => {
        if (seq === requestSeqRef.current) setLoading(false)
      })
  }, [keyword, message, page, pageSize, status, t])

  const columns = useMemo<ColumnsType<SupplierPreorderFormSummary>>(
    () => [
      {
        // 标题 + 供应商合成一列，避免列太多挤窄标题
        title: t('spAdmin.list.colTitle'),
        key: 'title',
        width: 300,
        render: (_, row) => (
          <div className="sp-admin-cell-stack">
            <span className="sp-admin-link-text sp-admin-ellipsis" title={row.title}>
              {row.title}
            </span>
            <span className="sp-admin-muted sp-admin-small sp-admin-ellipsis">
              {row.localSupplierCode} · {row.supplierName}
            </span>
          </div>
        ),
      },
      {
        title: t('spAdmin.list.colSeason'),
        key: 'season',
        width: 110,
        render: (_, row) => `${t(`spAdmin.common.holiday.${row.holidayType}`)} · ${row.seasonYear}`,
      },
      { title: t('spAdmin.list.colQuoteNo'), dataIndex: 'quoteNo', width: 110, render: (value?: string | null) => value || '--' },
      {
        title: t('spAdmin.list.colStatus'),
        dataIndex: 'status',
        width: 90,
        render: (value: SupplierPreorderFormStatus) => <FormStatusPill status={value} />,
      },
      {
        title: t('spAdmin.list.colDeadline'),
        key: 'deadline',
        width: 160,
        render: (_, row) =>
          row.status === 'Closed' ? (
            <DeadlineText deadlineAtUtc={row.deadlineAtUtc} nowMs={loadedAtMs} showRemaining={false} />
          ) : (
            <DeadlineText deadlineAtUtc={row.deadlineAtUtc} nowMs={loadedAtMs} />
          ),
      },
      { title: t('spAdmin.list.colItems'), dataIndex: 'itemCount', width: 72, align: 'right', className: 'sp-admin-number', render: formatInteger },
      { title: t('spAdmin.list.colStores'), dataIndex: 'enabledStoreCount', width: 80, align: 'right', className: 'sp-admin-number' },
      {
        title: t('spAdmin.list.colProgress'),
        key: 'progress',
        width: 140,
        render: (_, row) => (
          <span className="sp-admin-number sp-admin-small">
            {t('spAdmin.list.progressValue', { submitted: row.submittedStoreCount, draft: row.draftStoreCount })}
          </span>
        ),
      },
      { title: t('spAdmin.list.colCartons'), dataIndex: 'totalCartons', width: 80, align: 'right', className: 'sp-admin-number', render: formatInteger },
      { title: t('spAdmin.list.colAmount'), dataIndex: 'totalAmount', width: 110, align: 'right', className: 'sp-admin-number', render: formatMoney },
      { title: t('spAdmin.list.colCbm'), dataIndex: 'totalCbm', width: 70, align: 'right', className: 'sp-admin-number', render: formatCbm },
    ],
    [loadedAtMs, t],
  )

  return (
    <PageContainer
      title={t('spAdmin.list.title')}
      subtitle={t('spAdmin.list.subtitle')}
      extra={
        <div className="sp-admin-actions">
          <Button icon={<TeamOutlined />} onClick={() => navigate('/supplier-preorders/orderers')}>
            {t('spAdmin.list.orderers')}
          </Button>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/supplier-preorders/import')}>
            {t('spAdmin.list.import')}
          </Button>
        </div>
      }
    >
      <div className="sp-admin-card">
        <div className="sp-admin-toolbar">
          <Input.Search
            className="sp-admin-search"
            allowClear
            value={keywordInput}
            placeholder={t('spAdmin.list.searchPlaceholder')}
            onChange={(event) => setKeywordInput(event.target.value)}
            onSearch={(value) => {
              setKeyword(value.trim())
              setPage(1)
            }}
          />
          <Segmented<StatusFilter>
            value={status}
            onChange={(value) => {
              setStatus(value)
              setPage(1)
            }}
            options={[
              { value: '', label: t('spAdmin.list.statusAll') },
              { value: 'Draft', label: t('spAdmin.common.status.Draft') },
              { value: 'Open', label: t('spAdmin.common.status.Open') },
              { value: 'Closed', label: t('spAdmin.common.status.Closed') },
            ]}
          />
        </div>
        <MeasuredTable<SupplierPreorderFormSummary>
          metricId="supplier-preorder-forms"
          className="sp-admin-table"
          rowKey="formGuid"
          size="middle"
          loading={loading}
          columns={columns}
          dataSource={items}
          scroll={{ x: 1290 }}
          locale={{ emptyText: t('spAdmin.list.empty') }}
          onRow={(row) => ({
            className: 'sp-admin-clickable-row',
            onClick: () => navigate(`/supplier-preorders/forms/${row.formGuid}`),
          })}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            pageSizeOptions: PAGE_SIZE_OPTIONS,
            showTotal: (count) => t('spAdmin.list.total', { count }),
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
    </PageContainer>
  )
}
