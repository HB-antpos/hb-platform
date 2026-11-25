import { CheckOutlined } from '@ant-design/icons'
import { Button, Popconfirm } from 'antd'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'

import type { AttendanceHoursDayDto, AttendanceManualEntryTrace } from '../../../types/attendancePayroll'
import {
  buildCellDetail,
  minutesLabel,
  type Breakdown,
  type BreakdownKey,
  type ShiftSegmentView,
  type ShiftView,
} from './hoursCellDetailLogic'
import { formatHours, round2 } from './payrollLogic'

/** 工时统计单元格的气泡内容：该格对应的打卡记录（排班窗口、各班段与来源、偏差、用餐扣除）与补录留痕。 */
interface CellDetailContentProps {
  employeeName: string
  day: AttendanceHoursDayDto
  /** 补录抽屉入口：未打卡的班可直接补录；没权限 / 已锁定时不传。 */
  onAddHours?: () => void
  /** 补录留痕（打卡补录或假补录）。 */
  trace?: AttendanceManualEntryTrace | null
  /** 撤销补录；没权限时不传（留痕仍显示）。 */
  onRevoke?: () => Promise<void>
  revokeConfirmTitle?: string
  /** 补录的病假没有医嘱。 */
  noCertificate?: boolean
}

export function CellDetailContent(props: CellDetailContentProps) {
  const { employeeName, day, onAddHours, trace, onRevoke, revokeConfirmTitle, noCertificate } = props
  const { t } = useTranslation()
  const model = buildCellDetail(day)
  const units = { hour: t('attendancePayroll.cellDetail.units.hour'), minute: t('attendancePayroll.cellDetail.units.minute') }
  const hasShifts = model.shifts.length > 0

  const sourceText = (segment: ShiftSegmentView) => {
    const parts: string[] = []
    if (segment.inSource) parts.push(t(`attendancePayroll.cellDetail.sources.${segment.inSource}`))
    // 上下班来源不同（比如收银机扫码上班、店长改了下班）才分开写
    if (segment.outSource && segment.outSource !== segment.inSource) parts.push(`${t('attendancePayroll.cellDetail.clockOut')}：${t(`attendancePayroll.cellDetail.sources.${segment.outSource}`)}`)
    if (segment.deviceCode) parts.push(t('attendancePayroll.cellDetail.deviceCode', { code: segment.deviceCode }))
    return parts.join(' · ')
  }

  const deviationText = (shift: ShiftView) => {
    const d = shift.deviation
    if (!d) return ''
    const parts: string[] = []
    if (d.lateIn) parts.push(t('attendancePayroll.cellDetail.late', { text: minutesLabel(d.lateIn, units) }))
    if (d.earlyIn) parts.push(t('attendancePayroll.cellDetail.early', { text: minutesLabel(d.earlyIn, units) }))
    if (d.earlyOut) parts.push(t('attendancePayroll.cellDetail.earlyLeave', { text: minutesLabel(d.earlyOut, units) }))
    if (d.lateOut) parts.push(t('attendancePayroll.cellDetail.lateLeave', { text: minutesLabel(d.lateOut, units) }))
    return parts.length ? parts.join(' · ') : t('attendancePayroll.cellDetail.onTime')
  }

  const leave = day.leave
  const holidayPay = day.publicHolidayPay
  const leaveLine = (() => {
    if (!leave) return null
    const hours = formatHours(leave.hours, '0')
    switch (leave.type) {
      case 'PaidSick': return t('attendancePayroll.cellDetail.leaveSick', { hours })
      case 'PaidAnnual': return t('attendancePayroll.cellDetail.leaveAnnual', { hours })
      case 'AnnualCashOut': return t('attendancePayroll.cellDetail.leaveCashOut', { hours })
      default: return t('attendancePayroll.cellDetail.leaveUnpaid')
    }
  })()

  return (
    <div className="atp-cd">
      <div className="atp-cd-head">
        <b>{employeeName}</b>
        <span className="atp-cd-sub">
          {t('attendancePayroll.cellDetail.subtitle', { weekday: t(`attendancePayroll.weekdayShort.${dayjs(day.date).day()}`), date: dayjs(day.date).format('MM-DD') })}
        </span>
        {model.tier ? <span className={`atp-cd-tier atp-cd-tier-${model.tier}`}>{t(`attendancePayroll.cellDetail.tier.${model.tier}`)}</span> : null}
      </div>

      {model.shifts.map((shift, shiftIndex) => (
        <div className="atp-cd-shift" key={`${shift.scheduleText}-${shiftIndex}`}>
          {shift.scheduleText ? (
            <div className="atp-cd-row"><span className="atp-cd-k">{t('attendancePayroll.cellDetail.schedule')}</span><span>{shift.scheduleText}</span></div>
          ) : null}
          {shift.missing ? (
            <div className="atp-cd-note atp-cd-note-warn">
              {t('attendancePayroll.cellDetail.missingText')}
              {onAddHours ? (
                <div className="atp-cd-actions"><Button size="small" type="primary" onClick={onAddHours}>{t('attendancePayroll.cellDetail.missingAction')}</Button></div>
              ) : null}
            </div>
          ) : (
            <>
              <div className="atp-cd-sec">{t('attendancePayroll.cellDetail.punches', { count: shift.segments.length })}</div>
              {shift.segments.map((segment, index) => (
                <div className="atp-cd-seg" key={`${segment.start}-${index}`}>
                  <div className="atp-cd-seg-main">
                    <span className="atp-cd-in">{t('attendancePayroll.cellDetail.clockIn')} {segment.start}</span>
                    <span aria-hidden="true">→</span>
                    {segment.end
                      ? <span className="atp-cd-out">{t('attendancePayroll.cellDetail.clockOut')} {segment.end}</span>
                      : <span className="atp-cd-open">{t('attendancePayroll.cellDetail.openEnd')}</span>}
                    {segment.minutes !== null ? <span className="atp-cd-dur">{minutesLabel(segment.minutes, units)}</span> : null}
                  </div>
                  {segment.isAutoClockOut ? <div className="atp-cd-seg-sub">{t('attendancePayroll.cellDetail.autoOut')}</div> : null}
                  {sourceText(segment) ? <div className="atp-cd-seg-sub">{t('attendancePayroll.cellDetail.source')}：{sourceText(segment)}</div> : null}
                </div>
              ))}
              {deviationText(shift) ? (
                <div className="atp-cd-row"><span className="atp-cd-k">{t('attendancePayroll.cellDetail.deviation')}</span><span>{deviationText(shift)}</span></div>
              ) : null}
              {shift.mealDeductionMinutes > 0 || shift.mealAddBackMinutes > 0 ? (
                <div className="atp-cd-row">
                  <span className="atp-cd-k">{t('attendancePayroll.cellDetail.meal')}</span>
                  <span>
                    {shift.mealDeductionMinutes > 0 ? minutesLabel(shift.mealDeductionMinutes, units) : '—'}
                    {shift.mealAddBackMinutes > 0 ? ` · ${t('attendancePayroll.cellDetail.mealAddBack', { text: minutesLabel(shift.mealAddBackMinutes, units) })}` : ''}
                  </span>
                </div>
              ) : null}
              {shift.overtimeMinutes > 0 ? (
                <div className="atp-cd-row"><span className="atp-cd-k" /><span>{t('attendancePayroll.cellDetail.overtime', { text: minutesLabel(shift.overtimeMinutes, units) })}</span></div>
              ) : null}
            </>
          )}
        </div>
      ))}

      {hasShifts && (model.paidHours ?? 0) > 0 ? (
        <div className="atp-cd-row atp-cd-paid">
          <span className="atp-cd-k">{t('attendancePayroll.cellDetail.paid')}</span>
          <b>{formatHours(model.paidHours)} h</b>
        </div>
      ) : null}

      {leaveLine ? <div className="atp-cd-note atp-cd-note-info">{leaveLine}{noCertificate ? ` · ${t('attendancePayroll.manualLeave.noCertificateBadge')}` : ''}</div> : null}
      {holidayPay?.state === 'Confirmed' && (holidayPay.hours ?? 0) > 0
        ? <div className="atp-cd-note atp-cd-note-info">{t('attendancePayroll.cellDetail.holidayPay', { hours: formatHours(holidayPay.hours) })}</div>
        : null}
      {holidayPay?.state === 'Pending' ? <div className="atp-cd-note atp-cd-note-warn">{t('attendancePayroll.cellDetail.holidayPending')}</div> : null}
      {day.otherStores.map((other) => (
        <div className="atp-cd-row" key={other.storeCode}>
          <span className="atp-cd-k">{t('attendancePayroll.cellDetail.otherStore')}</span>
          <span>{t('attendancePayroll.cellDetail.otherStoreValue', { name: other.storeName ?? other.storeCode, hours: formatHours(other.hours, '0') })}</span>
        </div>
      ))}

      {trace ? (
        <div className="atp-cd-note atp-cd-note-info atp-cd-trace">
          <div>{t('attendancePayroll.manualEntry.traceReason', { reason: trace.reason ?? '—' })}</div>
          <div className="atp-cd-seg-sub">{t('attendancePayroll.manualEntry.traceBy', { by: trace.createdBy ?? '—', at: trace.createdAt ? dayjs(trace.createdAt).format('MM-DD HH:mm') : '—' })}</div>
          {trace.createdSchedule ? <div className="atp-cd-seg-sub">{t('attendancePayroll.manualEntry.traceSchedule')}</div> : null}
          {onRevoke ? (
            <Popconfirm
              title={revokeConfirmTitle}
              okText={t('attendancePayroll.manualEntry.revoke')}
              cancelText={t('attendancePayroll.manualEntry.cancel')}
              onConfirm={() => onRevoke()}
            >
              <Button type="link" size="small" danger className="atp-cd-revoke">{t('attendancePayroll.manualEntry.revoke')}</Button>
            </Popconfirm>
          ) : null}
        </div>
      ) : null}

      {day.pending.length > 0
        ? <div className="atp-cd-note atp-cd-note-warn">{t('attendancePayroll.cellDetail.pending')}</div>
        : day.approvedMarks.length > 0 && hasShifts
          ? <div className="atp-cd-note atp-cd-note-ok"><CheckOutlined /> {t('attendancePayroll.cellDetail.approved')}</div>
          : null}
    </div>
  )
}

/** 工资表某个数字（平日 / 周六 / 周日 / 公假 / 带薪假 / 合计）对应的逐日明细。 */
interface BreakdownContentProps {
  employeeName: string
  breakdownKey: BreakdownKey
  breakdown: Breakdown
  /** 表里这个数字，与气泡合计对不上时加一行「以表中数字为准」。 */
  mismatch: boolean
  /** 点某一天：跳到工时统计并定位到那一格；不传则不可点。 */
  onShowDay?: (date: string) => void
}

export function BreakdownContent(props: BreakdownContentProps) {
  const { employeeName, breakdownKey, breakdown, mismatch, onShowDay } = props
  const { t } = useTranslation()
  const keyLabel = breakdownKey === 'leave'
    ? t('attendancePayroll.breakdown.leave')
    : breakdownKey === 'total'
      ? t('attendancePayroll.breakdown.total')
      : t(`attendancePayroll.breakdown.tierName.${breakdownKey}`)
  const days = new Set(breakdown.items.map((item) => item.date)).size

  return (
    <div className="atp-cd atp-bd">
      <div className="atp-cd-head">
        <b>{employeeName}</b>
        <span className="atp-cd-sub">{keyLabel} · {t('attendancePayroll.breakdown.tierDays', { count: days })}</span>
      </div>
      {breakdown.items.length === 0 ? <div className="atp-cd-note">{t('attendancePayroll.breakdown.empty')}</div> : (
        <table className="atp-bd-table">
          <tbody>
            {breakdown.items.map((item, index) => {
              const kindLabel = item.kind === 'leave'
                ? t(`attendancePayroll.breakdown.leaveType.${item.leaveType ?? 'PaidSick'}`, { defaultValue: t('attendancePayroll.breakdown.leave') })
                : t(`attendancePayroll.breakdown.tierName.${item.kind}`)
              return (
                <tr
                  key={`${item.date}-${item.kind}-${index}`}
                  className={onShowDay ? 'is-link' : undefined}
                  onClick={onShowDay ? () => onShowDay(item.date) : undefined}
                >
                  <td>{t(`attendancePayroll.weekdayShort.${dayjs(item.date).day()}`)} {dayjs(item.date).format('MM-DD')}</td>
                  {breakdownKey === 'total' || breakdownKey === 'leave'
                    ? <td><span className={`atp-cd-tier atp-cd-tier-${item.kind}`}>{kindLabel}</span></td>
                    : null}
                  <td className="atp-bd-ranges">{item.ranges.join(' · ')}</td>
                  <td className="atp-bd-hours"><b>{formatHours(item.hours, '0')}</b></td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
      <div className="atp-bd-sum"><span>{t('attendancePayroll.breakdown.sum')}</span><b>{formatHours(round2(breakdown.total), '0')} h</b></div>
      {mismatch ? <div className="atp-cd-note atp-cd-note-warn">{t('attendancePayroll.breakdown.mismatch')}</div> : null}
      {onShowDay && breakdown.items.length > 0 ? <div className="atp-cd-seg-sub">{t('attendancePayroll.breakdown.jump')}</div> : null}
    </div>
  )
}
