import {
  ArrowDownOutlined,
  ArrowUpOutlined,
  LockOutlined,
  SearchOutlined,
  SyncOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Avatar,
  Button,
  Checkbox,
  DatePicker,
  Empty,
  Image,
  Input,
  InputNumber,
  Modal,
  Select,
  Space,
  Spin,
  Tabs,
  Tag,
  TimePicker,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { notifyAttendanceReviewChanged } from '../../../layout/attendancePendingBadge'
import {
  getAttendanceReviewQueue,
  reviewAttendanceApproval,
  type AttendanceReviewView,
} from '../../../services/attendancePayrollService'
import {
  createManagedAttendancePunchAdjustment,
  getAttendanceRecords,
  previewManagedAttendancePunchAdjustment,
} from '../../../services/scheduleAttendanceService'
import { useAuthStore } from '../../../store/auth'
import type { AttendanceApprovalDayPreviewDto } from '../../../types/attendancePayroll'
import type {
  AttendanceApprovalDto,
  AttendanceScheduleDto,
  ReviewAttendanceApprovalPayload,
} from '../../../types/scheduleAttendance'
import { formatHours } from '../AttendancePayroll/payrollLogic'
import { useAttendanceStores } from '../AttendancePayroll/useAttendanceStores'
import { validateOvertimeApproval } from '../ScheduleAttendance/attendanceRecordLogic'
import messagesEn from './attendanceReviewMessages.en.json'
import messagesZh from './attendanceReviewMessages.zh.json'
import {
  REVIEW_CATEGORIES,
  avatarColorOf,
  buildDayPreviewFromRecord,
  buildTimelineModel,
  canReviewItem,
  canSelectForBatch,
  countByCategory,
  describeSubmittedTime,
  findAutoClockOutPunchGuid,
  getReviewCategory,
  initialsOf,
  isSelfRequest,
  isSystemGenerated,
  pickNextApprovalGuid,
  tierOfWorkDate,
  type ReviewCategory,
} from './reviewLogic'
import './attendanceReview.css'

registerPageMessages({ zh: messagesZh, en: messagesEn })

const CATEGORY_COLORS: Record<ReviewCategory | 'Other', string> = {
  PunchAdjustment: 'blue',
  Overtime: 'orange',
  MissingClockOut: 'red',
  MealBreak: 'cyan',
  Leave: 'purple',
  Punch: 'default',
  PaidSick: 'magenta',
  PaidAnnual: 'geekblue',
  AnnualCashOut: 'green',
  PublicHolidayPay: 'gold',
  Other: 'default',
}

const timeOf = (value?: string) => (value ? dayjs(value).format('HH:mm') : '--')

/** 需要看当天打卡的审批类型：详情里画时间线、推算批准后工时。 */
const DAY_RECORD_SOURCES = new Set(['PunchAdjustment', 'MissingClockOut', 'Overtime', 'MealBreak', 'Punch'])

export default function AttendanceReviewPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)
  const currentUserGuid = currentUser?.userGUID
  const canReviewOwn = access.canReviewOwnAttendance
  const { storeOptions, storeCode, setStoreCode } = useAttendanceStores(false)
  const [view, setView] = useState<AttendanceReviewView>('pending')
  const [range, setRange] = useState<[Dayjs, Dayjs]>([dayjs().subtract(14, 'day'), dayjs().add(7, 'day')])
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [items, setItems] = useState<AttendanceApprovalDto[]>([])
  const [pendingTotal, setPendingTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [loadedAt, setLoadedAt] = useState<Dayjs | null>(null)
  const [category, setCategory] = useState<ReviewCategory | 'all'>('all')
  const [sortAsc, setSortAsc] = useState(false)
  const [selectedGuid, setSelectedGuid] = useState<string>()
  const [checkedGuids, setCheckedGuids] = useState<string[]>([])
  const [remark, setRemark] = useState('')
  const [overtimeMinutes, setOvertimeMinutes] = useState<number | null>(null)
  const [paidHours, setPaidHours] = useState<number | null>(null)
  const [clockOutTime, setClockOutTime] = useState<Dayjs | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [batchRejectOpen, setBatchRejectOpen] = useState(false)
  const [batchRemark, setBatchRemark] = useState('')
  const [dayRecord, setDayRecord] = useState<AttendanceScheduleDto | null>(null)
  const dayRecordRequestRef = useRef(0)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const result = await getAttendanceReviewQueue(view, {
        storeCode,
        fromDate: range[0].format('YYYY-MM-DD'),
        toDate: range[1].format('YYYY-MM-DD'),
        keyword: keyword || undefined,
        page: 1,
        pageSize: 200,
      })
      setItems(result.items)
      if (view === 'pending') {
        setPendingTotal(result.total)
        notifyAttendanceReviewChanged(result.total)
      }
      setLoadedAt(dayjs())
      setCheckedGuids([])
      setSelectedGuid((current) => (
        current && result.items.some((item) => item.approvalGuid === current) ? current : result.items[0]?.approvalGuid
      ))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendanceReview.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [keyword, range, storeCode, t, view])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    // 本人审批记录页签只给持 ReviewSelf 的人
    if (view === 'self' && !canReviewOwn) setView('pending')
  }, [canReviewOwn, view])

  const categoryCounts = useMemo(() => countByCategory(items), [items])
  const visibleItems = useMemo(() => {
    const filtered = category === 'all' ? items : items.filter((item) => getReviewCategory(item) === category)
    return [...filtered].sort((left, right) => {
      const compare = (left.workDate ?? '').localeCompare(right.workDate ?? '')
      return sortAsc ? compare : -compare
    })
  }, [category, items, sortAsc])
  const selected = visibleItems.find((item) => item.approvalGuid === selectedGuid) ?? visibleItems[0]
  const batchableGuids = useMemo(
    () => visibleItems.filter((item) => canSelectForBatch(item, currentUserGuid)).map((item) => item.approvalGuid),
    [currentUserGuid, visibleItems],
  )

  // 切换选中条目时重置右侧表单的默认值
  useEffect(() => {
    setRemark('')
    setOvertimeMinutes(selected?.candidateOvertimeMinutes ?? null)
    setPaidHours(selected?.publicHolidayPay?.suggestedHours ?? null)
    setClockOutTime(null)
  }, [selected?.approvalGuid])

  // 选中条目的当天考勤记录（排班、班段、工时）：后端审批不带当天详情，按员工 + 工作日取 records；
  // 没有打卡查看权限或取不到时只是不画时间线，不影响审核。
  useEffect(() => {
    const requestId = ++dayRecordRequestRef.current
    setDayRecord(null)
    const workDate = selected?.workDate?.slice(0, 10)
    if (!selected || !workDate || !DAY_RECORD_SOURCES.has(selected.sourceType) || !access.canViewAttendancePunches) return
    void getAttendanceRecords({
      storeCode: selected.storeCode,
      userGuid: selected.applicantUserGuid,
      fromDate: workDate,
      toDate: workDate,
      page: 1,
      pageSize: 20,
    })
      .then((result) => {
        if (dayRecordRequestRef.current !== requestId) return
        // 漏下班的 sourceGuid 就是排班 GUID；其它类型取当天该员工的排班
        const record = result.items.find((item) => item.scheduleGuid === selected.sourceGuid) ?? result.items[0] ?? null
        setDayRecord(record)
        if (record && selected.sourceType === 'MissingClockOut') {
          // 实际下班时间默认 = 排班结束
          const end = record.endTime?.slice(0, 5)
          if (end) setClockOutTime(dayjs(`2000-01-01T${end}`))
        }
      })
      .catch(() => undefined)
  }, [access.canViewAttendancePunches, selected?.approvalGuid])

  const dayPreview: AttendanceApprovalDayPreviewDto | undefined = useMemo(() => (
    selected && dayRecord
      ? buildDayPreviewFromRecord(dayRecord, selected, selected.sourceType === 'MissingClockOut' ? clockOutTime?.format('HH:mm') : undefined)
      : undefined
  ), [clockOutTime, dayRecord, selected])

  const categoryLabel = (value: ReviewCategory | 'Other') => t(`attendanceReview.category.${value}`)
  const tierLabel = (item: AttendanceApprovalDto) => {
    const tier = tierOfWorkDate(item.workDate)
    return tier && tier !== 'weekday' ? t(`attendanceReview.tier.${tier}`) : undefined
  }
  const workDateLabel = (item: AttendanceApprovalDto) => {
    if (item.leave && item.leave.startDate !== item.leave.endDate) {
      return `${dayjs(item.leave.startDate).format('MM-DD')} ~ ${dayjs(item.leave.endDate).format('MM-DD')}`
    }
    return item.workDate
      ? `${dayjs(item.workDate).format('MM-DD')} ${t(`attendanceReview.weekdayShort.${dayjs(item.workDate).day()}`)}`
      : '--'
  }
  const submittedLabel = (item: AttendanceApprovalDto) => {
    const described = describeSubmittedTime(item.createdAt)
    if (!described) return ''
    if (described.kind === 'justNow') return t('attendanceReview.submitted.justNow')
    if (described.kind === 'minutesAgo') return t('attendanceReview.submitted.minutesAgo', { count: described.value })
    if (described.kind === 'hoursAgo') return t('attendanceReview.submitted.hoursAgo', { count: described.value })
    if (described.kind === 'yesterday') return t('attendanceReview.submitted.yesterday', { time: described.time })
    return `${described.date} ${described.time}`
  }
  const submitterLabel = (item: AttendanceApprovalDto) => {
    if (isSelfRequest(item, currentUserGuid)) return t('attendanceReview.you')
    if (isSystemGenerated(item)) return t('attendanceReview.system')
    return (item.applicantName ?? '').split(/\s+/)[0]
  }

  /** 列表「内容」列：主句 + 灰色补充。 */
  const renderSummary = (item: AttendanceApprovalDto) => {
    const self = isSelfRequest(item, currentUserGuid)
    let main: ReactNode = item.detail ?? ''
    let sub: ReactNode = null
    const category = getReviewCategory(item)
    if (category === 'MissingClockOut') {
      main = t('attendanceReview.summary.missingClockOut')
      sub = t('attendanceReview.summary.missingClockOutSub')
    } else if (category === 'PunchAdjustment' && item.adjustment) {
      const punchLabel = t(`attendanceReview.punchType.${item.adjustment.punchType ?? 'ClockIn'}`)
      main = (
        <>
          {punchLabel}{' '}
          {item.adjustment.originalPunchTimeLocal ? <del className="atr-muted">{timeOf(item.adjustment.originalPunchTimeLocal)}</del> : null}
          {' → '}<strong>{timeOf(item.adjustment.requestedPunchTimeLocal)}</strong>
        </>
      )
      sub = item.adjustment.reason
    } else if (category === 'Overtime') {
      main = t('attendanceReview.summary.overtime', { minutes: item.candidateOvertimeMinutes ?? 0 })
      sub = item.detail
    } else if (category === 'MealBreak' && item.mealClaim) {
      main = t('attendanceReview.summary.mealBreak', {
        expected: item.mealClaim.expectedCount,
        recorded: item.mealClaim.recordedCount,
        minutes: item.mealClaim.claimedMinutes,
      })
      sub = item.mealClaim.reason
    } else if (item.leave) {
      main = t('attendanceReview.summary.leave', {
        type: categoryLabel(category),
        days: item.leave.dayCount ?? dayjs(item.leave.endDate).diff(item.leave.startDate, 'day') + 1,
      })
      if (item.leave.paidHours) main = `${main} · ${t('attendanceReview.hoursValue', { value: formatHours(item.leave.paidHours) })}`
      if (item.leave.attachmentUrls.length) main = `${main} · ${t('attendanceReview.summary.attachments', { count: item.leave.attachmentUrls.length })}`
      if (item.leave.scheduleConflicts?.length) {
        sub = t('attendanceReview.summary.conflicts', { count: item.leave.scheduleConflicts.length, detail: item.leave.scheduleConflicts.join('、') })
      }
    } else if (category === 'PublicHolidayPay' && item.publicHolidayPay) {
      main = t('attendanceReview.summary.publicHolidayPay', { name: item.publicHolidayPay.holidayName ?? '' })
      sub = t('attendanceReview.summary.publicHolidayPaySub', {
        weeks: item.publicHolidayPay.lookbackWeeks,
        count: item.publicHolidayPay.ordinaryCount,
        hours: formatHours(item.publicHolidayPay.suggestedHours),
      })
    }
    return (
      <div className="atr-summary">
        <div className="atr-summary-main">{main}</div>
        {self && item.reviewStatus === 'Pending' && !canReviewOwn ? (
          <div className="atr-muted"><LockOutlined /> {t('attendanceReview.selfLocked')}</div>
        ) : sub ? <div className="atr-muted">{sub}</div> : null}
      </div>
    )
  }

  const renderPerson = (item: AttendanceApprovalDto) => {
    const self = isSelfRequest(item, currentUserGuid)
    return (
      <Space size={10} align="start">
        <Avatar size={32} style={{ background: avatarColorOf(item.applicantName), fontSize: 12 }}>{initialsOf(item.applicantName)}</Avatar>
        <div>
          <div>
            {item.applicantName}
            {self ? <span className="atr-muted">{t('attendanceReview.youSuffix')}</span> : null}
          </div>
        </div>
      </Space>
    )
  }

  const columns: ColumnsType<AttendanceApprovalDto> = [
    {
      title: t('attendanceReview.columns.type'),
      key: 'type',
      width: 108,
      render: (_, item) => {
        const self = isSelfRequest(item, currentUserGuid)
        const value = getReviewCategory(item)
        return (
          <Space direction="vertical" size={4}>
            <Tag color={CATEGORY_COLORS[value]}>{categoryLabel(value)}</Tag>
            {self && canReviewOwn && item.reviewStatus === 'Pending' ? <Tag color="purple">{t('attendanceReview.selfReviewTag')}</Tag> : null}
            {item.isSelfReview ? <Tag color="purple">{t('attendanceReview.selfReviewedTag')}</Tag> : null}
          </Space>
        )
      },
    },
    { title: t('attendanceReview.columns.employee'), key: 'employee', width: 200, render: (_, item) => renderPerson(item) },
    {
      title: t('attendanceReview.columns.workDate'),
      key: 'workDate',
      width: 130,
      render: (_, item) => (
        <div>
          <div>{workDateLabel(item)}</div>
          {tierLabel(item) ? <div className="atr-muted atr-small">{tierLabel(item)}</div> : null}
        </div>
      ),
    },
    { title: t('attendanceReview.columns.content'), key: 'content', render: (_, item) => renderSummary(item) },
    {
      title: t('attendanceReview.columns.submitted'),
      key: 'submitted',
      width: 120,
      render: (_, item) => (
        <div className="atr-muted">
          <div>{submitterLabel(item)}</div>
          <div>{submittedLabel(item)}</div>
        </div>
      ),
    },
    ...(view === 'pending' ? [] : [{
      title: t('attendanceReview.columns.status'),
      key: 'status',
      width: 110,
      render: (_: unknown, item: AttendanceApprovalDto) => (
        <Space direction="vertical" size={2}>
          <Tag color={item.reviewStatus === 'Approved' ? 'green' : item.reviewStatus === 'Rejected' ? 'red' : 'orange'}>
            {t(`attendanceReview.status.${item.reviewStatus}`)}
          </Tag>
          {item.reviewerName ? <span className="atr-muted atr-small">{item.reviewerName}</span> : null}
        </Space>
      ),
    }]),
  ]

  // ===== 审核动作 =====

  const finishHandled = (handledGuids: string[]) => {
    const remaining = items.filter((item) => !handledGuids.includes(item.approvalGuid))
    const next = handledGuids.length === 1 ? pickNextApprovalGuid(visibleItems, handledGuids[0]) : remaining[0]?.approvalGuid
    if (view === 'pending') {
      setItems(remaining)
      const total = Math.max(0, pendingTotal - handledGuids.length)
      setPendingTotal(total)
      notifyAttendanceReviewChanged(total)
    } else {
      void load()
    }
    setCheckedGuids((current) => current.filter((guid) => !handledGuids.includes(guid)))
    setSelectedGuid(next)
  }

  const buildPayload = (item: AttendanceApprovalDto, action: 'approve' | 'reject'): ReviewAttendanceApprovalPayload | null => {
    const payload: ReviewAttendanceApprovalPayload = { reviewRemark: remark.trim() || undefined }
    if (action === 'reject' && !payload.reviewRemark) {
      message.warning(t('attendanceReview.validation.remarkRequired'))
      return null
    }
    const category = getReviewCategory(item)
    if (category === 'Overtime' && action === 'approve') {
      const minutes = overtimeMinutes ?? 0
      const error = validateOvertimeApproval({
        action,
        approvedMinutes: minutes,
        candidateMinutes: item.candidateOvertimeMinutes ?? 0,
        remark: payload.reviewRemark,
      })
      if (error) {
        message.warning(t(`attendanceReview.validation.overtime.${error}`))
        return null
      }
      payload.approvedOvertimeMinutes = minutes
    }
    if (category === 'PublicHolidayPay' && action === 'approve') {
      if (!paidHours || paidHours <= 0) {
        message.warning(t('attendanceReview.validation.paidHoursRequired'))
        return null
      }
      payload.approvedPaidHours = paidHours
    }
    return payload
  }

  /**
   * 漏下班「填实际下班时间」：店长代员工补一张下班卡（改掉系统午夜自动下班卡；旧数据没有自动卡就新增），
   * 先 preview 拿 previewRevision 再原样提交；后端补卡成功后自动把这条漏下班以「已批准」结案，不再单独调 approve。
   */
  const closeMissingClockOut = async (item: AttendanceApprovalDto) => {
    const workDate = item.workDate?.slice(0, 10)
    if (!clockOutTime || !workDate) {
      message.warning(t('attendanceReview.validation.clockOutRequired'))
      return
    }
    const payload = {
      storeCode: item.storeCode,
      userGuid: item.applicantUserGuid,
      scheduleGuid: item.sourceGuid,
      originalPunchGuid: dayRecord ? findAutoClockOutPunchGuid(dayRecord) : undefined,
      punchType: 'ClockOut' as const,
      requestedPunchTimeLocal: `${workDate}T${clockOutTime.format('HH:mm')}:00`,
      reason: remark.trim() || t('attendanceReview.detail.missingClockOutReason'),
    }
    setSubmitting(true)
    try {
      const preview = await previewManagedAttendancePunchAdjustment(payload)
      if (!preview.isValid || !preview.previewRevision) {
        message.error(preview.validationMessage || t('attendanceReview.messages.reviewFailed'))
        return
      }
      await createManagedAttendancePunchAdjustment({ ...payload, previewRevision: preview.previewRevision })
      message.success(t('attendanceReview.messages.missingClockOutClosed'))
      finishHandled([item.approvalGuid])
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendanceReview.messages.reviewFailed'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleReview = async (action: 'approve' | 'reject') => {
    if (!selected) return
    if (action === 'approve' && selected.sourceType === 'MissingClockOut') {
      await closeMissingClockOut(selected)
      return
    }
    const payload = buildPayload(selected, action)
    if (!payload) return
    setSubmitting(true)
    try {
      await reviewAttendanceApproval(selected.approvalGuid, action, payload)
      message.success(t(action === 'approve' ? 'attendanceReview.messages.approved' : 'attendanceReview.messages.rejected'))
      finishHandled([selected.approvalGuid])
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendanceReview.messages.reviewFailed'))
    } finally {
      setSubmitting(false)
    }
  }

  /** 批量只对勾选的可批量条目逐条调用（后端没有批量接口），失败的留在列表里。 */
  const runBatch = async (action: 'approve' | 'reject', reviewRemark?: string) => {
    const targets = checkedGuids.filter((guid) => batchableGuids.includes(guid))
    if (!targets.length) return
    setSubmitting(true)
    const handled: string[] = []
    let failed = 0
    for (const guid of targets) {
      try {
        await reviewAttendanceApproval(guid, action, { reviewRemark })
        handled.push(guid)
      } catch {
        failed += 1
      }
    }
    setSubmitting(false)
    if (handled.length) finishHandled(handled)
    if (failed) message.warning(t('attendanceReview.messages.batchPartial', { done: handled.length, failed }))
    else message.success(t('attendanceReview.messages.batchDone', { count: handled.length }))
  }

  // ===== 详情 =====

  const renderTimeline = (preview: AttendanceApprovalDayPreviewDto | undefined) => {
    const model = preview ? buildTimelineModel(preview) : undefined
    if (!preview || !model) return null
    const changed = preview.proposedSegments?.[0]
    return (
      <section className="atr-section">
        <div className="atr-section-head">
          <strong>{t('attendanceReview.detail.dayPunches')}</strong>
          {preview.segmentLimit ? (
            <span className="atr-muted">{t('attendanceReview.detail.segments', { count: preview.segments.length + (preview.proposedSegments?.length ?? 0), limit: preview.segmentLimit })}</span>
          ) : null}
        </div>
        <div className="atr-timeline">
          <div className="atr-timeline-row">
            <span className="atr-timeline-label">{t('attendanceReview.detail.schedule')}</span>
            <div className="atr-timeline-track">
              {model.schedule ? <span className="atr-bar atr-bar-schedule" style={{ left: `${model.schedule.left}%`, width: `${model.schedule.width}%` }} /> : null}
            </div>
          </div>
          <div className="atr-timeline-row">
            <span className="atr-timeline-label">{t('attendanceReview.detail.actual')}</span>
            <div className="atr-timeline-track">
              {model.actual.map((bar, index) => (
                <span key={index} className={`atr-bar atr-bar-${bar.kind}`} style={{ left: `${bar.left}%`, width: `${bar.width}%` }} />
              ))}
            </div>
          </div>
          <div className="atr-timeline-ticks">
            {model.ticks.map((tick) => <span key={tick}>{String(tick).padStart(2, '0')}</span>)}
          </div>
        </div>
        {changed ? (
          <div className="atr-muted atr-small">
            {t('attendanceReview.detail.proposedLegend', { start: changed.start, end: changed.end })}
            {preview.segments.some((segment) => segment.isSystemAutoClockOut) ? t('attendanceReview.detail.autoLegend') : ''}
          </div>
        ) : preview.segments.some((segment) => segment.isSystemAutoClockOut) ? (
          <div className="atr-muted atr-small">{t('attendanceReview.detail.autoOnlyLegend')}</div>
        ) : null}
      </section>
    )
  }

  const renderChanges = (item: AttendanceApprovalDto, preview: AttendanceApprovalDayPreviewDto | undefined) => {
    const punchType = item.sourceType === 'MissingClockOut' ? 'ClockOut' : item.adjustment?.punchType ?? 'ClockIn'
    const replaced = preview?.replaced
    const after = item.sourceType === 'MissingClockOut'
      ? clockOutTime?.format('HH:mm') ?? '--'
      : timeOf(item.adjustment?.requestedPunchTimeLocal)
    if (item.sourceType !== 'PunchAdjustment' && item.sourceType !== 'MissingClockOut') return null
    if (item.sourceType === 'PunchAdjustment' && !item.adjustment) return null
    const before = replaced?.originalTime
      ? replaced.isSystemAutoClockOut
        ? t('attendanceReview.detail.autoTime', { time: replaced.originalTime })
        : replaced.originalTime
      : item.adjustment?.originalPunchTimeLocal
        ? timeOf(item.adjustment.originalPunchTimeLocal)
        : t('attendanceReview.detail.none')
    const rows = [{
      label: `${t(`attendanceReview.punchType.${punchType}`)}${replaced ? ` #${replaced.segmentNumber}` : ''}`,
      before,
      after,
    }]
    // 改的是系统午夜自动下班卡：批准后同日漏下班自动结案
    if (replaced?.isSystemAutoClockOut || item.sourceType === 'MissingClockOut') {
      rows.push({
        label: t('attendanceReview.detail.sameDayMissingClockOut'),
        before: t('attendanceReview.detail.pendingState'),
        after: t('attendanceReview.detail.autoClosed'),
      })
    }
    return (
      <section className="atr-section">
        <strong>{t('attendanceReview.detail.changes')}</strong>
        <table className="atr-change-table">
          <thead>
            <tr><th /><th>{t('attendanceReview.detail.now')}</th><th>{t('attendanceReview.detail.after')}</th></tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.label}><td className="atr-muted">{row.label}</td><td>{row.before}</td><td className="atr-after">{row.after}</td></tr>
            ))}
          </tbody>
        </table>
        {item.adjustment?.reason ? (
          <div className="atr-muted atr-small">{t('attendanceReview.detail.reason', { reason: item.adjustment.reason })}</div>
        ) : null}
      </section>
    )
  }

  const renderTypeSpecific = (item: AttendanceApprovalDto, editable: boolean) => {
    const category = getReviewCategory(item)
    if (category === 'Overtime') {
      return (
        <section className="atr-section">
          <strong>{t('attendanceReview.detail.approvedOvertime')}</strong>
          <Space>
            <InputNumber
              min={0}
              max={item.candidateOvertimeMinutes ?? 0}
              step={15}
              value={overtimeMinutes}
              disabled={!editable}
              onChange={(value) => setOvertimeMinutes(value)}
              addonAfter={t('attendanceReview.minutesUnit')}
            />
            <span className="atr-muted">{t('attendanceReview.detail.overtimeHint', { minutes: item.candidateOvertimeMinutes ?? 0 })}</span>
          </Space>
        </section>
      )
    }
    if (category === 'MissingClockOut') {
      return (
        <section className="atr-section">
          <strong>{t('attendanceReview.detail.actualClockOut')}</strong>
          <Space wrap>
            <TimePicker
              format="HH:mm"
              minuteStep={1}
              value={clockOutTime}
              disabled={!editable || !access.canAdjustAttendancePunches}
              onChange={setClockOutTime}
            />
            <span className="atr-muted">{t('attendanceReview.detail.actualClockOutHint')}</span>
          </Space>
          {!access.canAdjustAttendancePunches ? (
            <Alert type="info" showIcon className="atr-inline-alert" message={t('attendanceReview.detail.adjustPermissionRequired')} />
          ) : null}
        </section>
      )
    }
    if (category === 'MealBreak' && item.mealClaim) {
      return (
        <section className="atr-section">
          <strong>{t('attendanceReview.detail.mealClaim')}</strong>
          <div>{t('attendanceReview.summary.mealBreak', { expected: item.mealClaim.expectedCount, recorded: item.mealClaim.recordedCount, minutes: item.mealClaim.claimedMinutes })}</div>
          {item.mealClaim.reason ? <div className="atr-muted">{t('attendanceReview.detail.reason', { reason: item.mealClaim.reason })}</div> : null}
        </section>
      )
    }
    if (category === 'PublicHolidayPay' && item.publicHolidayPay) {
      const holiday = item.publicHolidayPay
      return (
        <section className="atr-section">
          <strong>{t('attendanceReview.detail.publicHolidayPay')}</strong>
          <div>
            {t('attendanceReview.detail.publicHolidayRule', {
              date: dayjs(holiday.holidayDate).format('MM-DD'),
              name: holiday.holidayName ?? '',
              weeks: holiday.lookbackWeeks,
              count: holiday.ordinaryCount,
            })}
          </div>
          <Space>
            <InputNumber min={0} max={24} step={0.25} precision={2} value={paidHours} disabled={!editable} onChange={(value) => setPaidHours(value)} addonAfter="h" />
            <span className="atr-muted">{t('attendanceReview.detail.publicHolidayHint')}</span>
          </Space>
        </section>
      )
    }
    if (item.leave) {
      const leave = item.leave
      return (
        <section className="atr-section">
          <strong>{categoryLabel(category)}</strong>
          <div>
            {t('attendanceReview.detail.leaveRange', {
              start: dayjs(leave.startDate).format('YYYY-MM-DD'),
              end: dayjs(leave.endDate).format('YYYY-MM-DD'),
              days: leave.dayCount ?? dayjs(leave.endDate).diff(leave.startDate, 'day') + 1,
            })}
          </div>
          {leave.reason ? <div className="atr-muted">{t('attendanceReview.detail.reason', { reason: leave.reason })}</div> : null}
          {category === 'PaidSick' && leave.schedules?.length ? (
            <ul className="atr-leave-days">
              {leave.schedules.map((day) => (
                <li key={day.scheduleGuid}>
                  {dayjs(day.workDate).format('MM-DD')} {t(`attendanceReview.weekdayShort.${dayjs(day.workDate).day()}`)} · {day.startTime}–{day.endTime}
                  {' · '}{t('attendanceReview.hoursValue', { value: formatHours(day.paidHours, '0') })}
                  {day.hasPunch ? <Tag color="orange" className="atr-tag-inline">{t('attendanceReview.detail.hasPunch')}</Tag> : null}
                </li>
              ))}
            </ul>
          ) : null}
          {leave.paidHours ? (
            <div>{t(`attendanceReview.detail.paidHours.${category === 'PaidSick' ? 'sick' : category === 'PaidAnnual' ? 'annual' : category === 'AnnualCashOut' ? 'cashOut' : 'other'}`, { hours: formatHours(leave.paidHours) })}</div>
          ) : null}
          {category === 'PaidAnnual' || category === 'AnnualCashOut' ? (
            <div className="atr-muted atr-small">{t('attendanceReview.detail.annualFinanceHint')}</div>
          ) : null}
          {category === 'AnnualCashOut' ? <Alert type="warning" showIcon className="atr-inline-alert" message={t('attendanceReview.detail.cashOutCompliance')} /> : null}
          {leave.attachmentUrls.length ? (
            <div className="atr-attachments">
              <span className="atr-muted">{category === 'PaidSick' ? t('attendanceReview.detail.certificate') : t('attendanceReview.detail.attachments')}</span>
              <Image.PreviewGroup>
                <Space wrap>
                  {leave.attachmentUrls.map((url, index) => (
                    <Image key={`${url}-${index}`} src={url} width={64} height={84} className="atr-thumb" alt={t('attendanceReview.detail.certificate')} />
                  ))}
                </Space>
              </Image.PreviewGroup>
            </div>
          ) : category === 'PaidSick' ? (
            <Alert type="error" showIcon className="atr-inline-alert" message={t('attendanceReview.detail.certificateMissing')} />
          ) : null}
          {leave.scheduleConflicts?.length ? (
            <Alert
              type="warning"
              showIcon
              className="atr-inline-alert"
              message={t('attendanceReview.summary.conflicts', { count: leave.scheduleConflicts.length, detail: leave.scheduleConflicts.join('、') })}
            />
          ) : null}
        </section>
      )
    }
    if (category === 'Punch' && item.detail) {
      return (
        <section className="atr-section">
          <strong>{t('attendanceReview.detail.exception')}</strong>
          <div>{item.detail}</div>
        </section>
      )
    }
    return null
  }

  const renderAfterHours = (item: AttendanceApprovalDto, preview: AttendanceApprovalDayPreviewDto | undefined) => {
    if (!preview || preview.workedHours === undefined) return null
    const tier = tierOfWorkDate(item.workDate, preview.tier)
    const hasAfter = preview.workedHoursAfter !== undefined && item.reviewStatus === 'Pending'
    return (
      <section className="atr-section">
        <div className="atr-section-head">
          <strong>{hasAfter ? t('attendanceReview.detail.hoursAfter') : t('attendanceReview.detail.hoursNow')}</strong>
          <span className="atr-muted">{t('attendanceReview.detail.reviewedOnly')}</span>
        </div>
        <div className="atr-stats">
          <div className="atr-stat">
            <span>{t('attendanceReview.detail.worked')}</span>
            <strong>
              {t('attendanceReview.hoursValue', { value: formatHours(hasAfter ? preview.workedHoursAfter : preview.workedHours, '0') })}
            </strong>
            {hasAfter ? <span className="atr-stat-sub">{t('attendanceReview.detail.workedNow', { value: formatHours(preview.workedHours, '0') })}</span> : null}
          </div>
          <div className="atr-stat">
            <span>{t('attendanceReview.detail.paidNow')}</span>
            <strong>{preview.paidHours === undefined ? '--' : t('attendanceReview.hoursValue', { value: formatHours(preview.paidHours, '0') })}</strong>
          </div>
          {tier ? (
            <div className="atr-stat">
              <span>{t('attendanceReview.detail.tierLabel')}</span>
              <strong className="atr-stat-text">{t(`attendanceReview.tier.${tier}`)}</strong>
            </div>
          ) : null}
        </div>
        {hasAfter ? <div className="atr-muted atr-small">{t('attendanceReview.detail.paidAfterHint')}</div> : null}
      </section>
    )
  }

  const renderDetail = () => {
    if (!selected) {
      return <div className="atr-detail atr-detail-empty"><Empty description={t('attendanceReview.empty')} /></div>
    }
    const self = isSelfRequest(selected, currentUserGuid)
    const editable = canReviewItem(selected, currentUserGuid, canReviewOwn)
    const index = visibleItems.findIndex((item) => item.approvalGuid === selected.approvalGuid)
    const category = getReviewCategory(selected)
    const preview = dayPreview
    return (
      <div className="atr-detail">
        <div className="atr-detail-head">
          <Avatar size={44} style={{ background: avatarColorOf(selected.applicantName) }}>{initialsOf(selected.applicantName)}</Avatar>
          <div className="atr-detail-title">
            <div className="atr-detail-name">{selected.applicantName} · {categoryLabel(category)}</div>
            <div className="atr-muted">
              {workDateLabel(selected)}
              {tierLabel(selected) ? `（${tierLabel(selected)}）` : ''}
              {preview?.scheduleStart ? ` · ${t('attendanceReview.detail.scheduleRange', { start: preview.scheduleStart, end: preview.scheduleEnd })}` : ''}
            </div>
          </div>
          <Space direction="vertical" size={4} align="end">
            <Tag color={selected.reviewStatus === 'Pending' ? 'orange' : selected.reviewStatus === 'Approved' ? 'green' : 'red'}>
              {t(`attendanceReview.status.${selected.reviewStatus}`)}
            </Tag>
            {self && canReviewOwn && selected.reviewStatus === 'Pending' ? <Tag color="purple">{t('attendanceReview.selfReviewTag')}</Tag> : null}
          </Space>
        </div>
        <div className="atr-detail-body">
          {self && !canReviewOwn && selected.reviewStatus === 'Pending' ? (
            <Alert type="info" showIcon icon={<LockOutlined />} message={t('attendanceReview.selfLockedDetail')} />
          ) : null}
          {renderTimeline(preview)}
          {renderChanges(selected, preview)}
          {renderTypeSpecific(selected, editable)}
          {renderAfterHours(selected, preview)}
          {selected.reviewStatus === 'Pending' ? (
            <section className="atr-section">
              <div className="atr-section-head">
                <strong>{t('attendanceReview.detail.remark')}</strong>
                <span className="atr-muted">{t('attendanceReview.detail.remarkHint')}</span>
              </div>
              <Input.TextArea
                rows={3}
                value={remark}
                disabled={!editable}
                placeholder={t('attendanceReview.detail.remarkPlaceholder')}
                onChange={(event) => setRemark(event.target.value)}
              />
            </section>
          ) : (
            <section className="atr-section">
              <strong>{t('attendanceReview.detail.reviewResult')}</strong>
              <div>
                {t(`attendanceReview.status.${selected.reviewStatus}`)}
                {selected.reviewerName ? ` · ${selected.reviewerName}` : ''}
                {selected.reviewedAt ? ` · ${dayjs(selected.reviewedAt).format('YYYY-MM-DD HH:mm')}` : ''}
              </div>
              {selected.reviewRemark ? <div className="atr-muted">{selected.reviewRemark}</div> : null}
            </section>
          )}
        </div>
        <div className="atr-detail-foot">
          <Button disabled={index <= 0} onClick={() => setSelectedGuid(visibleItems[index - 1]?.approvalGuid)}>{t('attendanceReview.actions.previous')}</Button>
          {selected.reviewStatus === 'Pending' ? (
            <Space>
              <Button danger disabled={!editable} loading={submitting} onClick={() => void handleReview('reject')}>
                {category === 'PublicHolidayPay'
                  ? t('attendanceReview.actions.notApplicable')
                  : category === 'MissingClockOut'
                    ? t('attendanceReview.actions.excludeSegment')
                    : t('attendanceReview.actions.reject')}
              </Button>
              <Button
                type="primary"
                disabled={!editable || (category === 'MissingClockOut' && !access.canAdjustAttendancePunches)}
                loading={submitting}
                onClick={() => void handleReview('approve')}
              >
                {category === 'MissingClockOut' ? t('attendanceReview.actions.closeMissingClockOut') : t('attendanceReview.actions.approveNext')}
              </Button>
            </Space>
          ) : (
            <Button disabled={index < 0 || index >= visibleItems.length - 1} onClick={() => setSelectedGuid(visibleItems[index + 1]?.approvalGuid)}>
              {t('attendanceReview.actions.next')}
            </Button>
          )}
        </div>
      </div>
    )
  }

  const allBatchChecked = batchableGuids.length > 0 && batchableGuids.every((guid) => checkedGuids.includes(guid))
  const tabItems = [
    { key: 'pending', label: <span>{t('attendanceReview.tabs.pending')} <span className="atr-tab-count">{pendingTotal}</span></span> },
    { key: 'processed', label: t('attendanceReview.tabs.processed') },
    ...(canReviewOwn ? [{ key: 'self', label: t('attendanceReview.tabs.self') }] : []),
    { key: 'all', label: t('attendanceReview.tabs.all') },
  ]

  return (
    <div className="atr-page">
      <div className="atr-header">
        <div>
          <Typography.Title level={4} className="atr-title">{t('attendanceReview.title')}</Typography.Title>
          {loadedAt && view === 'pending' ? (
            <div className="atr-muted"><SyncOutlined /> {t('attendanceReview.reconciledHint', { time: loadedAt.format('HH:mm') })}</div>
          ) : null}
        </div>
        <Space wrap>
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            style={{ minWidth: 180 }}
            placeholder={t('attendanceReview.allStores')}
            options={storeOptions}
            value={storeCode}
            onChange={setStoreCode}
          />
          <DatePicker.RangePicker
            allowClear={false}
            value={range}
            onChange={(value) => {
              if (value?.[0] && value[1]) setRange([value[0], value[1]])
            }}
          />
          <Input
            allowClear
            prefix={<SearchOutlined />}
            placeholder={t('attendanceReview.searchPlaceholder')}
            value={keywordInput}
            onChange={(event) => {
              setKeywordInput(event.target.value)
              if (!event.target.value) setKeyword('')
            }}
            onPressEnter={() => setKeyword(keywordInput.trim())}
            style={{ width: 180 }}
          />
        </Space>
      </div>

      <Tabs activeKey={view} items={tabItems} onChange={(key) => {
        setView(key as AttendanceReviewView)
        setCategory('all')
      }} />

      <div className="atr-workbench">
        <div className="atr-list">
          <div className="atr-filter-bar">
            <Space wrap size={8}>
              <Button size="small" type={category === 'all' ? 'primary' : 'default'} ghost={category === 'all'} onClick={() => setCategory('all')}>
                {t('attendanceReview.category.all')} {items.length}
              </Button>
              {REVIEW_CATEGORIES.filter((value) => categoryCounts.get(value)).map((value) => (
                <Button key={value} size="small" type={category === value ? 'primary' : 'default'} ghost={category === value} onClick={() => setCategory(value)}>
                  {categoryLabel(value)} {categoryCounts.get(value)}
                </Button>
              ))}
            </Space>
            <Button type="text" size="small" onClick={() => setSortAsc((value) => !value)}>
              {t('attendanceReview.sortByWorkDate')} {sortAsc ? <ArrowUpOutlined /> : <ArrowDownOutlined />}
            </Button>
          </div>
          {view === 'pending' ? (
            <div className="atr-batch-bar">
              <Checkbox
                checked={allBatchChecked}
                indeterminate={!allBatchChecked && checkedGuids.length > 0}
                disabled={!batchableGuids.length}
                onChange={(event) => setCheckedGuids(event.target.checked ? batchableGuids : [])}
              >
                {t('attendanceReview.batch.selected', { count: checkedGuids.length })}
              </Checkbox>
              <Button type="primary" size="small" disabled={!checkedGuids.length} loading={submitting} onClick={() => void runBatch('approve')}>
                {t('attendanceReview.batch.approve')}
              </Button>
              <Button danger size="small" disabled={!checkedGuids.length} onClick={() => {
                setBatchRemark('')
                setBatchRejectOpen(true)
              }}>
                {t('attendanceReview.batch.reject')}
              </Button>
              <span className="atr-muted atr-small">{t('attendanceReview.batch.hint')}</span>
            </div>
          ) : null}
          <Spin spinning={loading}>
            <MeasuredTable
              metricId="pos-admin.attendance-review.list"
              rowKey="approvalGuid"
              size="middle"
              columns={columns}
              dataSource={visibleItems}
              pagination={false}
              scroll={{ x: 760 }}
              locale={{ emptyText: <Empty description={t('attendanceReview.empty')} /> }}
              rowSelection={view === 'pending' ? {
                selectedRowKeys: checkedGuids,
                onChange: (keys) => setCheckedGuids(keys as string[]),
                getCheckboxProps: (item) => ({ disabled: !canSelectForBatch(item, currentUserGuid) }),
                hideSelectAll: true,
              } : undefined}
              rowClassName={(item) => [
                item.approvalGuid === selected?.approvalGuid ? 'atr-row-active' : '',
                isSelfRequest(item, currentUserGuid) && item.reviewStatus === 'Pending' && !canReviewOwn ? 'atr-row-locked' : '',
              ].join(' ')}
              onRow={(item) => ({ onClick: () => setSelectedGuid(item.approvalGuid) })}
            />
          </Spin>
        </div>
        {renderDetail()}
      </div>

      <Modal
        open={batchRejectOpen}
        title={t('attendanceReview.batch.rejectTitle', { count: checkedGuids.length })}
        okText={t('attendanceReview.batch.reject')}
        okButtonProps={{ danger: true, disabled: !batchRemark.trim(), loading: submitting }}
        onCancel={() => setBatchRejectOpen(false)}
        onOk={async () => {
          await runBatch('reject', batchRemark.trim())
          setBatchRejectOpen(false)
        }}
      >
        <Input.TextArea rows={3} value={batchRemark} placeholder={t('attendanceReview.validation.remarkRequired')} onChange={(event) => setBatchRemark(event.target.value)} />
      </Modal>
    </div>
  )
}
