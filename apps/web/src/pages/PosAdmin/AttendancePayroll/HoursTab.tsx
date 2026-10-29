import { CheckOutlined, PaperClipOutlined } from '@ant-design/icons'
import { Avatar, Button, Empty, Space, Spin, Tag, message } from 'antd'
import dayjs from 'dayjs'
import { useMemo, useState, type ReactElement } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { reviewAttendanceApproval } from '../../../services/attendancePayrollService'
import type {
  AttendanceHoursAlertDto,
  AttendanceHoursDayDto,
  AttendanceHoursDayHeaderDto,
  AttendanceHoursEmployeeDto,
  AttendanceHoursResultDto,
} from '../../../types/attendancePayroll'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import { EMPLOYMENT_TYPE_TAG_COLORS, MANAGER_TAG_COLOR, formatHours, isoWeekNumber, round2, summarizeEmployeeHours } from './payrollLogic'

interface HoursTabProps {
  data: AttendanceHoursResultDto | null
  loading: boolean
  weeks: 1 | 2
  includePending: boolean
  mergeStores: boolean
  canReview: boolean
  onMergeStoresChange: (value: boolean) => void
  onReload: () => void
  onOpenRates?: () => void
}

const TOTAL_KEYS = ['weekday', 'saturday', 'sunday', 'publicHoliday', 'leave', 'total'] as const

/** 另一店角标：店号短（≤5 位）直接用店号，否则取店名前两个词首字母；完整店名放 title。 */
function storeAbbreviation(name?: string, code?: string) {
  if (code && code.length <= 5) return code.toUpperCase()
  const words = (name ?? code ?? '').split(/\s+/).filter(Boolean)
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase()
  return (words[0] ?? '').slice(0, 2).toUpperCase()
}

export default function HoursTab(props: HoursTabProps) {
  const { data, loading, weeks, mergeStores, canReview } = props
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [dismissed, setDismissed] = useState<string[]>([])
  const [acting, setActing] = useState<string>()

  const employees = data?.employees ?? []
  const days = data?.days ?? []
  const totalsByEmployee = useMemo(
    () => new Map(employees.map((employee) => [employee.userGuid, summarizeEmployeeHours(employee)])),
    [employees],
  )
  const grand = useMemo(() => {
    const sums = { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0, leave: 0, total: 0, sick: 0, annual: 0, cashOut: 0, publicHolidayPay: 0 }
    for (const totals of totalsByEmployee.values()) {
      for (const key of Object.keys(sums) as (keyof typeof sums)[]) sums[key] += totals[key]
    }
    return Object.fromEntries(Object.entries(sums).map(([key, value]) => [key, round2(value)])) as typeof sums
  }, [totalsByEmployee])
  /** 每天合计只算计薪工时（与设计一致）；带薪假在右侧「带薪假」列单独合计。 */
  const dayTotals = useMemo(() => days.map((_, index) => round2(employees.reduce(
    (sum, employee) => sum + (employee.days[index]?.paidHours ?? 0),
    0,
  ))), [days, employees])
  const publicHolidayPending = useMemo(
    () => employees.reduce((count, employee) => count + employee.days.filter((day) => day.publicHolidayPay?.state === 'Pending').length, 0),
    [employees],
  )

  const weekGroups = useMemo(() => {
    const groups: { label: string; days: AttendanceHoursDayHeaderDto[] }[] = []
    for (let index = 0; index < days.length; index += 7) {
      const slice = days.slice(index, index + 7)
      if (!slice.length) continue
      groups.push({
        label: t('attendancePayroll.hours.weekGroup', {
          week: isoWeekNumber(slice[0].date),
          from: dayjs(slice[0].date).format('MM-DD'),
          to: dayjs(slice[slice.length - 1].date).format('MM-DD'),
        }),
        days: slice,
      })
    }
    return groups
  }, [days, t])

  // 员工资料没填类型时不显示类型标签
  const typeLabel = (employee: AttendanceHoursEmployeeDto) => (employee.employmentType ? t(`attendancePayroll.employmentType.${employee.employmentType}`) : null)

  const confirmHolidayPay = async (alert: AttendanceHoursAlertDto, approve: boolean) => {
    if (!alert.approvalGuid) return
    setActing(alert.approvalGuid)
    try {
      await reviewAttendanceApproval(alert.approvalGuid, approve ? 'approve' : 'reject', approve
        ? { approvedPaidHours: alert.hours ?? undefined }
        // 「不适用」= 拒绝该待处理，备注写明原因
        : { reviewRemark: t('attendancePayroll.alerts.notApplicableRemark') })
      message.success(t('attendancePayroll.alerts.holidayDone'))
      props.onReload()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.actionFailed'))
    } finally {
      setActing(undefined)
    }
  }

  const alertKey = (alert: AttendanceHoursAlertDto) => `${alert.type}|${alert.userGuid ?? ''}|${alert.date ?? ''}`
  const visibleAlerts = (data?.alerts ?? []).filter((alert) => !dismissed.includes(alertKey(alert)))
  const dismiss = (alert: AttendanceHoursAlertDto) => setDismissed((current) => [...current, alertKey(alert)])

  const renderAlert = (alert: AttendanceHoursAlertDto) => {
    const key = alertKey(alert)
    if (alert.type === 'MultiStore') {
      const people = new Set((alert.otherStoreItems ?? []).map((item) => item.userGuid)).size
      const detail = (alert.otherStoreItems ?? [])
        .map((item) => t('attendancePayroll.alerts.multiStoreItem', {
          name: item.userName,
          date: dayjs(item.date).format('MM-DD'),
          store: item.storeName ?? item.storeCode,
          hours: formatHours(item.hours),
        }))
        .join(t('attendancePayroll.listSeparator'))
      return (
        <div key={key} className="atp-alert atp-alert-store">
          <div className="atp-alert-text">
            <strong>{t('attendancePayroll.alerts.multiStoreTitle', { count: people })}</strong>
            {detail}{t('attendancePayroll.alerts.multiStoreTail')}
          </div>
          <Space>
            <Button size="small" onClick={() => dismiss(alert)}>{t('attendancePayroll.alerts.keepSeparate')}</Button>
            <Button size="small" type="primary" onClick={() => props.onMergeStoresChange(true)}>{t('attendancePayroll.alerts.merge')}</Button>
          </Space>
        </div>
      )
    }
    if (alert.type === 'PublicHolidayPay') {
      return (
        <div key={key} className="atp-alert atp-alert-holiday">
          <div className="atp-alert-text">
            <strong>{t('attendancePayroll.alerts.holidayTitle', { date: dayjs(alert.date).format('MM-DD'), name: alert.holidayName ?? '' })}</strong>
            {t('attendancePayroll.alerts.holidayBody', {
              name: alert.userName,
              type: alert.employmentType ? t(`attendancePayroll.employmentType.${alert.employmentType}`) : '',
              weeks: alert.lookbackWeeks,
              count: alert.ordinaryCount,
              weekday: t(`attendancePayroll.weekdayShort.${dayjs(alert.date).day()}`),
              min: alert.minCount,
              hours: formatHours(alert.hours),
            })}
          </div>
          {canReview && alert.approvalGuid ? (
            <Space>
              <Button size="small" loading={acting === alert.approvalGuid} onClick={() => void confirmHolidayPay(alert, false)}>
                {t('attendancePayroll.alerts.notApplicable')}
              </Button>
              <Button size="small" type="primary" loading={acting === alert.approvalGuid} onClick={() => void confirmHolidayPay(alert, true)}>
                {t('attendancePayroll.alerts.confirmHoliday', { hours: formatHours(alert.hours) })}
              </Button>
            </Space>
          ) : null}
        </div>
      )
    }
    return (
      <div key={key} className="atp-alert atp-alert-birthday">
        <div className="atp-alert-text">
          <strong>
            {t('attendancePayroll.alerts.birthdayTitle', { name: alert.userName, date: dayjs(alert.date).format('MM-DD'), age: alert.ageYears })}
            {alert.isMinor ? t('attendancePayroll.alerts.minorSuffix') : ''}
          </strong>
          {alert.isMinor ? t('attendancePayroll.alerts.birthdayMinorBody') : ''}
        </div>
        <Space>
          <Button size="small" onClick={() => dismiss(alert)}>{t('attendancePayroll.alerts.gotIt')}</Button>
          {props.onOpenRates && alert.isMinor ? (
            <Button size="small" type="primary" onClick={props.onOpenRates}>{t('attendancePayroll.alerts.openRates')}</Button>
          ) : null}
        </Space>
      </div>
    )
  }

  /** 单元格：数字 + 角标；休息、不能上班、待审用不同底色。 */
  const renderCell = (day: AttendanceHoursDayDto, header: AttendanceHoursDayHeaderDto, weekStart: boolean) => {
    // 带薪公假待确认单独用「公 待确认」角标，不再叠「待审」
    const pending = day.pending.some((type) => type !== 'PublicHolidayPay')
    const holidayPay = day.publicHolidayPay
    let value: string | null = day.paidHours !== null ? formatHours(day.paidHours, '0') : null
    if (value === null && day.leave && day.leave.type !== 'AnnualCashOut') value = formatHours(day.leave.hours, '0')
    if (value === null && holidayPay?.state === 'Confirmed') value = formatHours(holidayPay.hours, '0')
    if (value === null && holidayPay?.state === 'Pending') value = '?'
    const classes = ['atp-cell']
    if (pending || holidayPay?.state === 'Pending') classes.push('atp-cell-pending')
    else if (day.leave?.type === 'PaidSick') classes.push('atp-cell-sick')
    else if (day.leave?.type === 'PaidAnnual') classes.push('atp-cell-annual')
    else if (value === null && day.isUnavailable) classes.push('atp-cell-unavailable')
    else if (value === null) classes.push('atp-cell-rest')
    if (header.isPublicHoliday && value === null) classes.push('atp-cell-holiday')

    const badges: ReactElement[] = []
    if (header.isPublicHoliday && (day.paidHours ?? 0) > 0) badges.push(<span key="ph" className="atp-badge atp-badge-holiday">{t('attendancePayroll.badges.publicHoliday')}</span>)
    if (holidayPay?.state === 'Confirmed') {
      badges.push(<span key="php" className="atp-badge atp-badge-holiday">{t('attendancePayroll.badges.publicHoliday')} <CheckOutlined /></span>)
    }
    if (holidayPay?.state === 'Pending') badges.push(<span key="phq" className="atp-badge atp-badge-pending">{t('attendancePayroll.badges.holidayPending')}</span>)
    if (day.leave?.type === 'PaidSick') {
      badges.push(<span key="sick" className="atp-badge atp-badge-sick">{t('attendancePayroll.badges.sick')}{day.leave.hasCertificate ? <PaperClipOutlined /> : null}</span>)
    }
    if (day.leave?.type === 'PaidAnnual') badges.push(<span key="annual" className="atp-badge atp-badge-annual">{t('attendancePayroll.badges.annual')}</span>)
    if (day.leave?.type === 'AnnualCashOut') {
      badges.push(<span key="cash" className="atp-badge atp-badge-cashout">{t('attendancePayroll.badges.cashOut')} {formatHours(day.leave.hours)}</span>)
    }
    for (const other of day.otherStores) {
      badges.push(<span key={`o-${other.storeCode}`} className="atp-badge atp-badge-store" title={other.storeName ?? other.storeCode}>{storeAbbreviation(other.storeName, other.storeCode)} {formatHours(other.hours)}</span>)
    }
    if (day.isBirthday) badges.push(<span key="bday" className="atp-badge atp-badge-birthday" role="img" aria-label={t('attendancePayroll.legend.birthday')}>🎂</span>)
    if (pending) badges.push(<span key="pend" className="atp-badge atp-badge-pending">{t('attendancePayroll.badges.pending')}</span>)
    if (!pending && day.approvedMarks.length) badges.push(<span key="ok" className="atp-badge atp-badge-ok"><CheckOutlined /></span>)

    const restText = day.isUnavailable ? t('attendancePayroll.hours.unavailable') : t('attendancePayroll.hours.rest')
    return (
      <td key={day.date} className={[weekStart ? 'atp-week-divider' : '', header.isPublicHoliday ? 'atp-col-holiday' : ''].join(' ')}>
        <div className={classes.join(' ')}>
          {value !== null ? <span className="atp-cell-value">{value}</span> : <span className="atp-cell-rest-text">{restText}</span>}
          {badges.length ? <span className="atp-cell-badges">{badges}</span> : null}
        </div>
      </td>
    )
  }

  if (!data && loading) return <div className="atp-loading"><Spin /></div>
  if (!data) return <Empty description={t('attendancePayroll.empty')} />

  const minorLabel = (employee: AttendanceHoursEmployeeDto) => (
    employee.ageYears != null && employee.ageYears < 18
      ? <Tag color="gold" className="atp-tag">{t('attendancePayroll.ageYears', { age: employee.ageYears })}</Tag>
      : null
  )

  return (
    <Spin spinning={loading}>
      <div className="atp-stack">
        {visibleAlerts.map(renderAlert)}
        {mergeStores ? (
          <div className="atp-alert atp-alert-store">
            <div className="atp-alert-text">{t('attendancePayroll.alerts.merged')}</div>
            <Button size="small" onClick={() => props.onMergeStoresChange(false)}>{t('attendancePayroll.alerts.unmerge')}</Button>
          </div>
        ) : null}

        <div className="atp-stats">
          <div className="atp-stat">
            <span className="atp-stat-label">{t('attendancePayroll.hours.statPaid')}</span>
            <strong className="atp-stat-value">{formatHours(round2(grand.total - grand.leave), '0')} h</strong>
            <span className="atp-stat-sub">
              {t('attendancePayroll.hours.statPaidSub', { people: employees.length, weeks })}
              {mergeStores ? t('attendancePayroll.hours.withOtherStores') : t('attendancePayroll.hours.withoutOtherStores')}
            </span>
          </div>
          <div className="atp-stat">
            <span className="atp-stat-label">{t('attendancePayroll.hours.statTiers')}</span>
            <strong className="atp-stat-value atp-stat-value-small">
              {t('attendancePayroll.hours.tiersLine', {
                weekday: formatHours(grand.weekday, '0'),
                saturday: formatHours(grand.saturday, '0'),
                sunday: formatHours(grand.sunday, '0'),
                publicHoliday: formatHours(grand.publicHoliday, '0'),
              })}
            </strong>
            <span className="atp-stat-sub">{t('attendancePayroll.hours.tiersSub')}</span>
          </div>
          <div className="atp-stat">
            <span className="atp-stat-label">{t('attendancePayroll.hours.statLeave')}</span>
            <strong className="atp-stat-value">{formatHours(grand.leave, '0')} h</strong>
            <span className="atp-stat-sub">
              {t('attendancePayroll.hours.leaveSub', {
                sick: formatHours(grand.sick, '0'),
                annual: formatHours(grand.annual, '0'),
                publicHoliday: formatHours(grand.publicHolidayPay, '0'),
                cashOut: formatHours(grand.cashOut, '0'),
              })}
            </span>
          </div>
          <div className={`atp-stat ${data.pendingCount > 0 ? 'atp-stat-warn' : ''}`}>
            <span className="atp-stat-label">{t('attendancePayroll.hours.statPending')}</span>
            <strong className="atp-stat-value">
              {t('attendancePayroll.hours.pendingCount', { count: data.pendingCount })}
              {data.pendingCount > 0 ? (
                <Button type="link" size="small" onClick={() => navigate('/pos-admin/schedule-attendance?tab=review')}>{t('attendancePayroll.hours.goReview')}</Button>
              ) : null}
            </strong>
            <span className="atp-stat-sub">
              {data.pendingCount > 0
                ? t('attendancePayroll.hours.pendingSub', { count: publicHolidayPending })
                : t('attendancePayroll.hours.pendingClear')}
            </span>
          </div>
        </div>

        {employees.length ? (
          <div className="atp-grid-wrap">
            <table className="atp-grid">
              <thead>
                <tr>
                  <th rowSpan={2} className="atp-sticky atp-employee-col">{t('attendancePayroll.hours.employee')}</th>
                  {weekGroups.map((group, index) => (
                    <th key={group.label} colSpan={group.days.length} className={index > 0 ? 'atp-week-divider' : undefined}>{group.label}</th>
                  ))}
                  <th colSpan={TOTAL_KEYS.length} className="atp-week-divider">{t('attendancePayroll.hours.totalsHead', { weeks })}</th>
                </tr>
                <tr>
                  {days.map((day, index) => {
                    const weekday = dayjs(day.date).day()
                    const tierHint = day.isPublicHoliday
                      ? t('attendancePayroll.hours.holidayRate')
                      : weekday === 6
                        ? t('attendancePayroll.hours.saturdayRate')
                        : weekday === 0 ? t('attendancePayroll.hours.sundayRate') : ''
                    return (
                      <th
                        key={day.date}
                        className={[
                          index > 0 && index % 7 === 0 ? 'atp-week-divider' : '',
                          day.isPublicHoliday ? 'atp-col-holiday atp-head-holiday' : '',
                          weekday === 6 || weekday === 0 ? 'atp-head-weekend' : '',
                        ].join(' ')}
                        title={day.holidayName}
                      >
                        <div>{t(`attendancePayroll.weekdayShort.${weekday}`)} {dayjs(day.date).format('MM-DD')}</div>
                        {tierHint ? <div className="atp-head-hint">{tierHint}</div> : null}
                      </th>
                    )
                  })}
                  {TOTAL_KEYS.map((key, index) => (
                    <th key={key} className={[index === 0 ? 'atp-week-divider' : '', key === 'publicHoliday' ? 'atp-col-holiday' : ''].join(' ')}>
                      {t(`attendancePayroll.tierShort.${key}`)}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {employees.map((employee) => {
                  const totals = totalsByEmployee.get(employee.userGuid)!
                  return (
                    <tr key={employee.userGuid}>
                      <td className="atp-sticky atp-employee-col">
                        <div className="atp-person">
                          <Avatar size={30} style={{ background: avatarColorOf(employee.userName), fontSize: 12 }}>{initialsOf(employee.userName)}</Avatar>
                          <div>
                            <div className="atp-person-name">{employee.userName}</div>
                            <Space size={4} wrap>
                              {typeLabel(employee) ? <Tag color={employee.employmentType ? EMPLOYMENT_TYPE_TAG_COLORS[employee.employmentType] : undefined} className="atp-tag">{typeLabel(employee)}</Tag> : null}
                              {employee.isManager ? <Tag color={MANAGER_TAG_COLOR} className="atp-tag">{t('attendancePayroll.manager')}</Tag> : null}
                              {minorLabel(employee)}
                              {employee.hasOtherStores ? <Tag color="cyan" className="atp-tag">{t('attendancePayroll.multiStore')}</Tag> : null}
                            </Space>
                          </div>
                        </div>
                      </td>
                      {employee.days.map((day, index) => renderCell(day, days[index], index > 0 && index % 7 === 0))}
                      {TOTAL_KEYS.map((key, index) => (
                        <td
                          key={key}
                          className={['atp-num', index === 0 ? 'atp-week-divider' : '', key === 'total' ? 'atp-num-strong' : '', key === 'publicHoliday' ? 'atp-col-holiday' : ''].join(' ')}
                        >
                          {formatHours(totals[key])}
                        </td>
                      ))}
                    </tr>
                  )
                })}
              </tbody>
              <tfoot>
                <tr>
                  <td className="atp-sticky atp-employee-col">{t('attendancePayroll.hours.footer', { count: employees.length })}</td>
                  {dayTotals.map((value, index) => (
                    <td key={days[index].date} className={['atp-num', index > 0 && index % 7 === 0 ? 'atp-week-divider' : ''].join(' ')}>{formatHours(value)}</td>
                  ))}
                  {TOTAL_KEYS.map((key, index) => (
                    <td key={key} className={['atp-num', index === 0 ? 'atp-week-divider' : '', key === 'total' ? 'atp-num-strong' : ''].join(' ')}>
                      {formatHours(grand[key])}
                    </td>
                  ))}
                </tr>
              </tfoot>
            </table>
            <div className="atp-legend">
              <span><span className="atp-badge atp-badge-holiday">{t('attendancePayroll.badges.publicHoliday')}</span> {t('attendancePayroll.legend.publicHoliday')}</span>
              <span><span className="atp-badge atp-badge-holiday">{t('attendancePayroll.badges.publicHoliday')} 7.6</span> {t('attendancePayroll.legend.publicHolidayPay')}</span>
              <span><span className="atp-badge atp-badge-sick">{t('attendancePayroll.badges.sick')}</span> {t('attendancePayroll.legend.sick')}</span>
              <span><span className="atp-badge atp-badge-annual">{t('attendancePayroll.badges.annual')}</span> {t('attendancePayroll.legend.annual')}</span>
              <span><span className="atp-badge atp-badge-cashout">{t('attendancePayroll.badges.cashOut')}</span> {t('attendancePayroll.legend.cashOut')}</span>
              <span><span className="atp-badge atp-badge-birthday">🎂</span> {t('attendancePayroll.legend.birthday')}</span>
              <span><span className="atp-badge atp-badge-store">SB 4</span> {t('attendancePayroll.legend.otherStore')}</span>
              <span><span className="atp-badge atp-badge-pending">{t('attendancePayroll.badges.pending')}</span> {t('attendancePayroll.legend.pending')}</span>
            </div>
            <div className="atp-legend-note">{t('attendancePayroll.legend.note')}</div>
          </div>
        ) : (
          <Empty description={t('attendancePayroll.empty')} />
        )}
      </div>
    </Spin>
  )
}
