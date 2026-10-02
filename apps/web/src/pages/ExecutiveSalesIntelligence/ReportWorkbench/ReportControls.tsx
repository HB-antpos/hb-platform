import { Button, DatePicker, Segmented, Select, Switch, Tooltip } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'
import { MAX_REPORT_DAYS, quickDateSelection, reportPeriod, validPeriod, type DateSelection, type QuickRange } from './logic'
import styles from './report.module.css'

export function useReportText() {
  const { i18n } = useTranslation()
  const english = i18n.language.toLowerCase().startsWith('en')
  return (zh: string, en: string) => english ? en : zh
}

export function ReportControls({ value, onChange, onRefresh, loading, compact = false }: {
  value: DateSelection; onChange: (value: DateSelection) => void; onRefresh: () => void; loading: boolean
  /** 紧凑模式：快捷日期、同比方式各收成一个下拉，刷新只留图标，整组控件能和页签放进同一行。 */
  compact?: boolean
}) {
  const text = useReportText()
  const period = reportPeriod(value)
  const ranges = [
    ['today', text('今天', 'Today')], ['yesterday', text('昨天', 'Yesterday')],
    ['thisWeek', text('本周', 'This week')], ['lastWeek', text('上周', 'Last week')],
    ['thisMonth', text('本月', 'This month')], ['lastMonth', text('上月', 'Last month')],
  ]
  const quickChange = (key: Exclude<QuickRange, 'custom'>) => onChange({ ...quickDateSelection(key), compare: value.compare, compareMode: value.compareMode })
  if (compact) {
    return <div className={styles.controls}>
      <DatePicker.RangePicker size="small" allowClear={false} value={[dayjs(value.startDate), dayjs(value.endDate)]}
        disabledDate={(date, info) => date.isAfter(dayjs(), 'day') || Boolean(info.from && Math.abs(date.diff(info.from, 'day')) >= MAX_REPORT_DAYS)}
        onChange={range => {
          if (!range?.[0] || !range[1]) return
          const startDate = range[0].format('YYYY-MM-DD'), endDate = range[1].format('YYYY-MM-DD')
          if (validPeriod(startDate, endDate)) onChange({ ...value, startDate, endDate, quick: 'custom' })
        }}
        aria-label={text('日期范围，最多两年', 'Date range, up to two years')} />
      <Select size="small" popupMatchSelectWidth={false} aria-label={text('快捷日期', 'Quick range')} placeholder={text('快捷', 'Quick')}
        value={value.quick === 'custom' ? undefined : value.quick} options={ranges.map(([key, label]) => ({ value: key, label }))}
        onChange={key => quickChange(key as Exclude<QuickRange, 'custom'>)} />
      {/* 「不对比」与两种同比方式合成一个下拉，等价于原来的开关 + 同比方式 */}
      <Select size="small" popupMatchSelectWidth={false} aria-label={text('同比方式', 'Comparison mode')}
        value={value.compare ? value.compareMode : 'none'}
        options={[{ value: 'ByWeek', label: text('按周同比', 'Same ISO week') }, { value: 'ByDate', label: text('按日期同比', 'Same date') }, { value: 'none', label: text('不对比', 'No comparison') }]}
        onChange={mode => onChange(mode === 'none' ? { ...value, compare: false } : { ...value, compare: true, compareMode: mode as DateSelection['compareMode'] })} />
      <Tooltip title={text('刷新数据', 'Refresh')}>
        <Button size="small" icon={<ReloadOutlined />} loading={loading} onClick={onRefresh} aria-label={text('刷新数据', 'Refresh')} />
      </Tooltip>
    </div>
  }
  return <div className={styles.controls}>
    <DatePicker.RangePicker allowClear={false} value={[dayjs(value.startDate), dayjs(value.endDate)]}
      disabledDate={(date, info) => date.isAfter(dayjs(), 'day') || Boolean(info.from && Math.abs(date.diff(info.from, 'day')) >= MAX_REPORT_DAYS)}
      onChange={range => {
        if (!range?.[0] || !range[1]) return
        const startDate = range[0].format('YYYY-MM-DD'), endDate = range[1].format('YYYY-MM-DD')
        if (validPeriod(startDate, endDate)) onChange({ ...value, startDate, endDate, quick: 'custom' })
      }}
      aria-label={text('日期范围，最多两年', 'Date range, up to two years')} />
    <Segmented value={value.quick} options={ranges.map(([key, label]) => ({ value: key, label }))}
      onChange={key => quickChange(key as Exclude<QuickRange, 'custom'>)} />
    <div className={styles.compareControls}>
      <Select aria-label={text('同比方式', 'Comparison mode')} value={value.compareMode} disabled={!value.compare}
        options={[{ value: 'ByWeek', label: text('按周同比', 'Same ISO week') }, { value: 'ByDate', label: text('按日期同比', 'Same date') }]}
        onChange={compareMode => onChange({ ...value, compareMode })} />
      <label><Switch size="small" checked={value.compare} onChange={compare => onChange({ ...value, compare })} /> {text('自动对比', 'Compare')}</label>
      <Button icon={<ReloadOutlined />} loading={loading} onClick={onRefresh}>{text('刷新数据', 'Refresh')}</Button>
    </div>
    {value.compare && <small className={styles.compareRange}>{text('同期', 'Previous')} {period.compareStartDate} — {period.compareEndDate}</small>}
  </div>
}

export function MetricPair({ current, previous, format = 'money', compare = true, revenue, compareRevenue, costMetric = false }: {
  current: number | null | undefined; previous?: number | null; format?: 'money' | 'integer' | 'rate'; compare?: boolean; revenue?: number; compareRevenue?: number | null; costMetric?: boolean
}) {
  const text = useReportText()
  const display = (value: number | null | undefined) => {
    if (value == null) return '—'
    if (format === 'rate') return new Intl.NumberFormat('en-AU', { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value)
    return new Intl.NumberFormat('en-AU', format === 'integer' ? { maximumFractionDigits: 0 } : { style: 'currency', currency: 'AUD', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
  }
  const pending = (value: number | null | undefined, amount: number | null | undefined) => costMetric && value == null && amount != null && amount !== 0
  return <span className={styles.pair} title={pending(current, revenue) ? text('成本待补全', 'Cost pending') : undefined}>
    <strong>{pending(current, revenue) && format === 'rate' ? <small className={styles.costPending}>{text('成本待补全', 'Cost pending')}</small> : display(current)}</strong>
    <small title={pending(previous, compareRevenue) ? text('同期成本待补全', 'Previous cost pending') : undefined}>{compare ? pending(previous, compareRevenue) && format === 'rate' ? text('成本待补全', 'Cost pending') : display(previous) : '—'}</small>
  </span>
}
