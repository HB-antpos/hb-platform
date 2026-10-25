import { Avatar, Empty, Input, InputNumber, Spin, Tag, Tooltip, message } from 'antd'
import { useEffect, useMemo, useState, type ReactElement } from 'react'
import { useTranslation } from 'react-i18next'
import { saveAttendanceLeaveApproval, saveAttendancePayNote } from '../../../services/attendancePayrollService'
import type {
  AttendanceLeaveApprovalDto,
  AttendancePayBuckets,
  AttendancePayResultDto,
  AttendancePayRowDto,
} from '../../../types/attendancePayroll'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import { TRANSFER_FILL_ORDER, formatHours, formatMoney, formatSignedMoney, round2 } from './payrollLogic'

interface PayTabProps {
  data: AttendancePayResultDto | null
  loading: boolean
  weeks: 1 | 2
  canEdit: boolean
  onReload: () => void
}

const HOUR_KEYS = ['weekday', 'saturday', 'sunday', 'publicHoliday'] as const

/** 年假核定工时（虚线框）：失焦或回车保存，值没变不发请求。 */
function LeaveApprovalInput(props: {
  approval: AttendanceLeaveApprovalDto
  disabled: boolean
  onSave: (value: number) => Promise<void>
}) {
  const [value, setValue] = useState<number | null>(props.approval.approvedHours)
  useEffect(() => setValue(props.approval.approvedHours), [props.approval.approvedHours])
  const commit = () => {
    if (value === null || value === props.approval.approvedHours) return
    void props.onSave(value).catch(() => setValue(props.approval.approvedHours))
  }
  return (
    <InputNumber
      className="atp-dashed-input atp-approval-input"
      size="small"
      min={0}
      max={props.approval.requestedHours}
      step={0.5}
      controls={false}
      value={value}
      disabled={props.disabled}
      onChange={setValue}
      onBlur={commit}
      onPressEnter={commit}
    />
  )
}

function NoteInput(props: { value?: string; disabled: boolean; placeholder: string; onSave: (value: string) => Promise<void> }) {
  const [value, setValue] = useState(props.value ?? '')
  useEffect(() => setValue(props.value ?? ''), [props.value])
  const commit = () => {
    if (value.trim() === (props.value ?? '').trim()) return
    void props.onSave(value.trim()).catch(() => setValue(props.value ?? ''))
  }
  return (
    <Input.TextArea
      className="atp-dashed-input atp-note-input"
      autoSize={{ minRows: 1, maxRows: 3 }}
      maxLength={200}
      value={value}
      disabled={props.disabled}
      placeholder={props.placeholder}
      onChange={(event) => setValue(event.target.value)}
      onBlur={commit}
    />
  )
}

export default function PayTab({ data, loading, weeks, canEdit, onReload }: PayTabProps) {
  const { t } = useTranslation()
  const rows = data?.rows ?? []

  const totals = useMemo(() => {
    const sum = (pick: (row: AttendancePayRowDto) => number) => round2(rows.reduce((acc, row) => acc + pick(row), 0))
    return {
      weekday: sum((row) => row.hours.weekday),
      saturday: sum((row) => row.hours.saturday),
      sunday: sum((row) => row.hours.sunday),
      publicHoliday: sum((row) => row.hours.publicHoliday),
      leave: sum((row) => row.hours.leave),
      total: sum((row) => row.hours.total),
      transferHours: sum((row) => row.transfer.hours),
      transferAmount: sum((row) => row.transfer.amount),
      cashHours: sum((row) => row.cash.hours),
      allowance: sum((row) => row.allowance),
      cashAmount: sum((row) => row.cash.amount + row.allowance),
      cashOnly: sum((row) => row.cash.amount),
      gross: sum((row) => row.grossPay),
    }
  }, [rows])

  const periodKey = data ? { storeCode: data.storeCode, periodStart: data.periodStart, weeks: data.weeks } : null

  const saveApproval = async (row: AttendancePayRowDto, approval: AttendanceLeaveApprovalDto, approvedHours: number) => {
    if (!periodKey) return
    try {
      await saveAttendanceLeaveApproval({ ...periodKey, userGuid: row.userGuid, leaveType: approval.leaveType, approvedHours })
      message.success(t('attendancePayroll.pay.approvalSaved'))
      onReload()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.actionFailed'))
      throw error
    }
  }

  const saveNote = async (row: AttendancePayRowDto, note: string) => {
    if (!periodKey) return
    try {
      await saveAttendancePayNote({ ...periodKey, userGuid: row.userGuid, note })
      message.success(t('attendancePayroll.pay.noteSaved'))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.actionFailed'))
      throw error
    }
  }

  /** 按档明细：「日 3.5 · 六 8 · 假 15.6 · 平 32.9」，按装填顺序列出非零档。 */
  const bucketText = (buckets: AttendancePayBuckets) => TRANSFER_FILL_ORDER
    .filter((key) => buckets[key] > 0)
    .map((key) => `${t(`attendancePayroll.bucketShort.${key}`)} ${formatHours(buckets[key])}`)
    .join(' · ')

  const renderLeave = (row: AttendancePayRowDto) => {
    const parts: ReactElement[] = []
    const { sick, publicHolidayPay, annual, cashOut } = row.leaveDetail
    if (sick > 0) parts.push(<span key="sick">{t('attendancePayroll.pay.leaveSick', { hours: formatHours(sick) })}</span>)
    if (publicHolidayPay > 0) parts.push(<span key="ph">{t('attendancePayroll.pay.leaveHoliday', { hours: formatHours(publicHolidayPay) })}</span>)
    const approvalPart = (approval: AttendanceLeaveApprovalDto, labelKey: string) => (
      <span key={approval.leaveType} className="atp-approval">
        {t(labelKey, { hours: formatHours(approval.requestedHours) })}
        {approval.requestedHours !== approval.approvedHours || canEdit ? ` → ${t('attendancePayroll.pay.approved')} ` : ' '}
        {canEdit ? (
          <LeaveApprovalInput approval={approval} disabled={!canEdit} onSave={(value) => saveApproval(row, approval, value)} />
        ) : (
          <span className="atp-dashed-box">{formatHours(approval.approvedHours, '0')}</span>
        )}
      </span>
    )
    if (annual) parts.push(approvalPart(annual, 'attendancePayroll.pay.leaveAnnual'))
    if (cashOut) parts.push(approvalPart(cashOut, 'attendancePayroll.pay.leaveCashOut'))
    return (
      <div className="atp-pay-cell">
        <span>{formatHours(row.hours.leave)}</span>
        {parts.length ? <span className="atp-pay-sub">{parts.reduce<(ReactElement | string)[]>((acc, part, index) => (index ? [...acc, ' · ', part] : [part]), [])}</span> : null}
      </div>
    )
  }

  if (!data && loading) return <div className="atp-loading"><Spin /></div>
  if (!data) return <Empty description={t('attendancePayroll.empty')} />

  return (
    <Spin spinning={loading}>
      <div className="atp-stack">
        <div className="atp-formula">
          <span><strong>{t('attendancePayroll.pay.formulaPayableLabel')}</strong><span className="atp-formula-chip">{t('attendancePayroll.pay.formulaPayable')}</span></span>
          <span><strong className="atp-transfer-text">{t('attendancePayroll.pay.transfer')}</strong><span className="atp-formula-chip">{t('attendancePayroll.pay.formulaTransfer')}</span></span>
          <span><strong className="atp-cash-text">{t('attendancePayroll.pay.cash')}</strong><span className="atp-formula-chip">{t('attendancePayroll.pay.formulaCash')}</span></span>
        </div>

        <div className="atp-stats">
          <div className="atp-stat">
            <span className="atp-stat-label">{t('attendancePayroll.pay.statHours')}</span>
            <strong className="atp-stat-value">{formatHours(totals.total, '0')} h</strong>
            <span className="atp-stat-sub">
              {t('attendancePayroll.pay.statHoursSub', {
                weekday: formatHours(totals.weekday, '0'),
                saturday: formatHours(totals.saturday, '0'),
                sunday: formatHours(totals.sunday, '0'),
                publicHoliday: formatHours(totals.publicHoliday, '0'),
                leave: formatHours(totals.leave, '0'),
              })}
            </span>
          </div>
          <div className="atp-stat">
            <span className="atp-stat-label">{t('attendancePayroll.pay.statTransfer')}</span>
            <strong className="atp-stat-value atp-transfer-text">{formatMoney(totals.transferAmount)}</strong>
            <span className="atp-stat-sub">{formatHours(totals.transferHours, '0')} h</span>
          </div>
          <div className="atp-stat">
            <span className="atp-stat-label">{t('attendancePayroll.pay.statCash')}</span>
            <strong className="atp-stat-value atp-cash-text">{formatMoney(totals.cashAmount)}</strong>
            <span className="atp-stat-sub">{t('attendancePayroll.pay.statCashSub', { hours: formatHours(totals.cashHours, '0'), allowance: formatMoney(totals.allowance) })}</span>
          </div>
          <div className={`atp-stat ${data.pendingCount > 0 ? 'atp-stat-warn' : ''}`}>
            <span className="atp-stat-label">{t('attendancePayroll.pay.statGross')}</span>
            <strong className="atp-stat-value atp-gross-text">{formatMoney(totals.gross)}</strong>
            <span className="atp-stat-sub">
              {data.periodStatus === 'Submitted'
                ? t('attendancePayroll.pay.submittedSub')
                : data.pendingCount > 0
                  ? t('attendancePayroll.pay.pendingSub', { count: data.pendingCount })
                  : t('attendancePayroll.pay.readySub')}
            </span>
          </div>
        </div>

        {rows.length ? (
          <div className="atp-grid-wrap">
            <table className="atp-grid atp-pay-grid">
              <thead>
                <tr>
                  <th rowSpan={2} className="atp-sticky atp-employee-col">{t('attendancePayroll.pay.employeeMode')}</th>
                  <th colSpan={6}>{t('attendancePayroll.pay.payableHours')}</th>
                  <th colSpan={2} className="atp-week-divider atp-head-transfer">{t('attendancePayroll.pay.transfer')}</th>
                  <th colSpan={3} className="atp-week-divider atp-head-cash">{t('attendancePayroll.pay.cash')}</th>
                  <th rowSpan={2} className="atp-week-divider">{t('attendancePayroll.pay.gross')}</th>
                  <th rowSpan={2}>{t('attendancePayroll.pay.note')}</th>
                </tr>
                <tr>
                  {HOUR_KEYS.map((key) => <th key={key}>{t(`attendancePayroll.tierLong.${key}`)}</th>)}
                  <th>{t('attendancePayroll.tierLong.leave')}</th>
                  <th>{t('attendancePayroll.tierLong.total')}</th>
                  <th className="atp-week-divider atp-head-transfer">{t('attendancePayroll.pay.hoursByTier')}</th>
                  <th className="atp-head-transfer">{t('attendancePayroll.pay.amount')}</th>
                  <th className="atp-week-divider atp-head-cash">{t('attendancePayroll.pay.hoursByTier')}</th>
                  <th className="atp-head-cash">{t('attendancePayroll.pay.allowance')}</th>
                  <th className="atp-head-cash">{t('attendancePayroll.pay.amount')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.userGuid}>
                    <td className="atp-sticky atp-employee-col">
                      <div className="atp-person">
                        <Avatar size={30} style={{ background: avatarColorOf(row.userName), fontSize: 12 }}>{initialsOf(row.userName)}</Avatar>
                        <div>
                          <div className="atp-person-name">
                            {row.userName}{' '}
                            <span className="atp-muted atp-small">
                              {t(`attendancePayroll.employmentType.${row.employmentType}`)}
                              {row.ageYears !== undefined && row.ageYears < 18 ? ` · ${t('attendancePayroll.ageYears', { age: row.ageYears })}` : ''}
                            </span>
                          </div>
                          <div>
                            <Tag className="atp-tag" color={row.payMode === 'Cash' ? 'green' : row.payMode === 'Mixed' ? 'purple' : 'blue'}>
                              {t(`attendancePayroll.payMode.${row.payMode}`)}
                            </Tag>
                            {row.payMode === 'Mixed' && row.fixedTransferHoursPerWeek ? (
                              <span className="atp-muted atp-small">{t('attendancePayroll.pay.fixedPerWeek', { hours: formatHours(row.fixedTransferHoursPerWeek) })}</span>
                            ) : null}
                            {row.warnings?.length ? (
                              <Tooltip title={t('attendancePayroll.pay.rateMissing')}><Tag color="red" className="atp-tag">!</Tag></Tooltip>
                            ) : null}
                          </div>
                        </div>
                      </div>
                    </td>
                    {HOUR_KEYS.map((key) => <td key={key} className="atp-num">{formatHours(row.hours[key])}</td>)}
                    <td className="atp-num">{renderLeave(row)}</td>
                    <td className="atp-num atp-num-strong">{formatHours(row.hours.total)}</td>
                    <td className="atp-num atp-week-divider atp-col-transfer">
                      <div className="atp-pay-cell">
                        <span>{formatHours(row.transfer.hours)}</span>
                        {row.transfer.hours > 0 ? <span className="atp-pay-sub">{bucketText(row.transfer.buckets)}</span> : null}
                      </div>
                    </td>
                    <td className="atp-num atp-col-transfer">{row.transfer.amount ? formatMoney(row.transfer.amount) : '—'}</td>
                    <td className="atp-num atp-week-divider atp-col-cash">
                      <div className="atp-pay-cell">
                        <span>{formatHours(row.cash.hours)}</span>
                        {row.cash.hours > 0 ? <span className="atp-pay-sub">{bucketText(row.cash.buckets)}</span> : null}
                      </div>
                    </td>
                    <td className={`atp-num atp-col-cash ${row.allowance < 0 ? 'atp-negative' : row.allowance > 0 ? 'atp-positive' : ''}`}>
                      {formatSignedMoney(row.allowance)}
                    </td>
                    <td className="atp-num atp-col-cash">{row.cash.amount || row.allowance ? formatMoney(round2(row.cash.amount + row.allowance)) : '—'}</td>
                    <td className="atp-num atp-num-strong atp-week-divider">{formatMoney(row.grossPay)}</td>
                    <td className="atp-note-col">
                      <NoteInput
                        value={row.note}
                        disabled={!canEdit}
                        placeholder={t('attendancePayroll.pay.notePlaceholder')}
                        onSave={(note) => saveNote(row, note)}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <td className="atp-sticky atp-employee-col">{t('attendancePayroll.hours.footer', { count: rows.length })}</td>
                  {HOUR_KEYS.map((key) => <td key={key} className="atp-num">{formatHours(totals[key])}</td>)}
                  <td className="atp-num">{formatHours(totals.leave)}</td>
                  <td className="atp-num atp-num-strong">{formatHours(totals.total)}</td>
                  <td className="atp-num atp-week-divider">{formatHours(totals.transferHours)}</td>
                  <td className="atp-num">{formatMoney(totals.transferAmount)}</td>
                  <td className="atp-num atp-week-divider">{formatHours(totals.cashHours)}</td>
                  <td className="atp-num">{formatMoney(totals.allowance)}</td>
                  <td className="atp-num">{formatMoney(totals.cashAmount)}</td>
                  <td className="atp-num atp-num-strong atp-week-divider">{formatMoney(totals.gross)}</td>
                  <td />
                </tr>
              </tfoot>
            </table>
            <div className="atp-legend-note">{t('attendancePayroll.pay.footnote', { weeks })}</div>
          </div>
        ) : (
          <Empty description={t('attendancePayroll.empty')} />
        )}
      </div>
    </Spin>
  )
}
