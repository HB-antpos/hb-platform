import { CheckOutlined } from '@ant-design/icons'
import { Avatar, Button, DatePicker, Drawer, Empty, Input, InputNumber, Segmented, Space, Spin, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { createAttendancePayRate, getAttendancePayRateHistory } from '../../../services/attendancePayrollService'
import type { AttendancePayMode, AttendancePayRateDto, AttendancePayResultDto } from '../../../types/attendancePayroll'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import { calculatePay, findRatesBelowMinimum, formatHours, formatMoney, formatSignedMoney, type PayRates } from './payrollLogic'

interface RatesTabProps {
  rates: AttendancePayRateDto[]
  pay: AttendancePayResultDto | null
  loading: boolean
  storeCode?: string
  canEdit: boolean
  onSaved: () => void
}

interface RateDraft extends PayRates {
  payMode: AttendancePayMode
  fixedTransferHoursPerWeek?: number
  cashAllowancePerPeriod: number
  minimumRate?: number
  effectiveFrom: Dayjs
  remark: string
}

const TRANSFER_KEYS = ['transferWeekdayRate', 'transferSaturdayRate', 'transferSundayRate', 'transferPublicHolidayRate'] as const

const money = (value?: number) => (value === undefined || value === null ? '—' : value.toFixed(2))

function toDraft(rate: AttendancePayRateDto): RateDraft {
  return {
    payMode: rate.payMode,
    fixedTransferHoursPerWeek: rate.fixedTransferHoursPerWeek,
    transferWeekdayRate: rate.transferWeekdayRate,
    transferSaturdayRate: rate.transferSaturdayRate,
    transferSundayRate: rate.transferSundayRate,
    transferPublicHolidayRate: rate.transferPublicHolidayRate,
    cashRate: rate.cashRate,
    cashAllowancePerPeriod: rate.cashAllowancePerPeriod ?? 0,
    minimumRate: rate.minimumRate,
    // 新生效记录默认从今天起；历史不改
    effectiveFrom: dayjs(),
    remark: rate.remark ?? '',
  }
}

export default function RatesTab({ rates, pay, loading, storeCode, canEdit, onSaved }: RatesTabProps) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState<AttendancePayRateDto | null>(null)
  const [draft, setDraft] = useState<RateDraft | null>(null)
  const [history, setHistory] = useState<AttendancePayRateDto[]>([])
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!editing) return
    setDraft(toDraft(editing))
    setHistory([])
    let cancelled = false
    void getAttendancePayRateHistory(editing.userGuid)
      .then((items) => {
        if (!cancelled) setHistory(items)
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [editing])

  const update = (patch: Partial<RateDraft>) => setDraft((current) => (current ? { ...current, ...patch } : current))
  const belowMinimum = useMemo(
    () => new Set(draft ? findRatesBelowMinimum(draft) : []),
    [draft],
  )
  const payRow = pay?.rows.find((row) => row.userGuid === editing?.userGuid)

  /** 实时试算：本期应付工时 × 抽屉里正在编辑的时薪（前端口径，与后端一致，保存后以后端为准）。 */
  const trial = useMemo(() => {
    if (!draft || !payRow || !pay) return null
    return calculatePay({
      hours: {
        sunday: payRow.hours.sunday,
        saturday: payRow.hours.saturday,
        leave: payRow.hours.leave,
        publicHoliday: payRow.hours.publicHoliday,
        weekday: payRow.hours.weekday,
      },
      payMode: draft.payMode,
      fixedTransferHoursPerWeek: draft.fixedTransferHoursPerWeek,
      weeks: pay.weeks,
      rates: draft,
      allowance: draft.cashAllowancePerPeriod,
    })
  }, [draft, pay, payRow])

  const save = async () => {
    if (!editing || !draft) return
    const needsTransfer = draft.payMode !== 'Cash'
    const needsCash = draft.payMode !== 'Transfer'
    if (needsTransfer && TRANSFER_KEYS.some((key) => draft[key] === undefined || draft[key] === null)) {
      message.warning(t('attendancePayroll.rates.validation.transferRequired'))
      return
    }
    if (needsCash && (draft.cashRate === undefined || draft.cashRate === null)) {
      message.warning(t('attendancePayroll.rates.validation.cashRequired'))
      return
    }
    if (draft.payMode === 'Mixed' && !draft.fixedTransferHoursPerWeek) {
      message.warning(t('attendancePayroll.rates.validation.fixedRequired'))
      return
    }
    setSaving(true)
    try {
      await createAttendancePayRate({
        storeCode,
        userGuid: editing.userGuid,
        payMode: draft.payMode,
        fixedTransferHoursPerWeek: draft.payMode === 'Mixed' ? draft.fixedTransferHoursPerWeek : undefined,
        transferWeekdayRate: needsTransfer ? draft.transferWeekdayRate : undefined,
        transferSaturdayRate: needsTransfer ? draft.transferSaturdayRate : undefined,
        transferSundayRate: needsTransfer ? draft.transferSundayRate : undefined,
        transferPublicHolidayRate: needsTransfer ? draft.transferPublicHolidayRate : undefined,
        cashRate: needsCash ? draft.cashRate : undefined,
        cashAllowancePerPeriod: draft.cashAllowancePerPeriod ?? 0,
        minimumRate: draft.minimumRate,
        effectiveFrom: draft.effectiveFrom.format('YYYY-MM-DD'),
        remark: draft.remark.trim() || undefined,
      })
      message.success(t('attendancePayroll.rates.saved'))
      setEditing(null)
      onSaved()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.actionFailed'))
    } finally {
      setSaving(false)
    }
  }

  const birthdayRows = rates.filter((rate) => rate.birthdayReviewHint)

  const columns: ColumnsType<AttendancePayRateDto> = [
    {
      title: t('attendancePayroll.rates.columns.employee'),
      key: 'employee',
      width: 230,
      fixed: 'left',
      render: (_, rate) => (
        <div className="atp-person">
          <Avatar size={30} style={{ background: avatarColorOf(rate.userName), fontSize: 12 }}>{initialsOf(rate.userName)}</Avatar>
          <div>
            <div className="atp-person-name">{rate.userName}</div>
            <div className="atp-muted atp-small">
              {rate.employmentType ? t(`attendancePayroll.employmentType.${rate.employmentType}`) : ''}
              {rate.isManager ? ` · ${t('attendancePayroll.manager')}` : ''}
              {rate.birthday && rate.ageYears !== undefined && rate.ageYears < 18 ? (
                <>
                  {' '}<Tag className="atp-tag" color="magenta">🎂 {dayjs(rate.birthday).format('MM-DD')}</Tag>
                  <Tag className="atp-tag" color="gold">{t('attendancePayroll.ageYears', { age: rate.ageYears })}</Tag>
                </>
              ) : null}
            </div>
          </div>
        </div>
      ),
    },
    {
      title: t('attendancePayroll.rates.columns.payMode'),
      key: 'payMode',
      width: 110,
      render: (_, rate) => (
        <Tag className="atp-tag" color={rate.payMode === 'Cash' ? 'green' : rate.payMode === 'Mixed' ? 'purple' : 'blue'}>
          {t(`attendancePayroll.payMode.${rate.payMode}`)}
        </Tag>
      ),
    },
    {
      title: t('attendancePayroll.rates.columns.fixed'),
      key: 'fixed',
      width: 100,
      align: 'right',
      render: (_, rate) => (rate.payMode === 'Mixed' ? formatHours(rate.fixedTransferHoursPerWeek) : '—'),
    },
    {
      title: t('attendancePayroll.rates.columns.transfer'),
      key: 'transfer',
      width: 250,
      align: 'right',
      render: (_, rate) => {
        if (rate.payMode === 'Cash') return '—'
        const low = new Set(findRatesBelowMinimum(rate))
        return (
          <span>
            {TRANSFER_KEYS.map((key, index) => (
              <span key={key}>
                {index ? ' / ' : ''}
                <span className={low.has(key) ? 'atp-negative' : undefined}>{money(rate[key])}</span>
              </span>
            ))}
          </span>
        )
      },
    },
    {
      title: t('attendancePayroll.rates.columns.cash'),
      key: 'cash',
      width: 100,
      align: 'right',
      render: (_, rate) => {
        if (rate.payMode === 'Transfer') return '—'
        const low = findRatesBelowMinimum(rate).includes('cashRate')
        return <span className={low ? 'atp-negative' : undefined}>{money(rate.cashRate)}</span>
      },
    },
    {
      title: t('attendancePayroll.rates.columns.allowance'),
      key: 'allowance',
      width: 90,
      align: 'right',
      render: (_, rate) => (
        <span className={rate.cashAllowancePerPeriod < 0 ? 'atp-negative' : rate.cashAllowancePerPeriod > 0 ? 'atp-positive' : undefined}>
          {formatSignedMoney(rate.cashAllowancePerPeriod).replace('.00', '')}
        </span>
      ),
    },
    {
      title: t('attendancePayroll.rates.columns.minimum'),
      key: 'minimum',
      width: 120,
      align: 'right',
      render: (_, rate) => {
        if (rate.minimumRate === undefined || rate.minimumRate === null) {
          return rate.ageYears !== undefined && rate.ageYears < 18
            ? <span className="atp-dashed-box atp-warn-box">{t('attendancePayroll.rates.minorMissing', { age: rate.ageYears })}</span>
            : <span className="atp-muted">{t('attendancePayroll.rates.notFilled')}</span>
        }
        const low = findRatesBelowMinimum(rate).length > 0
        return (
          <span className={low ? 'atp-negative' : undefined}>
            {formatMoney(rate.minimumRate)} {low ? t('attendancePayroll.rates.belowMinimum') : <CheckOutlined />}
          </span>
        )
      },
    },
    {
      title: t('attendancePayroll.rates.columns.effective'),
      key: 'effective',
      width: 90,
      render: (_, rate) => (rate.effectiveFrom ? dayjs(rate.effectiveFrom).format('MM-DD') : t('attendancePayroll.rates.notFilled')),
    },
  ]

  const rateInput = (key: keyof PayRates, placeholderKey?: string) => (
    <InputNumber
      className="atp-rate-input"
      min={0}
      step={0.01}
      precision={2}
      controls={false}
      value={draft?.[key]}
      status={belowMinimum.has(key) ? 'error' : undefined}
      placeholder={placeholderKey ? t(placeholderKey) : undefined}
      disabled={!canEdit}
      onChange={(value) => update({ [key]: value ?? undefined } as Partial<RateDraft>)}
    />
  )

  return (
    <Spin spinning={loading}>
      <div className="atp-stack">
        {birthdayRows.map((rate) => (
          <div key={rate.userGuid} className="atp-alert atp-alert-birthday">
            <div className="atp-alert-text">
              <strong>
                {t('attendancePayroll.rates.birthdayHint', {
                  name: rate.userName,
                  date: rate.birthday ? dayjs(rate.birthday).format('MM-DD') : '',
                  age: rate.ageYears,
                  previous: (rate.ageYears ?? 1) - 1,
                })}
              </strong>
            </div>
          </div>
        ))}
        {rates.length ? (
          <div className="atp-rates-table">
            <MeasuredTable
              metricId="pos-admin.attendance-payroll.rates"
              rowKey="userGuid"
              size="middle"
              columns={columns}
              dataSource={rates}
              pagination={false}
              scroll={{ x: 1090 }}
              rowClassName={(rate) => (rate.userGuid === editing?.userGuid ? 'atp-row-active' : '')}
              onRow={(rate) => ({ onClick: () => setEditing(rate) })}
            />
            <div className="atp-legend-note atp-legend-left">{t('attendancePayroll.rates.footnote')}</div>
          </div>
        ) : (
          <Empty description={t('attendancePayroll.empty')} />
        )}
      </div>

      <Drawer
        open={Boolean(editing)}
        width={460}
        onClose={() => setEditing(null)}
        title={editing ? (
          <div className="atp-person">
            <Avatar size={36} style={{ background: avatarColorOf(editing.userName) }}>{initialsOf(editing.userName)}</Avatar>
            <div>
              <div className="atp-person-name">{editing.userName}</div>
              <div className="atp-muted atp-small">
                {editing.employmentType ? t(`attendancePayroll.employmentType.${editing.employmentType}`) : ''}{t('attendancePayroll.rates.fromProfile')}
              </div>
            </div>
          </div>
        ) : null}
        footer={canEdit ? (
          <div className="atp-drawer-foot">
            <Button onClick={() => setEditing(null)}>{t('attendancePayroll.rates.cancel')}</Button>
            <Button type="primary" loading={saving} onClick={() => void save()}>{t('attendancePayroll.rates.saveNew')}</Button>
          </div>
        ) : null}
      >
        {draft ? (
          <div className="atp-drawer-body">
            <div className="atp-field">
              <span className="atp-field-label">{t('attendancePayroll.rates.payMode')}</span>
              <Segmented
                block
                disabled={!canEdit}
                value={draft.payMode}
                options={(['Transfer', 'Cash', 'Mixed'] as const).map((mode) => ({ value: mode, label: t(`attendancePayroll.payMode.${mode}`) }))}
                onChange={(value) => update({ payMode: value as AttendancePayMode })}
              />
            </div>
            <div className="atp-field-row">
              {draft.payMode === 'Mixed' ? (
                <div className="atp-field">
                  <span className="atp-field-label">{t('attendancePayroll.rates.fixedHours')}</span>
                  <InputNumber
                    min={0}
                    max={80}
                    step={1}
                    value={draft.fixedTransferHoursPerWeek}
                    disabled={!canEdit}
                    addonAfter={t('attendancePayroll.rates.perWeek')}
                    onChange={(value) => update({ fixedTransferHoursPerWeek: value ?? undefined })}
                  />
                </div>
              ) : null}
              <div className="atp-field">
                <span className="atp-field-label">{t('attendancePayroll.rates.effectiveFrom')}</span>
                <DatePicker allowClear={false} value={draft.effectiveFrom} disabled={!canEdit} onChange={(value) => value && update({ effectiveFrom: value })} />
              </div>
            </div>
            <div className="atp-field">
              <span className="atp-field-label">{t('attendancePayroll.rates.ratesTitle')}</span>
              {draft.payMode !== 'Cash' ? (
                <div className="atp-rate-grid">
                  <span />
                  {(['weekday', 'saturday', 'sunday', 'publicHoliday'] as const).map((key) => (
                    <span key={key} className="atp-muted atp-small atp-center">{t(`attendancePayroll.tierLong.${key}`)}</span>
                  ))}
                  <span className="atp-rate-row-label">{t('attendancePayroll.pay.transfer')}</span>
                  {TRANSFER_KEYS.map((key) => <span key={key}>{rateInput(key)}</span>)}
                </div>
              ) : null}
              {draft.payMode !== 'Transfer' ? (
                <div className="atp-rate-cash">
                  <span className="atp-rate-row-label">{t('attendancePayroll.pay.cash')}</span>
                  {rateInput('cashRate', 'attendancePayroll.rates.cashSinglePrice')}
                </div>
              ) : null}
              <span className="atp-muted atp-small">{t('attendancePayroll.rates.leaveRateHint')}</span>
            </div>
            <div className="atp-field-row">
              <div className="atp-field">
                <span className="atp-field-label">{t('attendancePayroll.rates.allowance')}</span>
                <InputNumber
                  step={1}
                  precision={2}
                  value={draft.cashAllowancePerPeriod}
                  disabled={!canEdit}
                  addonAfter="$"
                  onChange={(value) => update({ cashAllowancePerPeriod: value ?? 0 })}
                />
              </div>
              <div className="atp-field">
                <span className="atp-field-label">{t('attendancePayroll.rates.minimum')}</span>
                <InputNumber
                  min={0}
                  step={0.01}
                  precision={2}
                  value={draft.minimumRate}
                  disabled={!canEdit}
                  addonAfter="$ / h"
                  onChange={(value) => update({ minimumRate: value ?? undefined })}
                />
              </div>
            </div>
            {belowMinimum.size ? <Typography.Text type="danger">{t('attendancePayroll.rates.belowMinimumHint')}</Typography.Text> : null}
            <div className="atp-field">
              <span className="atp-field-label">{t('attendancePayroll.rates.remark')}</span>
              <Input value={draft.remark} maxLength={200} disabled={!canEdit} onChange={(event) => update({ remark: event.target.value })} />
            </div>

            {trial && pay && payRow ? (
              <div className="atp-trial">
                <div>
                  {t('attendancePayroll.rates.trialTitle', {
                    from: dayjs(pay.periodStart).format('MM-DD'),
                    to: dayjs(pay.periodStart).add(pay.weeks * 7 - 1, 'day').format('MM-DD'),
                    hours: formatHours(payRow.hours.total, '0'),
                  })}
                </div>
                {trial.transfer.hours > 0 ? (
                  <div>
                    {t('attendancePayroll.rates.trialTransfer', { hours: formatHours(trial.transfer.hours, '0') })}
                    {(['sunday', 'saturday', 'leave', 'publicHoliday', 'weekday'] as const)
                      .filter((key) => trial.transfer.buckets[key] > 0)
                      .map((key) => `${t(`attendancePayroll.bucketShort.${key}`)} ${formatHours(trial.transfer.buckets[key])}`)
                      .join(' + ')}
                    {' → '}<strong>{formatMoney(trial.transfer.amount)}</strong>
                  </div>
                ) : null}
                {trial.cash.hours > 0 ? (
                  <div>
                    {t('attendancePayroll.rates.trialCash', { hours: formatHours(trial.cash.hours, '0'), rate: formatMoney(draft.cashRate) })}
                    {' → '}<strong>{formatMoney(trial.cash.amount)}</strong>
                  </div>
                ) : null}
                {trial.allowance ? (
                  <div>
                    {t('attendancePayroll.rates.trialAllowance')}{' '}
                    <span className={trial.allowance < 0 ? 'atp-negative' : 'atp-positive'}>{formatSignedMoney(trial.allowance)}</span>
                  </div>
                ) : null}
                <div><strong>{t('attendancePayroll.rates.trialTotal', { amount: formatMoney(trial.grossPay) })}</strong></div>
                {trial.missingRates.length ? <Typography.Text type="danger">{t('attendancePayroll.pay.rateMissing')}</Typography.Text> : null}
              </div>
            ) : null}

            <div className="atp-field">
              <span className="atp-field-label">{t('attendancePayroll.rates.history')}</span>
              {history.length ? (
                <ul className="atp-history">
                  {history.map((item) => (
                    <li key={item.rateGuid ?? item.effectiveFrom}>
                      <span>
                        {t('attendancePayroll.rates.historyItem', {
                          date: item.effectiveFrom,
                          mode: t(`attendancePayroll.payMode.${item.payMode}`),
                          weekday: money(item.payMode === 'Cash' ? item.cashRate : item.transferWeekdayRate),
                          cash: money(item.cashRate),
                        })}
                        {item.payMode === 'Mixed' && item.fixedTransferHoursPerWeek ? ` · ${formatHours(item.fixedTransferHoursPerWeek)} h` : ''}
                      </span>
                      <span className="atp-muted">{item.createdBy}</span>
                    </li>
                  ))}
                </ul>
              ) : (
                <span className="atp-muted atp-small">{t('attendancePayroll.rates.noHistory')}</span>
              )}
            </div>
            {!canEdit ? <Space><Tag>{t('attendancePayroll.rates.readOnly')}</Tag></Space> : null}
          </div>
        ) : null}
      </Drawer>
    </Spin>
  )
}
