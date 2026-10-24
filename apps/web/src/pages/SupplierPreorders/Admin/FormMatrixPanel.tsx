import { App, Input, Segmented, Switch } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getSupplierPreorderMatrix } from '../../../services/supplierPreorderService'
import type { SupplierPreorderMatrix, SupplierPreorderMatrixRow } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { ItemThumb } from './SpAdminParts'
import {
  DEFAULT_PAGE_SIZE,
  PAGE_SIZE_OPTIONS,
  formatCbm,
  formatInteger,
  formatMatrixValue,
  formatMoney,
  getErrorText,
  getMatrixCellValue,
  getMatrixStoreTotal,
  isAbortError,
  type MatrixUnit,
} from './supplierPreorderAdmin.logic'

interface FormMatrixPanelProps {
  formGuid: string
  /** 分店开关变化后递增，触发重新加载 */
  refreshKey: number
}

/** 各分店下单矩阵：行是商品、列是分店；箱 / 件 / 金额切换，表尾合计取后端 totals（覆盖全部页）。 */
export default function FormMatrixPanel({ formGuid, refreshKey }: FormMatrixPanelProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [unit, setUnit] = useState<MatrixUnit>('cartons')
  const [onlyOrdered, setOnlyOrdered] = useState(true)
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [matrix, setMatrix] = useState<SupplierPreorderMatrix | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    getSupplierPreorderMatrix(formGuid, { page, pageSize, keyword: keyword || undefined, onlyOrdered }, controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setMatrix(result)
      })
      .catch((error) => {
        if (controller.signal.aborted || isAbortError(error)) return
        message.error(getErrorText(error, t('spAdmin.common.loadFailed')))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [formGuid, keyword, message, onlyOrdered, page, pageSize, refreshKey, t])

  const stores = useMemo(() => matrix?.stores ?? [], [matrix])
  const totals = matrix?.totals

  const columns = useMemo<ColumnsType<SupplierPreorderMatrixRow>>(() => {
    // 0 显示成淡色圆点，便于扫视哪些店下了单
    const renderValue = (value: number) =>
      value ? formatMatrixValue(value, unit) : <span className="sp-admin-faint" aria-label="0">·</span>
    return [
      { title: '', dataIndex: 'imageUrl', key: 'image', width: 52, fixed: 'left', render: (value?: string | null) => <ItemThumb url={value} /> },
      {
        title: t('spAdmin.matrix.colProduct'),
        key: 'product',
        width: 260,
        fixed: 'left',
        render: (_, row) => (
          <div className="sp-admin-cell-stack">
            <span className="sp-admin-strong sp-admin-ellipsis" title={row.name}>
              {row.name}
            </span>
            <span className="sp-admin-muted sp-admin-small sp-admin-mono">{row.supplierItemCode}</span>
          </div>
        ),
      },
      { title: t('spAdmin.matrix.colPrice'), dataIndex: 'price', width: 84, align: 'right', className: 'sp-admin-number', render: formatMoney },
      { title: t('spAdmin.matrix.colCartonQty'), dataIndex: 'cartonQty', width: 64, align: 'right', className: 'sp-admin-number' },
      ...stores.map((store) => ({
        title: store.storeName,
        key: `store-${store.storeGuid}`,
        width: unit === 'amount' ? 108 : 88,
        align: 'right' as const,
        className: 'sp-admin-number',
        ellipsis: true,
        render: (_: unknown, row: SupplierPreorderMatrixRow) => renderValue(getMatrixCellValue(row, store.storeGuid, unit)),
      })),
      {
        title: t('spAdmin.matrix.colTotalCartons'),
        dataIndex: 'totalCartons',
        width: 80,
        align: 'right',
        className: 'sp-admin-number sp-admin-strong sp-admin-col-total',
        render: formatInteger,
      },
      { title: t('spAdmin.matrix.colTotalPieces'), dataIndex: 'totalPieces', width: 84, align: 'right', className: 'sp-admin-number', render: formatInteger },
      { title: t('spAdmin.matrix.colTotalAmount'), dataIndex: 'totalAmount', width: 112, align: 'right', className: 'sp-admin-number', render: formatMoney },
      { title: 'm³', dataIndex: 'totalCbm', width: 72, align: 'right', className: 'sp-admin-number', render: formatCbm },
    ]
  }, [stores, t, unit])

  const tableWidth = 52 + 260 + 84 + 64 + stores.length * (unit === 'amount' ? 108 : 88) + 80 + 84 + 112 + 72

  return (
    <div className="sp-admin-card">
      <div className="sp-admin-toolbar">
        <h3 className="sp-admin-section-title">{t('spAdmin.matrix.title', { unit: t(`spAdmin.matrix.unit.${unit}`) })}</h3>
        <Segmented<MatrixUnit>
          size="small"
          value={unit}
          onChange={setUnit}
          options={[
            { value: 'cartons', label: t('spAdmin.matrix.unit.cartons') },
            { value: 'pieces', label: t('spAdmin.matrix.unit.pieces') },
            { value: 'amount', label: t('spAdmin.matrix.unit.amount') },
          ]}
        />
        <label className="sp-admin-switch-label">
          <Switch
            size="small"
            checked={onlyOrdered}
            onChange={(checked) => {
              setOnlyOrdered(checked)
              setPage(1)
            }}
          />
          <span>
            {t('spAdmin.matrix.onlyOrdered')}
            {totals ? <span className="sp-admin-muted">{t('spAdmin.matrix.orderedCount', { count: totals.orderedItemCount })}</span> : null}
          </span>
        </label>
        <span className="sp-admin-toolbar-spacer" />
        <Input.Search
          className="sp-admin-search"
          allowClear
          value={keywordInput}
          placeholder={t('spAdmin.matrix.searchPlaceholder')}
          onChange={(event) => setKeywordInput(event.target.value)}
          onSearch={(value) => {
            setKeyword(value.trim())
            setPage(1)
          }}
        />
      </div>
      <MeasuredTable<SupplierPreorderMatrixRow>
        metricId="supplier-preorder-matrix"
        className="sp-admin-table sp-admin-matrix"
        rowKey="itemGuid"
        size="small"
        loading={loading}
        columns={columns}
        dataSource={matrix?.rows.items ?? []}
        scroll={{ x: tableWidth }}
        locale={{ emptyText: onlyOrdered ? t('spAdmin.matrix.emptyOrdered') : t('spAdmin.matrix.empty') }}
        summary={() =>
          totals ? (
            <MeasuredTable.Summary fixed="bottom">
              <MeasuredTable.Summary.Row className="sp-admin-summary-row">
                <MeasuredTable.Summary.Cell index={0} />
                <MeasuredTable.Summary.Cell index={1}>
                  <span className="sp-admin-strong">{t('spAdmin.matrix.totalRow', { count: matrix?.rows.total ?? 0 })}</span>
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={2} />
                <MeasuredTable.Summary.Cell index={3} />
                {stores.map((store, index) => (
                  <MeasuredTable.Summary.Cell key={store.storeGuid} index={4 + index} align="right" className="sp-admin-number sp-admin-strong">
                    {formatMatrixValue(getMatrixStoreTotal(totals, store.storeGuid, unit), unit)}
                  </MeasuredTable.Summary.Cell>
                ))}
                <MeasuredTable.Summary.Cell index={4 + stores.length} align="right" className="sp-admin-number sp-admin-strong">
                  {formatInteger(totals.totalCartons)}
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={5 + stores.length} align="right" className="sp-admin-number sp-admin-strong">
                  {formatInteger(totals.totalPieces)}
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={6 + stores.length} align="right" className="sp-admin-number sp-admin-strong">
                  {formatMoney(totals.totalAmount)}
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={7 + stores.length} align="right" className="sp-admin-number sp-admin-strong">
                  {formatCbm(totals.totalCbm)}
                </MeasuredTable.Summary.Cell>
              </MeasuredTable.Summary.Row>
            </MeasuredTable.Summary>
          ) : null
        }
        pagination={{
          current: page,
          pageSize,
          total: matrix?.rows.total ?? 0,
          showSizeChanger: true,
          pageSizeOptions: PAGE_SIZE_OPTIONS,
          showTotal: (count, range) => t('spAdmin.matrix.pageTotal', { count, from: range[0], to: range[1] }),
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
