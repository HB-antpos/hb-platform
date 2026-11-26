import { UndoOutlined } from '@ant-design/icons'
import { Avatar, Button, Empty, Input, InputNumber, Popover, Spin, Tag, Tooltip, message } from 'antd'
import { useEffect, useMemo, useRef, useState, type ReactElement } from 'react'
import { useTranslation } from 'react-i18next'

import { saveAttendanceCashOverride, saveAttendanceLeaveApproval, saveAttendancePayNote } from '../../../services/attendancePayrollService'
import type {
  AttendanceCashOverrideBucket,
  AttendanceLeaveApprovalDto,
  AttendancePayBuckets,
  AttendancePayCashBucketDto,
  AttendancePayResultDto,
  AttendanceHoursResultDto,
  AttendancePayRowDto,
} from '../../../types/attendancePayroll'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import { formatMyobTime } from '../Myob/myobLogic'

import BirthdayRateReviewBanner from './BirthdayRateReviewBanner'
import { BreakdownContent } from './HoursCellPopover'
import { breakdownMismatch, buildBreakdown, type BreakdownKey } from './hoursCellDetailLogic'
import {
  TRANSFER_FILL_ORDER,
  employeeDisplayName,
  formatHours,
  formatMoney,
  formatSignedMoney,
  resolveCashOverrideCommit,
  visibleCashOverrideBuckets,
  round2,
  showsAgeBadge,
  summarizeCashOverrides,
  type BirthdayRateReview,
} from './payrollLogic'
import type { PayrollPushCell } from './payrollMyobPushLogic'

interface PayTabProps {
  data: AttendancePayResultDto | null
  loading: boolean
  weeks: 1 | 2
  canEdit: boolean
  onReload: () => void
  /** 生日后时薪还没更新的待办（已按本期筛过：生日 ≤ 本期末）。 */
  rateReviews?: readonly BirthdayRateReview[]
  /** 点待办条上的「去核对」跳到个人时薪表。 */
  onOpenRates?: () => void
  /** 每人的 MYOB 推送状态；有值才显示「MYOB 推送」这一列（没权限 / 服务器没开放 / 本期未锁定时为空）。 */
  myobPush?: Readonly<Record<string, PayrollPushCell>> | null
  /**
   * 手动补录工时入口：没权限 / 本期已锁定时不传（行上就没有「补录」按钮）。
   * 带 userGuid = 从某一行进来（预选该员工）；不带 = 从表格下方「添加员工补录」进来，在抽屉里选人
   * （本期一个工时都没有的人不在工资表里，只能走这个入口）。
   */
  onManualEntry?: (userGuid?: string) => void
  /**
   * 同一周期的工时统计数据：应付工时各数字悬停时弹出「这个数字是哪几天、各多少小时」。
   * 没有（没权限 / 还没加载 / 周期对不上）就不弹气泡，表格照常。
   */
  dayDetail?: AttendanceHoursResultDto | null
  /** 点气泡里的某一天：跳到工时统计并定位到那一格。 */
  onShowDay?: (userGuid: string, date: string) => void
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
      step={1}
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

/**
 * 公假 / 带薪假的现金金额（虚线框）。默认显示系统算的值；改了就是手填，**取代**这一档的系统现金金额（不是叠加）。
 * 清空或点「恢复系统值」回到系统算的；Esc 取消本次修改。这一档没有现金工时（工时全在转账里）时只读，避免同一笔钱付两次。
 */
function CashBucketInput(props: {
  bucket: AttendancePayCashBucketDto
  /** 这一档本期有没有工时（哪怕全在转账里）：只用来决定没有现金部分时要不要解释一句。 */
  hasHours: boolean
  disabled: boolean
  ariaLabel: string
  onSave: (amount: number | null) => Promise<void>
}) {
  const { t } = useTranslation()
  const { bucket } = props
  const [value, setValue] = useState<number | null>(bucket.amount)
  // Esc 取消：随后的失焦不能再提交（失焦时 state 还没更新，不能靠 value 判断）。
  const skipCommit = useRef(false)
  // 回车保存后紧接着的失焦、或连点，只发一次请求。
  const saving = useRef(false)
  useEffect(() => setValue(bucket.amount), [bucket.amount, bucket.isManual])

  if (!bucket.editable) {
    // 有手填但这一档已没有现金工时（例如后来改了时薪成全转账）：手填没生效，如实说明，行上另有告警。
    if (bucket.manualAmount !== null && bucket.manualAmount !== undefined) {
      return (
        <Tooltip title={t('attendancePayroll.pay.cashOverride.ignored', { amount: formatMoney(bucket.manualAmount) })}>
          <span className="atp-cash-bucket atp-cash-bucket-muted" tabIndex={0}>
            —
            <span className="atp-cash-bucket-sub atp-cash-bucket-sub-warn">{t('attendancePayroll.pay.cashOverride.ignoredShort')}</span>
          </span>
        </Tooltip>
      )
    }
    if (props.hasHours) {
      return (
        <Tooltip title={t('attendancePayroll.pay.cashOverride.noCashHours')}>
          <span className="atp-cash-bucket atp-cash-bucket-muted" tabIndex={0}>
            —
            <span className="atp-cash-bucket-sub">{t('attendancePayroll.pay.cashOverride.inTransfer')}</span>
          </span>
        </Tooltip>
      )
    }
    return <span className="atp-cash-bucket atp-cash-bucket-muted">—</span>
  }

  const commit = () => {
    if (skipCommit.current) {
      skipCommit.current = false
      setValue(bucket.amount)
      return
    }
    if (saving.current) return
    const action = resolveCashOverrideCommit(bucket, value)
    if (action.kind === 'none') {
      setValue(bucket.amount)
      return
    }
    saving.current = true
    // 先把输入框显示成钳制后的值（负数 → 0、超上限 → 上限），不让未钳制的原始输入在等后端返回期间一直挂着。
    if (action.kind === 'save') setValue(action.amount)
    void props.onSave(action.kind === 'clear' ? null : action.amount)
      .catch(() => setValue(bucket.amount))
      .finally(() => {
        saving.current = false
      })
  }

  const delta = round2(bucket.amount - bucket.systemAmount)
  const detail = bucket.isManual ? (
    <div className="atp-cash-tip">
      <div><span>{t('attendancePayroll.pay.cashOverride.manual')}</span><b>{formatMoney(bucket.amount)}</b></div>
      <div><span>{t('attendancePayroll.pay.cashOverride.systemLabel')}</span><span>{formatMoney(bucket.systemAmount)}</span></div>
      <div>
        <span>{t('attendancePayroll.pay.cashOverride.diffLabel')}</span>
        <span className={delta < 0 ? 'atp-negative' : 'atp-positive'}>{formatSignedMoney(delta, formatMoney(0))}</span>
      </div>
      {bucket.updatedBy ? (
        <div className="atp-cash-tip-muted">
          <span>{t('attendancePayroll.pay.cashOverride.editorLabel')}</span>
          <span>{bucket.updatedBy} · {formatMyobTime(bucket.updatedAt)}</span>
        </div>
      ) : null}
    </div>
  ) : undefined

  return (
    <div className="atp-cash-bucket">
      <Tooltip title={detail}>
        <InputNumber
          className={`atp-dashed-input atp-cash-input${bucket.isManual ? ' is-manual' : ''}`}
          size="small"
          // 不设 min / max：antd 对越界输入会在失焦时自己改显示值，却不先触发 onChange，
          // 导致「框里显示 0.00、实际没保存」。上下限统一交给 resolveCashOverrideCommit 钳制。
          precision={2}
          controls={false}
          prefix="$"
          value={value}
          disabled={props.disabled}
          aria-label={props.ariaLabel}
          onChange={setValue}
          onBlur={commit}
          onPressEnter={commit}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              skipCommit.current = true
              event.currentTarget.blur()
            }
          }}
        />
      </Tooltip>
      {bucket.isManual ? (
        <span className="atp-cash-bucket-sub atp-cash-bucket-sub-warn">
          {t('attendancePayroll.pay.cashOverride.systemShort', { amount: formatMoney(bucket.systemAmount) })}
          {!props.disabled ? (
            <Button
              type="text"
              size="small"
              className="atp-cash-restore"
              icon={<UndoOutlined />}
              title={t('attendancePayroll.pay.cashOverride.restore')}
              aria-label={t('attendancePayroll.pay.cashOverride.restore')}
              onClick={() => void props.onSave(null).catch(() => undefined)}
            />
          ) : null}
        </span>
      ) : null}
    </div>
  )
}

export default function PayTab({ data, loading, weeks, canEdit, onReload, rateReviews, onOpenRates, myobPush, onManualEntry, dayDetail, onShowDay }: PayTabProps) {
  const { t } = useTranslation()
  const rows = data?.rows ?? []
  const detailByUser = useMemo(() => new Map((dayDetail?.employees ?? []).map((employee) => [employee.userGuid, employee])), [dayDetail])

  /** 数字悬停气泡：逐日明细；拿不到工时统计数据或这个数字没有对应的天就原样返回。 */
  const withBreakdown = (row: AttendancePayRowDto, key: BreakdownKey, node: ReactElement) => {
    const employee = detailByUser.get(row.userGuid)
    if (!employee) return node
    const breakdown = buildBreakdown(employee, key)
    if (breakdown.items.length === 0) return node
    const tableValue = key === 'leave' ? row.hours.leave : key === 'total' ? row.hours.total : row.hours[key]
    return (
      <Popover
        trigger="hover"
        placement="bottom"
        mouseEnterDelay={0.35}
        destroyOnHidden
        classNames={{ root: 'atp-cell-pop' }}
        content={(
          <BreakdownContent
            employeeName={employeeDisplayName(row.userName, row.nickname)}
            breakdownKey={key}
            breakdown={breakdown}
            mismatch={breakdownMismatch(breakdown, tableValue)}
            onShowDay={onShowDay ? (date) => onShowDay(row.userGuid, date) : undefined}
          />
        )}
      >
        {node}
      </Popover>
    )
  }

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
      publicHolidayCash: sum((row) => row.cash.publicHoliday.amount),
      leaveCash: sum((row) => row.cash.leave.amount),
      gross: sum((row) => row.grossPay),
    }
  }, [rows])
  // 本页手填了几项、比系统值多（少）多少：显示在「现金合计」卡上，方便财务核对差额。
  const overrides = useMemo(() => summarizeCashOverrides(rows), [rows])
  // 「公假 $ / 假 $」列：本期没人能手填（全在转账里）时隐藏，减少一屏的空列
  const cashOverrideColumns = useMemo(() => visibleCashOverrideBuckets(rows), [rows])

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

  /** 手填 / 清除（amount 为 null）某人公假或带薪假的现金金额；后端重算工资，成功后整表重新取。 */
  const saveCashOverride = async (row: AttendancePayRowDto, bucket: AttendanceCashOverrideBucket, amount: number | null) => {
    if (!periodKey) return
    try {
      await saveAttendanceCashOverride({ ...periodKey, userGuid: row.userGuid, bucket, amount })
      message.success(t(amount === null ? 'attendancePayroll.pay.cashOverride.restored' : 'attendancePayroll.pay.cashOverride.saved'))
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
        {withBreakdown(row, 'leave', <span className="atp-bd-trigger">{formatHours(row.hours.leave)}</span>)}
        {parts.length ? <span className="atp-pay-sub">{parts.reduce<(ReactElement | string)[]>((acc, part, index) => (index ? [...acc, ' · ', part] : [part]), [])}</span> : null}
      </div>
    )
  }

  if (!data && loading) return <div className="atp-loading"><Spin /></div>
  if (!data) return <Empty description={t('attendancePayroll.empty')} />

  // 工资表只列本期有工时的人：一个工时都没有（没排班也没打卡）的人不在表里，要从这里选人补录
  const addEmployeeButton = onManualEntry ? (
    <div className="atp-me-add-row">
      <Button type="dashed" onClick={() => onManualEntry()}>
        {t('attendancePayroll.manualEntry.addEmployee')}
      </Button>
    </div>
  ) : null

  return (
    <Spin spinning={loading}>
      <div className="atp-stack">
        <BirthdayRateReviewBanner reviews={rateReviews ?? []} onOpenRates={onOpenRates} />
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
            <span className="atp-stat-sub">
              {t('attendancePayroll.pay.statCashSub', { hours: formatHours(totals.cashHours, '0'), allowance: formatMoney(totals.allowance) })}
              {overrides.count > 0 ? (
                <span className="atp-manual-chip">
                  {t('attendancePayroll.pay.cashOverride.chip', { count: overrides.count, delta: formatSignedMoney(overrides.delta, formatMoney(0)) })}
                </span>
              ) : null}
            </span>
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
                  <th colSpan={3 + cashOverrideColumns.length} className="atp-week-divider atp-head-cash">{t('attendancePayroll.pay.cash')}</th>
                  <th rowSpan={2} className="atp-week-divider">{t('attendancePayroll.pay.gross')}</th>
                  {myobPush ? <th rowSpan={2}>{t('attendancePayroll.pay.myobPush')}</th> : null}
                  <th rowSpan={2}>{t('attendancePayroll.pay.note')}</th>
                </tr>
                <tr>
                  {HOUR_KEYS.map((key) => <th key={key}>{t(`attendancePayroll.tierLong.${key}`)}</th>)}
                  <th className="atp-leave-col">{t('attendancePayroll.tierLong.leave')}</th>
                  <th>{t('attendancePayroll.tierLong.total')}</th>
                  <th className="atp-week-divider atp-head-transfer">{t('attendancePayroll.pay.hoursByTier')}</th>
                  <th className="atp-head-transfer">{t('attendancePayroll.pay.amount')}</th>
                  <th className="atp-week-divider atp-head-cash">{t('attendancePayroll.pay.hoursByTier')}</th>
                  {cashOverrideColumns.map(({ key }) => (
                    <th key={key} className="atp-head-cash">
                      {t(key === 'publicHoliday' ? 'attendancePayroll.pay.cashPublicHoliday' : 'attendancePayroll.pay.cashLeave')}
                    </th>
                  ))}
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
                            {employeeDisplayName(row.userName, row.nickname)}{' '}
                            <span className="atp-muted atp-small">
                              {row.employmentType ? t(`attendancePayroll.employmentType.${row.employmentType}`) : ''}
                              {showsAgeBadge(row.ageYears) ? ` · ${t('attendancePayroll.ageYears', { age: row.ageYears })}` : ''}
                            </span>
                          </div>
                          <div>
                            {row.payMode ? (
                              <Tag className="atp-tag" color={row.payMode === 'Cash' ? 'green' : row.payMode === 'Mixed' ? 'purple' : 'blue'}>
                                {t(`attendancePayroll.payMode.${row.payMode}`)}
                              </Tag>
                            ) : (
                              <Tag className="atp-tag" color="red">{t('attendancePayroll.payMode.none')}</Tag>
                            )}
                            {row.payMode === 'Mixed' && row.fixedTransferHoursPerWeek ? (
                              <span className="atp-muted atp-small">{t('attendancePayroll.pay.fixedPerWeek', { hours: formatHours(row.fixedTransferHoursPerWeek) })}</span>
                            ) : null}
                            {onManualEntry ? (
                              <Button type="link" size="small" className="atp-me-row-btn" onClick={() => onManualEntry(row.userGuid)}>
                                {t('attendancePayroll.manualEntry.rowAction')}
                              </Button>
                            ) : null}
                            {row.warnings?.filter((code) => code !== 'NO_RATE').length ? (
                              <Tooltip title={row.warnings.filter((code) => code !== 'NO_RATE').map((code) => t(`attendancePayroll.pay.warnings.${code}`, { defaultValue: code })).join('；')}>
                                <Tag color="red" className="atp-tag">!</Tag>
                              </Tooltip>
                            ) : null}
                          </div>
                        </div>
                      </div>
                    </td>
                    {HOUR_KEYS.map((key) => <td key={key} className="atp-num">{withBreakdown(row, key, <span className="atp-bd-trigger">{formatHours(row.hours[key])}</span>)}</td>)}
                    <td className="atp-num atp-leave-col">{renderLeave(row)}</td>
                    <td className="atp-num atp-num-strong">{withBreakdown(row, 'total', <span className="atp-bd-trigger">{formatHours(row.hours.total)}</span>)}</td>
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
                    {cashOverrideColumns.map(({ key, apiBucket }) => (
                      <td key={key} className="atp-num atp-col-cash atp-cash-bucket-col">
                        <CashBucketInput
                          bucket={row.cash[key]}
                          hasHours={row.hours[key] > 0}
                          disabled={!canEdit}
                          ariaLabel={t('attendancePayroll.pay.cashOverride.aria', {
                            name: row.userName,
                            bucket: t(`attendancePayroll.tierLong.${key}`),
                          })}
                          onSave={(amount) => saveCashOverride(row, apiBucket, amount)}
                        />
                      </td>
                    ))}
                    <td className={`atp-num atp-col-cash ${row.allowance < 0 ? 'atp-negative' : row.allowance > 0 ? 'atp-positive' : ''}`}>
                      {formatSignedMoney(row.allowance)}
                    </td>
                    <td className="atp-num atp-col-cash">{row.cash.amount || row.allowance ? formatMoney(round2(row.cash.amount + row.allowance)) : '—'}</td>
                    <td className="atp-num atp-num-strong atp-week-divider">{formatMoney(row.grossPay)}</td>
                    {myobPush ? (
                      <td className="atp-myob-col">
                        {myobPush[row.userGuid] ? (
                          <>
                            <Tag color={myobPush[row.userGuid].view.tone} className="atp-tag">{t(myobPush[row.userGuid].view.labelKey)}</Tag>
                            {myobPush[row.userGuid].view.lastAt ? <div className="atp-muted atp-small">{formatMyobTime(myobPush[row.userGuid].view.lastAt)}</div> : null}
                          </>
                        ) : '—'}
                      </td>
                    ) : null}
                    <td className="atp-note-col">
                      <NoteInput
                        value={row.note ?? undefined}
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
                  {cashOverrideColumns.map(({ key }) => (
                    <td key={key} className="atp-num">{formatMoney(key === 'publicHoliday' ? totals.publicHolidayCash : totals.leaveCash)}</td>
                  ))}
                  <td className="atp-num">{formatMoney(totals.allowance)}</td>
                  <td className="atp-num">{formatMoney(totals.cashAmount)}</td>
                  <td className="atp-num atp-num-strong atp-week-divider">{formatMoney(totals.gross)}</td>
                  {myobPush ? <td /> : null}
                  <td />
                </tr>
              </tfoot>
            </table>
            <div className="atp-legend-note">{t('attendancePayroll.pay.footnote', { weeks })}</div>
            {addEmployeeButton}
          </div>
        ) : (
          <>
            <Empty description={t('attendancePayroll.empty')} />
            {addEmployeeButton}
          </>
        )}
      </div>
    </Spin>
  )
}
