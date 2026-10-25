import { InfoCircleFilled, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Segmented, Select, Tag, Tooltip, message } from 'antd'
import type { RangePickerProps } from 'antd/es/date-picker'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import type { TFunction } from 'i18next'
import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { POS_API_ENTRY_USAGE_MAX_RANGE_DAYS, getPosApiEntryUsage } from '../../../services/posApiEntryService'
import type { StoreOption } from '../../../types/deviceRegistration'
import type {
  PosApiEntryUsageBucket,
  PosApiEntryUsageCounts,
  PosApiEntryUsageEntry,
  PosApiEntryUsageEntryItem,
  PosApiEntryUsageGranularity,
  PosApiEntryUsageResult,
  PosApiEntryUsageStore,
} from '../../../types/posApiEntry'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'

import { EMPTY_VALUE } from './deviceCells'
import { MODE_LABEL_KEY } from './posApiEntryCells'
import {
  USAGE_GRANULARITIES,
  buildStackedChart,
  buildUsagePresets,
  formatBucketAxisLabel,
  formatBucketTitle,
  formatCount,
  formatErrorRate,
  formatShare,
  getDefaultUsageRange,
  validateUsageRange,
  type UsagePresetKey,
  type UsageRange,
  type UsageRangeProblem,
} from './posApiEntryUsageLogic'

const REFRESH_INTERVAL_MS = 60_000
const BUCKET_PAGE_SIZE = 20
const STORE_PAGE_SIZE = 20
const CHART_WIDTH = 960
const CHART_HEIGHT = 280

// 文案键写成字面量，页面文案契约测试才能扫描到并校验中英文都存在
const GRANULARITY_LABEL_KEY: Record<PosApiEntryUsageGranularity, string> = {
  day: 'posAdmin.devices.apiUsage.granularity.day',
  week: 'posAdmin.devices.apiUsage.granularity.week',
  month: 'posAdmin.devices.apiUsage.granularity.month',
}
const PRESET_LABEL_KEY: Record<UsagePresetKey, string> = {
  last7: 'posAdmin.devices.apiUsage.presets.last7',
  last30: 'posAdmin.devices.apiUsage.presets.last30',
  last90: 'posAdmin.devices.apiUsage.presets.last90',
  thisMonth: 'posAdmin.devices.apiUsage.presets.thisMonth',
  lastMonth: 'posAdmin.devices.apiUsage.presets.lastMonth',
  last12Months: 'posAdmin.devices.apiUsage.presets.last12Months',
}
const RANGE_PROBLEM_KEY: Record<UsageRangeProblem, string> = {
  invalid: 'posAdmin.devices.apiUsage.rangeProblem.invalid',
  reversed: 'posAdmin.devices.apiUsage.rangeProblem.reversed',
  tooLarge: 'posAdmin.devices.apiUsage.rangeProblem.tooLarge',
}
const UNKNOWN_LABEL_KEY = 'posAdmin.devices.apiUsage.entries.unknown'
const UNKNOWN_HINT_KEY = 'posAdmin.devices.apiUsage.entryHints.unknown'

const ENTRY_ORDER: readonly PosApiEntryUsageEntry[] = ['edge', 'aws', 'direct', 'unknown']

/** 把文案里的 <b>…</b> 渲染成加粗，不使用 dangerouslySetInnerHTML。 */
function renderRichText(text: string): ReactNode {
  return text.split(/<b>(.*?)<\/b>/g).map((part, index) =>
    index % 2 === 1 ? <strong key={index}>{part}</strong> : <Fragment key={index}>{part}</Fragment>,
  )
}

function findEntry(entries: PosApiEntryUsageEntryItem[], entry: PosApiEntryUsageEntry): PosApiEntryUsageEntryItem {
  return entries.find((item) => item.entry === entry) ?? { entry, requests: 0, status4xx: 0, status5xx: 0, share: 0 }
}

function getEntryLabel(entry: PosApiEntryUsageEntry, t: TFunction): string {
  return entry === 'unknown' ? t(UNKNOWN_LABEL_KEY) : t(MODE_LABEL_KEY[entry])
}

function entryDotClass(entry: PosApiEntryUsageEntry): string {
  return `dev-entry-dot dev-entry-dot-${entry === 'unknown' ? 'neutral' : entry}`
}

/** 线路列：请求数 + 占比（占该行合计）。 */
function buildEntryColumn<T extends { entries: PosApiEntryUsageEntryItem[] }>(
  entry: PosApiEntryUsageEntry,
  t: TFunction,
): ColumnsType<T>[number] {
  return {
    title: (
      <span className="dev-usage-th">
        <span className={entryDotClass(entry)} />
        {getEntryLabel(entry, t)}
      </span>
    ),
    key: `entry-${entry}`,
    align: 'right',
    width: 120,
    sorter: (a, b) => findEntry(a.entries, entry).requests - findEntry(b.entries, entry).requests,
    render: (_value, record) => {
      const item = findEntry(record.entries, entry)
      return (
        <div className="dev-usage-cell">
          <span className="dev-usage-num">{formatCount(item.requests)}</span>
          <span className="dev-mgmt-sub">{formatShare(item.share)}</span>
        </div>
      )
    },
  }
}

/** 合计 / 4xx / 5xx / 5xx 占比四列，时间明细表和分店明细表共用。 */
function buildTotalColumns<T extends { total: PosApiEntryUsageCounts }>(t: TFunction): ColumnsType<T> {
  return [
    {
      title: t('posAdmin.devices.apiUsage.columns.total'),
      key: 'total',
      align: 'right',
      width: 110,
      sorter: (a, b) => a.total.requests - b.total.requests,
      render: (_value, record) => <span className="dev-usage-num dev-mgmt-strong">{formatCount(record.total.requests)}</span>,
    },
    {
      title: t('posAdmin.devices.apiUsage.columns.status4xx'),
      key: 'status4xx',
      align: 'right',
      width: 90,
      sorter: (a, b) => a.total.status4xx - b.total.status4xx,
      render: (_value, record) => <span className="dev-usage-num">{formatCount(record.total.status4xx)}</span>,
    },
    {
      title: t('posAdmin.devices.apiUsage.columns.status5xx'),
      key: 'status5xx',
      align: 'right',
      width: 90,
      sorter: (a, b) => a.total.status5xx - b.total.status5xx,
      render: (_value, record) => (
        <span className={`dev-usage-num${record.total.status5xx > 0 ? ' dev-usage-num-warn' : ''}`}>
          {formatCount(record.total.status5xx)}
        </span>
      ),
    },
    {
      title: t('posAdmin.devices.apiUsage.columns.errorRate'),
      key: 'errorRate',
      align: 'right',
      width: 100,
      render: (_value, record) => <span className="dev-usage-num">{formatErrorRate(record.total)}</span>,
    },
  ]
}

interface UsagePanelProps {
  stores: StoreOption[]
}

export default function PosApiEntryUsagePanel({ stores }: UsagePanelProps) {
  const { t } = useTranslation()
  // 悉尼「今天」：整个面板的日期口径与后端一致，不用浏览器本地日期
  const today = useMemo(() => formatSydneyIsoDate(), [])
  const [granularity, setGranularity] = useState<PosApiEntryUsageGranularity>('day')
  const [range, setRange] = useState<UsageRange>(() => getDefaultUsageRange('day', today))
  // 用户手动选过日期后，切换粒度就不再覆盖他选的范围
  const rangeTouchedRef = useRef(false)
  const [storeCode, setStoreCode] = useState<string>()
  const [result, setResult] = useState<PosApiEntryUsageResult | null>(null)
  const [loading, setLoading] = useState(false)
  const requestGuardRef = useRef(createLatestRequestGuard())

  const problem = validateUsageRange(range.startDate, range.endDate)
  // 桶的标题 / 轴标签按「当前展示的数据」的粒度格式化：切换粒度后、新数据返回前，旧数据仍按旧粒度显示，不会错配
  const bucketGranularity = result?.granularity ?? granularity

  function load(showLoading = true) {
    if (problem) {
      return Promise.resolve()
    }
    return runLatestGuardedRequest(
      requestGuardRef.current,
      () => getPosApiEntryUsage({ ...range, granularity, storeCode }),
      {
        onStart: () => {
          if (showLoading) {
            setLoading(true)
          }
        },
        onSuccess: setResult,
        onError: (error) => {
          console.error(t('posAdmin.devices.apiUsage.messages.loadFailed'), error)
          // 后台自动刷新失败不弹窗，避免网络抖动时每分钟刷一条错误
          if (showLoading) {
            message.error(t('posAdmin.devices.apiUsage.messages.loadFailed'))
          }
        },
        onSettled: () => setLoading(false),
      },
    )
  }

  useEffect(() => {
    void load()
    const intervalId = window.setInterval(() => void load(false), REFRESH_INTERVAL_MS)
    return () => window.clearInterval(intervalId)
    // 筛选条件变化重新取数；load 内部读取的就是这些条件
  }, [granularity, range.startDate, range.endDate, storeCode])

  useEffect(() => () => requestGuardRef.current.invalidate(), [])

  function changeGranularity(next: PosApiEntryUsageGranularity) {
    setGranularity(next)
    if (!rangeTouchedRef.current) {
      setRange(getDefaultUsageRange(next, today))
    }
  }

  const presets = useMemo<RangePickerProps['presets']>(
    () =>
      buildUsagePresets(today).map((preset) => ({
        label: t(PRESET_LABEL_KEY[preset.key]),
        value: [dayjs(preset.startDate), dayjs(preset.endDate)],
      })),
    [today, t],
  )

  const storeOptions = useMemo(
    () => stores.map((store) => ({ value: store.storeCode, label: `${store.storeName} (${store.storeCode})` })),
    [stores],
  )

  const entryLabel = (entry: PosApiEntryUsageEntry) => getEntryLabel(entry, t)

  const chart = useMemo(
    () => (result ? buildStackedChart(result.buckets, { width: CHART_WIDTH, height: CHART_HEIGHT }) : null),
    [result],
  )

  const hasData = (result?.total.requests ?? 0) > 0

  // ───────────── 表格列 ─────────────

  const bucketColumns = useMemo<ColumnsType<PosApiEntryUsageBucket>>(
    () => [
      {
        title: t('posAdmin.devices.apiUsage.columns.period'),
        key: 'period',
        width: 200,
        fixed: 'left',
        render: (_value, bucket) => (
          <span className="dev-usage-period">
            <span className="dev-mgmt-strong dev-mgmt-mono">{formatBucketTitle(bucket, bucketGranularity)}</span>
            {bucket.partial ? (
              <Tooltip title={t('posAdmin.devices.apiUsage.partialHint')}>
                <Tag bordered={false} className="dev-usage-partial">
                  {t('posAdmin.devices.apiUsage.partialTag')}
                </Tag>
              </Tooltip>
            ) : null}
          </span>
        ),
      },
      ...ENTRY_ORDER.map((entry) => buildEntryColumn<PosApiEntryUsageBucket>(entry, t)),
      ...buildTotalColumns<PosApiEntryUsageBucket>(t),
    ],
    [bucketGranularity, t],
  )

  const storeColumns = useMemo<ColumnsType<PosApiEntryUsageStore>>(
    () => [
      {
        title: t('posAdmin.devices.apiUsage.columns.store'),
        key: 'store',
        width: 220,
        fixed: 'left',
        render: (_value, store) =>
          store.storeCode ? (
            <div className="dev-mgmt-two">
              <span className="dev-mgmt-strong dev-mgmt-ellipsis" title={store.storeName || store.storeCode}>
                {store.storeName || store.storeCode}
              </span>
              <span className="dev-mgmt-sub dev-mgmt-mono">{store.storeCode}</span>
            </div>
          ) : (
            <div className="dev-mgmt-two">
              <span className="dev-mgmt-strong">{t('posAdmin.devices.apiUsage.unassignedStore')}</span>
              <span className="dev-mgmt-sub">{t('posAdmin.devices.apiUsage.unassignedHint')}</span>
            </div>
          ),
      },
      ...ENTRY_ORDER.map((entry) => buildEntryColumn<PosApiEntryUsageStore>(entry, t)),
      ...buildTotalColumns<PosApiEntryUsageStore>(t),
    ],
    [t],
  )

  // 表格里最新的时间段排最前
  const bucketRows = useMemo(() => (result ? [...result.buckets].reverse() : []), [result])

  // ───────────── 渲染 ─────────────

  const summaryEntries = ENTRY_ORDER.map((entry) => findEntry(result?.entries ?? [], entry))

  return (
    <div className="dev-mgmt-card dev-entry-scope dev-usage">
      <div className="dev-entry-note" role="note">
        <InfoCircleFilled className="dev-entry-note-icon" aria-hidden="true" />
        <div>{renderRichText(t('posAdmin.devices.apiUsage.intro'))}</div>
      </div>

      <div className="dev-mgmt-toolbar">
        <Segmented<PosApiEntryUsageGranularity>
          aria-label={t('posAdmin.devices.apiUsage.toolbar.granularityAria')}
          value={granularity}
          onChange={changeGranularity}
          options={USAGE_GRANULARITIES.map((value) => ({ value, label: t(GRANULARITY_LABEL_KEY[value]) }))}
        />
        <DatePicker.RangePicker
          allowClear={false}
          aria-label={t('posAdmin.devices.apiUsage.toolbar.rangeAria')}
          value={[dayjs(range.startDate), dayjs(range.endDate)]}
          presets={presets}
          // 不能选悉尼「明天」及以后：那一天不可能有数据
          disabledDate={(current) => current.format('YYYY-MM-DD') > today}
          onChange={(values) => {
            if (values?.[0] && values[1]) {
              rangeTouchedRef.current = true
              setRange({ startDate: values[0].format('YYYY-MM-DD'), endDate: values[1].format('YYYY-MM-DD') })
            }
          }}
        />
        <Select<string>
          allowClear
          showSearch
          optionFilterProp="label"
          placeholder={t('posAdmin.devices.apiUsage.toolbar.storePlaceholder')}
          aria-label={t('posAdmin.devices.apiUsage.toolbar.storePlaceholder')}
          style={{ width: 240 }}
          value={storeCode}
          onChange={(value) => setStoreCode(value)}
          options={storeOptions}
        />
        <span className="dev-mgmt-toolbar-spacer" />
        <Tooltip title={t('common.refresh')}>
          <Button icon={<ReloadOutlined spin={loading} />} aria-label={t('common.refresh')} onClick={() => void load()} />
        </Tooltip>
      </div>

      {problem ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message={t(RANGE_PROBLEM_KEY[problem], { max: POS_API_ENTRY_USAGE_MAX_RANGE_DAYS })}
        />
      ) : null}

      <div className="dev-usage-stats">
        <div className="dev-entry-stat">
          <div className="dev-entry-stat-label">{t('posAdmin.devices.apiUsage.summary.total')}</div>
          <div className="dev-entry-stat-value">
            {formatCount(result?.total.requests ?? 0)}
            <small>{t('posAdmin.devices.apiUsage.summary.requestUnit')}</small>
          </div>
          <div className="dev-entry-stat-hint">
            {t('posAdmin.devices.apiUsage.summary.errors', {
              s4xx: formatCount(result?.total.status4xx ?? 0),
              s5xx: formatCount(result?.total.status5xx ?? 0),
            })}
          </div>
        </div>
        {summaryEntries.map((item) => (
          <div className="dev-entry-stat" key={item.entry}>
            <div className="dev-entry-stat-label">
              <span className={entryDotClass(item.entry)} />
              {item.entry === 'unknown' ? (
                <Tooltip title={t(UNKNOWN_HINT_KEY)}>
                  <span>{entryLabel(item.entry)}</span>
                </Tooltip>
              ) : (
                entryLabel(item.entry)
              )}
            </div>
            <div className="dev-entry-stat-value">
              {formatCount(item.requests)}
              <small>{formatShare(item.share)}</small>
            </div>
            <div className="dev-entry-stat-hint">
              {t('posAdmin.devices.apiUsage.summary.errors', {
                s4xx: formatCount(item.status4xx),
                s5xx: formatCount(item.status5xx),
              })}
            </div>
          </div>
        ))}
      </div>

      <section className="dev-usage-section" aria-busy={loading}>
        <h3 className="dev-usage-heading">{t('posAdmin.devices.apiUsage.chart.title')}</h3>
        {chart && hasData ? (
          <>
            <div className="dev-entry-legend dev-usage-legend">
              {ENTRY_ORDER.map((entry) => (
                <span key={entry} className="dev-entry-legend-item">
                  <span className={entryDotClass(entry)} />
                  {entryLabel(entry)}
                </span>
              ))}
            </div>
            <div className="dev-usage-chart-wrap">
            <svg
              className="dev-usage-chart"
              viewBox={`0 0 ${CHART_WIDTH} ${CHART_HEIGHT}`}
              role="img"
              aria-label={t('posAdmin.devices.apiUsage.chart.aria', {
                count: result?.buckets.length ?? 0,
                summary: summaryEntries
                  .map((item) => `${entryLabel(item.entry)} ${formatCount(item.requests)} (${formatShare(item.share)})`)
                  .join('，'),
              })}
            >
              {chart.ticks.map((tick) => (
                <g key={tick.value}>
                  <line
                    className="dev-usage-grid"
                    x1={chart.plot.left}
                    x2={chart.plot.left + chart.plot.width}
                    y1={tick.y}
                    y2={tick.y}
                  />
                  <text className="dev-usage-axis" x={chart.plot.left - 6} y={tick.y + 4} textAnchor="end">
                    {formatCount(tick.value)}
                  </text>
                </g>
              ))}
              {chart.bars.map((bar, index) => {
                const bucket = result!.buckets[index]
                const tooltip = [
                  formatBucketTitle(bucket, bucketGranularity) +
                    (bucket.partial ? ` ${t('posAdmin.devices.apiUsage.chart.tooltipPartial')}` : ''),
                  t('posAdmin.devices.apiUsage.chart.tooltipTotal', { count: formatCount(bar.total) }),
                  ...bucket.entries.map(
                    (item) => `${entryLabel(item.entry)}: ${formatCount(item.requests)} (${formatShare(item.share)})`,
                  ),
                ].join('\n')
                return (
                  <g key={bar.bucketStart}>
                    <title>{tooltip}</title>
                    {/* 透明底板：让整根柱子（含空白部分）都能悬停出提示 */}
                    <rect
                      x={bar.x}
                      y={chart.plot.top}
                      width={bar.width}
                      height={chart.plot.height}
                      fill="transparent"
                    />
                    {bar.segments.map((segment) => (
                      <rect
                        key={segment.entry}
                        className={`dev-usage-seg dev-usage-seg-${segment.entry}`}
                        x={bar.x}
                        y={segment.y}
                        width={bar.width}
                        height={segment.height}
                      />
                    ))}
                    {bar.showLabel ? (
                      <text
                        className="dev-usage-axis"
                        x={bar.x + bar.width / 2}
                        y={chart.plot.top + chart.plot.height + 18}
                        textAnchor="middle"
                      >
                        {formatBucketAxisLabel(bar.bucketStart, bucketGranularity)}
                      </text>
                    ) : null}
                  </g>
                )
              })}
            </svg>
            </div>
          </>
        ) : (
          <div className="dev-mgmt-sub dev-usage-empty">
            {loading && !result ? EMPTY_VALUE : t('posAdmin.devices.apiUsage.chart.empty')}
          </div>
        )}
      </section>

      <section className="dev-usage-section">
        <h3 className="dev-usage-heading">{t('posAdmin.devices.apiUsage.sections.buckets')}</h3>
        <MeasuredTable<PosApiEntryUsageBucket>
          metricId="pos-admin.device-registration.api-usage.buckets"
          className="dev-mgmt-table"
          rowKey="bucketStart"
          size="middle"
          loading={loading}
          columns={bucketColumns}
          dataSource={bucketRows}
          scroll={{ x: 1150 }}
          locale={{ emptyText: <div className="dev-mgmt-sub dev-usage-empty">{t('posAdmin.devices.apiUsage.empty')}</div> }}
          pagination={{ pageSize: BUCKET_PAGE_SIZE, hideOnSinglePage: true, showSizeChanger: false }}
        />
      </section>

      <section className="dev-usage-section">
        <h3 className="dev-usage-heading">{t('posAdmin.devices.apiUsage.sections.stores')}</h3>
        <MeasuredTable<PosApiEntryUsageStore>
          metricId="pos-admin.device-registration.api-usage.stores"
          className="dev-mgmt-table"
          rowKey={(store) => store.storeCode || '__unassigned__'}
          size="middle"
          loading={loading}
          columns={storeColumns}
          dataSource={result?.stores ?? []}
          scroll={{ x: 1170 }}
          locale={{ emptyText: <div className="dev-mgmt-sub dev-usage-empty">{t('posAdmin.devices.apiUsage.empty')}</div> }}
          pagination={{ pageSize: STORE_PAGE_SIZE, hideOnSinglePage: true, showSizeChanger: false }}
        />
      </section>

      {result ? (
        <div className="dev-entry-footer">
          {t('posAdmin.devices.apiUsage.footer', {
            timeZone: result.timeZone,
            start: result.startDate,
            end: result.endDate,
            buckets: result.buckets.length,
            stores: result.stores.length,
          })}
        </div>
      ) : null}
    </div>
  )
}
