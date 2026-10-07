import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { AutoComplete, Button, DatePicker, Input, Pagination, Select, Tabs, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getDailyCloseDetail, getDailyCloses } from '../../../services/dailyCloseService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  DailyCloseClientKind,
  DailyCloseDetail,
  DailyCloseListItem,
  DailyCloseListResult,
  DailyCloseStatusFilter,
} from '../../../types/dailyClose'
import { buildStoreOptionsFromUserStores } from '../../../utils/managedStoreScope'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'

import DailyCloseDrawer from './DailyCloseDrawer'
import { BackfillChip, DifferenceTag, SaveSequenceChip, useSourceLabel } from './DailyCloseTags'
import {
  CLIENT_KINDS,
  DATE_PRESET_KEYS,
  DEFAULT_PAGE_SIZE,
  EMPTY_VALUE,
  PAGE_SIZE_OPTIONS,
  STATUS_TABS,
  buildListQuery,
  buildTotalsView,
  classifyDailyCloseError,
  createAbortableRequestGuard,
  createDefaultFilters,
  formatCount,
  formatInStoreTime,
  formatMoney,
  formatSignedMoney,
  getPageRange,
  getSaveSequenceBadge,
  isAbortError,
  isBackfilled,
  isBusinessDateSelectable,
  parseDetailIdParam,
  resolveDatePreset,
  shouldShowTotals,
  validateBusinessDateRange,
  withDetailIdParam,
  type DailyCloseErrorKind,
  type DailyCloseFilters,
  type DatePresetKey,
  type RangeValidation,
} from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'
import './dailyCloses.css'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）；全局语言包只有菜单标题一个键。
registerPageMessages({ zh, en })

const { RangePicker } = DatePicker

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
const TAB_LABEL_KEYS: Record<DailyCloseStatusFilter, string> = {
  all: 'dailyCloses.status.all',
  short: 'dailyCloses.status.short',
  over: 'dailyCloses.status.over',
  even: 'dailyCloses.status.even',
  none: 'dailyCloses.status.none',
}

const PRESET_LABEL_KEYS: Record<DatePresetKey, string> = {
  today: 'dailyCloses.presets.today',
  yesterday: 'dailyCloses.presets.yesterday',
  last7: 'dailyCloses.presets.last7',
  last30: 'dailyCloses.presets.last30',
  thisMonth: 'dailyCloses.presets.thisMonth',
  lastMonth: 'dailyCloses.presets.lastMonth',
}

const VALIDATION_KEYS: Record<Exclude<RangeValidation, 'ok'>, string> = {
  required: 'dailyCloses.validation.rangeRequired',
  reversed: 'dailyCloses.validation.rangeReversed',
  tooLong: 'dailyCloses.validation.rangeTooLong',
}

/** 各列宽度之和：容器比它窄时表格在卡片内横向滚动，「操作」列固定在右侧。 */
const TABLE_MIN_WIDTH = 1082

/** 已生效的查询条件：筛选、状态页签、分页；任何一项变化都会重新请求。 */
interface AppliedQuery {
  filters: DailyCloseFilters
  status: DailyCloseStatusFilter
  page: number
  pageSize: number
}

interface SeenDevice {
  storeCode: string
  deviceCode: string
}

export default function DailyClosesPage() {
  const { t } = useTranslation()
  const idPrefix = useId()
  const sourceLabel = useSourceLabel()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)
  // keepAlive：页面被缓存（切到别的标签页）时 active 为 false，此时地址栏属于别的页面，不能再按它改本页状态。
  const { active } = useKeepAliveContext()
  const [searchParams, setSearchParams] = useSearchParams()

  // 分店范围由后端收口；前端选项用账号「全部关联分店」口径（不是只含主分店的可管理分店），管理员为全部启用分店。
  const managedStoreCodes = access.managedStoreCodes?.()
  const managedStoreKey = managedStoreCodes === null || managedStoreCodes === undefined ? null : managedStoreCodes.join(',')
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])

  const [draft, setDraft] = useState<DailyCloseFilters>(() => createDefaultFilters(formatSydneyIsoDate()))
  const [applied, setApplied] = useState<AppliedQuery>(() => ({
    filters: createDefaultFilters(formatSydneyIsoDate()),
    status: 'all',
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
  }))
  const [result, setResult] = useState<DailyCloseListResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [listError, setListError] = useState<DailyCloseErrorKind | null>(null)
  const [reloadTick, setReloadTick] = useState(0)
  const [seenDevices, setSeenDevices] = useState<SeenDevice[]>([])
  const [listGuard] = useState(createAbortableRequestGuard)

  // 抽屉：selectedId 是页面内状态，地址栏 ?id= 与它同步（激活期间以地址栏为准）。
  const urlId = parseDetailIdParam(searchParams.get('id'))
  const [selectedId, setSelectedId] = useState<string | null>(urlId)
  const selectedIdRef = useRef(selectedId)
  selectedIdRef.current = selectedId
  const wasActiveRef = useRef(active)
  const [detail, setDetail] = useState<DailyCloseDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<DailyCloseErrorKind | null>(null)
  const [detailTick, setDetailTick] = useState(0)
  const [detailGuard] = useState(createAbortableRequestGuard)

  // ───────────── 分店选项 ─────────────
  useEffect(() => {
    if (managedStoreKey !== null) {
      setStoreOptions(buildStoreOptionsFromUserStores(currentUser?.stores))
      return
    }
    let disposed = false
    void getActiveStores()
      .then((stores) => {
        if (!disposed) setStoreOptions(stores)
      })
      .catch((error) => {
        console.error(error)
        if (!disposed) message.error(t('dailyCloses.errors.loadStoresFailed'))
      })
    return () => {
      disposed = true
    }
    // t 随语言变化，不应因此重新请求分店。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentUser?.stores, managedStoreKey])

  // ───────────── 列表请求 ─────────────
  const loadList = useCallback(
    async (query: AppliedQuery) => {
      const { requestId, signal } = listGuard.begin()
      setLoading(true)
      setListError(null)
      try {
        const data = await getDailyCloses(
          buildListQuery(query.filters, query.status, query.page, query.pageSize),
          signal,
        )
        if (!listGuard.isLatest(requestId)) return
        // 页码超出范围（数据在翻页期间变少）：回到最后一页重新查。
        if (data.items.length === 0 && data.total > 0 && query.page > 1) {
          setApplied((current) => ({ ...current, page: Math.max(1, Math.ceil(data.total / data.pageSize)) }))
          return
        }
        setResult(data)
        // 记下出现过的终端，给「终端」输入框做候选（后端没有终端清单接口）。
        setSeenDevices((current) => {
          const known = new Set(current.map((entry) => `${entry.storeCode}|${entry.deviceCode}`))
          const added = data.items
            .filter((item) => item.deviceCode && !known.has(`${item.storeCode}|${item.deviceCode}`))
            .map((item) => ({ storeCode: item.storeCode, deviceCode: item.deviceCode }))
          return added.length ? [...current, ...added] : current
        })
      } catch (error) {
        if (isAbortError(error) || !listGuard.isLatest(requestId)) return
        console.error(error)
        // 失败时不保留上一次的结果：它属于另一组筛选条件，留着会被误读成当前结果。
        setResult(null)
        setListError(classifyDailyCloseError(error))
      } finally {
        if (listGuard.isLatest(requestId)) setLoading(false)
      }
    },
    [listGuard],
  )

  useEffect(() => {
    void loadList(applied)
  }, [applied, loadList, reloadTick])

  useEffect(
    () => () => {
      listGuard.abort()
      detailGuard.abort()
    },
    [listGuard, detailGuard],
  )

  // ───────────── 抽屉状态与地址栏同步 ─────────────
  const writeDetailId = useCallback(
    (id: string | null) => {
      setSearchParams((current) => withDetailIdParam(current, id), { replace: true })
    },
    [setSearchParams],
  )

  useEffect(() => {
    const justActivated = active && !wasActiveRef.current
    wasActiveRef.current = active
    if (!active) return
    if (justActivated && !urlId && selectedIdRef.current) {
      // 从别的标签页切回来：标签页导航不带查询参数，但抽屉之前是开着的，把它写回地址栏而不是悄悄关掉。
      writeDetailId(selectedIdRef.current)
      return
    }
    setSelectedId(urlId)
  }, [active, urlId, writeDetailId])

  const openDetail = useCallback(
    (guid: string) => {
      const id = guid.toLowerCase()
      setSelectedId(id)
      writeDetailId(id)
    },
    [writeDetailId],
  )

  const closeDetail = useCallback(() => {
    setSelectedId(null)
    writeDetailId(null)
  }, [writeDetailId])

  useEffect(() => {
    if (!selectedId) {
      detailGuard.abort()
      setDetail(null)
      setDetailError(null)
      setDetailLoading(false)
      return
    }
    const { requestId, signal } = detailGuard.begin()
    setDetailLoading(true)
    setDetailError(null)
    setDetail((current) => (current?.dailyCloseGuid.toLowerCase() === selectedId ? current : null))
    getDailyCloseDetail(selectedId, signal)
      .then((data) => {
        if (detailGuard.isLatest(requestId)) setDetail(data)
      })
      .catch((error) => {
        if (isAbortError(error) || !detailGuard.isLatest(requestId)) return
        console.error(error)
        setDetail(null)
        setDetailError(classifyDailyCloseError(error))
      })
      .finally(() => {
        if (detailGuard.isLatest(requestId)) setDetailLoading(false)
      })
  }, [selectedId, detailGuard, detailTick])

  // ───────────── 筛选操作 ─────────────
  const submitFilters = (event?: FormEvent) => {
    event?.preventDefault()
    const validation = validateBusinessDateRange(draft.businessDateFrom, draft.businessDateTo)
    if (validation !== 'ok') {
      message.warning(t(VALIDATION_KEYS[validation]))
      return
    }
    // 每次点「查询」都生成新对象：条件没变时也会重新请求，等同刷新。
    setApplied((current) => ({ ...current, filters: { ...draft }, page: 1 }))
  }

  const resetFilters = () => {
    const filters = createDefaultFilters(formatSydneyIsoDate())
    setDraft(filters)
    setApplied({ filters, status: 'all', page: 1, pageSize: DEFAULT_PAGE_SIZE })
  }

  const changeStatus = (key: string) => {
    setApplied((current) => ({ ...current, status: key as DailyCloseStatusFilter, page: 1 }))
  }

  // ───────────── 视图数据 ─────────────
  const counts = result?.counts ?? null
  const items = result?.items ?? []
  const total = result?.total ?? 0
  const todayIso = formatSydneyIsoDate()
  const pageRange = getPageRange(applied.page, applied.pageSize, total)
  const totalsView = result && shouldShowTotals(applied.status, result.counts) ? buildTotalsView(result.totals) : null
  const previewItem = selectedId
    ? items.find((item) => item.dailyCloseGuid.toLowerCase() === selectedId) ?? null
    : null

  const storeSelectOptions = useMemo(
    () =>
      storeOptions.map((option) => ({
        value: option.value,
        label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
        short: option.label || option.value,
      })),
    [storeOptions],
  )

  // 终端候选：见过的终端；已选分店时只列这些分店的终端。
  const deviceOptions = useMemo(() => {
    const scoped = draft.storeCodes.length
      ? seenDevices.filter((entry) => draft.storeCodes.includes(entry.storeCode))
      : seenDevices
    return Array.from(new Set(scoped.map((entry) => entry.deviceCode))).sort().map((value) => ({ value }))
  }, [draft.storeCodes, seenDevices])

  const datePresets = DATE_PRESET_KEYS.map((key) => {
    const [from, to] = resolveDatePreset(key, todayIso)
    return { label: t(PRESET_LABEL_KEYS[key]), value: [dayjs(from), dayjs(to)] as [dayjs.Dayjs, dayjs.Dayjs] }
  })

  const columns: ColumnsType<DailyCloseListItem> = [
    {
      key: 'businessDate',
      title: t('dailyCloses.columns.businessDate'),
      width: 160,
      render: (_, record) => {
        const sequence = getSaveSequenceBadge(record)
        return (
          <div className="daily-closes-date">
            <span className="daily-closes-date-main">
              <span>{record.businessDate || EMPTY_VALUE}</span>
              {sequence !== null ? <SaveSequenceChip sequence={sequence} /> : null}
              {isBackfilled(record) ? <BackfillChip /> : null}
            </span>
            {record.businessDateInferred ? <small>{t('dailyCloses.inferred')}</small> : null}
          </div>
        )
      },
    },
    {
      key: 'store',
      title: t('dailyCloses.columns.storeDevice'),
      width: 150,
      render: (_, record) => (
        <div className="daily-closes-two-line">
          <span>{record.storeName || record.storeCode}</span>
          <small>
            {[record.storeCode, record.deviceCode, sourceLabel(record.clientKind)].filter(Boolean).join(' · ')}
          </small>
        </div>
      ),
    },
    {
      key: 'cashier',
      title: t('dailyCloses.columns.cashier'),
      width: 96,
      ellipsis: true,
      render: (_, record) => record.cashierName || record.cashierId || EMPTY_VALUE,
    },
    {
      key: 'orderCount',
      title: t('dailyCloses.columns.orderCount'),
      width: 64,
      align: 'right',
      render: (_, record) => formatCount(record.orderCount),
    },
    {
      key: 'expected',
      title: t('dailyCloses.columns.expectedCash'),
      width: 96,
      align: 'right',
      render: (_, record) => formatMoney(record.expectedCashAmount),
    },
    {
      key: 'counted',
      title: t('dailyCloses.columns.countedCash'),
      width: 96,
      align: 'right',
      render: (_, record) => formatMoney(record.countedCashAmount),
    },
    {
      key: 'difference',
      title: t('dailyCloses.columns.difference'),
      width: 164,
      align: 'right',
      render: (_, record) => (
        <span className={`daily-closes-diff is-${record.differenceKind}`}>
          <b>{formatSignedMoney(record.cashDifference)}</b>
          <DifferenceTag kind={record.differenceKind} />
        </span>
      ),
    },
    {
      key: 'cardNet',
      title: t('dailyCloses.columns.cardNet'),
      width: 96,
      align: 'right',
      render: (_, record) => formatMoney(record.cardNetAmount),
    },
    {
      key: 'savedAt',
      title: t('dailyCloses.columns.savedAt'),
      width: 100,
      render: (_, record) => (
        <span
          className="daily-closes-nowrap"
          // 悬停给出含秒与时区的完整时间，核对「按哪个时区显示」时不用打开抽屉。
          title={`${formatInStoreTime(record.savedAtUtc, record.storeTimeZoneId, 'seconds')} (${
            record.storeTimeZoneId || 'Australia/Sydney'
          })`}
        >
          {formatInStoreTime(record.savedAtUtc, record.storeTimeZoneId, 'short')}
        </span>
      ),
    },
    {
      key: 'action',
      title: t('dailyCloses.columns.action'),
      width: 60,
      fixed: 'right',
      render: (_, record) => (
        <Button
          type="link"
          size="small"
          className="daily-closes-view"
          aria-label={t('dailyCloses.actions.viewLabel', {
            date: record.businessDate,
            store: record.storeName || record.storeCode,
          })}
          onClick={(event) => {
            // 整行也可点击，这里只保证键盘与读屏有明确的按钮入口，避免重复触发。
            event.stopPropagation()
            openDetail(record.dailyCloseGuid)
          }}
        >
          {t('dailyCloses.actions.view')}
        </Button>
      ),
    },
  ]

  const listErrorMessage: Record<DailyCloseErrorKind, string> = {
    forbidden: t('dailyCloses.errors.forbidden'),
    notFound: t('dailyCloses.errors.loadFailed'),
    invalidQuery: t('dailyCloses.errors.invalidQuery'),
    failed: t('dailyCloses.errors.loadFailed'),
  }

  const emptyContent = loading ? (
    <span aria-hidden="true" />
  ) : listError ? (
    <div className="daily-closes-state is-error" role="alert">
      <strong>{listErrorMessage[listError]}</strong>
      {listError === 'failed' || listError === 'notFound' ? (
        <Button onClick={() => setReloadTick((tick) => tick + 1)}>{t('dailyCloses.errors.retry')}</Button>
      ) : null}
    </div>
  ) : (
    <div className="daily-closes-state" role="status">
      <strong>{t('dailyCloses.empty.title')}</strong>
      <span>{t('dailyCloses.empty.hint')}</span>
    </div>
  )

  return (
    <PageContainer
      compact
      title={t('dailyCloses.title')}
      subtitle={counts ? t('dailyCloses.total', { count: formatCount(counts.all) }) : undefined}
      extra={
        <Tooltip title={t('dailyCloses.refresh')}>
          <Button
            icon={<ReloadOutlined />}
            aria-label={t('dailyCloses.refresh')}
            onClick={() => setReloadTick((tick) => tick + 1)}
          />
        </Tooltip>
      }
    >
      <form className="daily-closes-card daily-closes-filter" onSubmit={submitFilters}>
        <div className="daily-closes-field is-range">
          <label htmlFor={`${idPrefix}-range`}>{t('dailyCloses.filters.businessDate')}</label>
          <RangePicker
            id={{ start: `${idPrefix}-range`, end: `${idPrefix}-range-end` }}
            allowClear={false}
            format="YYYY-MM-DD"
            presets={datePresets}
            value={[dayjs(draft.businessDateFrom), dayjs(draft.businessDateTo)]}
            // 与后端同口径：区间最长 93 天，营业日不会晚于悉尼的今天。
            disabledDate={(current, info) =>
              !isBusinessDateSelectable(current.format('YYYY-MM-DD'), formatSydneyIsoDate(), info?.from?.format('YYYY-MM-DD'))
            }
            onChange={(dates) => {
              if (!dates?.[0] || !dates?.[1]) return
              setDraft((current) => ({
                ...current,
                businessDateFrom: dates[0]!.format('YYYY-MM-DD'),
                businessDateTo: dates[1]!.format('YYYY-MM-DD'),
              }))
            }}
          />
        </div>
        <div className="daily-closes-field is-store">
          <label htmlFor={`${idPrefix}-store`}>{t('dailyCloses.filters.store')}</label>
          <Select
            id={`${idPrefix}-store`}
            mode="multiple"
            allowClear
            showSearch
            maxTagCount="responsive"
            optionFilterProp="label"
            optionLabelProp="short"
            placeholder={t('dailyCloses.filters.storePlaceholder')}
            options={storeSelectOptions}
            value={draft.storeCodes}
            onChange={(codes: string[]) => setDraft((current) => ({ ...current, storeCodes: codes }))}
          />
        </div>
        <div className="daily-closes-field is-device">
          <label htmlFor={`${idPrefix}-device`}>{t('dailyCloses.filters.device')}</label>
          <AutoComplete
            id={`${idPrefix}-device`}
            allowClear
            options={deviceOptions}
            placeholder={t('dailyCloses.filters.devicePlaceholder')}
            value={draft.deviceCode}
            filterOption={(input, option) => String(option?.value ?? '').toLowerCase().includes(input.trim().toLowerCase())}
            onChange={(value) => setDraft((current) => ({ ...current, deviceCode: value ?? '' }))}
          />
        </div>
        <div className="daily-closes-field is-source">
          <label htmlFor={`${idPrefix}-source`}>{t('dailyCloses.filters.source')}</label>
          <Select
            id={`${idPrefix}-source`}
            allowClear
            placeholder={t('dailyCloses.filters.sourceAll')}
            options={CLIENT_KINDS.map((kind) => ({ value: kind, label: sourceLabel(kind) }))}
            value={draft.clientKind}
            onChange={(kind?: DailyCloseClientKind) => setDraft((current) => ({ ...current, clientKind: kind }))}
          />
        </div>
        <div className="daily-closes-field is-keyword">
          <label htmlFor={`${idPrefix}-keyword`}>{t('dailyCloses.filters.cashier')}</label>
          <Input
            id={`${idPrefix}-keyword`}
            allowClear
            placeholder={t('dailyCloses.filters.cashierPlaceholder')}
            value={draft.keyword}
            onChange={(event) => setDraft((current) => ({ ...current, keyword: event.target.value }))}
          />
        </div>
        <div className="daily-closes-filter-actions">
          <Button type="primary" htmlType="submit" icon={<SearchOutlined />}>
            {t('dailyCloses.filters.search')}
          </Button>
          <Button onClick={resetFilters}>{t('dailyCloses.filters.reset')}</Button>
        </div>
      </form>

      <div className="daily-closes-card daily-closes-results">
        <div className="daily-closes-tabsbar">
          <Tabs
            className="daily-closes-tabs"
            activeKey={applied.status}
            onChange={changeStatus}
            items={STATUS_TABS.map((key) => ({
              key,
              label: (
                <span className={`daily-closes-tab is-${key}`}>
                  {t(TAB_LABEL_KEYS[key])}
                  {counts ? <span className="daily-closes-count">{formatCount(counts[key])}</span> : null}
                </span>
              ),
            }))}
          />
          {totalsView ? (
            <div className="daily-closes-totals" aria-label={t('dailyCloses.totals.label')}>
              <span>
                {t('dailyCloses.totals.expected')} <b>{totalsView.expected}</b>
              </span>
              <span>
                {t('dailyCloses.totals.counted')} <b>{totalsView.counted}</b>
              </span>
              <span>
                {t('dailyCloses.totals.difference')}{' '}
                <b className={`is-${totalsView.differenceKind}`}>{totalsView.difference}</b>
              </span>
            </div>
          ) : applied.status === 'none' && counts ? (
            <div className="daily-closes-totals">{t('dailyCloses.totals.noneNote')}</div>
          ) : null}
        </div>

        <MeasuredTable<DailyCloseListItem>
          metricId="pos-admin.daily-closes.table-1"
          className="daily-closes-table"
          rowKey="dailyCloseGuid"
          size="middle"
          loading={loading}
          columns={columns}
          dataSource={items}
          pagination={false}
          scroll={{ x: TABLE_MIN_WIDTH }}
          locale={{ emptyText: emptyContent }}
          rowClassName={(record) =>
            record.dailyCloseGuid.toLowerCase() === selectedId && active ? 'is-selected' : ''
          }
          onRow={(record) => ({ onClick: () => openDetail(record.dailyCloseGuid) })}
        />

        <div className="daily-closes-pager">
          <span>
            {t('dailyCloses.pager', {
              from: formatCount(pageRange.from),
              to: formatCount(pageRange.to),
              total: formatCount(total),
            })}
          </span>
          <Pagination
            current={applied.page}
            pageSize={applied.pageSize}
            total={total}
            showSizeChanger
            responsive={false}
            pageSizeOptions={[...PAGE_SIZE_OPTIONS]}
            onChange={(page, pageSize) =>
              setApplied((current) => ({
                ...current,
                page: pageSize !== current.pageSize ? 1 : page,
                pageSize,
              }))
            }
          />
        </div>
      </div>

      <DailyCloseDrawer
        // 页面被缓存（切到别的标签页）时抽屉必须收起，否则它挂在 body 上会盖住别的页面。
        open={Boolean(selectedId) && active}
        guid={selectedId}
        preview={previewItem}
        detail={detail}
        loading={detailLoading}
        errorKind={detailError}
        onClose={closeDetail}
        onRetry={() => setDetailTick((tick) => tick + 1)}
      />
    </PageContainer>
  )
}
