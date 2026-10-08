import { CopyOutlined, DownloadOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Grid, Segmented, Select, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveLocalSuppliers } from '../../../services/localSupplierService'
import { getSeasonalCardStatsStoreDetail, getSeasonalCardStatsSummary } from '../../../services/seasonalCardStatsService'
import type { LocalSupplierDto } from '../../../types/localSupplier'
import type {
  SeasonalCardPriceOption,
  SeasonalCardStatsStoreDetail,
  SeasonalCardStatsStoreRef,
  SeasonalCardStatsStoreRow,
  SeasonalCardStatsSummary,
  SeasonalCardType,
} from '../../../types/seasonalCardStats'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'

import { exportSeasonalCardStatsWorkbook } from './export'
import {
  CARD_TYPES,
  EMPTY_VALUE,
  PRICE_OPTIONS,
  STATUS_FILTERS,
  averagePerFilledStore,
  buildFooterTotals,
  buildPriceBars,
  buildSummaryQuery,
  buildUnfilledCopyText,
  buildYearOptions,
  classifyStatsError,
  countByStatus,
  createAbortableRequestGuard,
  createDefaultFilters,
  fillPercent,
  filterRowsByStatus,
  formatCount,
  formatLocalTime,
  formatMoney,
  OTHER_PRICE_OPTION,
  formatSupplierList,
  getPriceLabel,
  getPriceQuantity,
  hasQuantityFilter,
  isAbortError,
  mergeStoreUniverse,
  parseStoreParam,
  withStoreParam,
  type FillStatusFilter,
  type SeasonalCardStatsFilters,
  type StatsErrorKind,
} from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import StoreDetailDrawer from './StoreDetailDrawer'
import './seasonalCardStats.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）；全局语言包只有菜单标题一个键。
registerPageMessages({ zh, en })

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
const CARD_TYPE_LABEL_KEYS: Record<SeasonalCardType, string> = {
  1: 'seasonalCardStats.cardTypes.christmas',
  2: 'seasonalCardStats.cardTypes.valentines',
  3: 'seasonalCardStats.cardTypes.mothersDay',
  4: 'seasonalCardStats.cardTypes.easter',
  5: 'seasonalCardStats.cardTypes.fathersDay',
}

const STATUS_LABEL_KEYS: Record<FillStatusFilter, string> = {
  all: 'seasonalCardStats.status.all',
  filled: 'seasonalCardStats.status.filled',
  unfilled: 'seasonalCardStats.status.unfilled',
}

/** 各列宽度之和：容器比它窄时表格在卡片内横向滚动，「操作」列固定在右侧。 */
const TABLE_MIN_WIDTH = 1296

export default function SeasonalCardStatsPage() {
  const { t } = useTranslation()
  const idPrefix = useId()
  // keepAlive：页面被缓存（切到别的标签页）时 active 为 false，此时地址栏属于别的页面，不能再按它改本页状态。
  const { active } = useKeepAliveContext()
  // 窄屏（< md）不固定左右列：分店列 + 操作列会占满 390px 宽的手机屏，中间的数量列被挤得看不见。
  const screens = Grid.useBreakpoint()
  const pinColumns = screens.md !== false
  const [searchParams, setSearchParams] = useSearchParams()

  // 「今年」取悉尼日历年（门店都在澳洲），不按浏览器时区。
  const [currentYear] = useState(() => Number(formatSydneyIsoDate().slice(0, 4)))
  const yearOptions = useMemo(() => buildYearOptions(currentYear), [currentYear])
  const [filters, setFilters] = useState<SeasonalCardStatsFilters>(() => createDefaultFilters(currentYear))
  const [status, setStatus] = useState<FillStatusFilter>('all')

  const [summary, setSummary] = useState<SeasonalCardStatsSummary | null>(null)
  const [loading, setLoading] = useState(true)
  const [listError, setListError] = useState<StatsErrorKind | null>(null)
  const [reloadTick, setReloadTick] = useState(0)
  const [listGuard] = useState(createAbortableRequestGuard)
  const [exporting, setExporting] = useState(false)

  const [suppliers, setSuppliers] = useState<LocalSupplierDto[]>([])
  // 分店选项来自汇总接口本身（应填报分店），不依赖需要 Stores.View 的分店清单接口。
  const [storeUniverse, setStoreUniverse] = useState<SeasonalCardStatsStoreRef[]>([])

  // 抽屉：selectedStore 是页面内状态，地址栏 ?store= 与它同步（激活期间以地址栏为准）。
  const urlStore = parseStoreParam(searchParams.get('store'))
  const [selectedStore, setSelectedStore] = useState<string | null>(urlStore)
  const selectedStoreRef = useRef(selectedStore)
  selectedStoreRef.current = selectedStore
  const wasActiveRef = useRef(active)
  const [detail, setDetail] = useState<SeasonalCardStatsStoreDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<StatsErrorKind | null>(null)
  const [detailTick, setDetailTick] = useState(0)
  const [detailGuard] = useState(createAbortableRequestGuard)

  // ───────────── 供应商选项 ─────────────
  useEffect(() => {
    let disposed = false
    void getActiveLocalSuppliers()
      .then((items) => {
        if (!disposed) setSuppliers(items)
      })
      .catch((error) => {
        console.error(error)
        if (!disposed) message.error(t('seasonalCardStats.errors.loadSuppliersFailed'))
      })
    return () => {
      disposed = true
    }
    // t 随语言变化，不应因此重新请求供应商。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // ───────────── 汇总请求：筛选一变就重新请求，只认最后一次 ─────────────
  useEffect(() => {
    const { requestId, signal } = listGuard.begin()
    setLoading(true)
    setListError(null)
    getSeasonalCardStatsSummary(buildSummaryQuery(filters), signal)
      .then((data) => {
        if (!listGuard.isLatest(requestId)) return
        setSummary(data)
        setStoreUniverse((current) => mergeStoreUniverse(current, data.stores))
      })
      .catch((error) => {
        if (isAbortError(error) || !listGuard.isLatest(requestId)) return
        console.error(error)
        // 失败时不保留上一次的结果：它属于另一组筛选条件，留着会被误读成当前结果。
        setSummary(null)
        setListError(classifyStatsError(error))
      })
      .finally(() => {
        if (listGuard.isLatest(requestId)) setLoading(false)
      })
  }, [filters, listGuard, reloadTick])

  useEffect(
    () => () => {
      listGuard.abort()
      detailGuard.abort()
    },
    [listGuard, detailGuard],
  )

  // ───────────── 抽屉状态与地址栏同步 ─────────────
  const writeStoreParam = useCallback(
    (storeCode: string | null) => {
      setSearchParams((current) => withStoreParam(current, storeCode), { replace: true })
    },
    [setSearchParams],
  )

  useEffect(() => {
    const justActivated = active && !wasActiveRef.current
    wasActiveRef.current = active
    if (!active) return
    if (justActivated && !urlStore && selectedStoreRef.current) {
      // 从别的标签页切回来：标签页导航不带查询参数，但抽屉之前是开着的，把它写回地址栏而不是悄悄关掉。
      writeStoreParam(selectedStoreRef.current)
      return
    }
    setSelectedStore(urlStore)
  }, [active, urlStore, writeStoreParam])

  const openDetail = useCallback(
    (storeCode: string) => {
      setSelectedStore(storeCode)
      writeStoreParam(storeCode)
    },
    [writeStoreParam],
  )

  const closeDetail = useCallback(() => {
    setSelectedStore(null)
    writeStoreParam(null)
  }, [writeStoreParam])

  const { seasonYear, cardType } = filters
  useEffect(() => {
    if (!selectedStore) {
      detailGuard.abort()
      setDetail(null)
      setDetailError(null)
      setDetailLoading(false)
      return
    }
    const { requestId, signal } = detailGuard.begin()
    setDetailLoading(true)
    setDetailError(null)
    setDetail(null)
    getSeasonalCardStatsStoreDetail(selectedStore, { seasonYear, cardType }, signal)
      .then((data) => {
        if (detailGuard.isLatest(requestId)) setDetail(data)
      })
      .catch((error) => {
        if (isAbortError(error) || !detailGuard.isLatest(requestId)) return
        console.error(error)
        setDetailError(classifyStatsError(error))
      })
      .finally(() => {
        if (detailGuard.isLatest(requestId)) setDetailLoading(false)
      })
  }, [selectedStore, seasonYear, cardType, detailGuard, detailTick])

  // ───────────── 视图数据 ─────────────
  const holidayLabel = t(CARD_TYPE_LABEL_KEYS[filters.cardType])
  const otherLabel = t('seasonalCardStats.price.other')
  const priceLabel = (option: SeasonalCardPriceOption) => getPriceLabel(option, otherLabel)
  const supplierSeparator = t('seasonalCardStats.listSeparator')

  const rows = useMemo(() => summary?.stores ?? [], [summary])
  const statusCounts = countByStatus(rows)
  const visibleRows = useMemo(() => filterRowsByStatus(rows, status), [rows, status])
  const footer = buildFooterTotals(visibleRows)
  const average = summary ? averagePerFilledStore(summary.totalQuantity, summary.filledStoreCount) : null
  const priceBars = summary ? buildPriceBars(summary.priceTotals) : []
  const quantityFiltered = hasQuantityFilter(filters)
  const previewRow = selectedStore ? rows.find((row) => row.storeCode === selectedStore) ?? null : null

  const supplierOptions = useMemo(
    () =>
      suppliers.map((supplier) => ({
        value: supplier.localSupplierCode,
        label: supplier.name || supplier.localSupplierCode,
        code: supplier.localSupplierCode,
      })),
    [suppliers],
  )

  const storeOptions = useMemo(
    () =>
      storeUniverse.map((store) => ({
        value: store.storeCode,
        label: store.storeName ? `${store.storeCode} · ${store.storeName}` : store.storeCode,
      })),
    [storeUniverse],
  )

  const updateFilters = (patch: Partial<SeasonalCardStatsFilters>) => setFilters((current) => ({ ...current, ...patch }))

  const copyUnfilled = () => {
    const text = buildUnfilledCopyText(summary?.unfilledStores ?? [])
    void copyTextToClipboard(text, {
      successMessage: t('seasonalCardStats.unfilled.copied', { count: summary?.unfilledStores.length ?? 0 }),
      failureMessage: t('seasonalCardStats.unfilled.copyFailed'),
    })
  }

  const handleExport = async () => {
    if (!summary || exporting) return
    setExporting(true)
    try {
      const supplier = filters.localSupplierCode
        ? supplierOptions.find((option) => option.value === filters.localSupplierCode)?.label ?? filters.localSupplierCode
        : t('seasonalCardStats.filters.allSuppliers')
      const { fileName } = await exportSeasonalCardStatsWorkbook({
        summary,
        context: {
          seasonYear: filters.seasonYear,
          holiday: holidayLabel,
          supplier,
          price: filters.priceOption ? priceLabel(filters.priceOption) : t('seasonalCardStats.filters.allPrices'),
        },
        text: (key, params) => t(key, params),
      })
      message.success(t('seasonalCardStats.export.done', { fileName }))
    } catch (error) {
      console.error(error)
      message.error(t('seasonalCardStats.export.failed'))
    } finally {
      setExporting(false)
    }
  }

  const priceColumns: ColumnsType<SeasonalCardStatsStoreRow> = PRICE_OPTIONS.map((option) => ({
    key: `price-${option}`,
    title: priceLabel(option),
    // 「其他价格 / Other price」比 $1 长：单独放宽，英文表头不折行
    width: option === OTHER_PRICE_OPTION ? 104 : 76,
    align: 'right' as const,
    render: (_: unknown, record: SeasonalCardStatsStoreRow) =>
      record.isFilled ? formatCount(getPriceQuantity(record.prices, option)) : EMPTY_VALUE,
  }))

  const columns: ColumnsType<SeasonalCardStatsStoreRow> = [
    {
      key: 'store',
      title: t('seasonalCardStats.columns.store'),
      width: 200,
      fixed: pinColumns ? 'left' : undefined,
      render: (_, record) => (
        <div className="seasonal-card-stats-store">
          <span className="seasonal-card-stats-code">{record.storeCode}</span>
          {/* 店名过长时单行省略，悬停看全名，避免固定列里折成两行把行高撑得参差不齐 */}
          <span className="seasonal-card-stats-store-name" title={record.storeName || undefined}>
            {record.storeName || EMPTY_VALUE}
          </span>
        </div>
      ),
    },
    {
      key: 'status',
      title: t('seasonalCardStats.columns.status'),
      // 英文「Not submitted」标签与合计行「21 submitted」都要一行放下
      width: 120,
      render: (_, record) => (
        <span className={`seasonal-card-stats-tag ${record.isFilled ? 'is-filled' : 'is-unfilled'}`}>
          {record.isFilled ? t('seasonalCardStats.status.filled') : t('seasonalCardStats.status.unfilled')}
        </span>
      ),
    },
    ...priceColumns,
    {
      key: 'totalQuantity',
      title: t('seasonalCardStats.columns.totalQuantity'),
      width: 96,
      align: 'right',
      render: (_, record) => (record.isFilled ? <b>{formatCount(record.totalQuantity)}</b> : EMPTY_VALUE),
    },
    {
      key: 'totalAmount',
      title: t('seasonalCardStats.columns.totalAmount'),
      width: 110,
      align: 'right',
      render: (_, record) => (record.isFilled ? formatMoney(record.totalAmount) : EMPTY_VALUE),
    },
    {
      key: 'suppliers',
      title: t('seasonalCardStats.columns.suppliers'),
      // 紧跟右对齐的金额列：加左内边距，避免「$197.00 示例供应商」挤在一起
      className: 'seasonal-card-stats-col-gap',
      width: 220,
      ellipsis: { showTitle: true },
      render: (_, record) => (record.isFilled ? formatSupplierList(record.suppliers, supplierSeparator) : EMPTY_VALUE),
    },
    {
      key: 'lastSubmitted',
      title: t('seasonalCardStats.columns.lastSubmitted'),
      width: 132,
      render: (_, record) =>
        record.lastSubmittedAt ? (
          <div className="seasonal-card-stats-two-line" title={formatLocalTime(record.lastSubmittedAt, 'full')}>
            <span>{formatLocalTime(record.lastSubmittedAt, 'short')}</span>
            <small>{record.lastSubmittedByName || EMPTY_VALUE}</small>
          </div>
        ) : (
          EMPTY_VALUE
        ),
    },
    {
      key: 'action',
      title: t('seasonalCardStats.columns.action'),
      width: 92,
      fixed: pinColumns ? 'right' : undefined,
      render: (_, record) => (
        <Button
          type="link"
          size="small"
          className="seasonal-card-stats-view"
          aria-label={t('seasonalCardStats.actions.viewLabel', { store: record.storeName || record.storeCode })}
          onClick={(event) => {
            // 整行也可点击，这里只保证键盘与读屏有明确的按钮入口，避免重复触发。
            event.stopPropagation()
            openDetail(record.storeCode)
          }}
        >
          {record.isFilled ? t('seasonalCardStats.actions.view') : t('seasonalCardStats.actions.viewHistory')}
        </Button>
      ),
    },
  ]

  const listErrorMessage: Record<StatsErrorKind, string> = {
    forbidden: t('seasonalCardStats.errors.forbidden'),
    notFound: t('seasonalCardStats.errors.loadFailed'),
    invalidQuery: t('seasonalCardStats.errors.invalidQuery'),
    failed: t('seasonalCardStats.errors.loadFailed'),
  }

  const emptyContent = loading ? (
    <span aria-hidden="true" />
  ) : listError ? (
    <div className="seasonal-card-stats-state is-error" role="alert">
      <strong>{listErrorMessage[listError]}</strong>
      {listError === 'failed' || listError === 'notFound' ? (
        <Button onClick={() => setReloadTick((tick) => tick + 1)}>{t('seasonalCardStats.errors.retry')}</Button>
      ) : null}
    </div>
  ) : (
    <div className="seasonal-card-stats-state" role="status">
      <strong>{rows.length ? t('seasonalCardStats.empty.filteredTitle') : t('seasonalCardStats.empty.title')}</strong>
      <span>{rows.length ? t('seasonalCardStats.empty.filteredHint') : t('seasonalCardStats.empty.hint')}</span>
    </div>
  )

  const kpiValue = (value: string) => (summary ? value : EMPTY_VALUE)
  const percent = summary ? fillPercent(summary.filledStoreCount, summary.storeCount) : 0
  const unfilledStores = summary?.unfilledStores ?? []

  return (
    <PageContainer
      compact
      title={t('seasonalCardStats.title')}
      subtitle={t('seasonalCardStats.subtitle')}
      extra={
        <div className="seasonal-card-stats-actions">
          <Tooltip title={t('seasonalCardStats.refresh')}>
            <Button
              icon={<ReloadOutlined />}
              aria-label={t('seasonalCardStats.refresh')}
              onClick={() => setReloadTick((tick) => tick + 1)}
            />
          </Tooltip>
          <Button icon={<DownloadOutlined />} loading={exporting} disabled={!summary || loading} onClick={handleExport}>
            {t('seasonalCardStats.export.button')}
          </Button>
        </div>
      }
    >
      <div className="seasonal-card-stats-card seasonal-card-stats-filter">
        <div className="seasonal-card-stats-field is-year">
          <label htmlFor={`${idPrefix}-year`}>{t('seasonalCardStats.filters.year')}</label>
          <Select
            id={`${idPrefix}-year`}
            value={filters.seasonYear}
            options={yearOptions.map((year) => ({
              value: year,
              label: year === currentYear ? t('seasonalCardStats.filters.currentYear', { year }) : String(year),
            }))}
            onChange={(year: number) => updateFilters({ seasonYear: year })}
          />
        </div>
        <div className="seasonal-card-stats-field is-holiday">
          <label htmlFor={`${idPrefix}-holiday`}>{t('seasonalCardStats.filters.holiday')}</label>
          <Select
            id={`${idPrefix}-holiday`}
            value={filters.cardType}
            options={CARD_TYPES.map((type) => ({ value: type, label: t(CARD_TYPE_LABEL_KEYS[type]) }))}
            onChange={(type: SeasonalCardType) => updateFilters({ cardType: type })}
          />
        </div>
        <div className="seasonal-card-stats-field is-supplier">
          <label htmlFor={`${idPrefix}-supplier`}>{t('seasonalCardStats.filters.supplier')}</label>
          <Select<string>
            id={`${idPrefix}-supplier`}
            allowClear
            showSearch
            placeholder={t('seasonalCardStats.filters.allSuppliers')}
            options={supplierOptions}
            value={filters.localSupplierCode}
            // 122 家供应商：搜索同时匹配名称与编码。
            filterOption={(input, option) => {
              const keyword = input.trim().toLowerCase()
              if (!keyword) return true
              return (
                String(option?.label ?? '').toLowerCase().includes(keyword) ||
                String(option?.code ?? '').toLowerCase().includes(keyword)
              )
            }}
            optionRender={(option) => (
              <span className="seasonal-card-stats-option">
                <span>{option.label}</span>
                <small>{(option.data as { code?: string }).code}</small>
              </span>
            )}
            onChange={(code?: string) => updateFilters({ localSupplierCode: code || undefined })}
          />
        </div>
        <div className="seasonal-card-stats-field is-price">
          <label htmlFor={`${idPrefix}-price`}>{t('seasonalCardStats.filters.price')}</label>
          <Select<SeasonalCardPriceOption>
            id={`${idPrefix}-price`}
            allowClear
            placeholder={t('seasonalCardStats.filters.allPrices')}
            options={PRICE_OPTIONS.map((option) => ({ value: option, label: priceLabel(option) }))}
            value={filters.priceOption}
            onChange={(option?: SeasonalCardPriceOption) => updateFilters({ priceOption: option })}
          />
        </div>
        <div className="seasonal-card-stats-field is-store">
          <label htmlFor={`${idPrefix}-store`}>{t('seasonalCardStats.filters.store')}</label>
          <Select<string[]>
            id={`${idPrefix}-store`}
            mode="multiple"
            allowClear
            showSearch
            maxTagCount="responsive"
            optionFilterProp="label"
            placeholder={t('seasonalCardStats.filters.allStores')}
            options={storeOptions}
            value={filters.storeCodes}
            onChange={(codes: string[]) => updateFilters({ storeCodes: codes })}
          />
        </div>
        <div className="seasonal-card-stats-field is-status">
          <span id={`${idPrefix}-status`}>{t('seasonalCardStats.filters.status')}</span>
          <Segmented<FillStatusFilter>
            aria-labelledby={`${idPrefix}-status`}
            value={status}
            onChange={setStatus}
            options={STATUS_FILTERS.map((key) => ({
              value: key,
              label: (
                <span className={`seasonal-card-stats-segment is-${key}`}>
                  {t(STATUS_LABEL_KEYS[key])}
                  {summary ? <span className="seasonal-card-stats-count">{formatCount(statusCounts[key])}</span> : null}
                </span>
              ),
            }))}
          />
        </div>
      </div>

      {quantityFiltered ? <p className="seasonal-card-stats-filter-note">{t('seasonalCardStats.filters.quantityOnlyNote')}</p> : null}

      <div className="seasonal-card-stats-kpis" aria-busy={loading}>
        <div className="seasonal-card-stats-card seasonal-card-stats-kpi">
          <span>{t('seasonalCardStats.kpi.filled')}</span>
          <div className="seasonal-card-stats-kpi-value">
            <b>{kpiValue(formatCount(summary?.filledStoreCount))}</b>
            <small>{summary ? t('seasonalCardStats.kpi.ofStores', { count: formatCount(summary.storeCount) }) : null}</small>
          </div>
          <div
            className="seasonal-card-stats-progress"
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percent}
            aria-label={t('seasonalCardStats.kpi.progress', { percent })}
          >
            <div style={{ width: `${percent}%` }} />
          </div>
        </div>
        <div className="seasonal-card-stats-card seasonal-card-stats-kpi">
          <span>{t('seasonalCardStats.kpi.unfilled')}</span>
          <div className="seasonal-card-stats-kpi-value">
            <b className={summary && summary.unfilledStoreCount > 0 ? 'is-warn' : undefined}>
              {kpiValue(formatCount(summary?.unfilledStoreCount))}
            </b>
            <small>{summary ? t('seasonalCardStats.kpi.storeUnit') : null}</small>
          </div>
          <small>{t('seasonalCardStats.kpi.unfilledHint')}</small>
        </div>
        <div className="seasonal-card-stats-card seasonal-card-stats-kpi">
          <span>{t('seasonalCardStats.kpi.totalQuantity')}</span>
          <div className="seasonal-card-stats-kpi-value">
            <b>{kpiValue(formatCount(summary?.totalQuantity))}</b>
            <small>{summary ? t('seasonalCardStats.kpi.cardUnit') : null}</small>
          </div>
          <small>
            {average === null
              ? t('seasonalCardStats.kpi.averageNone')
              : t('seasonalCardStats.kpi.average', { count: formatCount(average) })}
          </small>
        </div>
        <div className="seasonal-card-stats-card seasonal-card-stats-kpi">
          <span>{t('seasonalCardStats.kpi.totalAmount')}</span>
          <div className="seasonal-card-stats-kpi-value">
            <b>{kpiValue(formatMoney(summary?.totalAmount))}</b>
          </div>
          <small>{t('seasonalCardStats.kpi.amountHint')}</small>
        </div>
      </div>

      <div className="seasonal-card-stats-card seasonal-card-stats-results">
        <div className="seasonal-card-stats-results-head">
          <h2>{t('seasonalCardStats.table.title')}</h2>
          <span>
            {t('seasonalCardStats.table.caption', {
              count: formatCount(visibleRows.length),
              year: filters.seasonYear,
              holiday: holidayLabel,
            })}
          </span>
        </div>
        <MeasuredTable<SeasonalCardStatsStoreRow>
          metricId="pos-admin.seasonal-card-stats.table-1"
          className="seasonal-card-stats-table"
          rowKey="storeCode"
          size="middle"
          loading={loading}
          columns={columns}
          dataSource={visibleRows}
          pagination={false}
          scroll={{ x: TABLE_MIN_WIDTH }}
          locale={{ emptyText: emptyContent }}
          rowClassName={(record) =>
            [record.isFilled ? '' : 'is-unfilled', record.storeCode === selectedStore && active ? 'is-selected' : '']
              .filter(Boolean)
              .join(' ')
          }
          onRow={(record) => ({ onClick: () => openDetail(record.storeCode) })}
          summary={() =>
            visibleRows.length ? (
              <MeasuredTable.Summary>
                <MeasuredTable.Summary.Row className="seasonal-card-stats-total">
                  <MeasuredTable.Summary.Cell index={0}>{t('seasonalCardStats.table.total')}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={1}>
                    {t('seasonalCardStats.table.totalFilled', { count: formatCount(footer.filledCount) })}
                  </MeasuredTable.Summary.Cell>
                  {PRICE_OPTIONS.map((option, index) => (
                    <MeasuredTable.Summary.Cell key={option} index={2 + index} align="right">
                      {formatCount(footer.quantities[option])}
                    </MeasuredTable.Summary.Cell>
                  ))}
                  <MeasuredTable.Summary.Cell index={6} align="right">
                    <b>{formatCount(footer.totalQuantity)}</b>
                  </MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={7} align="right">
                    <b>{formatMoney(footer.totalAmount)}</b>
                  </MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={8} colSpan={3} />
                </MeasuredTable.Summary.Row>
              </MeasuredTable.Summary>
            ) : null
          }
        />
      </div>

      <div className="seasonal-card-stats-panels">
        <section className="seasonal-card-stats-card seasonal-card-stats-panel" aria-labelledby={`${idPrefix}-by-price`}>
          <h3 id={`${idPrefix}-by-price`}>{t('seasonalCardStats.byPrice.title')}</h3>
          {summary ? (
            <ul className="seasonal-card-stats-bars">
              {priceBars.map((bar) => (
                <li key={bar.priceOption}>
                  <span className="seasonal-card-stats-bar-label">{priceLabel(bar.priceOption)}</span>
                  <span className="seasonal-card-stats-bar-track" aria-hidden="true">
                    <span style={{ width: `${bar.widthPercent}%` }} />
                  </span>
                  <span className="seasonal-card-stats-bar-value">
                    {t('seasonalCardStats.byPrice.quantity', { count: formatCount(bar.quantity) })}
                  </span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="seasonal-card-stats-muted">{EMPTY_VALUE}</p>
          )}
        </section>

        <section className="seasonal-card-stats-card seasonal-card-stats-panel" aria-labelledby={`${idPrefix}-by-supplier`}>
          <h3 id={`${idPrefix}-by-supplier`}>{t('seasonalCardStats.bySupplier.title')}</h3>
          {summary && summary.supplierTotals.length ? (
            <ul className="seasonal-card-stats-suppliers">
              {summary.supplierTotals.map((supplier) => (
                <li key={supplier.localSupplierCode ?? '__unassigned__'}>
                  <div>
                    <span>{supplier.supplierName || supplier.localSupplierCode || t('seasonalCardStats.unassignedSupplier')}</span>
                    <small>{t('seasonalCardStats.bySupplier.stores', { count: formatCount(supplier.storeCount) })}</small>
                  </div>
                  <div className="seasonal-card-stats-supplier-figures">
                    <span>{t('seasonalCardStats.byPrice.quantity', { count: formatCount(supplier.quantity) })}</span>
                    <small>{formatMoney(supplier.amount)}</small>
                  </div>
                </li>
              ))}
            </ul>
          ) : (
            <p className="seasonal-card-stats-muted">{summary ? t('seasonalCardStats.bySupplier.empty') : EMPTY_VALUE}</p>
          )}
        </section>

        <section className="seasonal-card-stats-card seasonal-card-stats-panel" aria-labelledby={`${idPrefix}-unfilled`}>
          <div className="seasonal-card-stats-panel-head">
            <h3 id={`${idPrefix}-unfilled`}>{t('seasonalCardStats.unfilled.title')}</h3>
            <Button size="small" icon={<CopyOutlined />} disabled={!unfilledStores.length} onClick={copyUnfilled}>
              {t('seasonalCardStats.unfilled.copy')}
            </Button>
          </div>
          {summary && unfilledStores.length ? (
            <ul className="seasonal-card-stats-chips">
              {unfilledStores.map((store) => (
                <li key={store.storeCode}>
                  <button type="button" onClick={() => openDetail(store.storeCode)}>
                    {store.storeCode} {store.storeName}
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <p className="seasonal-card-stats-muted">{summary ? t('seasonalCardStats.unfilled.none') : EMPTY_VALUE}</p>
          )}
          <small className="seasonal-card-stats-muted">
            {t('seasonalCardStats.unfilled.hint', { year: filters.seasonYear, holiday: holidayLabel })}
          </small>
        </section>
      </div>

      <div className="seasonal-card-stats-footnote" role="note">
        <strong>{t('seasonalCardStats.notes.title')}</strong>
        <ul>
          <li>{t('seasonalCardStats.notes.latest')}</li>
          <li>
            {summary && summary.excludedStores.length
              ? t('seasonalCardStats.notes.excluded', {
                  stores: summary.excludedStores
                    .map((store) => [store.storeCode, store.storeName].filter(Boolean).join(' '))
                    .join(supplierSeparator),
                })
              : t('seasonalCardStats.notes.excludedNone')}
          </li>
          <li>{t('seasonalCardStats.notes.filter')}</li>
          <li>{t('seasonalCardStats.notes.amount')}</li>
          <li>{t('seasonalCardStats.notes.time')}</li>
        </ul>
      </div>

      <StoreDetailDrawer
        // 页面被缓存（切到别的标签页）时抽屉必须收起，否则它挂在 body 上会盖住别的页面。
        open={Boolean(selectedStore) && active}
        storeCode={selectedStore}
        preview={previewRow}
        detail={detail}
        loading={detailLoading}
        errorKind={detailError}
        seasonYear={filters.seasonYear}
        holidayLabel={holidayLabel}
        onClose={closeDetail}
        onRetry={() => setDetailTick((tick) => tick + 1)}
      />
    </PageContainer>
  )
}
