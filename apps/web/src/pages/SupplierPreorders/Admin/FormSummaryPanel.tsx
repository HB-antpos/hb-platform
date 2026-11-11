import { App, Button, Empty, Pagination } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { enableSupplierPreorderFormStore, getSupplierPreorderMatrix } from '../../../services/supplierPreorderService'
import type { SupplierPreorderMatrix, SupplierPreorderMatrixFilter, SupplierPreorderMatrixSort, SupplierPreorderStoreCandidate } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { FreshStoreBanner, HiddenColumnsHint } from './SummaryColumnNotices'
import { collectOrderedStoreGuids, summarizeHiddenColumns } from './summaryColumns.logic'
import SummaryDrawer from './SummaryDrawer'
import SummaryEnableStoreModal from './SummaryEnableStoreModal'
import { applyPendingEdits, applySaveResult, stepIndex } from './summaryMatrix.logic'
import SummaryMetrics from './SummaryMetrics'
import { DEFAULT_SORT, nextStoreSort, toSortQuery } from './summarySort.logic'
import SummaryStorePicker from './SummaryStorePicker'
import SummaryTable from './SummaryTable'
import type { SummaryColumnControls } from './SummaryTableHead'
import SummaryToolbar from './SummaryToolbar'
import { DEFAULT_PAGE_SIZE, PAGE_SIZE_OPTIONS, getErrorText, isAbortError, type MatrixUnit } from './supplierPreorderAdmin.logic'
import { useColumnDrag } from './useColumnDrag'
import { useStoreCandidates } from './useStoreCandidates'
import { useSummaryColumns } from './useSummaryColumns'
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
  /** 在这里开启了新分店：页面要刷新页头统计、「分店开启」页签与操作记录（等同那边开关变化后的刷新）；不传则只重拉矩阵 */
  onStoresChanged?: () => void
}

/** 刚开启的分店整列高亮多久（毫秒），之后淡出 */
const FRESH_HIGHLIGHT_MS = 3000

/**
 * 「订货汇总」页签：指标条 + 工具条 + 可直接改所有分店箱数的矩阵 + 商品历史进销抽屉。
 * 数据流：矩阵来自 GET matrix；格子 / 抽屉输入框都走同一条逐格保存队列（PUT stores/{store}/lines/{item}），
 * 保存中的值用 applyPendingEdits 叠加显示，成功后用服务端返回的 totals / 行合计 / 标记覆盖。
 * v2：商品排序（服务端排）、分店列显示 / 顺序 / 拖拽（客户端视图，按订单表记在本机；合计仍是全部开启分店）、
 * 「添加分店 / 开启并下单」。表格只拿「显示的分店」，指标条与抽屉仍拿全部开启分店。
 */
export default function FormSummaryPanel({ formGuid, refreshKey, containerType, containerCapacityCbm, onEdited, onStoresChanged }: FormSummaryPanelProps) {
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
  // 加载矩阵时算一次的「有订单的分店」：默认显示规则用它，避免把某店数量改成 0 后整列消失
  const [orderedGuids, setOrderedGuids] = useState<ReadonlySet<string>>(() => new Set())
  const cols = useSummaryColumns(formGuid, matrix?.stores, orderedGuids)
  const sort = cols.sort
  const candidates = useStoreCandidates(formGuid, refreshKey)
  const drag = useColumnDrag(cols.reorder)
  const [pickerOpen, setPickerOpen] = useState(false)
  const [enableTarget, setEnableTarget] = useState<SupplierPreorderStoreCandidate | null>(null)
  const [enabling, setEnabling] = useState(false)
  // 刚开启的分店：列头标「新开启」，整列高亮 FRESH_HIGHLIGHT_MS 后淡出，提示条留到用户关掉
  const [fresh, setFresh] = useState<{ guid: string; name: string; highlight: boolean } | null>(null)
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    getSupplierPreorderMatrix(formGuid, { page, pageSize, keyword: keyword || undefined, filter, category: category || undefined, ...toSortQuery(sort) }, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return
        setMatrix(result)
        setOrderedGuids(collectOrderedStoreGuids(result.stores, result.storeStatuses, result.totals.storeCartons))
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
  }, [category, filter, formGuid, keyword, message, page, pageSize, refreshKey, reloadTick, sort, t])

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
  const hiddenSummary = summarizeHiddenColumns(cols.resolved, orderedGuids)
  const resetKey = `${page}|${pageSize}|${filter}|${category}|${keyword}|${refreshKey}|${sort.sort}|${sort.storeGuid ?? ''}`

  // 表格只渲染「显示的分店」；view 本身（指标条 / 抽屉）仍带全部开启分店
  const visibleStores = cols.resolved.visible
  const tableMatrix = useMemo(() => (view ? { ...view, stores: visibleStores } : null), [view, visibleStores])

  // 刚开启的分店高亮到点后淡出（列头「新开启」标记与提示条继续保留）
  const highlightOn = fresh?.highlight ?? false
  useEffect(() => {
    if (!highlightOn) return
    const timer = window.setTimeout(() => setFresh((current) => (current ? { ...current, highlight: false } : current)), FRESH_HIGHLIGHT_MS)
    return () => window.clearTimeout(timer)
  }, [highlightOn, fresh?.guid])

  const changeSort = (value: SupplierPreorderMatrixSort) => {
    cols.setSort({ sort: value })
    resetPage()
  }

  /** 键盘 Alt+←/→ 移列后，列重排可能让焦点丢失：下一帧把焦点放回这一列头的排序按钮 */
  const moveColumn = (storeGuid: string, delta: number) => {
    if (!cols.moveBy(storeGuid, delta)) return
    requestAnimationFrame(() => rootRef.current?.querySelector<HTMLElement>(`th[data-store-th="${storeGuid}"] .sp-admin-sum-sortbtn`)?.focus())
  }

  /** 开启并下单：确认框里点了「开启并添加列」后才真正调用接口；成功后加列、提示、刷新矩阵 */
  const confirmEnable = async (switchToAll: boolean) => {
    const target = enableTarget
    if (!target || enabling) return
    setEnabling(true)
    try {
      await enableSupplierPreorderFormStore(formGuid, target.storeGuid)
      cols.show(target.storeGuid)
      candidates.remove(target.storeGuid)
      setEnableTarget(null)
      setFresh({ guid: target.storeGuid, name: target.storeName, highlight: true })
      message.success(t('spAdmin.summary.enableDone', { store: target.storeName }))
      if (switchToAll && filter !== 'all') {
        setFilter('all')
        resetPage()
      }
      // 优先走页面回调（刷新页头统计 / 分店开启页签 / 操作记录，同时递增 refreshKey 重拉矩阵）；没有回调时自己重拉
      if (onStoresChanged) onStoresChanged()
      else setReloadTick((value) => value + 1)
    } catch (error) {
      // 失败时不加列，保留确认框，提示后端给的原因（如 STORE_INVALID：分店不存在或已停用）
      message.error(getErrorText(error, t('spAdmin.summary.enableFailed')))
    } finally {
      setEnabling(false)
    }
  }

  const controls: SummaryColumnControls = {
    sort,
    onSortSeq: () => {
      cols.setSort(DEFAULT_SORT)
      resetPage()
    },
    onSortStore: (storeGuid) => {
      cols.setSort(nextStoreSort(sort, storeGuid))
      resetPage()
    },
    drag,
    onMoveBy: moveColumn,
    freshGuid: fresh?.guid ?? null,
    freshHighlight: highlightOn,
    hiddenCount: cols.resolved.hiddenStores.length,
    offCount: candidates.candidates ? candidates.candidates.length : null,
    onOpenPicker: () => setPickerOpen(true),
  }

  if (!view || !tableMatrix) {
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
    <div className="sp-admin-sum" ref={rootRef}>
      <SummaryMetrics matrix={view} containerType={containerType} containerCapacityCbm={containerCapacityCbm} shownColumnCount={visibleStores.length} />
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
          sort={sort}
          sortStoreName={view.stores.find((store) => store.storeGuid === sort.storeGuid)?.storeName ?? ''}
          onSortChange={changeSort}
          shownColumnCount={visibleStores.length}
          enabledStoreCount={view.stores.length}
          pickerOpen={pickerOpen}
          onPickerOpenChange={setPickerOpen}
          pickerContent={
            pickerOpen ? (
              <SummaryStorePicker
                resolved={cols.resolved}
                storeStatuses={view.storeStatuses}
                storeCartons={view.totals.storeCartons}
                candidates={candidates}
                drag={drag}
                onShow={cols.show}
                onHide={cols.hide}
                onShowOnlyOrdered={cols.showOnlyOrdered}
                onReset={cols.reset}
                onEnable={(candidate) => {
                  // 开启会改变订单表：先关下拉，再弹确认框，确认后才调用接口
                  setPickerOpen(false)
                  setEnableTarget(candidate)
                }}
                onClose={() => setPickerOpen(false)}
              />
            ) : null
          }
        />
        <HiddenColumnsHint
          shownCount={visibleStores.length}
          hiddenCount={hiddenSummary.count}
          hiddenNames={hiddenSummary.names}
          allWithoutOrder={hiddenSummary.allWithoutOrder}
          offCount={controls.offCount}
          onManage={() => setPickerOpen(true)}
        />
        {fresh ? (
          <FreshStoreBanner
            storeName={fresh.name}
            orderedCount={filter === 'ordered' ? view.counts.ordered : null}
            onShowAll={() => {
              setFilter('all')
              resetPage()
            }}
            onClose={() => setFresh(null)}
          />
        ) : null}
        <SummaryTable
          matrix={tableMatrix}
          unit={unit}
          compare={compare}
          editable={editable && unit === 'cartons'}
          loading={loading}
          selectedGuid={selectedGuid}
          busyKeys={queue.busyKeys}
          resetKey={resetKey}
          controls={controls}
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
      <SummaryEnableStoreModal
        candidate={enableTarget}
        confirming={enabling}
        defaultSwitchToAll={filter !== 'all'}
        onConfirm={(switchToAll) => void confirmEnable(switchToAll)}
        onCancel={() => setEnableTarget(null)}
      />
      {/* 键盘移动列后的播报（屏幕阅读器） */}
      <div className="sp-admin-sum-sr" role="status" aria-live="polite">
        {cols.announcement}
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
