import {
  CalendarOutlined,
  CheckOutlined,
  CloseOutlined,
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  EnvironmentOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  DatePicker,
  Descriptions,
  Drawer,
  Empty,
  Form,
  Input,
  InputNumber,
  Modal,
  Select,
  Space,
  Switch,
  Tabs,
  Tag,
  Timeline,
  TimePicker,
  Tooltip,
  Typography,
  message,
} from 'antd'
import enGB from 'antd/es/date-picker/locale/en_GB'
import type { ColumnsType } from 'antd/es/table'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import 'dayjs/locale/en-gb'
import isoWeek from 'dayjs/plugin/isoWeek'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  approveAttendanceApproval,
  batchUpsertAttendanceHolidays,
  createAttendanceHoliday,
  createAttendanceSchedule,
  deleteAttendanceHoliday,
  deleteAttendanceSchedule,
  getAttendanceAvailability,
  getAttendanceHolidays,
  getAttendanceLocationSamples,
  getAttendancePunches,
  getAttendanceScheduleWeek,
  getAttendanceRecords,
  getAttendanceSettings,
  getPendingAttendanceApprovals,
  publishAttendanceScheduleWeek,
  rejectAttendanceApproval,
  syncAttendanceHolidays,
  updateAttendanceHoliday,
  updateAttendanceSchedule,
  updateAttendanceSettings,
  createMyAttendancePunchAdjustment,
  previewMyAttendancePunchAdjustment,
} from '../../../services/scheduleAttendanceService'
import { getActiveStores } from '../../../services/storeService'
import { getStoreUsersGrid } from '../../../services/storeUserService'
import { useAuthStore } from '../../../store/auth'
import type { StoreUserListDto } from '../../../services/storeUserService'
import type {
  AttendanceApprovalDto,
  AttendanceAvailabilityDto,
  AttendanceHolidayBusinessStatus,
  AttendanceLocationSampleDto,
  AttendancePunchDto,
  AttendancePunchStatus,
  AttendanceReviewStatus,
  AttendanceScheduleDto,
  AttendanceScheduleStatus,
  AttendanceSettingsDto,
  AttendanceStoreHolidayDto,
  BatchUpsertAttendanceHolidayPayload,
  SaveAttendanceHolidayPayload,
  SaveAttendanceSchedulePayload,
  SaveAttendanceSettingsPayload,
  SaveAttendancePunchAdjustmentPayload,
  AttendancePunchAdjustmentPreviewDto,
} from '../../../types/scheduleAttendance'
import {
  buildAttendanceRecordSummary,
  buildLocalPunchAdjustmentPreview,
  validateOvertimeApproval,
  resolveSegmentPunchTime,
  deriveOriginalPunchGuid,
  getPunchAdjustmentOptions,
  getPunchAdjustmentPayloadSnapshot,
  isLatestMatchingPunchAdjustmentPreview,
  getDefaultPunchAdjustmentMode,
  resolvePunchAdjustmentOriginalGuid,
  canAdjustOwnAttendanceRecord,
  getProposedAdjustmentPunchStatus,
  isKnownAttendanceApprovalSourceType,
  getSupplementalAttendanceApprovalDetail,
  getAttendanceWeekdayIndex,
  insertAttendanceWeekday,
  insertAttendanceWeekdaysInText,
} from './attendanceRecordLogic'
import type { LocalPunchAdjustmentPreview } from './attendanceRecordLogic'
import AttendanceLocationTrajectoryMap from './AttendanceLocationTrajectoryMap'
import {
  buildAttendanceLocationTrajectory,
  normalizeAttendanceUtcText,
  splitAttendanceSampleDateRange,
} from './attendanceLocationTrajectoryLogic'
import {
  buildStoreOptionsFromUserStores,
  filterStoreOptionsByManagedCodes,
  isStoreCodeInManagedScope,
  shouldSkipStoreQueryForScope,
} from '../../../utils/managedStoreScope'
import { MeasuredTable } from '../../../components/MeasuredTable'

dayjs.extend(isoWeek)

type TabKey = 'schedules' | 'records' | 'availability' | 'punches' | 'approvals' | 'holidays' | 'settings'
type ReviewAction = 'approve' | 'reject'

interface AttendanceMapPreview {
  title: string
  latitude: number
  longitude: number
  accuracy?: number
  capturedAt?: string
  timeZone?: string
}

interface ScheduleRow {
  rowKey: string
  storeCode: string
  storeName?: string
  userGuid: string
  userName?: string
  schedulesByDate: Record<string, AttendanceScheduleDto[]>
}

interface ScheduleFormValues {
  storeCode: string
  userGuid: string
  workDate: Dayjs
  timeRange: [Dayjs, Dayjs]
  status?: AttendanceScheduleStatus
  remark?: string
}

interface HolidayFormValues {
  storeCode?: string
  storeCodes?: string[]
  holidayDate: Dayjs
  holidayName: string
  businessStatus: AttendanceHolidayBusinessStatus
  businessTime?: [Dayjs, Dayjs]
  isPaidHoliday?: boolean
  remark?: string
}

interface BatchHolidayFormValues {
  storeCodes: string[]
  holidayDate: Dayjs
  holidayName: string
  businessStatus: AttendanceHolidayBusinessStatus
  businessTime?: [Dayjs, Dayjs]
  isPaidHoliday?: boolean
  remark?: string
}

interface PunchAdjustmentFormValues {
  adjustmentMode: 'create' | 'replace'
  punchType: 'ClockIn' | 'ClockOut'
  originalPunchGuid?: string
  requestedPunchTimeLocal: Dayjs
  reason: string
}

interface ListState<T> {
  loading: boolean
  items: T[]
  total: number
  page: number
  pageSize: number
}

const initialListState = {
  loading: false,
  items: [],
  total: 0,
  page: 1,
  pageSize: 20,
}

const ATTENDANCE_LOCATION_SAMPLE_LIMIT = 1000
const ATTENDANCE_LOCATION_SAMPLES_TRUNCATED = 'ATTENDANCE_LOCATION_SAMPLES_TRUNCATED'

function formatDate(value?: string) {
  return value ? dayjs(value).format('YYYY-MM-DD') : '--'
}

function formatDateTime(value?: string) {
  return value ? dayjs(value).format('YYYY-MM-DD HH:mm') : '--'
}

function formatDateInTimeZone(value: string, timeZone?: string) {
  const normalized = normalizeAttendanceUtcText(value)
  if (!normalized) return ''
  const parsed = new Date(normalized)
  if (Number.isNaN(parsed.getTime())) return ''
  if (!timeZone) return parsed.toISOString().slice(0, 10)

  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).formatToParts(parsed)
    const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((item) => item.type === type)?.value ?? ''
    return `${part('year')}-${part('month')}-${part('day')}`
  } catch {
    return parsed.toISOString().slice(0, 10)
  }
}

function formatDateTimeInTimeZone(value?: string, timeZone?: string) {
  if (!value) return '--'
  const normalized = normalizeAttendanceUtcText(value)
  if (!normalized) return '--'
  const parsed = new Date(normalized)
  if (Number.isNaN(parsed.getTime())) return '--'
  if (!timeZone) return `${parsed.toISOString().slice(0, 16).replace('T', ' ')} UTC`

  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
    }).formatToParts(parsed)
    const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((item) => item.type === type)?.value ?? ''
    return `${part('year')}-${part('month')}-${part('day')} ${part('hour')}:${part('minute')}`
  } catch {
    return `${parsed.toISOString().slice(0, 16).replace('T', ' ')} UTC`
  }
}

function formatStoredLocalDateTime(value?: string) {
  if (!value) return '--'
  const match = value.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/)
  return match ? `${match[1]} ${match[2]}` : value
}

function formatTime(value?: string) {
  if (!value) return '--'
  const parsed = dayjs(value, ['HH:mm:ss', 'HH:mm'])
  return parsed.isValid() ? parsed.format('HH:mm') : value
}

function toDayjsTime(value?: string) {
  if (!value) return undefined
  const parsed = dayjs(value, ['HH:mm:ss', 'HH:mm'])
  return parsed.isValid() ? parsed : undefined
}

function getIsoWeekStart(value: Dayjs) {
  return value.isoWeekday(1).startOf('day')
}

function getIsoWeekEnd(value: Dayjs) {
  return getIsoWeekStart(value).add(6, 'day')
}

function buildPunchAdjustmentPayload(
  target: AttendanceScheduleDto,
  values: PunchAdjustmentFormValues,
): SaveAttendancePunchAdjustmentPayload {
  return {
    storeCode: target.storeCode,
    scheduleGuid: target.scheduleGuid,
    originalPunchGuid: resolvePunchAdjustmentOriginalGuid(values.adjustmentMode, values.originalPunchGuid),
    punchType: values.punchType,
    requestedPunchTimeLocal: values.requestedPunchTimeLocal.format('YYYY-MM-DDTHH:mm:ss'),
    reason: values.reason.trim(),
  }
}

export default function ScheduleAttendancePage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)
  const managedStoreCodes = access.managedStoreCodes?.()
  const managedStoreCodeKey = managedStoreCodes?.join(',') ?? 'all'
  const visibleStoreCodes = access.visibleStoreCodes?.()
  const visibleStoreCodeKey = visibleStoreCodes?.join(',') ?? 'all'
  const [activeTab, setActiveTab] = useState<TabKey>('schedules')
  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [storeCode, setStoreCode] = useState<string | undefined>()
  const [userGuid, setUserGuid] = useState<string | undefined>()
  const [selectedWeek, setSelectedWeek] = useState<Dayjs>(dayjs())
  const [dateRange, setDateRange] = useState<[Dayjs, Dayjs] | null>([getIsoWeekStart(dayjs()), getIsoWeekEnd(dayjs())])
  const [schedules, setSchedules] = useState<ListState<AttendanceScheduleDto>>(initialListState)
  const [records, setRecords] = useState<ListState<AttendanceScheduleDto>>(initialListState)
  const [storeUsers, setStoreUsers] = useState<StoreUserListDto[]>([])
  const [storeUsersLoading, setStoreUsersLoading] = useState(false)
  const [scheduleHolidays, setScheduleHolidays] = useState<AttendanceStoreHolidayDto[]>([])
  const [scheduleHolidaysLoading, setScheduleHolidaysLoading] = useState(false)
  const [availability, setAvailability] = useState<ListState<AttendanceAvailabilityDto>>(initialListState)
  const [punches, setPunches] = useState<ListState<AttendancePunchDto>>(initialListState)
  const [approvals, setApprovals] = useState<ListState<AttendanceApprovalDto>>(initialListState)
  const [holidays, setHolidays] = useState<ListState<AttendanceStoreHolidayDto>>(initialListState)
  const [settings, setSettings] = useState<AttendanceSettingsDto | null>(null)
  const [settingsLoading, setSettingsLoading] = useState(false)
  const [scheduleDrawerOpen, setScheduleDrawerOpen] = useState(false)
  const [editingSchedule, setEditingSchedule] = useState<AttendanceScheduleDto | null>(null)
  const [holidayDrawerOpen, setHolidayDrawerOpen] = useState(false)
  const [editingHoliday, setEditingHoliday] = useState<AttendanceStoreHolidayDto | null>(null)
  const [batchHolidayDrawerOpen, setBatchHolidayDrawerOpen] = useState(false)
  const [reviewModalOpen, setReviewModalOpen] = useState(false)
  const [reviewTarget, setReviewTarget] = useState<AttendanceApprovalDto | null>(null)
  const [reviewAction, setReviewAction] = useState<ReviewAction>('approve')
  const [adjustmentTarget, setAdjustmentTarget] = useState<AttendanceScheduleDto | null>(null)
  const [adjustmentPreview, setAdjustmentPreview] = useState<LocalPunchAdjustmentPreview | null>(null)
  const [serverAdjustmentPreview, setServerAdjustmentPreview] = useState<AttendancePunchAdjustmentPreviewDto | null>(null)
  const [previewPayloadSnapshot, setPreviewPayloadSnapshot] = useState<string | null>(null)
  const [adjustmentModalOpen, setAdjustmentModalOpen] = useState(false)
  const [mapPreview, setMapPreview] = useState<AttendanceMapPreview | null>(null)
  const [sampleDrawerOpen, setSampleDrawerOpen] = useState(false)
  const [sampleLoading, setSampleLoading] = useState(false)
  const [trajectoryResult, setTrajectoryResult] = useState<ReturnType<typeof buildAttendanceLocationTrajectory> | null>(null)
  const [trajectoryMapVisible, setTrajectoryMapVisible] = useState(false)
  const [sampleTitle, setSampleTitle] = useState('')
  const [sampleTimeZone, setSampleTimeZone] = useState('')
  const [saving, setSaving] = useState(false)
  const [adjustmentPreviewLoading, setAdjustmentPreviewLoading] = useState(false)
  const [publishing, setPublishing] = useState(false)
  const [syncingHolidays, setSyncingHolidays] = useState(false)
  const [scheduleForm] = Form.useForm<ScheduleFormValues>()
  const [holidayForm] = Form.useForm<HolidayFormValues>()
  const [batchHolidayForm] = Form.useForm<BatchHolidayFormValues>()
  const [reviewForm] = Form.useForm<{ reviewRemark?: string; approvedOvertimeMinutes?: number }>()
  const [adjustmentForm] = Form.useForm<PunchAdjustmentFormValues>()
  const [settingsForm] = Form.useForm<SaveAttendanceSettingsPayload>()
  const previewRequestIdRef = useRef(0)
  const sampleRequestIdRef = useRef(0)
  const selectedAdjustmentPunchType = Form.useWatch('punchType', adjustmentForm)
  const selectedAdjustmentMode = Form.useWatch('adjustmentMode', adjustmentForm)
  const getBusinessWeekdayLabel = useCallback((displayText: string) => {
    const weekdayIndex = getAttendanceWeekdayIndex(displayText)
    return weekdayIndex === undefined
      ? undefined
      : t(`posAdmin.scheduleAttendance.weekdays.${weekdayIndex}`)
  }, [t])
  const formatBusinessDisplayText = useCallback((displayText: string) => (
    insertAttendanceWeekday(displayText, getBusinessWeekdayLabel(displayText))
  ), [getBusinessWeekdayLabel])
  const formatBusinessDetailText = useCallback((detailText: string) => (
    insertAttendanceWeekdaysInText(detailText, getBusinessWeekdayLabel)
  ), [getBusinessWeekdayLabel])
  const formatRecordMinutes = useCallback((minutes?: number) => (
    typeof minutes === 'number'
      ? t('posAdmin.scheduleAttendance.recordLabels.minutes', { minutes })
      : '--'
  ), [t])
  const renderBusinessDisplay = useCallback((displayText: string) => {
    const weekdayLabel = getBusinessWeekdayLabel(displayText)
    return (
      <Space direction="vertical" size={0}>
        <Typography.Text>{displayText}</Typography.Text>
        {weekdayLabel ? (
          <Typography.Text type="secondary" style={{ fontSize: 12, lineHeight: 1.25 }}>
            {weekdayLabel}
          </Typography.Text>
        ) : null}
      </Space>
    )
  }, [getBusinessWeekdayLabel])
  const trajectoryMapPoints = useMemo(
    () => trajectoryResult?.ok
      ? trajectoryResult.mapPoints.map((point) => ({
        key: point.key,
        kind: point.kind,
        latitude: point.latitude!,
        longitude: point.longitude!,
        accuracy: point.accuracy,
        label: `${t(`posAdmin.scheduleAttendance.messages.trajectoryPoint${point.kind}`)} · ${
          point.kind === 'Sample' || !point.displayTimeSource
            ? formatBusinessDisplayText(formatDateTimeInTimeZone(point.capturedAtUtc, sampleTimeZone))
            : formatBusinessDisplayText(formatStoredLocalDateTime(point.displayTimeSource))
        }`,
      }))
      : [],
    [formatBusinessDisplayText, sampleTimeZone, t, trajectoryResult],
  )
  const trajectoryMapPointKeys = useMemo(
    () => new Set(trajectoryMapPoints.map((point) => point.key)),
    [trajectoryMapPoints],
  )
  const adjustmentPunchOptions = useMemo(
    () => adjustmentTarget && selectedAdjustmentPunchType
      ? getPunchAdjustmentOptions(adjustmentTarget, selectedAdjustmentPunchType).map((option) => ({
        value: option.value,
        label: `#${option.segmentIndex} / ${formatBusinessDisplayText(formatDateTime(option.punchTimeLocal))} / ${option.value}`,
      }))
      : [],
    [adjustmentTarget, formatBusinessDisplayText, selectedAdjustmentPunchType],
  )
  const proposedAdjustmentPunchStatus = useMemo(
    () => adjustmentPreview && serverAdjustmentPreview
      ? getProposedAdjustmentPunchStatus(
        serverAdjustmentPreview,
        adjustmentPreview.punchType,
        adjustmentPreview.requestedPunchTimeLocal,
      )
      : undefined,
    [adjustmentPreview, serverAdjustmentPreview],
  )

  const storeNameMap = useMemo(() => new Map(storeOptions.map((item) => [item.value, item.label])), [storeOptions])
  const externalMapUrl = useMemo(() => {
    if (!mapPreview) {
      return ''
    }
    return `https://maps.google.com/?q=${mapPreview.latitude},${mapPreview.longitude}`
  }, [mapPreview])
  const editableStoreOptions = useMemo(
    () => filterStoreOptionsByManagedCodes(storeOptions, managedStoreCodes),
    [managedStoreCodeKey, storeOptions],
  )
  const canEditSelectedStore = isStoreCodeInManagedScope(storeCode, managedStoreCodes)
  const canEditStoreCode = useCallback(
    (targetStoreCode?: string) => isStoreCodeInManagedScope(targetStoreCode, managedStoreCodes),
    [managedStoreCodeKey],
  )
  const canViewStoreCode = useCallback(
    (targetStoreCode?: string) => isStoreCodeInManagedScope(targetStoreCode, visibleStoreCodes),
    [visibleStoreCodeKey],
  )
  const shouldSkipVisibleStoreQuery = useCallback(
    () => shouldSkipStoreQueryForScope(storeCode, visibleStoreCodes),
    [storeCode, visibleStoreCodeKey],
  )
  const weekStart = useMemo(() => getIsoWeekStart(selectedWeek), [selectedWeek])
  const weekEnd = useMemo(() => getIsoWeekEnd(selectedWeek), [selectedWeek])
  const weekDays = useMemo(() => Array.from({ length: 7 }, (_, index) => weekStart.add(index, 'day')), [weekStart])
  const formatWeekLabel = useCallback((value: Dayjs) => {
    const start = getIsoWeekStart(value)
    return t('posAdmin.scheduleAttendance.weekTable.yearWeek', {
      year: start.isoWeekYear(),
      week: String(start.isoWeek()).padStart(2, '0'),
    })
  }, [t])
  const selectedWeekLabel = useMemo(() => formatWeekLabel(weekStart), [formatWeekLabel, weekStart])

  const queryParams = useCallback((page: number, pageSize: number) => ({
    page,
    pageSize,
    storeCode,
    userGuid: userGuid?.trim() || undefined,
    weekStartDate: dateRange?.[0]?.format('YYYY-MM-DD'),
    fromDate: dateRange?.[0]?.format('YYYY-MM-DD'),
    toDate: dateRange?.[1]?.format('YYYY-MM-DD'),
  }), [dateRange, storeCode, userGuid])

  const scheduleQueryParams = useCallback((page: number, pageSize: number) => ({
    page,
    pageSize,
    storeCode,
    userGuid: userGuid?.trim() || undefined,
    weekStartDate: weekStart.format('YYYY-MM-DD'),
    fromDate: weekStart.format('YYYY-MM-DD'),
    toDate: weekEnd.format('YYYY-MM-DD'),
  }), [storeCode, userGuid, weekEnd, weekStart])

  const loadSchedules = useCallback(async (page = schedules.page, pageSize = schedules.pageSize) => {
    if (shouldSkipVisibleStoreQuery()) {
      setSchedules({ ...initialListState, page, pageSize })
      return
    }

    setSchedules((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      const result = await getAttendanceScheduleWeek(scheduleQueryParams(page, pageSize))
      setSchedules({ loading: false, items: result, total: result.length, page, pageSize })
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadSchedulesFailed'))
      setSchedules((prev) => ({ ...prev, loading: false }))
    }
  }, [scheduleQueryParams, schedules.page, schedules.pageSize, shouldSkipVisibleStoreQuery, t])

  const loadRecords = useCallback(async (page = records.page, pageSize = records.pageSize) => {
    if (!access.canViewAttendancePunches) {
      setRecords({ ...initialListState, page, pageSize })
      return
    }
    if (shouldSkipVisibleStoreQuery()) {
      setRecords({ ...initialListState, page, pageSize })
      return
    }

    setRecords((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      const result = await getAttendanceRecords(queryParams(page, pageSize))
      setRecords({ loading: false, items: result.items, total: result.total, page: result.page, pageSize: result.pageSize })
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadRecordsFailed', '考勤记录加载失败'))
      setRecords((prev) => ({ ...prev, loading: false }))
    }
  }, [access.canViewAttendancePunches, queryParams, records.page, records.pageSize, shouldSkipVisibleStoreQuery, t])

  const loadStoreUsers = useCallback(async () => {
    if (!storeCode || !canViewStoreCode(storeCode)) {
      setStoreUsers([])
      return
    }

    setStoreUsersLoading(true)
    try {
      const result = await getStoreUsersGrid({
        storeCode,
        keyword: userGuid?.trim() || undefined,
        status: 1,
      })
      setStoreUsers(result.items)
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadStoreUsersFailed'))
      setStoreUsers([])
    } finally {
      setStoreUsersLoading(false)
    }
  }, [canViewStoreCode, storeCode, t, userGuid])

  const loadScheduleHolidays = useCallback(async () => {
    if (!storeCode || !canViewStoreCode(storeCode)) {
      setScheduleHolidays([])
      return
    }

    setScheduleHolidaysLoading(true)
    try {
      const result = await getAttendanceHolidays({
        storeCode,
        fromDate: weekStart.format('YYYY-MM-DD'),
        toDate: weekEnd.format('YYYY-MM-DD'),
      })
      setScheduleHolidays(result.items)
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadHolidaysFailed'))
      setScheduleHolidays([])
    } finally {
      setScheduleHolidaysLoading(false)
    }
  }, [canViewStoreCode, storeCode, t, weekEnd, weekStart])

  const loadAvailability = useCallback(async (page = availability.page, pageSize = availability.pageSize) => {
    if (shouldSkipVisibleStoreQuery()) {
      setAvailability({ ...initialListState, page, pageSize })
      return
    }

    setAvailability((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      const result = await getAttendanceAvailability(queryParams(page, pageSize))
      setAvailability({ loading: false, items: result.items, total: result.total, page: result.page, pageSize: result.pageSize })
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadAvailabilityFailed'))
      setAvailability((prev) => ({ ...prev, loading: false }))
    }
  }, [availability.page, availability.pageSize, queryParams, shouldSkipVisibleStoreQuery, t])

  const loadPunches = useCallback(async (page = punches.page, pageSize = punches.pageSize) => {
    if (shouldSkipVisibleStoreQuery()) {
      setPunches({ ...initialListState, page, pageSize })
      return
    }

    setPunches((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      const result = await getAttendancePunches(queryParams(page, pageSize))
      setPunches({ loading: false, items: result.items, total: result.total, page: result.page, pageSize: result.pageSize })
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadPunchesFailed'))
      setPunches((prev) => ({ ...prev, loading: false }))
    }
  }, [punches.page, punches.pageSize, queryParams, shouldSkipVisibleStoreQuery, t])

  const loadApprovals = useCallback(async (page = approvals.page, pageSize = approvals.pageSize) => {
    if (shouldSkipVisibleStoreQuery()) {
      setApprovals({ ...initialListState, page, pageSize })
      return
    }

    setApprovals((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      const result = await getPendingAttendanceApprovals(queryParams(page, pageSize))
      setApprovals({ loading: false, items: result.items, total: result.total, page: result.page, pageSize: result.pageSize })
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadApprovalsFailed'))
      setApprovals((prev) => ({ ...prev, loading: false }))
    }
  }, [approvals.page, approvals.pageSize, queryParams, shouldSkipVisibleStoreQuery, t])

  const loadHolidays = useCallback(async (page = holidays.page, pageSize = holidays.pageSize) => {
    if (shouldSkipVisibleStoreQuery()) {
      setHolidays({ ...initialListState, page, pageSize })
      return
    }

    setHolidays((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      const result = await getAttendanceHolidays(queryParams(page, pageSize))
      setHolidays({ loading: false, items: result.items, total: result.total, page: result.page, pageSize: result.pageSize })
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadHolidaysFailed'))
      setHolidays((prev) => ({ ...prev, loading: false }))
    }
  }, [holidays.page, holidays.pageSize, queryParams, shouldSkipVisibleStoreQuery, t])

  const loadSettings = useCallback(async () => {
    setSettingsLoading(true)
    try {
      const result = await getAttendanceSettings()
      setSettings(result)
      settingsForm.setFieldsValue(result)
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadSettingsFailed'))
    } finally {
      setSettingsLoading(false)
    }
  }, [settingsForm, t])

  const loadActiveTab = useCallback((page = 1) => {
    if (activeTab === 'schedules') {
      void loadSchedules(page)
      void loadStoreUsers()
      void loadScheduleHolidays()
    }
    if (activeTab === 'records' && access.canViewAttendancePunches) void loadRecords(page)
    if (activeTab === 'availability') void loadAvailability(page)
    if (activeTab === 'punches') void loadPunches(page)
    if (activeTab === 'approvals') void loadApprovals(page)
    if (activeTab === 'holidays') void loadHolidays(page)
    if (activeTab === 'settings') void loadSettings()
  }, [access.canViewAttendancePunches, activeTab, loadApprovals, loadAvailability, loadHolidays, loadPunches, loadRecords, loadScheduleHolidays, loadSchedules, loadSettings, loadStoreUsers])

  useEffect(() => {
    if (visibleStoreCodes !== null) {
      const stores = buildStoreOptionsFromUserStores(currentUser?.stores)
      setStoreOptions(stores)
      if (storeCode && !stores.some((store) => store.value === storeCode)) {
        setStoreCode(undefined)
      }
      return
    }

    void getActiveStores().then(setStoreOptions).catch((error) => {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadStoresFailed'))
    })
  }, [currentUser?.stores, storeCode, t, visibleStoreCodeKey])

  useEffect(() => {
    loadActiveTab(1)
  }, [activeTab, storeCode, dateRange, selectedWeek])

  useEffect(() => {
    if (activeTab === 'records' && !access.canViewAttendancePunches) setActiveTab('schedules')
  }, [access.canViewAttendancePunches, activeTab])

  const statusLabel = useCallback((type: 'schedule' | 'punch' | 'review' | 'scheduleState' | 'segment' | 'overtimeApproval', value?: string) => {
    if (!value) return '--'
    return t(`posAdmin.scheduleAttendance.status.${type}.${value}`, value)
  }, [t])

  const scheduleStatusTag = (value: AttendanceScheduleStatus) => (
    <Tag color={value === 'Active' ? 'green' : value === 'Draft' ? 'gold' : 'red'}>{statusLabel('schedule', value)}</Tag>
  )

  const punchStatusTag = (value: AttendancePunchStatus) => {
    const color = value === 'Normal' || value === 'Approved' ? 'green' : value === 'PendingApproval' ? 'orange' : 'red'
    return <Tag color={color}>{statusLabel('punch', value)}</Tag>
  }

  const reviewStatusTag = (value: AttendanceReviewStatus) => {
    const color = value === 'Approved' ? 'green' : value === 'Pending' ? 'orange' : value === 'Rejected' ? 'red' : 'default'
    return <Tag color={color}>{statusLabel('review', value)}</Tag>
  }

  const holidayStatusTag = (value: AttendanceHolidayBusinessStatus) => {
    const color = value === 'Open' ? 'green' : value === 'Partial' ? 'orange' : 'red'
    return <Tag color={color}>{t(`posAdmin.scheduleAttendance.status.holiday.${value}`, value)}</Tag>
  }

  const openScheduleDrawer = (record?: AttendanceScheduleDto, defaults?: Partial<ScheduleFormValues>) => {
    const targetStoreCode = record?.storeCode ?? defaults?.storeCode ?? storeCode
    if (!access.canEditAttendanceSchedule || !canEditStoreCode(targetStoreCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    setEditingSchedule(record ?? null)
    scheduleForm.resetFields()
    if (record) {
      scheduleForm.setFieldsValue({
        storeCode: record.storeCode,
        userGuid: record.userGuid,
        workDate: dayjs(record.workDate),
        timeRange: [toDayjsTime(record.startTime), toDayjsTime(record.endTime)].filter(Boolean) as [Dayjs, Dayjs],
        status: record.status,
        remark: record.remark,
      })
    } else {
      scheduleForm.setFieldsValue({
        storeCode,
        status: 'Draft',
        timeRange: [dayjs('09:00', 'HH:mm'), dayjs('17:00', 'HH:mm')],
        ...defaults,
      })
    }
    setScheduleDrawerOpen(true)
  }

  const saveSchedule = async () => {
    try {
      const values = await scheduleForm.validateFields()
      if (!access.canEditAttendanceSchedule || !canEditStoreCode(values.storeCode)) {
        message.error(t('message.noPermission', '无权操作该数据'))
        return
      }

      const payload: SaveAttendanceSchedulePayload = {
        storeCode: values.storeCode,
        userGuid: values.userGuid.trim(),
        workDate: values.workDate.format('YYYY-MM-DD'),
        startTime: values.timeRange[0].format('HH:mm'),
        endTime: values.timeRange[1].format('HH:mm'),
        status: editingSchedule ? values.status : 'Draft',
        remark: values.remark?.trim() || undefined,
      }
      setSaving(true)
      if (editingSchedule) {
        await updateAttendanceSchedule(editingSchedule.scheduleGuid, payload)
        message.success(t('message.updateSuccess'))
      } else {
        await createAttendanceSchedule(payload)
        message.success(t('message.createSuccess'))
      }
      setScheduleDrawerOpen(false)
      void loadSchedules()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.saveScheduleFailed'))
    } finally {
      setSaving(false)
    }
  }

  const confirmDeleteSchedule = (record: AttendanceScheduleDto) => {
    if (!access.canEditAttendanceSchedule || !canEditStoreCode(record.storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    Modal.confirm({
      title: t('posAdmin.scheduleAttendance.confirm.deleteSchedule'),
      okText: t('common.delete'),
      cancelText: t('common.cancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        await deleteAttendanceSchedule(record.scheduleGuid)
        message.success(t('message.deleteSuccess'))
        setScheduleDrawerOpen(false)
        void loadSchedules()
      },
    })
  }

  const openHolidayDrawer = (record?: AttendanceStoreHolidayDto) => {
    if (record && (!access.canEditAttendanceHoliday || !canEditStoreCode(record.storeCode))) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    setEditingHoliday(record ?? null)
    holidayForm.resetFields()
    if (record) {
      const openTime = toDayjsTime(record.openTime)
      const closeTime = toDayjsTime(record.closeTime)
      holidayForm.setFieldsValue({
        storeCode: record.storeCode,
        holidayDate: dayjs(record.holidayDate),
        holidayName: record.holidayName,
        businessStatus: record.businessStatus,
        businessTime: openTime && closeTime ? [openTime, closeTime] : undefined,
        isPaidHoliday: record.isPaidHoliday,
        remark: record.remark,
      })
    } else {
      holidayForm.setFieldsValue({
        storeCodes: storeCode && canEditStoreCode(storeCode) ? [storeCode] : [],
        businessStatus: 'Open',
        isPaidHoliday: true,
      })
    }
    setHolidayDrawerOpen(true)
  }

  const saveHoliday = async () => {
    try {
      const values = await holidayForm.validateFields()
      const targetStoreCodes = values.storeCodes?.filter(Boolean) ?? []
      const selectedStoreCodes = editingHoliday
        ? [values.storeCode].filter(Boolean)
        : targetStoreCodes
      if (
        !access.canEditAttendanceHoliday ||
        selectedStoreCodes.length === 0 ||
        selectedStoreCodes.some((item) => !canEditStoreCode(item))
      ) {
        message.error(t('message.noPermission', '无权操作该数据'))
        return
      }
      const payloadStoreCode = values.storeCode ?? targetStoreCodes[0]
      if (!payloadStoreCode) {
        message.error(t('message.noPermission', '无权操作该数据'))
        return
      }

      const payload: SaveAttendanceHolidayPayload = {
        storeCode: payloadStoreCode,
        holidayDate: values.holidayDate.format('YYYY-MM-DD'),
        holidayName: values.holidayName.trim(),
        businessStatus: values.businessStatus,
        openTime: values.businessTime?.[0]?.format('HH:mm'),
        closeTime: values.businessTime?.[1]?.format('HH:mm'),
        isPaidHoliday: values.isPaidHoliday ?? true,
        remark: values.remark?.trim() || undefined,
      }
      setSaving(true)
      if (editingHoliday) {
        await updateAttendanceHoliday(editingHoliday.holidayGuid, payload)
        message.success(t('message.updateSuccess'))
      } else {
        if (targetStoreCodes.length > 1) {
          const result = await batchUpsertAttendanceHolidays({
            storeCodes: targetStoreCodes,
            holidayDate: payload.holidayDate,
            holidayName: payload.holidayName,
            businessStatus: payload.businessStatus,
            openTime: payload.openTime,
            closeTime: payload.closeTime,
            isPaidHoliday: payload.isPaidHoliday,
            remark: payload.remark,
          })
          message.success(t('posAdmin.scheduleAttendance.messages.batchHolidaySuccess', {
            created: result.createdCount,
            updated: result.updatedCount,
          }))
        } else {
          await createAttendanceHoliday(payload)
          message.success(t('message.createSuccess'))
        }
      }
      setHolidayDrawerOpen(false)
      void loadHolidays()
      void loadScheduleHolidays()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.saveHolidayFailed'))
    } finally {
      setSaving(false)
    }
  }

  const openBatchHolidayDrawer = () => {
    if (!access.canEditAttendanceHoliday || !editableStoreOptions.length) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    batchHolidayForm.resetFields()
    batchHolidayForm.setFieldsValue({
      storeCodes: storeCode && canEditStoreCode(storeCode) ? [storeCode] : [],
      businessStatus: 'Open',
      isPaidHoliday: true,
    })
    setBatchHolidayDrawerOpen(true)
  }

  const saveBatchHoliday = async () => {
    try {
      const values = await batchHolidayForm.validateFields()
      if (
        !access.canEditAttendanceHoliday ||
        !values.storeCodes.length ||
        values.storeCodes.some((item) => !canEditStoreCode(item))
      ) {
        message.error(t('message.noPermission', '无权操作该数据'))
        return
      }

      const payload: BatchUpsertAttendanceHolidayPayload = {
        storeCodes: values.storeCodes,
        holidayDate: values.holidayDate.format('YYYY-MM-DD'),
        holidayName: values.holidayName.trim(),
        businessStatus: values.businessStatus,
        openTime: values.businessTime?.[0]?.format('HH:mm'),
        closeTime: values.businessTime?.[1]?.format('HH:mm'),
        isPaidHoliday: values.isPaidHoliday ?? true,
        remark: values.remark?.trim() || undefined,
      }
      setSaving(true)
      const result = await batchUpsertAttendanceHolidays(payload)
      message.success(t('posAdmin.scheduleAttendance.messages.batchHolidaySuccess', {
        created: result.createdCount,
        updated: result.updatedCount,
      }))
      setBatchHolidayDrawerOpen(false)
      void loadHolidays()
      void loadScheduleHolidays()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.saveHolidayFailed'))
    } finally {
      setSaving(false)
    }
  }

  const syncFutureHolidays = async () => {
    if (!storeCode) {
      message.warning(t('posAdmin.scheduleAttendance.messages.selectStoreBeforeHolidaySync'))
      return
    }
    if (!access.canEditAttendanceHoliday || !canEditStoreCode(storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    setSyncingHolidays(true)
    try {
      const result = await syncAttendanceHolidays({
        storeCode,
        daysAhead: 30,
      })
      message.success(t('posAdmin.scheduleAttendance.messages.holidaySyncSuccess', {
        synced: result.syncedCount,
        created: result.createdCount,
        updated: result.updatedCount,
        skipped: result.skippedCount,
      }))
      void loadHolidays()
      void loadScheduleHolidays()
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.holidaySyncFailed'))
    } finally {
      setSyncingHolidays(false)
    }
  }

  const confirmDeleteHoliday = (record: AttendanceStoreHolidayDto) => {
    if (!access.canEditAttendanceHoliday || !canEditStoreCode(record.storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    Modal.confirm({
      title: t('posAdmin.scheduleAttendance.confirm.deleteHoliday'),
      okText: t('common.delete'),
      cancelText: t('common.cancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        await deleteAttendanceHoliday(record.holidayGuid)
        message.success(t('message.deleteSuccess'))
        void loadHolidays()
      },
    })
  }

  const openReviewModal = (record: AttendanceApprovalDto, action: ReviewAction) => {
    if (!access.canReviewAttendance || !canEditStoreCode(record.storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    setReviewTarget(record)
    setReviewAction(action)
    reviewForm.resetFields()
    reviewForm.setFieldsValue({
      approvedOvertimeMinutes: action === 'reject' ? 0 : record.candidateOvertimeMinutes,
    })
    setReviewModalOpen(true)
  }

  const submitReview = async () => {
    if (!reviewTarget) return
    if (!access.canReviewAttendance || !canEditStoreCode(reviewTarget.storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    try {
      const values = await reviewForm.validateFields()
      const isOvertime = reviewTarget.sourceType === 'Overtime'
      if (isOvertime) {
        const validationError = validateOvertimeApproval({
          candidateMinutes: reviewTarget.candidateOvertimeMinutes ?? 0,
          approvedMinutes: reviewAction === 'reject' ? 0 : values.approvedOvertimeMinutes ?? 0,
          action: reviewAction,
          remark: values.reviewRemark,
        })
        if (validationError) {
          const validationMessages = {
            outOfRange: '批准加班必须在 0 到候选分钟之间',
            invalidIncrement: '批准加班必须为 15 分钟倍数',
            remarkRequired: '减少或拒绝候选加班时必须填写备注',
          }
          message.warning(t(`posAdmin.scheduleAttendance.validation.overtime.${validationError}`, validationMessages[validationError]))
          return
        }
      }
      setSaving(true)
      const payload = {
        reviewRemark: values.reviewRemark?.trim() || undefined,
        approvedOvertimeMinutes: isOvertime
          ? reviewAction === 'reject' ? 0 : values.approvedOvertimeMinutes
          : undefined,
      }
      if (reviewAction === 'approve') {
        await approveAttendanceApproval(reviewTarget.approvalGuid, payload)
      } else {
        await rejectAttendanceApproval(reviewTarget.approvalGuid, payload)
      }
      message.success(t('posAdmin.scheduleAttendance.messages.reviewSuccess'))
      setReviewModalOpen(false)
      void loadApprovals()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.reviewFailed'))
    } finally {
      setSaving(false)
    }
  }

  const openPunchAdjustmentModal = (record: AttendanceScheduleDto) => {
    if (!canAdjustOwnAttendanceRecord({
      isAdmin: access.isAdmin,
      isStoreManager: access.isStoreManager,
      isOwnSchedule: record.userGuid === currentUser?.userGUID,
      isManagedStore: canEditStoreCode(record.storeCode),
    })) {
      message.error(t('message.noPermission'))
      return
    }

    const punchType = record.hasMissingClockOut ? 'ClockOut' : 'ClockIn'
    const adjustmentMode = getDefaultPunchAdjustmentMode(record, punchType)
    const boundaryTime = punchType === 'ClockOut' ? record.endTime : record.startTime
    adjustmentForm.resetFields()
    adjustmentForm.setFieldsValue({
      punchType,
      adjustmentMode,
      originalPunchGuid: adjustmentMode === 'replace' ? deriveOriginalPunchGuid(record, punchType) : undefined,
      requestedPunchTimeLocal: dayjs(`${dayjs(record.workDate).format('YYYY-MM-DD')}T${boundaryTime}`),
    })
    previewRequestIdRef.current += 1
    setAdjustmentTarget(record)
    setAdjustmentPreview(null)
    setServerAdjustmentPreview(null)
    setPreviewPayloadSnapshot(null)
    setAdjustmentModalOpen(true)
  }

  const previewPunchAdjustment = async () => {
    if (!adjustmentTarget) return
    const requestId = ++previewRequestIdRef.current
    try {
      const values = await adjustmentForm.validateFields()
      const payload = buildPunchAdjustmentPayload(adjustmentTarget, values)
      const payloadSnapshot = getPunchAdjustmentPayloadSnapshot(payload)
      setAdjustmentPreview(null)
      setServerAdjustmentPreview(null)
      setPreviewPayloadSnapshot(null)
      setAdjustmentPreviewLoading(true)
      const serverPreview = await previewMyAttendancePunchAdjustment(payload)
      const currentValues = adjustmentForm.getFieldsValue(true) as PunchAdjustmentFormValues
      const currentPayloadSnapshot = currentValues.requestedPunchTimeLocal && currentValues.reason
        ? getPunchAdjustmentPayloadSnapshot(buildPunchAdjustmentPayload(adjustmentTarget, currentValues))
        : ''
      if (!isLatestMatchingPunchAdjustmentPreview({
        requestId,
        latestRequestId: previewRequestIdRef.current,
        previewPayloadSnapshot: payloadSnapshot,
        currentPayloadSnapshot,
      })) return
      if (!serverPreview.isValid) {
        message.warning(serverPreview.validationMessage || t('posAdmin.scheduleAttendance.messages.adjustmentPreviewInvalid'))
        setAdjustmentPreview(null)
        setServerAdjustmentPreview(serverPreview)
        return
      }
      if (!serverPreview.previewRevision) {
        message.error(t('posAdmin.scheduleAttendance.messages.previewRevisionMissing'))
        setAdjustmentPreview(null)
        setServerAdjustmentPreview(serverPreview)
        return
      }
      setServerAdjustmentPreview(serverPreview)
      setPreviewPayloadSnapshot(payloadSnapshot)
      setAdjustmentPreview(buildLocalPunchAdjustmentPreview(
        adjustmentTarget,
        values.punchType,
        values.requestedPunchTimeLocal.format('YYYY-MM-DDTHH:mm:ss'),
        values.adjustmentMode,
        resolvePunchAdjustmentOriginalGuid(values.adjustmentMode, values.originalPunchGuid),
      ))
    } catch (error) {
      if (requestId !== previewRequestIdRef.current) return
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.adjustmentPreviewFailed'))
    } finally {
      if (requestId === previewRequestIdRef.current) setAdjustmentPreviewLoading(false)
    }
  }

  const submitPunchAdjustment = async () => {
    if (!adjustmentTarget || !adjustmentPreview || !serverAdjustmentPreview?.isValid) return
    if (!serverAdjustmentPreview.previewRevision) {
      message.error(t('posAdmin.scheduleAttendance.messages.previewRevisionMissing'))
      return
    }
    try {
      const values = await adjustmentForm.validateFields()
      const payload = buildPunchAdjustmentPayload(adjustmentTarget, values)
      const currentPayloadSnapshot = getPunchAdjustmentPayloadSnapshot(payload)
      if (currentPayloadSnapshot !== previewPayloadSnapshot) {
        message.warning(t('posAdmin.scheduleAttendance.messages.previewExpired'))
        setAdjustmentPreview(null)
        setServerAdjustmentPreview(null)
        setPreviewPayloadSnapshot(null)
        return
      }
      setSaving(true)
      const result = await createMyAttendancePunchAdjustment({
        ...payload,
        previewRevision: serverAdjustmentPreview.previewRevision,
      })
      message.success(result.status === 'Applied'
        ? t('posAdmin.scheduleAttendance.messages.adjustmentApplied')
        : t('posAdmin.scheduleAttendance.messages.adjustmentSubmitted'))
      setAdjustmentModalOpen(false)
      setAdjustmentPreview(null)
      setServerAdjustmentPreview(null)
      setPreviewPayloadSnapshot(null)
      void loadRecords()
      void loadPunches()
      void loadApprovals()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.adjustmentFailed'))
    } finally {
      setSaving(false)
    }
  }

  const saveSettings = async () => {
    try {
      const values = await settingsForm.validateFields()
      setSettingsLoading(true)
      const result = await updateAttendanceSettings(values)
      setSettings(result)
      settingsForm.setFieldsValue(result)
      message.success(t('message.saveSuccess'))
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) return
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.saveSettingsFailed'))
    } finally {
      setSettingsLoading(false)
    }
  }

  const userCell = (record: { userName?: string; userGuid?: string; applicantName?: string; applicantUserGuid?: string }) => (
    <Space direction="vertical" size={0}>
      <Typography.Text>{record.userName || record.applicantName || '--'}</Typography.Text>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>{record.userGuid || record.applicantUserGuid || '--'}</Typography.Text>
    </Space>
  )

  const handlePublishWeek = async () => {
    if (!storeCode) {
      message.warning(t('posAdmin.scheduleAttendance.messages.selectStoreBeforePublish'))
      return
    }
    if (!access.canEditAttendanceSchedule || !canEditStoreCode(storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    setPublishing(true)
    try {
      await publishAttendanceScheduleWeek({
        storeCode,
        weekStartDate: weekStart.format('YYYY-MM-DD'),
      })
      message.success(t('posAdmin.scheduleAttendance.messages.publishWeekSuccess'))
      void loadSchedules()
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.publishWeekFailed'))
    } finally {
      setPublishing(false)
    }
  }

  const displayStoreUserName = (user: StoreUserListDto) => user.fullName || user.username || user.userGuid

  const holidaysByDate = useMemo(() => {
    const map = new Map<string, AttendanceStoreHolidayDto[]>()
    scheduleHolidays.forEach((holiday) => {
      const dateKey = dayjs(holiday.holidayDate).format('YYYY-MM-DD')
      map.set(dateKey, [...(map.get(dateKey) ?? []), holiday])
    })
    return map
  }, [scheduleHolidays])

  const scheduleRows = useMemo<ScheduleRow[]>(() => {
    const rows = new Map<string, ScheduleRow>()
    storeUsers.forEach((user) => {
      rows.set(user.userGuid, {
        rowKey: `${user.storeCode || storeCode || ''}-${user.userGuid}`,
        storeCode: user.storeCode || storeCode || '',
        storeName: user.storeName || storeNameMap.get(user.storeCode || storeCode || ''),
        userGuid: user.userGuid,
        userName: displayStoreUserName(user),
        schedulesByDate: {},
      })
    })

    schedules.items.forEach((schedule) => {
      if (storeCode && schedule.storeCode !== storeCode) return
      if (userGuid?.trim() && !schedule.userGuid.toLowerCase().includes(userGuid.trim().toLowerCase()) && !schedule.userName?.toLowerCase().includes(userGuid.trim().toLowerCase())) return

      const existing = rows.get(schedule.userGuid) ?? {
        rowKey: `${schedule.storeCode}-${schedule.userGuid}`,
        storeCode: schedule.storeCode,
        storeName: schedule.storeName || storeNameMap.get(schedule.storeCode),
        userGuid: schedule.userGuid,
        userName: schedule.userName,
        schedulesByDate: {},
      }
      const dateKey = dayjs(schedule.workDate).format('YYYY-MM-DD')
      existing.schedulesByDate[dateKey] = [...(existing.schedulesByDate[dateKey] ?? []), schedule]
      rows.set(schedule.userGuid, existing)
    })

    return Array.from(rows.values())
  }, [schedules.items, storeCode, storeNameMap, storeUsers, userGuid])

  const openCreateScheduleForCell = (row: ScheduleRow, date: Dayjs) => {
    if (!access.canEditAttendanceSchedule || !canEditStoreCode(row.storeCode || storeCode)) return
    openScheduleDrawer(undefined, {
      storeCode: row.storeCode || storeCode,
      userGuid: row.userGuid,
      workDate: date,
      status: 'Draft',
    })
  }

  const renderScheduleCell = (row: ScheduleRow, date: Dayjs) => {
    const dateKey = date.format('YYYY-MM-DD')
    const daySchedules = [...(row.schedulesByDate[dateKey] ?? [])].sort((left, right) => left.startTime.localeCompare(right.startTime))
    const canEditRowStore = access.canEditAttendanceSchedule && canEditStoreCode(row.storeCode || storeCode)

    if (!daySchedules.length) {
      return (
        <button
          type="button"
          disabled={!canEditRowStore}
          onClick={() => openCreateScheduleForCell(row, date)}
          style={{
            width: '100%',
            minHeight: 58,
            border: '1px dashed #d9d9d9',
            borderRadius: 6,
            background: '#fafafa',
            color: '#8c8c8c',
            cursor: canEditRowStore ? 'pointer' : 'default',
          }}
        >
          {canEditRowStore ? t('posAdmin.scheduleAttendance.actions.addShift') : t('posAdmin.scheduleAttendance.emptyShift')}
        </button>
      )
    }

    return (
      <Space direction="vertical" size={6} style={{ width: '100%' }}>
        {daySchedules.map((schedule) => {
          const canEditSchedule = access.canEditAttendanceSchedule && canEditStoreCode(schedule.storeCode)
          return (
            <button
              key={schedule.scheduleGuid}
              type="button"
              onClick={() => openScheduleDrawer(schedule)}
              style={{
                width: '100%',
                border: '1px solid #91caff',
                borderRadius: 6,
                background: schedule.status === 'Draft' ? '#fffbe6' : schedule.status === 'Cancelled' ? '#fff1f0' : '#e6f4ff',
                padding: '6px 8px',
                textAlign: 'left',
                cursor: canEditSchedule ? 'pointer' : 'default',
              }}
              disabled={!canEditSchedule}
            >
              <Space direction="vertical" size={2}>
                <Typography.Text strong style={{ fontSize: 12 }}>{formatTime(schedule.startTime)} - {formatTime(schedule.endTime)}</Typography.Text>
                {scheduleStatusTag(schedule.status)}
                {schedule.remark ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>{schedule.remark}</Typography.Text> : null}
              </Space>
            </button>
          )
        })}
      </Space>
    )
  }

  const renderHolidayHeaderTag = (holiday: AttendanceStoreHolidayDto) => {
    const color = holiday.businessStatus === 'Closed' ? 'red' : holiday.businessStatus === 'Partial' ? 'orange' : 'green'
    const status = t(`posAdmin.scheduleAttendance.status.holiday.${holiday.businessStatus}`, holiday.businessStatus)
    return (
      <Tooltip
        key={holiday.holidayGuid}
        title={`${t('posAdmin.scheduleAttendance.weekTable.holidayColumn')}: ${holiday.holidayName} (${status})`}
      >
        <Tag color={color} style={{ marginInlineEnd: 0, maxWidth: 130, overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {holiday.holidayName}
        </Tag>
      </Tooltip>
    )
  }

  const renderWeekDayHeader = (day: Dayjs, index: number) => {
    const dateKey = day.format('YYYY-MM-DD')
    const dayHolidays = holidaysByDate.get(dateKey) ?? []
    return (
      <Space direction="vertical" size={2} style={{ width: '100%' }}>
        <Typography.Text strong>{day.format('MM-DD')}</Typography.Text>
        <Typography.Text type="secondary">{t(`posAdmin.scheduleAttendance.weekdays.${index}`)}</Typography.Text>
        {dayHolidays.length ? (
          <Space size={[4, 2]} wrap>
            {dayHolidays.map(renderHolidayHeaderTag)}
          </Space>
        ) : null}
      </Space>
    )
  }

  const scheduleColumns: ColumnsType<ScheduleRow> = [
    {
      title: t('posAdmin.scheduleAttendance.fields.employee'),
      key: 'employee',
      fixed: 'left',
      width: 240,
      render: (_, record) => (
        <Space direction="vertical" size={0} style={{ width: '100%' }}>
          <Typography.Text strong>{record.userName || '--'}</Typography.Text>
        </Space>
      ),
    },
    ...weekDays.map((day, index): ColumnsType<ScheduleRow>[number] => ({
      title: renderWeekDayHeader(day, index),
      key: day.format('YYYY-MM-DD'),
      width: 170,
      render: (_, record) => renderScheduleCell(record, day),
    })),
  ]

  const recordColumns: ColumnsType<AttendanceScheduleDto> = [
    {
      title: t('posAdmin.scheduleAttendance.fields.store'),
      dataIndex: 'storeCode',
      width: 140,
      fixed: 'left',
      render: (value: string, record) => record.storeName || storeNameMap.get(value) || value,
    },
    { title: t('posAdmin.scheduleAttendance.fields.employee'), key: 'user', width: 210, fixed: 'left', render: (_, record) => userCell(record) },
    {
      title: t('posAdmin.scheduleAttendance.fields.schedule'),
      key: 'schedule',
      width: 180,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          {renderBusinessDisplay(formatDate(record.workDate))}
          <Typography.Text type="secondary">{formatTime(record.startTime)} - {formatTime(record.endTime)}</Typography.Text>
        </Space>
      ),
    },
    {
      title: t('posAdmin.scheduleAttendance.fields.segments'),
      key: 'segments',
      width: 260,
      render: (_, record) => record.segments?.length ? (
        <Space direction="vertical" size={3}>
          {record.segments.map((segment) => (
            <Space key={`${record.scheduleGuid}-${segment.segmentIndex}`} size={6} wrap>
              <Tag style={{ marginInlineEnd: 0 }}>#{segment.segmentIndex}</Tag>
              <Typography.Text>
                {formatBusinessDisplayText(formatDateTime(resolveSegmentPunchTime(segment.clockIn)))} → {formatBusinessDisplayText(formatDateTime(resolveSegmentPunchTime(segment.clockOut)))}
              </Typography.Text>
              <Typography.Text type="secondary">
                {formatRecordMinutes(segment.durationMinutes ?? 0)}
              </Typography.Text>
              {segment.status ? (
                <Tag color={segment.status === 'Completed' ? 'green' : segment.status === 'Open' ? 'blue' : 'orange'}>
                  {statusLabel('segment', segment.status)}
                </Tag>
              ) : null}
            </Space>
          ))}
        </Space>
      ) : <Typography.Text type="secondary">--</Typography.Text>,
    },
    {
      title: t('posAdmin.scheduleAttendance.fields.boundaries'),
      key: 'boundaries',
      width: 205,
      render: (_, record) => {
        const summary = buildAttendanceRecordSummary(record)
        return (
          <Space direction="vertical" size={0}>
            <Typography.Text>{formatBusinessDisplayText(formatDateTime(summary.firstClockIn))}</Typography.Text>
            <Typography.Text>{formatBusinessDisplayText(formatDateTime(summary.finalClockOut))}</Typography.Text>
          </Space>
        )
      },
    },
    {
      title: t('posAdmin.scheduleAttendance.fields.workedAndBreak'),
      key: 'worked',
      width: 130,
      render: (_, record) => {
        const summary = buildAttendanceRecordSummary(record)
        return `${formatRecordMinutes(summary.workedMinutes)} / ${formatRecordMinutes(summary.breakMinutes)}`
      },
    },
    {
      title: t('posAdmin.scheduleAttendance.fields.exceptions'),
      key: 'exceptions',
      width: 140,
      render: (_, record) => {
        const summary = buildAttendanceRecordSummary(record)
        return (
          <Space size={4} wrap>
            <Tag color={summary.lateMinutes ? 'red' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.late', { minutes: summary.lateMinutes })}
            </Tag>
            <Tag color={summary.earlyLeaveMinutes ? 'red' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.earlyLeave', { minutes: summary.earlyLeaveMinutes })}
            </Tag>
          </Space>
        )
      },
    },
    {
      title: t('posAdmin.scheduleAttendance.fields.overtime'),
      key: 'overtime',
      width: 210,
      render: (_, record) => {
        const summary = buildAttendanceRecordSummary(record)
        return (
          <Space size={[4, 3]} wrap>
            <Tag>{t('posAdmin.scheduleAttendance.recordLabels.earlyArrival', { minutes: summary.earlyOvertimeMinutes })}</Tag>
            <Tag>{t('posAdmin.scheduleAttendance.recordLabels.lateDeparture', { minutes: summary.lateOvertimeMinutes })}</Tag>
            <Tag color={summary.candidateOvertimeMinutes ? 'gold' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.candidateOvertime', { minutes: summary.candidateOvertimeMinutes })}
            </Tag>
            <Tag color={summary.approvedOvertimeMinutes ? 'green' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.approvedOvertime', { minutes: summary.approvedOvertimeMinutes })}
            </Tag>
          </Space>
        )
      },
    },
    {
      title: t('common.status'),
      key: 'recordStatus',
      width: 180,
      render: (_, record) => (
        <Space size={[4, 3]} wrap>
          <Tag color={record.hasOpenSegment ? 'blue' : 'default'}>
            {record.scheduleState ? statusLabel('scheduleState', record.scheduleState) : statusLabel('schedule', record.status)}
          </Tag>
          {record.hasMissingClockOut ? <Tag color="red">{t('posAdmin.scheduleAttendance.status.missingClockOut')}</Tag> : null}
          {record.crossStoreMissingClockOutStoreCode ? (
            <Tag color="volcano">
              {t('posAdmin.scheduleAttendance.recordLabels.crossStore', { storeCode: record.crossStoreMissingClockOutStoreCode })}
            </Tag>
          ) : null}
          {record.overtimeApprovalStatus ? (
            <Tag color={record.overtimeApprovalStatus === 'Approved' ? 'green' : 'orange'}>
              {statusLabel('overtimeApproval', record.overtimeApprovalStatus)}
            </Tag>
          ) : null}
        </Space>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      width: 120,
      fixed: 'right',
      render: (_, record) => canAdjustOwnAttendanceRecord({
        isAdmin: access.isAdmin,
        isStoreManager: access.isStoreManager,
        isOwnSchedule: record.userGuid === currentUser?.userGUID,
        isManagedStore: canEditStoreCode(record.storeCode),
      }) ? (
          <Button type="link" size="small" icon={<EditOutlined />} onClick={() => openPunchAdjustmentModal(record)}>
            {t('posAdmin.scheduleAttendance.actions.adjustPunch')}
          </Button>
        ) : null,
    },
  ]

  const availabilityColumns: ColumnsType<AttendanceAvailabilityDto> = [
    { title: t('posAdmin.scheduleAttendance.fields.store'), dataIndex: 'storeCode', width: 150, render: (value: string, record) => record.storeName || storeNameMap.get(value) || value },
    { title: t('posAdmin.scheduleAttendance.fields.employee'), key: 'user', width: 220, render: (_, record) => userCell(record) },
    { title: t('posAdmin.scheduleAttendance.fields.weekStartDate'), dataIndex: 'weekStartDate', width: 130, render: (value?: string) => renderBusinessDisplay(formatDate(value)) },
    { title: t('posAdmin.scheduleAttendance.fields.availableDate'), dataIndex: 'availableDate', width: 130, render: (value?: string) => renderBusinessDisplay(formatDate(value)) },
    { title: t('posAdmin.scheduleAttendance.fields.availableTime'), key: 'time', width: 150, render: (_, record) => `${formatTime(record.startTime)} - ${formatTime(record.endTime)}` },
    // 员工可同时上报可上班与不能上班时段，类型用颜色区分，避免把不能上班误当成可排班。
    {
      title: t('posAdmin.scheduleAttendance.fields.availabilityType'),
      dataIndex: 'isUnavailable',
      width: 110,
      render: (value?: boolean) => value
        ? <Tag color="red">{t('posAdmin.scheduleAttendance.availabilityTypes.unavailable')}</Tag>
        : <Tag color="green">{t('posAdmin.scheduleAttendance.availabilityTypes.available')}</Tag>,
    },
    { title: t('common.status'), dataIndex: 'status', width: 120, render: scheduleStatusTag },
    { title: t('column.remarks'), dataIndex: 'remark', ellipsis: true },
  ]

  const openPunchMap = (record: AttendancePunchDto) => {
    if (typeof record.locationLatitude !== 'number' || typeof record.locationLongitude !== 'number') {
      message.info(t('posAdmin.scheduleAttendance.messages.locationNotRecorded'))
      return
    }

    setMapPreview({
      title: `${record.userName || record.userGuid} / ${t(`posAdmin.scheduleAttendance.status.punchType.${record.punchType}`, record.punchType)}`,
      latitude: record.locationLatitude,
      longitude: record.locationLongitude,
      accuracy: record.locationAccuracy,
      capturedAt: record.locationCapturedAtUtc || record.punchTimeUtc || record.punchTimeLocal,
      timeZone: record.locationCapturedAtUtc || record.punchTimeUtc ? record.storeTimeZone : undefined,
    })
  }

  const openLocationSamples = async (record: AttendancePunchDto) => {
    const requestId = ++sampleRequestIdRef.current
    const nowUtc = new Date().toISOString()
    const currentStoreLocalDate = formatDateInTimeZone(nowUtc, record.storeTimeZone)
      || dayjs(record.workDate).format('YYYY-MM-DD')
    setSampleTitle(`${record.userName || record.userGuid} / ${formatBusinessDisplayText(formatDate(record.workDate))}`)
    setSampleTimeZone(record.storeTimeZone || '')
    setTrajectoryResult(null)
    setTrajectoryMapVisible(false)
    setSampleDrawerOpen(true)
    setSampleLoading(true)

    try {
      const punchResult = await getAttendancePunches({
        storeCode: record.storeCode,
        userGuid: record.userGuid,
        fromDate: dayjs(record.workDate).format('YYYY-MM-DD'),
        toDate: dayjs(record.workDate).format('YYYY-MM-DD'),
        page: 1,
        pageSize: 1000,
      })
      if (sampleRequestIdRef.current !== requestId) return

      const punches = punchResult.items.some((item) => item.punchGuid === record.punchGuid)
        ? punchResult.items
        : [...punchResult.items, record]
      const boundaryResult = buildAttendanceLocationTrajectory({
        selectedPunch: record,
        punches,
        samples: [],
        nowUtc,
        currentStoreLocalDate,
      })
      if (!boundaryResult.ok) {
        setTrajectoryResult(boundaryResult)
        return
      }
      const trajectoryTimeZone = boundaryResult.clockIn?.storeTimeZone || record.storeTimeZone || ''
      setSampleTimeZone(trajectoryTimeZone)

      const samplesByGuid = new Map<string, AttendanceLocationSampleDto>()
      const dateRanges = splitAttendanceSampleDateRange(boundaryResult.fromDate, boundaryResult.toDate)
      for (const range of dateRanges) {
        if (sampleRequestIdRef.current !== requestId) return
        const rangeSamples = await getAttendanceLocationSamples({
          storeCode: record.storeCode,
          userGuid: record.userGuid,
          fromDate: range.fromDate,
          toDate: range.toDate,
          storeTimeZone: trajectoryTimeZone,
        })

        // 接口最多返回 1000 条；命中上限时拆为单日重查，避免长期开放班段静默丢失早期点位。
        const batches = rangeSamples.length >= ATTENDANCE_LOCATION_SAMPLE_LIMIT
          ? await Promise.all(splitAttendanceSampleDateRange(range.fromDate, range.toDate, 1).map(async (dayRange) => {
            const daySamples = await getAttendanceLocationSamples({
              storeCode: record.storeCode,
              userGuid: record.userGuid,
              fromDate: dayRange.fromDate,
              toDate: dayRange.toDate,
              storeTimeZone: trajectoryTimeZone,
            })
            if (daySamples.length >= ATTENDANCE_LOCATION_SAMPLE_LIMIT) {
              throw new Error(ATTENDANCE_LOCATION_SAMPLES_TRUNCATED)
            }
            return daySamples
          }))
          : [rangeSamples]
        for (const batch of batches) {
          for (const sample of batch) samplesByGuid.set(sample.sampleGuid, sample)
        }
      }
      if (sampleRequestIdRef.current !== requestId) return

      setTrajectoryResult(buildAttendanceLocationTrajectory({
        selectedPunch: record,
        punches,
        samples: [...samplesByGuid.values()],
        nowUtc,
        currentStoreLocalDate,
      }))
    } catch (error) {
      console.error(error)
      if (sampleRequestIdRef.current === requestId) {
        message.error(t(
          error instanceof Error && error.message === ATTENDANCE_LOCATION_SAMPLES_TRUNCATED
            ? 'posAdmin.scheduleAttendance.messages.trajectorySamplesTooDense'
            : 'posAdmin.scheduleAttendance.messages.loadLocationSamplesFailed',
        ))
        setTrajectoryResult(null)
      }
    } finally {
      if (sampleRequestIdRef.current === requestId) {
        setSampleLoading(false)
      }
    }
  }

  const punchColumns: ColumnsType<AttendancePunchDto> = [
    { title: t('posAdmin.scheduleAttendance.fields.store'), dataIndex: 'storeCode', width: 150, render: (value: string, record) => record.storeName || storeNameMap.get(value) || value },
    { title: t('posAdmin.scheduleAttendance.fields.employee'), key: 'user', width: 220, render: (_, record) => userCell(record) },
    { title: t('posAdmin.scheduleAttendance.fields.workDate'), dataIndex: 'workDate', width: 130, render: (value?: string) => renderBusinessDisplay(formatDate(value)) },
    { title: t('posAdmin.scheduleAttendance.fields.punchType'), dataIndex: 'punchType', width: 120, render: (value: string) => t(`posAdmin.scheduleAttendance.status.punchType.${value}`, value) },
    { title: t('posAdmin.scheduleAttendance.fields.punchTimeLocal'), dataIndex: 'punchTimeLocal', width: 170, render: (value?: string) => renderBusinessDisplay(formatDateTime(value)) },
    {
      title: t('posAdmin.scheduleAttendance.fields.segmentAudit', '班段 / 原始与有效'),
      key: 'segmentAudit',
      width: 210,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>#{record.segmentIndex ?? '--'} {record.segmentStatus || ''}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.supersedesPunchGuid ? `原始 ${record.supersedesPunchGuid}` : '原始时间同有效时间'}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            有效 {formatBusinessDisplayText(formatDateTime(record.punchTimeLocal))}{record.adjustmentGuid ? ` / ${record.adjustmentGuid}` : ''}
          </Typography.Text>
          {record.isBreakBoundary ? <Tag color="blue">休息边界</Tag> : null}
        </Space>
      ),
    },
    { title: t('posAdmin.scheduleAttendance.fields.timeZone'), dataIndex: 'storeTimeZone', width: 180 },
    { title: t('common.status'), dataIndex: 'status', width: 150, render: punchStatusTag },
    {
      title: t('posAdmin.scheduleAttendance.fields.location'),
      key: 'location',
      width: 140,
      render: (_, record) => (
        typeof record.locationLatitude === 'number' && typeof record.locationLongitude === 'number' ? (
          <Button type="link" size="small" icon={<EnvironmentOutlined />} onClick={() => openPunchMap(record)}>
            {t('posAdmin.scheduleAttendance.actions.viewMap')}
          </Button>
        ) : (
          <Typography.Text type="secondary">{t('posAdmin.scheduleAttendance.messages.locationNotRecorded')}</Typography.Text>
        )
      ),
    },
    {
      title: t('posAdmin.scheduleAttendance.fields.locationSamples'),
      key: 'locationSamples',
      width: 130,
      render: (_, record) => (
        <Button type="link" size="small" onClick={() => void openLocationSamples(record)}>
          {t('posAdmin.scheduleAttendance.actions.viewSamples')}
        </Button>
      ),
    },
    { title: t('column.remarks'), dataIndex: 'remark', ellipsis: true },
  ]

  const approvalDisplayTitle = (record: AttendanceApprovalDto) => (
    isKnownAttendanceApprovalSourceType(record.sourceType)
      ? t(`posAdmin.scheduleAttendance.approvalPresentation.${record.sourceType}.title`)
      : record.title || record.sourceType
  )

  const approvalSystemDetails = (record: AttendanceApprovalDto) => {
    if (!isKnownAttendanceApprovalSourceType(record.sourceType)) {
      return record.detail ? <Typography.Text type="secondary">{formatBusinessDetailText(record.detail)}</Typography.Text> : null
    }

    if (record.sourceType === 'PunchAdjustment') {
      return record.adjustment ? (
        <>
          {record.adjustment.originalPunchTimeLocal ? (
            <Typography.Text>{t('posAdmin.scheduleAttendance.fields.originalPunchTime')}: {formatBusinessDisplayText(formatDateTime(record.adjustment.originalPunchTimeLocal))}</Typography.Text>
          ) : null}
          {record.adjustment.requestedPunchTimeLocal ? (
            <Typography.Text>{t('posAdmin.scheduleAttendance.fields.requestedPunchTime')}: {formatBusinessDisplayText(formatDateTime(record.adjustment.requestedPunchTimeLocal))}</Typography.Text>
          ) : null}
          {record.adjustment.effectivePunchTimeLocal ? (
            <Typography.Text>{t('posAdmin.scheduleAttendance.fields.effectivePunchTime')}: {formatBusinessDisplayText(formatDateTime(record.adjustment.effectivePunchTimeLocal))}</Typography.Text>
          ) : null}
          {record.adjustment.reason ? (
            <Typography.Text type="secondary">{t('posAdmin.scheduleAttendance.fields.reason')}: {record.adjustment.reason}</Typography.Text>
          ) : null}
          {record.adjustment.isDirectAdjustment ? (
            <Typography.Text type="secondary">{t('posAdmin.scheduleAttendance.messages.directAdjustment')}</Typography.Text>
          ) : null}
        </>
      ) : null
    }

    if (record.sourceType === 'Overtime') {
      return (
        <Space size={4} wrap>
          <Tag color="gold">{t('posAdmin.scheduleAttendance.fields.candidateOvertime', { minutes: record.candidateOvertimeMinutes ?? 0 })}</Tag>
          {record.approvedOvertimeMinutes !== undefined ? (
            <Tag color="green">{t('posAdmin.scheduleAttendance.fields.approvedOvertime', { minutes: record.approvedOvertimeMinutes })}</Tag>
          ) : null}
        </Space>
      )
    }

    if (record.sourceType === 'Punch' || record.sourceType === 'Leave') {
      const supplementalDetail = getSupplementalAttendanceApprovalDetail({
        sourceType: record.sourceType,
        detail: record.detail,
        displayedTitle: approvalDisplayTitle(record),
      })
      return (
        <>
          {record.workDate ? (
            <Typography.Text type="secondary">
              {t(`posAdmin.scheduleAttendance.approvalPresentation.${record.sourceType}.detail`, {
                workDate: formatBusinessDisplayText(formatDate(record.workDate)),
              })}
            </Typography.Text>
          ) : null}
          {supplementalDetail ? <Typography.Text type="secondary">{formatBusinessDetailText(supplementalDetail)}</Typography.Text> : null}
        </>
      )
    }

    return record.workDate ? (
      <Typography.Text type="secondary">
        {t(`posAdmin.scheduleAttendance.approvalPresentation.${record.sourceType}.detail`, {
          workDate: formatBusinessDisplayText(formatDate(record.workDate)),
        })}
      </Typography.Text>
    ) : null
  }

  const approvalColumns: ColumnsType<AttendanceApprovalDto> = [
    { title: t('posAdmin.scheduleAttendance.fields.store'), dataIndex: 'storeCode', width: 150, render: (value: string, record) => record.storeName || storeNameMap.get(value) || value },
    { title: t('posAdmin.scheduleAttendance.fields.applicant'), key: 'applicant', width: 220, render: (_, record) => userCell(record) },
    { title: t('posAdmin.scheduleAttendance.fields.sourceType'), dataIndex: 'sourceType', width: 120, render: (value: string) => t(`posAdmin.scheduleAttendance.status.sourceType.${value}`, value) },
    {
      title: t('posAdmin.scheduleAttendance.fields.workDate'),
      dataIndex: 'workDate',
      width: 130,
      render: (value?: string) => renderBusinessDisplay(formatDate(value)),
    },
    { title: t('common.status'), dataIndex: 'reviewStatus', width: 130, render: reviewStatusTag },
    {
      title: t('posAdmin.scheduleAttendance.fields.approvalDetail'),
      key: 'approvalDetail',
      width: 330,
      render: (_, record) => (
        <Space direction="vertical" size={2} style={{ width: '100%' }}>
          <Typography.Text strong>{approvalDisplayTitle(record)}</Typography.Text>
          {approvalSystemDetails(record)}
        </Space>
      ),
    },
    { title: t('posAdmin.scheduleAttendance.fields.reviewedAt'), dataIndex: 'reviewedAt', width: 170, render: formatDateTime },
    { title: t('column.remarks'), dataIndex: 'reviewRemark', ellipsis: true },
    {
      title: t('column.action'),
      key: 'action',
      width: 150,
      render: (_, record) => access.canReviewAttendance && record.reviewStatus === 'Pending' && canEditStoreCode(record.storeCode) ? (
        <Space>
          <Button type="link" size="small" icon={<CheckOutlined />} onClick={() => openReviewModal(record, 'approve')}>
            {record.sourceType === 'Overtime'
              ? t('posAdmin.scheduleAttendance.actions.approveOvertime')
              : t('posAdmin.scheduleAttendance.actions.approve')}
          </Button>
          <Button type="link" danger size="small" icon={<CloseOutlined />} onClick={() => openReviewModal(record, 'reject')}>
            {record.sourceType === 'Overtime'
              ? t('posAdmin.scheduleAttendance.actions.rejectOvertime')
              : t('posAdmin.scheduleAttendance.actions.reject')}
          </Button>
        </Space>
      ) : null,
    },
  ]

  const holidayColumns: ColumnsType<AttendanceStoreHolidayDto> = [
    { title: t('posAdmin.scheduleAttendance.fields.store'), dataIndex: 'storeCode', width: 150, render: (value: string, record) => record.storeName || storeNameMap.get(value) || value },
    { title: t('posAdmin.scheduleAttendance.fields.holidayDate'), dataIndex: 'holidayDate', width: 130, render: (value?: string) => renderBusinessDisplay(formatDate(value)) },
    { title: t('posAdmin.scheduleAttendance.fields.holidayName'), dataIndex: 'holidayName', width: 180 },
    { title: t('posAdmin.scheduleAttendance.fields.businessStatus'), dataIndex: 'businessStatus', width: 130, render: holidayStatusTag },
    { title: t('posAdmin.scheduleAttendance.fields.businessTime'), key: 'businessTime', width: 150, render: (_, record) => `${formatTime(record.openTime)} - ${formatTime(record.closeTime)}` },
    { title: t('posAdmin.scheduleAttendance.fields.paidHoliday'), dataIndex: 'isPaidHoliday', width: 110, render: (value: boolean) => value ? t('common.yes') : t('common.no') },
    { title: t('column.remarks'), dataIndex: 'remark', ellipsis: true },
    {
      title: t('column.action'),
      key: 'action',
      width: 150,
      render: (_, record) => access.canEditAttendanceHoliday && canEditStoreCode(record.storeCode) ? (
        <Space>
          <Button type="link" size="small" icon={<EditOutlined />} onClick={() => openHolidayDrawer(record)}>{t('common.edit')}</Button>
          <Button type="link" size="small" danger icon={<DeleteOutlined />} onClick={() => confirmDeleteHoliday(record)}>{t('common.delete')}</Button>
        </Space>
      ) : null,
    },
  ]

  const pagination = (state: ListState<unknown>, loader: (page: number, pageSize: number) => void) => ({
    current: state.page,
    pageSize: state.pageSize,
    total: state.total,
    showSizeChanger: true,
    showTotal: (total: number) => t('common.total', { count: total }),
    onChange: loader,
  })

  const filterBar = (
    <Space wrap>
      <Select
        allowClear
        showSearch
        optionFilterProp="label"
        placeholder={t('posAdmin.scheduleAttendance.filters.store')}
        style={{ width: 180 }}
        value={storeCode}
        options={storeOptions}
        onChange={setStoreCode}
      />
      <Input
        allowClear
        placeholder={t('posAdmin.scheduleAttendance.filters.userGuid')}
        style={{ width: 220 }}
        value={userGuid}
        onChange={(event) => setUserGuid(event.target.value)}
      />
      {activeTab === 'schedules' ? (
        <>
          <DatePicker
            picker="week"
            locale={enGB}
            allowClear={false}
            placeholder={t('posAdmin.scheduleAttendance.weekTable.selectWeek')}
            value={selectedWeek}
            onChange={(value) => setSelectedWeek(value ?? dayjs())}
          />
          <Tag color="blue" style={{ lineHeight: '30px', paddingInline: 12 }}>{selectedWeekLabel}</Tag>
        </>
      ) : (
        <DatePicker.RangePicker
          value={dateRange}
          onChange={(value) => setDateRange(value as [Dayjs, Dayjs] | null)}
        />
      )}
      <Button icon={<ReloadOutlined />} onClick={() => loadActiveTab(1)}>{t('common.search')}</Button>
    </Space>
  )

  const recordTabItem = access.canViewAttendancePunches ? {
    key: 'records',
    label: t('posAdmin.scheduleAttendance.tabs.records'),
    children: (
      <MeasuredTable metricId="pos-admin.schedule-attendance.table-1"
        rowKey="scheduleGuid"
        loading={records.loading}
        columns={recordColumns}
        dataSource={records.items}
        pagination={pagination(records, loadRecords)}
        scroll={{ x: 1900 }}
        size="small"
      />
    ),
  } : null

  const tabItems = [
    {
      key: 'schedules',
      label: t('posAdmin.scheduleAttendance.tabs.schedules'),
      children: (
        <MeasuredTable metricId="pos-admin.schedule-attendance.table-2"
          rowKey="rowKey"
          loading={schedules.loading || storeUsersLoading || scheduleHolidaysLoading}
          columns={scheduleColumns}
          dataSource={storeCode ? scheduleRows : []}
          pagination={false}
          locale={{ emptyText: storeCode ? t('posAdmin.scheduleAttendance.emptyEmployees') : t('posAdmin.scheduleAttendance.selectStoreToSchedule') }}
          scroll={{ x: 1430 }}
        />
      ),
    },
    ...(recordTabItem ? [recordTabItem] : []),
    {
      key: 'availability',
      label: t('posAdmin.scheduleAttendance.tabs.availability'),
      children: (
        <MeasuredTable metricId="pos-admin.schedule-attendance.table-3"
          rowKey="availabilityGuid"
          loading={availability.loading}
          columns={availabilityColumns}
          dataSource={availability.items}
          pagination={pagination(availability, loadAvailability)}
          scroll={{ x: 1000 }}
        />
      ),
    },
    {
      key: 'punches',
      label: t('posAdmin.scheduleAttendance.tabs.punches'),
      children: (
        <MeasuredTable metricId="pos-admin.schedule-attendance.table-4"
          rowKey="punchGuid"
          loading={punches.loading}
          columns={punchColumns}
          dataSource={punches.items}
          pagination={pagination(punches, loadPunches)}
          scroll={{ x: 1350 }}
        />
      ),
    },
    {
      key: 'approvals',
      label: t('posAdmin.scheduleAttendance.tabs.approvals'),
      children: (
        <MeasuredTable metricId="pos-admin.schedule-attendance.table-5"
          rowKey="approvalGuid"
          loading={approvals.loading}
          columns={approvalColumns}
          dataSource={approvals.items}
          pagination={pagination(approvals, loadApprovals)}
          scroll={{ x: 1000 }}
        />
      ),
    },
    {
      key: 'holidays',
      label: t('posAdmin.scheduleAttendance.tabs.holidays'),
      children: (
        <MeasuredTable metricId="pos-admin.schedule-attendance.table-6"
          rowKey="holidayGuid"
          loading={holidays.loading}
          columns={holidayColumns}
          dataSource={holidays.items}
          pagination={pagination(holidays, loadHolidays)}
          scroll={{ x: 1100 }}
        />
      ),
    },
    {
      key: 'settings',
      label: t('posAdmin.scheduleAttendance.tabs.settings'),
      children: (
        <Form form={settingsForm} layout="vertical" disabled={!access.canEditAttendanceSettings || settingsLoading} style={{ maxWidth: 720 }}>
          <Form.Item name="lateGraceMinutes" label={t('posAdmin.scheduleAttendance.fields.lateGraceMinutes')} rules={[{ required: true }]}>
            <InputNumber min={0} max={120} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="earlyLeaveGraceMinutes" label={t('posAdmin.scheduleAttendance.fields.earlyLeaveGraceMinutes')} rules={[{ required: true }]}>
            <InputNumber min={0} max={120} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="allowNoSchedulePunch" label={t('posAdmin.scheduleAttendance.fields.allowNoSchedulePunch')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="requireApprovalForLate" label={t('posAdmin.scheduleAttendance.fields.requireApprovalForLate')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="requireApprovalForEarlyLeave" label={t('posAdmin.scheduleAttendance.fields.requireApprovalForEarlyLeave')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="requireApprovalForNoSchedule" label={t('posAdmin.scheduleAttendance.fields.requireApprovalForNoSchedule')} valuePropName="checked">
            <Switch />
          </Form.Item>
          {settings?.overtimeMinimumMinutes !== undefined ? (
            <Form.Item
              name="overtimeMinimumMinutes"
              label={t('posAdmin.scheduleAttendance.fields.overtimeMinimumMinutes', '最小候选加班分钟')}
              rules={[{ required: true }]}
            >
              <InputNumber min={0} max={240} step={15} style={{ width: '100%' }} />
            </Form.Item>
          ) : null}
          {settings?.requireOvertimeApproval !== undefined ? (
            <Form.Item name="requireOvertimeApproval" label={t('posAdmin.scheduleAttendance.fields.requireOvertimeApproval', '候选加班需要审批')} valuePropName="checked">
              <Switch />
            </Form.Item>
          ) : null}
          {settings?.allowManagerDirectOwnAdjustment !== undefined ? (
            <Form.Item name="allowManagerDirectOwnAdjustment" label={t('posAdmin.scheduleAttendance.fields.allowManagerDirectOwnAdjustment', '店长本人在管理分店可直接补卡')} valuePropName="checked">
              <Switch />
            </Form.Item>
          ) : null}
          <Space>
            <Button icon={<ReloadOutlined />} loading={settingsLoading} onClick={() => void loadSettings()}>{t('common.refresh')}</Button>
            {access.canEditAttendanceSettings ? (
              <Button type="primary" icon={<SaveOutlined />} loading={settingsLoading} onClick={() => void saveSettings()}>{t('common.save')}</Button>
            ) : null}
            {settings?.updatedAt ? <Typography.Text type="secondary">{t('posAdmin.scheduleAttendance.fields.updatedAt')}: {formatDateTime(settings.updatedAt)}</Typography.Text> : null}
          </Space>
        </Form>
      ),
    },
  ]

  return (
    <Card
      title={<Space><CalendarOutlined />{t('posAdmin.scheduleAttendance.title')}</Space>}
      extra={
        <Space wrap>
          {filterBar}
          {activeTab === 'schedules' && access.canEditAttendanceSchedule ? (
            <>
              <Tooltip title={!storeCode ? t('posAdmin.scheduleAttendance.messages.selectStoreBeforePublish') : !canEditSelectedStore ? t('message.noPermission', '无权操作该数据') : undefined}>
                <Button
                  icon={<CheckOutlined />}
                  disabled={!storeCode || !canEditSelectedStore}
                  loading={publishing}
                  onClick={() => void handlePublishWeek()}
                >
                  {t('posAdmin.scheduleAttendance.actions.publishWeek')}
                </Button>
              </Tooltip>
              <Button type="primary" icon={<PlusOutlined />} disabled={!canEditSelectedStore} onClick={() => openScheduleDrawer()}>{t('posAdmin.scheduleAttendance.actions.createSchedule')}</Button>
            </>
          ) : null}
          {activeTab === 'holidays' && access.canEditAttendanceHoliday ? (
            <>
              <Tooltip title={!storeCode ? t('posAdmin.scheduleAttendance.messages.selectStoreBeforeHolidaySync') : !canEditSelectedStore ? t('message.noPermission', '无权操作该数据') : undefined}>
                <Button
                  icon={<ReloadOutlined />}
                  disabled={!storeCode || !canEditSelectedStore}
                  loading={syncingHolidays}
                  onClick={() => void syncFutureHolidays()}
                >
                  {t('posAdmin.scheduleAttendance.actions.syncFutureHolidays')}
                </Button>
              </Tooltip>
              <Tooltip title={t('posAdmin.scheduleAttendance.messages.batchHolidayOverwriteHint')}>
                <Button
                  icon={<CopyOutlined />}
                  disabled={!editableStoreOptions.length}
                  onClick={() => openBatchHolidayDrawer()}
                >
                  {t('posAdmin.scheduleAttendance.actions.batchHoliday')}
                </Button>
              </Tooltip>
              <Button type="primary" icon={<PlusOutlined />} disabled={Boolean(storeCode && !canEditSelectedStore)} onClick={() => openHolidayDrawer()}>{t('posAdmin.scheduleAttendance.actions.createHoliday')}</Button>
            </>
          ) : null}
        </Space>
      }
    >
      <Tabs activeKey={activeTab} onChange={(key) => setActiveTab(key as TabKey)} items={tabItems} />

      <Drawer
        title={editingSchedule ? t('posAdmin.scheduleAttendance.drawer.editSchedule') : t('posAdmin.scheduleAttendance.drawer.createSchedule')}
        width={520}
        open={scheduleDrawerOpen}
        onClose={() => setScheduleDrawerOpen(false)}
        extra={(
          <Space>
            {editingSchedule ? (
              <Button danger icon={<DeleteOutlined />} onClick={() => confirmDeleteSchedule(editingSchedule)}>{t('common.delete')}</Button>
            ) : null}
            <Button type="primary" loading={saving} onClick={() => void saveSchedule()}>{t('common.save')}</Button>
          </Space>
        )}
      >
        <Form form={scheduleForm} layout="vertical">
          <Form.Item name="storeCode" label={t('posAdmin.scheduleAttendance.fields.store')} rules={[{ required: true, message: t('form.pleaseSelectStore') }]}>
            <Select showSearch optionFilterProp="label" options={editableStoreOptions} />
          </Form.Item>
          <Form.Item name="userGuid" label={t('posAdmin.scheduleAttendance.fields.employeeGuid')} rules={[{ required: true, message: t('posAdmin.scheduleAttendance.validation.userGuidRequired') }]}>
            <Select
              showSearch
              optionFilterProp="label"
              options={storeUsers.map((user) => ({
                value: user.userGuid,
                label: `${displayStoreUserName(user)} / ${user.userGuid}`,
              }))}
            />
          </Form.Item>
          <Form.Item name="workDate" label={t('posAdmin.scheduleAttendance.fields.workDate')} rules={[{ required: true }]}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="timeRange" label={t('posAdmin.scheduleAttendance.fields.shift')} rules={[{ required: true }]}>
            <TimePicker.RangePicker format="HH:mm" style={{ width: '100%' }} />
          </Form.Item>
          {editingSchedule ? (
            <Form.Item name="status" label={t('common.status')} rules={[{ required: true }]}>
              <Select options={[
                { value: 'Draft', label: statusLabel('schedule', 'Draft') },
                { value: 'Active', label: statusLabel('schedule', 'Active') },
                { value: 'Cancelled', label: statusLabel('schedule', 'Cancelled') },
              ]} />
            </Form.Item>
          ) : null}
          <Form.Item name="remark" label={t('column.remarks')}>
            <Input.TextArea rows={3} />
          </Form.Item>
        </Form>
      </Drawer>

      <Drawer
        title={editingHoliday ? t('posAdmin.scheduleAttendance.drawer.editHoliday') : t('posAdmin.scheduleAttendance.drawer.createHoliday')}
        width={520}
        open={holidayDrawerOpen}
        onClose={() => setHolidayDrawerOpen(false)}
        extra={<Button type="primary" loading={saving} onClick={() => void saveHoliday()}>{t('common.save')}</Button>}
      >
        <Form form={holidayForm} layout="vertical">
          {editingHoliday ? (
            <Form.Item name="storeCode" label={t('posAdmin.scheduleAttendance.fields.store')} rules={[{ required: true, message: t('form.pleaseSelectStore') }]}>
              <Select showSearch optionFilterProp="label" options={editableStoreOptions} />
            </Form.Item>
          ) : (
            <Form.Item
              name="storeCodes"
              label={t('posAdmin.scheduleAttendance.fields.stores')}
              rules={[{ required: true, message: t('posAdmin.scheduleAttendance.validation.storeCodesRequired') }]}
            >
              <Select
                mode="multiple"
                showSearch
                optionFilterProp="label"
                maxTagCount="responsive"
                options={editableStoreOptions}
              />
            </Form.Item>
          )}
          <Form.Item name="holidayDate" label={t('posAdmin.scheduleAttendance.fields.holidayDate')} rules={[{ required: true }]}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="holidayName" label={t('posAdmin.scheduleAttendance.fields.holidayName')} rules={[{ required: true, message: t('posAdmin.scheduleAttendance.validation.holidayNameRequired') }]}>
            <Input />
          </Form.Item>
          <Form.Item name="businessStatus" label={t('posAdmin.scheduleAttendance.fields.businessStatus')} rules={[{ required: true }]}>
            <Select options={[
              { value: 'Open', label: t('posAdmin.scheduleAttendance.status.holiday.Open') },
              { value: 'Closed', label: t('posAdmin.scheduleAttendance.status.holiday.Closed') },
              { value: 'Partial', label: t('posAdmin.scheduleAttendance.status.holiday.Partial') },
            ]} />
          </Form.Item>
          <Form.Item name="businessTime" label={t('posAdmin.scheduleAttendance.fields.businessTime')}>
            <TimePicker.RangePicker format="HH:mm" style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="isPaidHoliday" label={t('posAdmin.scheduleAttendance.fields.paidHoliday')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="remark" label={t('column.remarks')}>
            <Input.TextArea rows={3} />
          </Form.Item>
        </Form>
      </Drawer>

      <Drawer
        title={t('posAdmin.scheduleAttendance.drawer.batchHoliday')}
        width={560}
        open={batchHolidayDrawerOpen}
        onClose={() => setBatchHolidayDrawerOpen(false)}
        extra={<Button type="primary" loading={saving} onClick={() => void saveBatchHoliday()}>{t('common.save')}</Button>}
      >
        <Form form={batchHolidayForm} layout="vertical">
          <Form.Item
            name="storeCodes"
            label={t('posAdmin.scheduleAttendance.fields.stores')}
            rules={[{ required: true, message: t('posAdmin.scheduleAttendance.validation.storeCodesRequired') }]}
          >
            <Select
              mode="multiple"
              showSearch
              optionFilterProp="label"
              maxTagCount="responsive"
              options={editableStoreOptions}
            />
          </Form.Item>
          <Form.Item name="holidayDate" label={t('posAdmin.scheduleAttendance.fields.holidayDate')} rules={[{ required: true }]}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="holidayName" label={t('posAdmin.scheduleAttendance.fields.holidayName')} rules={[{ required: true, message: t('posAdmin.scheduleAttendance.validation.holidayNameRequired') }]}>
            <Input />
          </Form.Item>
          <Form.Item name="businessStatus" label={t('posAdmin.scheduleAttendance.fields.businessStatus')} rules={[{ required: true }]}>
            <Select options={[
              { value: 'Open', label: t('posAdmin.scheduleAttendance.status.holiday.Open') },
              { value: 'Closed', label: t('posAdmin.scheduleAttendance.status.holiday.Closed') },
              { value: 'Partial', label: t('posAdmin.scheduleAttendance.status.holiday.Partial') },
            ]} />
          </Form.Item>
          <Form.Item name="businessTime" label={t('posAdmin.scheduleAttendance.fields.businessTime')}>
            <TimePicker.RangePicker format="HH:mm" style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="isPaidHoliday" label={t('posAdmin.scheduleAttendance.fields.paidHoliday')} valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="remark" label={t('column.remarks')}>
            <Input.TextArea rows={3} />
          </Form.Item>
        </Form>
      </Drawer>

      <Drawer
        title={sampleTitle ? `${t('posAdmin.scheduleAttendance.drawer.locationTrajectory')} - ${sampleTitle}` : t('posAdmin.scheduleAttendance.drawer.locationTrajectory')}
        width="min(960px, 100vw)"
        open={sampleDrawerOpen}
        onClose={() => {
          sampleRequestIdRef.current += 1
          setSampleDrawerOpen(false)
          setTrajectoryMapVisible(false)
          setTrajectoryResult(null)
        }}
      >
        {sampleLoading ? (
          <Card loading style={{ minHeight: 240 }} />
        ) : trajectoryResult?.ok && trajectoryResult.clockIn ? (
          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <Card size="small">
              <Descriptions
                size="small"
                column={{ xs: 1, sm: 2, md: 4 }}
                items={[
                  {
                    key: 'segment',
                    label: t('posAdmin.scheduleAttendance.fields.segment'),
                    children: (
                      <Space size={6}>
                        <Typography.Text>#{trajectoryResult.segmentIndex ?? '--'}</Typography.Text>
                        {trajectoryResult.isOpen ? (
                          <Tag color="processing">
                            {t('posAdmin.scheduleAttendance.messages.trajectoryInProgress')}
                          </Tag>
                        ) : null}
                      </Space>
                    ),
                  },
                  {
                    key: 'timezone',
                    label: t('posAdmin.scheduleAttendance.fields.timeZone'),
                    children: sampleTimeZone || '--',
                  },
                  {
                    key: 'range',
                    label: t('posAdmin.scheduleAttendance.fields.trajectoryRange'),
                    children: (
                      <Typography.Text>
                        {trajectoryResult.clockIn.punchTimeLocal
                          ? formatBusinessDisplayText(formatStoredLocalDateTime(trajectoryResult.clockIn.punchTimeLocal))
                          : formatBusinessDisplayText(formatDateTimeInTimeZone(trajectoryResult.clockIn.punchTimeUtc, sampleTimeZone))}
                        {' → '}
                        {trajectoryResult.clockOut
                          ? trajectoryResult.clockOut.punchTimeLocal
                            ? formatBusinessDisplayText(formatStoredLocalDateTime(trajectoryResult.clockOut.punchTimeLocal))
                            : formatBusinessDisplayText(formatDateTimeInTimeZone(trajectoryResult.clockOut.punchTimeUtc, sampleTimeZone))
                          : t('posAdmin.scheduleAttendance.messages.trajectoryInProgress')}
                      </Typography.Text>
                    ),
                  },
                  {
                    key: 'samples',
                    label: t('posAdmin.scheduleAttendance.fields.sampleCount'),
                    children: trajectoryResult.sampleCount,
                  },
                ]}
              />
            </Card>

            {trajectoryResult.isOpen ? (
              <Alert
                type="info"
                showIcon
                message={t('posAdmin.scheduleAttendance.messages.trajectoryOpen')}
              />
            ) : null}
            {trajectoryResult.sampleCount === 0 ? (
              <Alert
                type="info"
                showIcon
                message={t('posAdmin.scheduleAttendance.messages.trajectoryNoSamples')}
              />
            ) : null}

            <Card
              size="small"
              title={t('posAdmin.scheduleAttendance.drawer.locationMap')}
              extra={!trajectoryMapVisible ? (
                <Button
                  type="primary"
                  size="small"
                  icon={<EnvironmentOutlined />}
                  disabled={trajectoryResult.mapPoints.length < 2}
                  onClick={() => setTrajectoryMapVisible(true)}
                >
                  {t('posAdmin.scheduleAttendance.actions.loadTrajectoryMap')}
                </Button>
              ) : null}
            >
              <Space direction="vertical" size={10} style={{ width: '100%' }}>
                <Typography.Text type="secondary">
                  {t('posAdmin.scheduleAttendance.messages.trajectoryMapPrivacy')}
                </Typography.Text>
                {trajectoryResult.mapPoints.length < 2 ? (
                  <Alert
                    type="warning"
                    showIcon
                    message={t('posAdmin.scheduleAttendance.messages.trajectoryMapNeedsTwoPoints')}
                  />
                ) : null}
                {trajectoryMapVisible ? (
                  <AttendanceLocationTrajectoryMap
                    points={trajectoryMapPoints}
                    ariaLabel={t('posAdmin.scheduleAttendance.messages.trajectoryMapAria')}
                    loadFailedText={t('posAdmin.scheduleAttendance.messages.trajectoryMapLoadFailed')}
                    tileLoadFailedText={t('posAdmin.scheduleAttendance.messages.trajectoryTileLoadFailed')}
                  />
                ) : null}
                <Typography.Text type="secondary">
                  {t('posAdmin.scheduleAttendance.messages.trajectoryMapDisclaimer')}
                </Typography.Text>
              </Space>
            </Card>

            <Typography.Title level={5} style={{ margin: 0 }}>
              {t('posAdmin.scheduleAttendance.drawer.locationSamples')}
            </Typography.Title>
            <Timeline
              items={trajectoryResult.points.map((point) => {
                const hasCoordinates = typeof point.latitude === 'number' && typeof point.longitude === 'number'
                const canOpenMap = trajectoryMapPointKeys.has(point.key)
                const pointTime = point.kind === 'Sample' || !point.displayTimeSource
                  ? formatBusinessDisplayText(formatDateTimeInTimeZone(point.capturedAtUtc, sampleTimeZone))
                  : formatBusinessDisplayText(formatStoredLocalDateTime(point.displayTimeSource))
                return {
                  color: point.kind === 'ClockIn' ? 'green' : point.kind === 'ClockOut' ? 'red' : 'blue',
                  children: (
                    <Space direction="vertical" size={3} style={{ width: '100%' }}>
                      <Space size={[8, 4]} wrap>
                        <Typography.Text strong>
                          {t(`posAdmin.scheduleAttendance.messages.trajectoryPoint${point.kind}`)}
                        </Typography.Text>
                        <Typography.Text>{pointTime}</Typography.Text>
                        {typeof point.accuracy === 'number' ? (
                          <Tag>{t('posAdmin.scheduleAttendance.fields.locationAccuracy')}: {Math.round(point.accuracy)}m</Tag>
                        ) : null}
                        {point.deviceSystem ? <Tag>{point.deviceSystem}</Tag> : null}
                      </Space>
                      <Space size={[8, 4]} wrap>
                        {hasCoordinates ? (
                          <Typography.Text type={canOpenMap ? undefined : 'danger'}>
                            {point.latitude!.toFixed(6)}, {point.longitude!.toFixed(6)}
                          </Typography.Text>
                        ) : (
                          <Typography.Text type="secondary">
                            {t('posAdmin.scheduleAttendance.messages.locationNotRecorded')}
                          </Typography.Text>
                        )}
                        {canOpenMap ? (
                          <Button
                            type="link"
                            size="small"
                            icon={<EnvironmentOutlined />}
                            onClick={() => setMapPreview({
                              title: `${sampleTitle} / ${pointTime}`,
                              latitude: point.latitude!,
                              longitude: point.longitude!,
                              accuracy: point.accuracy,
                              capturedAt: point.capturedAtUtc,
                              timeZone: sampleTimeZone,
                            })}
                          >
                            {t('posAdmin.scheduleAttendance.actions.viewMap')}
                          </Button>
                        ) : null}
                      </Space>
                    </Space>
                  ),
                }
              })}
            />
          </Space>
        ) : trajectoryResult?.reason ? (
          <Alert
            type="warning"
            showIcon
            message={t(`posAdmin.scheduleAttendance.messages.trajectoryReason.${trajectoryResult.reason}`)}
          />
        ) : (
          <Empty description={t('posAdmin.scheduleAttendance.messages.locationSamplesEmpty')} />
        )}
      </Drawer>

      <Modal
        title={mapPreview?.title || t('posAdmin.scheduleAttendance.drawer.locationMap')}
        open={Boolean(mapPreview)}
        footer={null}
        width={760}
        onCancel={() => setMapPreview(null)}
      >
        {mapPreview ? (
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Space size={[12, 4]} wrap>
              <Typography.Text>
                {t('posAdmin.scheduleAttendance.fields.location')}: {mapPreview.latitude.toFixed(6)}, {mapPreview.longitude.toFixed(6)}
              </Typography.Text>
              {typeof mapPreview.accuracy === 'number' ? (
                <Typography.Text type="secondary">
                  {t('posAdmin.scheduleAttendance.fields.locationAccuracy')}: {Math.round(mapPreview.accuracy)}m
                </Typography.Text>
              ) : null}
              {mapPreview.capturedAt ? (
                <Typography.Text type="secondary">
                  {t('posAdmin.scheduleAttendance.fields.locationCapturedAt')}: {
                    mapPreview.timeZone
                      ? formatBusinessDisplayText(formatDateTimeInTimeZone(mapPreview.capturedAt, mapPreview.timeZone))
                      : formatBusinessDisplayText(formatDateTime(mapPreview.capturedAt))
                  }
                </Typography.Text>
              ) : null}
            </Space>
            <Typography.Text type="secondary">
              {t('posAdmin.scheduleAttendance.messages.externalMapNotice')}
            </Typography.Text>
            <Button
              type="primary"
              icon={<EnvironmentOutlined />}
              onClick={() => window.open(externalMapUrl, '_blank', 'noopener,noreferrer')}
            >
              {t('posAdmin.scheduleAttendance.actions.openExternalMap')}
            </Button>
          </Space>
        ) : null}
      </Modal>

      <Modal
        title={t('posAdmin.scheduleAttendance.drawer.adjustPunch')}
        open={adjustmentModalOpen}
        width={680}
        onCancel={() => {
          previewRequestIdRef.current += 1
          setAdjustmentModalOpen(false)
          setAdjustmentPreview(null)
          setServerAdjustmentPreview(null)
          setPreviewPayloadSnapshot(null)
          setAdjustmentPreviewLoading(false)
        }}
        footer={(
          <Space>
            <Button onClick={() => {
              previewRequestIdRef.current += 1
              setAdjustmentModalOpen(false)
              setPreviewPayloadSnapshot(null)
              setAdjustmentPreviewLoading(false)
            }}>{t('common.cancel')}</Button>
            <Button loading={adjustmentPreviewLoading} onClick={() => void previewPunchAdjustment()}>{t('posAdmin.scheduleAttendance.actions.preview')}</Button>
            <Tooltip title={!adjustmentPreview
              ? t('posAdmin.scheduleAttendance.messages.previewBeforeSave')
              : !serverAdjustmentPreview?.previewRevision
                ? t('posAdmin.scheduleAttendance.messages.previewRevisionMissing')
                : undefined}>
              <Button type="primary" disabled={!adjustmentPreview || !previewPayloadSnapshot || !serverAdjustmentPreview?.previewRevision} loading={saving} onClick={() => void submitPunchAdjustment()}>
                {t('common.save')}
              </Button>
            </Tooltip>
          </Space>
        )}
      >
        <Form
          form={adjustmentForm}
          layout="vertical"
          onValuesChange={() => {
            previewRequestIdRef.current += 1
            setAdjustmentPreview(null)
            setServerAdjustmentPreview(null)
            setPreviewPayloadSnapshot(null)
            setAdjustmentPreviewLoading(false)
          }}
        >
          <Form.Item name="punchType" label={t('posAdmin.scheduleAttendance.fields.punchType')} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'ClockIn', label: t('posAdmin.scheduleAttendance.status.punchType.ClockIn') },
                { value: 'ClockOut', label: t('posAdmin.scheduleAttendance.status.punchType.ClockOut') },
              ]}
              onChange={(punchType: 'ClockIn' | 'ClockOut') => {
                if (!adjustmentTarget) return
                const adjustmentMode = getDefaultPunchAdjustmentMode(adjustmentTarget, punchType)
                adjustmentForm.setFieldsValue({
                  adjustmentMode,
                  originalPunchGuid: adjustmentMode === 'replace'
                    ? deriveOriginalPunchGuid(adjustmentTarget, punchType)
                    : undefined,
                })
              }}
            />
          </Form.Item>
          <Form.Item name="adjustmentMode" label={t('posAdmin.scheduleAttendance.fields.adjustmentMode')} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'create', label: t('posAdmin.scheduleAttendance.actions.createMissingPunch') },
                { value: 'replace', label: t('posAdmin.scheduleAttendance.actions.replacePunch') },
              ]}
              onChange={(adjustmentMode: 'create' | 'replace') => {
                adjustmentForm.setFieldValue(
                  'originalPunchGuid',
                  adjustmentMode === 'replace' && adjustmentTarget && selectedAdjustmentPunchType
                    ? deriveOriginalPunchGuid(adjustmentTarget, selectedAdjustmentPunchType)
                    : undefined,
                )
              }}
            />
          </Form.Item>
          {selectedAdjustmentMode === 'replace' ? (
            <Form.Item
              name="originalPunchGuid"
              label={t('posAdmin.scheduleAttendance.fields.originalPunch')}
              rules={[{ required: true, message: t('posAdmin.scheduleAttendance.validation.originalPunchRequired') }]}
              extra={t('posAdmin.scheduleAttendance.messages.originalPunchAudit')}
            >
              <Select
                options={adjustmentPunchOptions}
                placeholder={adjustmentPunchOptions.length
                  ? t('common.select')
                  : t('posAdmin.scheduleAttendance.messages.noOriginalPunch')}
              />
            </Form.Item>
          ) : (
            <Typography.Paragraph type="secondary">
              {t('posAdmin.scheduleAttendance.messages.newPunchAudit')}
            </Typography.Paragraph>
          )}
          <Form.Item name="requestedPunchTimeLocal" label={t('posAdmin.scheduleAttendance.fields.requestedPunchTimeLocal')} rules={[{ required: true }]}>
            <DatePicker showTime={{ format: 'HH:mm' }} format="YYYY-MM-DD HH:mm" style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="reason" label={t('posAdmin.scheduleAttendance.fields.reason')} rules={[
            { required: true, whitespace: true, message: t('posAdmin.scheduleAttendance.validation.reasonRequired') },
          ]}>
            <Input.TextArea rows={3} maxLength={500} showCount />
          </Form.Item>
        </Form>
        {adjustmentPreview ? (
          <Space direction="vertical" size={8} style={{ width: '100%', padding: 12, background: '#fafafa', border: '1px solid #f0f0f0', borderRadius: 6 }}>
            <Typography.Text strong>{t('posAdmin.scheduleAttendance.fields.preview')}</Typography.Text>
            <Space size={[12, 4]} wrap>
              <Typography.Text>
                {t('posAdmin.scheduleAttendance.recordLabels.original', {
                  time: formatBusinessDisplayText(formatDateTime(adjustmentPreview.originalPunchTimeLocal)),
                })}
              </Typography.Text>
              <Typography.Text>
                {t('posAdmin.scheduleAttendance.recordLabels.effective', {
                  time: formatBusinessDisplayText(formatDateTime(adjustmentPreview.requestedPunchTimeLocal)),
                })}
              </Typography.Text>
              {serverAdjustmentPreview ? (
                <>
                  <Typography.Text>
                    {t('posAdmin.scheduleAttendance.recordLabels.workedMinutesChange', {
                      before: formatRecordMinutes(serverAdjustmentPreview.existingSession?.workedMinutes),
                      after: formatRecordMinutes(serverAdjustmentPreview.proposedSession?.workedMinutes),
                    })}
                  </Typography.Text>
                  <Typography.Text>
                    {t('posAdmin.scheduleAttendance.recordLabels.candidateOvertimeChange', {
                      before: formatRecordMinutes(serverAdjustmentPreview.existingSession?.candidateOvertimeMinutes),
                      after: formatRecordMinutes(serverAdjustmentPreview.proposedSession?.candidateOvertimeMinutes),
                    })}
                  </Typography.Text>
                </>
              ) : null}
            </Space>
            <Space size={4} wrap>
              {proposedAdjustmentPunchStatus
                ? <Tag color={proposedAdjustmentPunchStatus === 'Normal' ? 'green' : 'orange'}>{statusLabel('punch', proposedAdjustmentPunchStatus)}</Tag>
                : <Typography.Text type="secondary">{t('posAdmin.scheduleAttendance.messages.proposedPunchStatusUnavailable')}</Typography.Text>}
            </Space>
            <Typography.Text type="secondary">
              {serverAdjustmentPreview?.wouldAutoApprove
                ? t('posAdmin.scheduleAttendance.messages.adjustmentWillAutoApprove')
                : t('posAdmin.scheduleAttendance.messages.adjustmentWillReview')}
            </Typography.Text>
          </Space>
        ) : null}
      </Modal>

      <Modal
        title={reviewTarget?.sourceType === 'Overtime'
          ? reviewAction === 'approve'
            ? t('posAdmin.scheduleAttendance.actions.approveOvertime')
            : t('posAdmin.scheduleAttendance.actions.rejectOvertime')
          : reviewAction === 'approve'
            ? t('posAdmin.scheduleAttendance.drawer.approve')
            : t('posAdmin.scheduleAttendance.drawer.reject')}
        open={reviewModalOpen}
        okText={reviewTarget?.sourceType === 'Overtime'
          ? reviewAction === 'approve'
            ? t('posAdmin.scheduleAttendance.actions.approveOvertime')
            : t('posAdmin.scheduleAttendance.actions.rejectOvertime')
          : reviewAction === 'approve'
            ? t('posAdmin.scheduleAttendance.actions.approve')
            : t('posAdmin.scheduleAttendance.actions.reject')}
        cancelText={t('common.cancel')}
        okButtonProps={{ danger: reviewAction === 'reject', loading: saving }}
        onOk={() => void submitReview()}
        onCancel={() => setReviewModalOpen(false)}
      >
        {reviewTarget ? (
          <Space direction="vertical" size={2} style={{ marginBottom: 16 }}>
            <Typography.Text strong>{approvalDisplayTitle(reviewTarget)}</Typography.Text>
            {approvalSystemDetails(reviewTarget)}
            <Typography.Text>{t('posAdmin.scheduleAttendance.fields.workDate')}: {formatBusinessDisplayText(formatDate(reviewTarget.workDate))}</Typography.Text>
          </Space>
        ) : null}
        <Form form={reviewForm} layout="vertical">
          {reviewTarget?.sourceType === 'Overtime' && reviewAction === 'approve' ? (
            <Form.Item
              name="approvedOvertimeMinutes"
              label={t('posAdmin.scheduleAttendance.fields.approvedOvertimeMinutes', '批准加班分钟')}
              rules={[{ required: true }]}
              extra={t('posAdmin.scheduleAttendance.messages.overtimeApprovalRange', {
                defaultValue: '只能选择 0 到 {{candidate}} 分钟，且必须为 15 分钟倍数',
                candidate: reviewTarget.candidateOvertimeMinutes ?? 0,
              })}
            >
              <InputNumber min={0} max={reviewTarget.candidateOvertimeMinutes ?? 0} step={15} style={{ width: '100%' }} />
            </Form.Item>
          ) : null}
          <Form.Item name="reviewRemark" label={t('posAdmin.scheduleAttendance.fields.reviewRemark')}>
            <Input.TextArea rows={4} />
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  )
}
