import { App, Button, Empty, Pagination } from 'antd'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { getSupplierPreorderMatrix } from '../../../services/supplierPreorderService'
import type { SupplierPreorderMatrix, SupplierPreorderMatrixFilter } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import SummaryDrawer from './SummaryDrawer'
import { applyPendingEdits, applySaveResult, stepIndex } from './summaryMatrix.logic'
import SummaryMetrics from './SummaryMetrics'
import SummaryTable from './SummaryTable'
import SummaryToolbar from './SummaryToolbar'
import { DEFAULT_PAGE_SIZE, PAGE_SIZE_OPTIONS, getErrorText, isAbortError, type MatrixUnit } from './supplierPreorderAdmin.logic'
import { useSummaryHistory, useSummarySaveQueue } from './useSummaryEditing'
import './summary.css'

interface FormSummaryPanelProps {
  formGuid: string
  /** 分店开关 / 修订版应用后递增，触发重新加载矩阵并清空抽屉历史缓存 */
  refreshKey: number
  containerType?: string | null
  containerCapacityCbm?: number | null
  /** 保存队列清空后通知页面刷新页头统计与操作记录 */
  onEdited: () => void
}

/**
 * 「订货汇总」页签：指标条 + 工具条 + 可直接改所有分店箱数的矩阵 + 商品历史进销抽屉。
 * 数据流：矩阵来自 GET matrix；格子 / 抽屉输入框都走同一条逐格保存队列（PUT stores/{store}/lines/{item}），
 * 保存中的值用 applyPendingEdits 叠加显示，成功后用服务端返回的 totals / 行合计 / 标记覆盖。
 */
export default function FormSummaryPanel({ formGuid, refreshKey, containerType, containerCapacityCbm, onEdited }: FormSummaryPanelProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [filter, setFilter] = useState<SupplierPreorderMatrixFilter>('ordered')
  const [category, setCategory] = useState('')
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [unit, setUnit] = useState<MatrixUnit>('cartons')
  const [compare, setCompare] = useState(true)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [matrix, setMatrix] = useState<SupplierPreorderMatrix | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadFailed, setLoadFailed] = useState(false)
  const [reloadTick, setReloadTick] = useState(0)
  const [selectedGuid, setSelectedGuid] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    getSupplierPreorderMatrix(formGuid, { page, pageSize, keyword: keyword || undefined, filter, category: category || undefined }, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return
        setMatrix(result)
        setLoadFailed(false)
      })
      .catch((error) => {
        if (controller.signal.aborted || isAbortError(error)) return
        setLoadFailed(true)
        message.error(getErrorText(error, t('spAdmin.common.loadFailed')))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [category, filter, formGuid, keyword, message, page, pageSize, refreshKey, reloadTick, t])

  const handleSaved = useCallback((result: Parameters<typeof applySaveResult>[1]) => {
    setMatrix((current) => (current ? applySaveResult(current, result) : current))
  }, [])
  const queue = useSummarySaveQueue({ formGuid, onSaved: handleSaved, onIdle: onEdited })
  const history = useSummaryHistory(formGuid, refreshKey)

  // 叠加保存中的值后的矩阵：表格、指标条、抽屉都读它
  const view = useMemo(() => (matrix ? applyPendingEdits(matrix, queue.pending) : null), [matrix, queue.pending])

  const rows = view?.rows.items
  const selectedIndex = useMemo(() => (rows && selectedGuid ? rows.findIndex((row) => row.itemGuid === selectedGuid) : -1), [rows, selectedGuid])
  // 当前页 / 筛选变了、选中的商品已不在列表里：关闭抽屉
  useEffect(() => {
    if (selectedGuid && rows && selectedIndex < 0 && !loading) setSelectedGuid(null)
  }, [loading, rows, selectedGuid, selectedIndex])

  // 只在当前页范围内切换
  const rowCount = rows?.length ?? 0
  const handleStep = useCallback(
    (delta: number) => {
      if (!rows) return
      const next = stepIndex(selectedIndex, delta, rowCount)
      if (next !== null) setSelectedGuid(rows[next].itemGuid)
    },
    [rowCount, rows, selectedIndex],
  )
  const closeDrawer = useCallback(() => setSelectedGuid(null), [])

  const resetPage = () => setPage(1)
  const editable = !!view?.editable
  const resetKey = `${page}|${pageSize}|${filter}|${category}|${keyword}|${refreshKey}`

  if (!view) {
    return (
      <div className="sp-admin-card sp-admin-sum-initial">
        {loadFailed ? (
          <Empty description={t('spAdmin.common.loadFailed')}>
            <Button type="primary" onClick={() => setReloadTick((value) => value + 1)}>
              {t('spAdmin.common.retry')}
            </Button>
          </Empty>
        ) : (
          <div className="sp-admin-loading">{t('spAdmin.summary.loading')}</div>
        )}
      </div>
    )
  }

  return (
    <div className="sp-admin-sum">
      <SummaryMetrics matrix={view} containerType={containerType} containerCapacityCbm={containerCapacityCbm} />
      <div className="sp-admin-card sp-admin-sum-card-main">
        <SummaryToolbar
          keywordInput={keywordInput}
          onKeywordInputChange={setKeywordInput}
          onSearch={(value) => {
            setKeyword(value.trim())
            resetPage()
          }}
          category={category}
          categories={view.categories}
          onCategoryChange={(value) => {
            setCategory(value)
            resetPage()
          }}
          filter={filter}
          counts={view.counts}
          onFilterChange={(value) => {
            setFilter(value)
            resetPage()
          }}
          compare={compare}
          onCompareChange={setCompare}
          unit={unit}
          onUnitChange={setUnit}
          editable={editable}
          busyCount={queue.busyCount}
          savedAtText={queue.savedAtText}
        />
        <SummaryTable
          matrix={view}
          unit={unit}
          compare={compare}
          editable={editable && unit === 'cartons'}
          loading={loading}
          selectedGuid={selectedGuid}
          busyKeys={queue.busyKeys}
          resetKey={resetKey}
          onSelectRow={setSelectedGuid}
          commit={queue.commit}
        />
        <div className="sp-admin-sum-pager sp-admin-number">
          <span className="sp-admin-sum-hints">
            {t('spAdmin.summary.pageTotal', { count: view.rows.total, from: view.rows.total ? (page - 1) * pageSize + 1 : 0, to: (page - 1) * pageSize + rowCount })}
            <span className="sp-admin-sum-keys">
              <kbd>Enter</kbd> {t('spAdmin.summary.keyEnter')} <kbd>Tab</kbd> {t('spAdmin.summary.keyTab')} <kbd>↑↓←→</kbd> {t('spAdmin.summary.keyArrows')} <kbd>Esc</kbd> {t('spAdmin.summary.keyEsc')}
            </span>
          </span>
          <Pagination
            size="small"
            current={page}
            pageSize={pageSize}
            total={view.rows.total}
            showSizeChanger
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            showLessItems
            onChange={(nextPage, nextPageSize) => {
              if (nextPageSize !== pageSize) {
                setPageSize(nextPageSize)
                setPage(1)
              } else {
                setPage(nextPage)
              }
            }}
          />
        </div>
      </div>
      <SummaryDrawer
        row={selectedIndex >= 0 && rows ? rows[selectedIndex] : null}
        stores={view.stores}
        index={selectedIndex}
        pageCount={rowCount}
        pageStartNumber={(page - 1) * pageSize + 1}
        total={view.rows.total}
        entry={selectedGuid ? history.entries[selectedGuid] : undefined}
        editable={editable}
        busyKeys={queue.busyKeys}
        busyCount={queue.busyCount}
        savedAtText={queue.savedAtText}
        onClose={closeDrawer}
        onStep={handleStep}
        onLoadHistory={history.load}
        commit={queue.commit}
      />
    </div>
  )
}
