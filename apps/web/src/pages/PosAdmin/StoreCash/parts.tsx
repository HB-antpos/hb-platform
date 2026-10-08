import { DownloadOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Select, Tooltip } from 'antd'
import dayjs, { type Dayjs } from 'dayjs'
import type { ReactNode } from 'react'
import type { CashStoreOption } from '../../../types/storeCash'
import {
  formatAud,
  formatSignedAud,
  isDateDisabled,
  isNegativeAmount,
  MAX_RANGE_DAYS,
  type CashRegisterFilter,
} from './logic'

export type Tr = (key: string, params?: Record<string, string | number>) => string

const DATE_FORMAT = 'YYYY-MM-DD'

function classNames(...names: (string | false | null | undefined)[]) {
  return names.filter(Boolean).join(' ')
}

/** 金额单元格：千分位、澳元符号、等宽数字；负数（短款、负余额）用醒目色；null 显示「—」。 */
export function Amount({ value, signed = false, strong = false, muted = false }: {
  value: number | null | undefined
  signed?: boolean
  strong?: boolean
  muted?: boolean
}) {
  const negative = isNegativeAmount(value)
  return (
    <span
      className={classNames(
        'store-cash-amount',
        negative && 'store-cash-negative',
        strong && 'store-cash-strong',
        muted && 'store-cash-muted',
        value === null || value === undefined ? 'store-cash-empty' : null,
      )}
    >
      {signed ? formatSignedAud(value) : formatAud(value)}
    </span>
  )
}

export type KpiTone = 'default' | 'warning' | 'danger'

/** 总览顶部的指标卡：标签 + 大号数值 + 一行说明。 */
export function KpiTile({ label, value, sub, tone = 'default', loading = false }: {
  label: string
  value: ReactNode
  sub?: ReactNode
  tone?: KpiTone
  loading?: boolean
}) {
  return (
    <div className={classNames('store-cash-kpi', tone !== 'default' && `store-cash-kpi-${tone}`)}>
      <span className="store-cash-kpi-label">{label}</span>
      <span className="store-cash-kpi-value">
        {loading ? <span className="store-cash-skeleton" aria-hidden="true" /> : value}
      </span>
      {sub ? <span className="store-cash-kpi-sub">{sub}</span> : null}
    </div>
  )
}

/**
 * 日期区间：晚于门店今天的日期不可选；选了一端后，超出 93 天窗口的另一端不可选（与后端上限一致）。
 * 不允许清空，避免出现只剩一端的半个区间。
 */
export function RangeFilter({ from, to, today, disabled, tr, onChange }: {
  from: string
  to: string
  today: string
  disabled?: boolean
  tr: Tr
  onChange: (range: { from: string; to: string }) => void
}) {
  return (
    <label className="store-cash-field">
      <span className="store-cash-field-label">{tr('filters.range')}</span>
      <DatePicker.RangePicker
        className="store-cash-range"
        value={[dayjs(from), dayjs(to)]}
        allowClear={false}
        disabled={disabled}
        format={DATE_FORMAT}
        aria-label={tr('filters.range')}
        disabledDate={(current: Dayjs, info?: { from?: Dayjs }) =>
          isDateDisabled(current.format(DATE_FORMAT), today, info?.from ? info.from.format(DATE_FORMAT) : null)}
        onChange={(values) => {
          const [start, end] = values ?? []
          if (start && end) onChange({ from: start.format(DATE_FORMAT), to: end.format(DATE_FORMAT) })
        }}
      />
      <span className="store-cash-sr-only">{tr('filters.rangeHint', { days: MAX_RANGE_DAYS })}</span>
    </label>
  )
}

export function storeOptionLabel(store: Pick<CashStoreOption, 'storeCode' | 'storeName'>): string {
  return store.storeName && store.storeName !== store.storeCode ? `${store.storeName} (${store.storeCode})` : store.storeCode
}

function matchStore(input: string, option?: { label?: unknown; value?: unknown }) {
  const keyword = input.trim().toLowerCase()
  return `${String(option?.label ?? '')} ${String(option?.value ?? '')}`.toLowerCase().includes(keyword)
}

/** 单店选择（按日明细 / 存款 / 支出）。 */
export function StoreSelect({ stores, value, disabled, tr, onChange }: {
  stores: readonly CashStoreOption[]
  value: string | null
  disabled?: boolean
  tr: Tr
  onChange: (storeCode: string) => void
}) {
  return (
    <label className="store-cash-field">
      <span className="store-cash-field-label">{tr('filters.store')}</span>
      <Select
        className="store-cash-store-select"
        value={value ?? undefined}
        disabled={disabled}
        showSearch
        aria-label={tr('filters.store')}
        filterOption={matchStore}
        options={stores.map((store) => ({ value: store.storeCode, label: storeOptionLabel(store) }))}
        onChange={onChange}
      />
    </label>
  )
}

/** 收银系统筛选（所有页签共用）：缺省只看启用收银系统的分店。 */
export function RegisterSelect({ value, tr, onChange }: {
  value: CashRegisterFilter | undefined
  tr: Tr
  onChange: (value: CashRegisterFilter) => void
}) {
  return (
    <label className="store-cash-field">
      <span className="store-cash-field-label">{tr('filters.register')}</span>
      <Select
        className="store-cash-small-select"
        value={value ?? 'on'}
        aria-label={tr('filters.register')}
        // 文案键写成字面量，契约测试靠静态扫描核对键名。
        options={[
          { value: 'on', label: tr('filters.registerOn') },
          { value: 'off', label: tr('filters.registerOff') },
          { value: 'all', label: tr('filters.registerAll') },
        ] satisfies { value: CashRegisterFilter; label: string }[]}
        onChange={onChange}
      />
    </label>
  )
}

/** 刷新按钮：重新取当前条件下的数据。 */
export function RefreshButton({ loading, tr, onClick }: { loading: boolean; tr: Tr; onClick: () => void }) {
  return (
    <Tooltip title={tr('actions.refresh')}>
      <Button icon={<ReloadOutlined />} aria-label={tr('actions.refresh')} loading={loading} onClick={onClick} />
    </Tooltip>
  )
}

export interface ExportProgressState {
  fetched: number
  total: number
}

/** 导出按钮：分页取数时显示进度并禁用，避免重复点击。 */
export function ExportButton({ running, progress, disabled, tr, onClick }: {
  running: boolean
  progress: ExportProgressState | null
  disabled?: boolean
  tr: Tr
  onClick: () => void
}) {
  const label = running
    ? progress && progress.total > 0
      ? tr('export.progress', { done: progress.fetched.toLocaleString('en-US'), total: progress.total.toLocaleString('en-US') })
      : tr('export.preparing')
    : tr('export.button')
  return (
    <Button icon={<DownloadOutlined />} loading={running} disabled={disabled && !running} onClick={onClick} aria-busy={running || undefined}>
      {label}
    </Button>
  )
}

/** 加载失败提示：带重试按钮。 */
export function LoadErrorAlert({ message, tr, onRetry }: { message: string; tr: Tr; onRetry: () => void }) {
  return (
    <Alert
      type="error"
      showIcon
      message={message}
      action={<Button size="small" onClick={onRetry}>{tr('actions.retry')}</Button>}
    />
  )
}
