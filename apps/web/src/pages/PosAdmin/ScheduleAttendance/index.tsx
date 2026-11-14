import {
  CalendarOutlined,
  CheckCircleOutlined,
  CheckOutlined,
  CloseOutlined,
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  EnvironmentOutlined,
  FullscreenOutlined,
  LeftOutlined,
  LoadingOutlined,
  LockOutlined,
  PlusOutlined,
  ReloadOutlined,
  RightOutlined,
  SaveOutlined,
  SearchOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Badge,
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
  Popconfirm,
  Popover,
  Select,
  Space,
  Spin,
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
import { cloneElement, lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, DragEvent, ReactElement } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useSearchParams } from 'react-router-dom'
import { useAttendancePendingCount } from '../../../layout/attendancePendingBadge'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { restoreAttendanceSchedule } from '../../../services/attendancePayrollService'
import {
  batchUpsertAttendanceHolidays,
  createAttendanceHoliday,
  createAttendanceSchedule,
  deleteAttendanceHoliday,
  deleteAttendanceSchedule,
  getAttendanceAvailability,
  getAttendanceEmployees,
  getAttendanceHolidays,
  getAttendanceLocationSamples,
  getAttendancePunches,
  getAttendanceScheduleWeek,
  getAttendanceRecords,
  getAttendanceSettings,
  publishAttendanceScheduleWeek,
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
  AttendanceAvailabilityDto,
  AttendanceHolidayBusinessStatus,
  AttendanceLocationSampleDto,
  AttendancePunchDto,
  AttendancePunchStatus,
  AttendanceScheduleDto,
  AttendanceScheduleStatus,
  AttendanceSettingsDto,
  AttendanceStoreHolidayDto,
  BatchUpsertAttendanceHolidayPayload,
  SaveAttendanceHolidayPayload,
  SaveAttendanceSettingsPayload,
  SaveAttendancePunchAdjustmentPayload,
  AttendancePunchAdjustmentPreviewDto,
} from '../../../types/scheduleAttendance'
import {
  buildAttendanceRecordSummary,
  buildLocalPunchAdjustmentPreview,
  resolveSegmentPunchTime,
  deriveOriginalPunchGuid,
  getPunchAdjustmentOptions,
  getPunchAdjustmentPayloadSnapshot,
  isLatestMatchingPunchAdjustmentPreview,
  getDefaultPunchAdjustmentMode,
  resolvePunchAdjustmentOriginalGuid,
  canAdjustOwnAttendanceRecord,
  getProposedAdjustmentPunchStatus,
  getAttendanceWeekdayIndex,
  insertAttendanceWeekday,
  buildAttendanceMealPaySummary,
  splitExceptionDuration,
} from './attendanceRecordLogic'
import type { LocalPunchAdjustmentPreview } from './attendanceRecordLogic'
import AttendanceLocationTrajectoryMap from './AttendanceLocationTrajectoryMap'
import AttendancePayrollSettingsFields from './AttendancePayrollSettingsFields'
import BatchScheduleModal from './BatchScheduleModal'
import type { BatchModalDay, BatchModalInitial, BatchModalUser } from './BatchScheduleModal'
import DeletedSchedulesDrawer from './DeletedSchedulesDrawer'
import ScheduleCellEditor from './ScheduleCellEditor'
import type { CellEditorValues } from './ScheduleCellEditor'
import {
  SHIFT_PRESETS,
  buildScheduleSavePayloads,
  getMealSummary,
  getOccupiedDateKeys,
  normalizeHm,
  toMealChoice,
} from './scheduleCellEditorLogic'
import type { ShiftPreset } from './scheduleCellEditorLogic'
import {
  buildCopyPayload,
  buildMoveUpdatePayload,
  buildTransferCreatePayload,
  collectPunchScheduleGuids,
  getDropVerdict,
  getMealBadge,
  isScheduleLocked,
} from './scheduleDragLogic'
import type { DragSource, DropVerdict } from './scheduleDragLogic'
import LandscapeShell, { releaseLandscape, tryLockLandscape, usePhoneEnvironment } from './LandscapeShell'
import { getShellMode, nextAutoDismissed } from './phoneShellLogic'
import { FALLBACK_STAFF_COLOR, assignStaffColors } from './staffColorLogic'
import type { StaffColor } from './staffColorLogic'
import WeekBoard from './WeekBoard'
import type { WeekBoardDay, WeekBoardDayDrop, WeekBoardLegendItem } from './WeekBoard'
import { buildStaffLegend, findAvailableUnscheduled, groupSchedulesByDay, matchStaffKeyword, summarizeDayList } from './weekBoardLogic'
import { formatHours, getScheduleHours, summarizeSchedules } from './weekSummaryLogic'
import './scheduleCellEditor.css'
import {
  buildAvailabilityIndex,
  getDayAvailability,
  getScheduleAvailabilityConflict,
} from './availabilityLogic'
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

import scheduleAttendanceMessagesEn from './scheduleAttendanceMessages.en.json'
import scheduleAttendanceMessagesZh from './scheduleAttendanceMessages.zh.json'

dayjs.extend(isoWeek)
// 用餐未休息加工时等新增文案放页面级消息文件，随页面懒加载，不占首屏 i18n 包。
registerPageMessages({ zh: scheduleAttendanceMessagesZh, en: scheduleAttendanceMessagesEn })

type TabKey = 'schedules' | 'records' | 'review' | 'availability' | 'punches' | 'holidays' | 'settings'

// 考勤审核整页作为「考勤记录」后面的子标签：首次切到该标签才加载代码，不进排班页首屏包。
const AttendanceReviewPanel = lazy(() => import('../AttendanceReview'))
const REVIEW_TAB_QUERY = 'review'
const SCHEDULE_ATTENDANCE_PATH = '/pos-admin/schedule-attendance'

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
  /** 只在 21 岁以下时有值（后端 ResolveMinorAge），姓名旁显示年龄角标。 */
  age?: number
  schedulesByDate: Record<string, AttendanceScheduleDto[]>
}

/** 把员工颜色挂成 CSS 变量，卡片、图例、弹窗里的色块共用同一套样式。 */
const staffColorVars = (color: StaffColor): CSSProperties => (
  { '--sce-bg': color.bg, '--sce-bar': color.bar, '--sce-ink': color.ink } as CSSProperties
)

/** 周排班表中正在就地编辑的单元格；scheduleGuid 为空表示在该日新建班次。 */
interface EditingCell {
  userGuid: string
  dateKey: string
  scheduleGuid?: string
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
// 翻页上限：最多 30 页（3 万点），远超一个班段的正常点数。
const ATTENDANCE_LOCATION_SAMPLE_MAX_PAGES = 30
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
  // 页内子标签：排班类标签按 canViewAttendanceSchedule 显示，「考勤审核」按 canReviewAttendance 显示，
  // 两者互相独立（只有审核权限的账号也能进入本页，只看到「考勤审核」）。
  const canViewSchedulePart = access.canViewAttendanceSchedule
  const canReviewTab = access.canReviewAttendance
  const location = useLocation()
  const [searchParams, setSearchParams] = useSearchParams()
  // 本页被保活缓存时 useSearchParams 读到的是「当前地址」的参数，必须确认当前地址就是本页才认 ?tab=review。
  const wantsReviewTab = location.pathname === SCHEDULE_ATTENDANCE_PATH && searchParams.get('tab') === REVIEW_TAB_QUERY
  const [activeTab, setActiveTab] = useState<TabKey>(() => (
    canReviewTab && (wantsReviewTab || !canViewSchedulePart) ? 'review' : 'schedules'
  ))
  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [storeCode, setStoreCode] = useState<string | undefined>()
  const [userGuid, setUserGuid] = useState<string | undefined>()
  // 周排班页的员工搜索：员工已一次性加载，姓名只在本地过滤，不再带进接口查询
  const [staffKeyword, setStaffKeyword] = useState('')
  // 点图例选中要高亮的员工（可多选）；搜索框命中的人同样高亮
  const [legendSelected, setLegendSelected] = useState<ReadonlySet<string>>(() => new Set())
  // 手机横屏看板：手机横过来自动进入；竖着拿时点「横屏查看整周」进入
  const phoneEnv = usePhoneEnvironment()
  const [shellRequested, setShellRequested] = useState(false)
  const [shellDismissed, setShellDismissed] = useState(false)
  const [batchOpen, setBatchOpen] = useState(false)
  const [batchInitial, setBatchInitial] = useState<BatchModalInitial | null>(null)
  const [editingCell, setEditingCell] = useState<EditingCell | null>(null)
  const [cellSaving, setCellSaving] = useState(false)
  const [selectedWeek, setSelectedWeek] = useState<Dayjs>(dayjs())
  const [dateRange, setDateRange] = useState<[Dayjs, Dayjs] | null>([getIsoWeekStart(dayjs()), getIsoWeekEnd(dayjs())])
  const [schedules, setSchedules] = useState<ListState<AttendanceScheduleDto>>(initialListState)
  const [records, setRecords] = useState<ListState<AttendanceScheduleDto>>(initialListState)
  const [storeUsers, setStoreUsers] = useState<StoreUserListDto[]>([])
  const [storeUsersLoading, setStoreUsersLoading] = useState(false)
  // 21 岁以下员工的周岁（userGuid → 岁），来自考勤员工接口；排班表姓名旁显示年龄角标（青少年时薪按年龄计，排班也要留意工时）
  const [employeeAges, setEmployeeAges] = useState<Map<string, number>>(new Map())
  const [scheduleHolidays, setScheduleHolidays] = useState<AttendanceStoreHolidayDto[]>([])
  const [scheduleHolidaysLoading, setScheduleHolidaysLoading] = useState(false)
  const [availability, setAvailability] = useState<ListState<AttendanceAvailabilityDto>>(initialListState)
  // 周排班格内展示用：本店本周全部员工上报的可上班 / 不能上班时间（与「员工可用时间」标签的分页列表互不影响）
  const [weekAvailability, setWeekAvailability] = useState<AttendanceAvailabilityDto[]>([])
  const weekAvailabilityRequestRef = useRef(0)
  // 本周已有打卡记录的排班：有打卡的班次锁定、不能拖动。known＝成功拉到了打卡（需要打卡查看权限）；没拉到时按保守规则锁今天及以前的班次。
  const [weekPunches, setWeekPunches] = useState<{ guids: ReadonlySet<string>; known: boolean }>(() => ({ guids: new Set<string>(), known: false }))
  const weekPunchesRequestRef = useRef(0)
  // 拖拽：正在拖的内容、是否按住 ⌥（复制）、当前悬停的格子
  const [dragSource, setDragSource] = useState<DragSource | null>(null)
  const [altCopy, setAltCopy] = useState(false)
  const [hoverDropKey, setHoverDropKey] = useState<string | null>(null)
  const dropBusyRef = useRef(false)
  const [punches, setPunches] = useState<ListState<AttendancePunchDto>>(initialListState)
  const [holidays, setHolidays] = useState<ListState<AttendanceStoreHolidayDto>>(initialListState)
  const [settings, setSettings] = useState<AttendanceSettingsDto | null>(null)
  const [settingsLoading, setSettingsLoading] = useState(false)
  const [holidayDrawerOpen, setHolidayDrawerOpen] = useState(false)
  const [editingHoliday, setEditingHoliday] = useState<AttendanceStoreHolidayDto | null>(null)
  const [batchHolidayDrawerOpen, setBatchHolidayDrawerOpen] = useState(false)
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
  const [deletedSchedulesOpen, setDeletedSchedulesOpen] = useState(false)
  const [holidayForm] = Form.useForm<HolidayFormValues>()
  const [batchHolidayForm] = Form.useForm<BatchHolidayFormValues>()
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
  // 考勤记录里所有时长（迟到 / 早退 / 早到 / 晚退、班段、工时、间隔、计薪、加班、用餐）统一口径：
  // 不足 60 分钟显示分钟，满 60 分钟起显示「X 小时 Y 分」（整小时不带分钟），如 412 分 → 6 小时 52 分。
  const formatExceptionDuration = useCallback((totalMinutes: number) => {
    const { hours, minutes } = splitExceptionDuration(totalMinutes)
    if (hours === 0) return t('posAdmin.scheduleAttendance.recordLabels.duration.minutes', { minutes })
    if (minutes === 0) return t('posAdmin.scheduleAttendance.recordLabels.duration.hours', { hours })
    return t('posAdmin.scheduleAttendance.recordLabels.duration.hoursMinutes', { hours, minutes })
  }, [t])
  // 后端没返回（旧版本 / 预览缺字段）时显示 --，有值时按上面的小时口径显示。
  const formatRecordMinutes = useCallback((minutes?: number) => (
    typeof minutes === 'number' ? formatExceptionDuration(minutes) : '--'
  ), [formatExceptionDuration])
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
    weekStartDate: weekStart.format('YYYY-MM-DD'),
    fromDate: weekStart.format('YYYY-MM-DD'),
    toDate: weekEnd.format('YYYY-MM-DD'),
  }), [storeCode, weekEnd, weekStart])

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
      setEmployeeAges(new Map())
      return
    }

    setStoreUsersLoading(true)
    try {
      // 一次取回该分店全部在职员工，姓名搜索在本地过滤；年龄角标另取考勤员工接口（只给 21 岁以下的周岁），取不到只是没有角标，不影响排班表
      const [result, employees] = await Promise.all([
        getStoreUsersGrid({
          storeCode,
          status: 1,
        }),
        getAttendanceEmployees(storeCode).catch((error: unknown) => {
          console.error(error)
          return []
        }),
      ])
      setStoreUsers(result.items)
      setEmployeeAges(new Map(employees.flatMap((employee) => (employee.age == null ? [] : [[employee.userGuid, employee.age] as const]))))
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.loadStoreUsersFailed'))
      setStoreUsers([])
      setEmployeeAges(new Map())
    } finally {
      setStoreUsersLoading(false)
    }
  }, [canViewStoreCode, storeCode, t])

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

  // 周排班格内的可上班 / 不能上班：整周一次拉全（后端不分页）。失败只静默清空，不打断排班；
  // 没有「员工可用时间」查看权限就不请求。请求编号防止快速切周时旧响应覆盖新周。
  const loadWeekAvailability = useCallback(async () => {
    const requestId = ++weekAvailabilityRequestRef.current
    if (!access.canViewAttendanceAvailability || !storeCode || shouldSkipVisibleStoreQuery()) {
      setWeekAvailability([])
      return
    }
    try {
      const result = await getAttendanceAvailability({
        storeCode,
        fromDate: weekStart.format('YYYY-MM-DD'),
        toDate: weekEnd.format('YYYY-MM-DD'),
      })
      if (requestId === weekAvailabilityRequestRef.current) setWeekAvailability(result.items)
    } catch (error) {
      console.error(error)
      if (requestId === weekAvailabilityRequestRef.current) setWeekAvailability([])
    }
  }, [access.canViewAttendanceAvailability, shouldSkipVisibleStoreQuery, storeCode, weekEnd, weekStart])

  const availabilityIndex = useMemo(() => buildAvailabilityIndex(weekAvailability), [weekAvailability])

  // 本周打卡：只为判断「哪些班次已有打卡、不能拖动」。失败 / 无权限时 known=false，走保守规则。
  const loadWeekPunches = useCallback(async () => {
    const requestId = ++weekPunchesRequestRef.current
    const unknown = { guids: new Set<string>(), known: false }
    if (!access.canViewAttendancePunches || !storeCode || shouldSkipVisibleStoreQuery()) {
      setWeekPunches(unknown)
      return
    }
    try {
      const result = await getAttendancePunches({
        storeCode,
        fromDate: weekStart.format('YYYY-MM-DD'),
        toDate: weekEnd.format('YYYY-MM-DD'),
      })
      if (requestId === weekPunchesRequestRef.current) setWeekPunches({ guids: collectPunchScheduleGuids(result.items), known: true })
    } catch (error) {
      console.error(error)
      if (requestId === weekPunchesRequestRef.current) setWeekPunches(unknown)
    }
  }, [access.canViewAttendancePunches, shouldSkipVisibleStoreQuery, storeCode, weekEnd, weekStart])

  const loadAvailability = useCallback(async (page = availability.page, pageSize = availability.pageSize) => {
    if (shouldSkipVisibleStoreQuery()) {
      setAvailability({ ...initialListState, page, pageSize })
      return
    }

    setAvailability((prev) => ({ ...prev, loading: true, page, pageSize }))
    try {
      // 可上班时间按所选日期范围查（后端 FromDate / ToDate 过滤 AvailableDate）；
      // 不带 weekStartDate，否则范围起点不是周一时会被「周起始日 = 起点」条件筛空。
      const result = await getAttendanceAvailability({ ...queryParams(page, pageSize), weekStartDate: undefined })
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
      void loadWeekAvailability()
      void loadWeekPunches()
    }
    if (activeTab === 'records' && access.canViewAttendancePunches) void loadRecords(page)
    if (activeTab === 'availability') void loadAvailability(page)
    if (activeTab === 'punches') void loadPunches(page)
    if (activeTab === 'holidays') void loadHolidays(page)
    if (activeTab === 'settings') void loadSettings()
  }, [access.canViewAttendancePunches, activeTab, loadAvailability, loadHolidays, loadPunches, loadRecords, loadScheduleHolidays, loadSchedules, loadSettings, loadStoreUsers, loadWeekAvailability, loadWeekPunches])

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

  // 切页签 / 换分店 / 换周后，上一张表里的编辑浮层已经没有意义，直接收起
  useEffect(() => {
    setEditingCell(null)
  }, [activeTab, storeCode, selectedWeek])

  useEffect(() => {
    if (activeTab === 'records' && !access.canViewAttendancePunches) setActiveTab('schedules')
  }, [access.canViewAttendancePunches, activeTab])

  // 旧地址 /pos-admin/attendance-review 重定向到 ?tab=review：本页已挂载（保活）时也要切过去。
  useEffect(() => {
    if (wantsReviewTab && canReviewTab) setActiveTab('review')
  }, [canReviewTab, wantsReviewTab])

  // 权限与当前标签对不上时回落：没有审核权限不停在审核；只有审核权限没有排班查看权限时不停在排班类标签。
  useEffect(() => {
    if (activeTab === 'review' && !canReviewTab) setActiveTab('schedules')
    else if (activeTab !== 'review' && !canViewSchedulePart && canReviewTab) setActiveTab('review')
  }, [activeTab, canReviewTab, canViewSchedulePart])

  // 切标签同时把「考勤审核」写进地址（刷新、复制链接能回到同一标签）；切走时清掉，避免 ?tab=review 残留把之后的访问又拉回审核。
  const handleTabChange = (key: string) => {
    setActiveTab(key as TabKey)
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous)
      if (key === 'review') next.set('tab', REVIEW_TAB_QUERY)
      else next.delete('tab')
      return next
    }, { replace: true })
  }

  // 子标签上的待审数（侧栏「排班考勤」角标同源）；只在本页处于前台时拉取，避免保活缓存期间重复请求。
  const pendingReviewCount = useAttendancePendingCount(
    canReviewTab && location.pathname === SCHEDULE_ATTENDANCE_PATH,
    location.pathname,
  )

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

  const holidayStatusTag = (value: AttendanceHolidayBusinessStatus) => {
    const color = value === 'Open' ? 'green' : value === 'Partial' ? 'orange' : 'red'
    return <Tag color={color}>{t(`posAdmin.scheduleAttendance.status.holiday.${value}`, value)}</Tag>
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
        setEditingCell(null)
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
        age: employeeAges.get(user.userGuid),
        schedulesByDate: {},
      })
    })

    schedules.items.forEach((schedule) => {
      if (storeCode && schedule.storeCode !== storeCode) return

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

    // 全部员工 + 本周班次的汇总（按员工查班次、拖拽落点判定用）；搜索框只做高亮，不再过滤
    return Array.from(rows.values())
  }, [employeeAges, schedules.items, storeCode, storeNameMap, storeUsers])

  const weekDateKeys = useMemo(() => weekDays.map((day) => day.format('YYYY-MM-DD')), [weekDays])

  // ===== 按天分列的周视图数据 =====
  // 本周班次（已按分店过滤）；员工姓名 / 颜色统一从这里取
  const weekSchedules = useMemo(
    () => schedules.items.filter((schedule) => !storeCode || schedule.storeCode === storeCode),
    [schedules.items, storeCode],
  )
  const userNameMap = useMemo(() => {
    const map = new Map<string, string>()
    storeUsers.forEach((user) => map.set(user.userGuid, displayStoreUserName(user)))
    weekSchedules.forEach((schedule) => {
      if (!map.has(schedule.userGuid)) map.set(schedule.userGuid, schedule.userName || schedule.userGuid)
    })
    return map
  }, [storeUsers, weekSchedules])
  const nameOf = useCallback((userGuid: string) => userNameMap.get(userGuid) ?? userGuid, [userNameMap])
  // 颜色按「全部在职员工 + 本周有班次的人」一起分配：排班前后颜色不变，同一批人不撞色
  const staffColors = useMemo(
    () => assignStaffColors(Array.from(userNameMap, ([userGuid, name]) => ({ userGuid, name }))),
    [userNameMap],
  )
  const rowByGuid = useMemo(() => new Map(scheduleRows.map((row) => [row.userGuid, row])), [scheduleRows])
  // 每天的班次：按「开始时间 → 员工名」排好
  const daySchedules = useMemo(() => groupSchedulesByDay(weekSchedules, weekDateKeys, nameOf), [nameOf, weekDateKeys, weekSchedules])
  const schedulesByUserDate = useMemo(() => {
    const map = new Map<string, AttendanceScheduleDto[]>()
    weekSchedules.forEach((schedule) => {
      const key = `${schedule.userGuid}|${schedule.workDate.slice(0, 10)}`
      map.set(key, [...(map.get(key) ?? []), schedule])
    })
    return map
  }, [weekSchedules])
  const staffLegend = useMemo(() => buildStaffLegend(weekSchedules, nameOf), [nameOf, weekSchedules])
  const existingOf = useCallback(
    (userGuid: string, dateKey: string) => schedulesByUserDate.get(`${userGuid}|${dateKey}`) ?? [],
    [schedulesByUserDate],
  )
  const availabilityOf = useCallback(
    (userGuid: string, dateKey: string) => getDayAvailability(availabilityIndex, userGuid, dateKey),
    [availabilityIndex],
  )
  // 每天「上报了可上班、但当天没排」的员工
  const availableUnscheduledByDay = useMemo(() => new Map(weekDateKeys.map((dateKey) => [
    dateKey,
    findAvailableUnscheduled(dateKey, storeUsers.map((user) => user.userGuid), daySchedules.get(dateKey) ?? [], availabilityOf),
  ])), [availabilityOf, daySchedules, storeUsers, weekDateKeys])
  // 高亮：点图例选中的人 + 搜索框命中的人；有高亮对象时其余卡片变淡（搜索无命中则全部变淡，提示没找到）
  const keywordMatches = useMemo(
    () => matchStaffKeyword(Array.from(userNameMap, ([userGuid, name]) => ({ userGuid, name })), staffKeyword),
    [staffKeyword, userNameMap],
  )
  const highlighting = legendSelected.size > 0 || staffKeyword.trim() !== ''
  const isHighlighted = (userGuid: string) => legendSelected.has(userGuid) || keywordMatches.has(userGuid)

  // ===== 手机横屏看板 =====
  const shellMode = activeTab === 'schedules' && storeCode
    ? getShellMode({ phone: phoneEnv.phone, landscape: phoneEnv.landscape, requested: shellRequested, autoDismissed: shellDismissed })
    : 'none'
  // 旋转兜底模式下编辑浮层、弹窗不会跟着旋转，所以只读；把手机转过来（landscape）才可编辑
  const shellReadOnly = shellMode === 'rotated'
  // 回到竖屏后，之前点的「退出」不再生效
  useEffect(() => {
    setShellDismissed((prev) => nextAutoDismissed(phoneEnv.landscape, prev))
  }, [phoneEnv.landscape])
  // 点「横屏查看整周」：尽力锁定横屏（Android），锁不了就由 rotated 模式兜底（iPhone）
  const enterShell = () => {
    setEditingCell(null)
    setShellRequested(true)
    void tryLockLandscape()
  }
  const exitShell = () => {
    setShellRequested(false)
    if (phoneEnv.landscape) setShellDismissed(true)
    releaseLandscape()
  }

  // 批量排班弹窗的数据
  const batchDays = useMemo<BatchModalDay[]>(() => weekDays.map((day, index) => {
    const dateKey = day.format('YYYY-MM-DD')
    return {
      dateKey,
      dateLabel: day.format('MM-DD'),
      weekdayLabel: t(`posAdmin.scheduleAttendance.weekdays.${index}`),
      isoIndex: index,
      holiday: holidaysByDate.get(dateKey)?.map((item) => item.holidayName).join(' · '),
      scheduledCount: summarizeDayList(daySchedules.get(dateKey) ?? []).people,
    }
  }), [daySchedules, holidaysByDate, t, weekDays])
  const batchUsers = useMemo<BatchModalUser[]>(() => storeUsers.map((user) => {
    const total = summarizeSchedules(weekSchedules.filter((schedule) => schedule.userGuid === user.userGuid))
    const daysWith = (kind: 'available' | 'unavailable') => batchDays
      .filter((day) => (availabilityOf(user.userGuid, day.dateKey)?.[kind].length ?? 0) > 0)
      .map((day) => day.weekdayLabel)
    return {
      userGuid: user.userGuid,
      name: displayStoreUserName(user),
      age: employeeAges.get(user.userGuid),
      color: staffColors.get(user.userGuid) ?? FALLBACK_STAFF_COLOR,
      weekShifts: total.shifts,
      weekHours: total.hours,
      availableDays: daysWith('available'),
      unavailableDays: daysWith('unavailable'),
    }
  }), [availabilityOf, batchDays, employeeAges, staffColors, storeUsers, weekSchedules])

  const isSameEditingCell = (left: EditingCell | null, right: EditingCell) => (
    left?.userGuid === right.userGuid && left.dateKey === right.dateKey && left.scheduleGuid === right.scheduleGuid
  )

  // 保存单个单元格：已有班次走更新；空白格新建为草稿，可同时铺到本周其他空白日期
  const saveCellSchedule = async (
    row: ScheduleRow,
    dateKey: string,
    schedule: AttendanceScheduleDto | undefined,
    values: CellEditorValues,
  ) => {
    const targetStoreCode = schedule?.storeCode ?? (row.storeCode || storeCode)
    if (!targetStoreCode || !access.canEditAttendanceSchedule || !canEditStoreCode(targetStoreCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    setCellSaving(true)
    try {
      let savedDateKeys = [dateKey]
      if (schedule) {
        const [payload] = buildScheduleSavePayloads({
          storeCode: targetStoreCode,
          userGuid: schedule.userGuid,
          dateKeys: [dateKey],
          start: values.start,
          end: values.end,
          remark: values.remark,
          status: values.status,
          mealChoice: values.mealChoice,
          // 选「自动」要显式带恢复标记，后端才会清掉原来手动指定的次数
          resetAutoMeal: true,
        })
        await updateAttendanceSchedule(schedule.scheduleGuid, payload)
        message.success(t('message.updateSuccess'))
      } else {
        const payloads = buildScheduleSavePayloads({
          storeCode: targetStoreCode,
          userGuid: row.userGuid,
          dateKeys: [dateKey, ...values.extraDateKeys],
          start: values.start,
          end: values.end,
          remark: values.remark,
          status: 'Draft',
          mealChoice: values.mealChoice,
        })
        const results = await Promise.allSettled(payloads.map((payload) => createAttendanceSchedule(payload)))
        const failedDates = payloads.filter((_, index) => results[index].status === 'rejected').map((payload) => payload.workDate)
        // 全部失败：抛出让编辑浮层保持打开，输入不丢
        if (failedDates.length === payloads.length) throw (results[0] as PromiseRejectedResult).reason
        savedDateKeys = payloads.filter((_, index) => results[index].status === 'fulfilled').map((payload) => payload.workDate)
        if (failedDates.length) {
          message.warning(t('posAdmin.scheduleAttendance.cellEditor.partialFailed', {
            ok: savedDateKeys.length,
            failed: failedDates.length,
            dates: failedDates.map((key) => key.slice(5)).join('、'),
          }))
        } else {
          message.success(t('message.createSuccess'))
        }
      }

      // 按天分列的视图里没有「右侧下一个空白格」，保存后直接关闭浮层
      setEditingCell(null)
      void loadSchedules()
    } catch (error) {
      console.error(error)
      message.error(t('posAdmin.scheduleAttendance.messages.saveScheduleFailed'))
    } finally {
      setCellSaving(false)
    }
  }

  // ===== 拖拽：移动 / ⌥ 复制 / 托盘添加，以及班次右上角「×」删除 =====
  // 撤销回调里要拿到「最新的」loadSchedules：用户可能在 5 秒内已经切了周，用旧闭包会把旧周数据刷回来
  const loadSchedulesRef = useRef(loadSchedules)
  loadSchedulesRef.current = loadSchedules
  const todayKey = dayjs().format('YYYY-MM-DD')

  const endDrag = () => {
    setDragSource(null)
    setAltCopy(false)
    setHoverDropKey(null)
  }

  // 拖动开始时不能立刻改界面：同步 setState 会让浏览器在首帧取消这次拖动，推迟到下一个宏任务
  const handleChipDragStart = (event: DragEvent<HTMLElement>, schedule: AttendanceScheduleDto) => {
    event.dataTransfer.effectAllowed = 'copyMove'
    event.dataTransfer.setData('text/plain', schedule.scheduleGuid)
    const alt = event.altKey
    const source: DragSource = {
      kind: alt ? 'copy' : 'move',
      schedule,
      start: normalizeHm(schedule.startTime),
      end: normalizeHm(schedule.endTime),
    }
    setEditingCell(null)
    window.setTimeout(() => {
      setAltCopy(alt)
      setDragSource(source)
    }, 0)
  }

  const handlePresetDragStart = (event: DragEvent<HTMLElement>, preset: ShiftPreset) => {
    event.dataTransfer.effectAllowed = 'copy'
    event.dataTransfer.setData('text/plain', `${preset.start}-${preset.end}`)
    setEditingCell(null)
    window.setTimeout(() => setDragSource({ kind: 'preset', start: preset.start, end: preset.end }), 0)
  }

  const dragErrorMessage = (error: unknown) => (
    error instanceof Error && error.message ? error.message : t('posAdmin.scheduleAttendance.messages.saveScheduleFailed')
  )

  // 带「撤销」的提示：5 秒内可撤销，撤销失败要如实告诉用户
  const showUndoToast = (text: string, undo: () => Promise<void>, kind: 'success' | 'warning' = 'success') => {
    const key = `sce-undo-${Date.now()}`
    message[kind]({
      key,
      duration: 5,
      content: (
        <span>
          {text}
          <a
            role="button"
            style={{ marginInlineStart: 12 }}
            onClick={async () => {
              message.destroy(key)
              try {
                await undo()
                message.success(t('posAdmin.scheduleAttendance.drag.undone'))
              } catch (error) {
                console.error(error)
                message.error(t('posAdmin.scheduleAttendance.drag.undoFailed'))
              }
              void loadSchedulesRef.current()
            }}
          >
            {t('posAdmin.scheduleAttendance.drag.undo')}
          </a>
        </span>
      ),
    })
  }

  // 松手后的落地：
  // - 托盘 / ⌥ 复制：新建草稿；
  // - 同员工换日期：调用「更新排班」（保持原状态）；
  // - 换员工：后端更新接口不改员工，所以「新建 + 删除原班次」。新建接口一律建成草稿，有效班次要再补一次更新把状态改回去。
  const runDrop = async (source: DragSource, row: ScheduleRow, dateKey: string) => {
    if (dropBusyRef.current) return
    const targetStoreCode = row.storeCode || storeCode
    if (!targetStoreCode || !access.canEditAttendanceSchedule || !canEditStoreCode(targetStoreCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    dropBusyRef.current = true
    const params = { name: row.userName || '--', date: dateKey.slice(5) }
    const origin = source.schedule
    const originDateKey = origin ? dayjs(origin.workDate).format('YYYY-MM-DD') : undefined
    const isMove = source.kind === 'move'
    try {
      if (origin && source.kind === 'copy') {
        const created = await createAttendanceSchedule(buildCopyPayload(origin, row.userGuid, dateKey))
        showUndoToast(t('posAdmin.scheduleAttendance.drag.copied', params), async () => {
          await deleteAttendanceSchedule(created.scheduleGuid)
        })
      } else if (origin && originDateKey && isMove && origin.userGuid === row.userGuid) {
        await updateAttendanceSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, dateKey))
        showUndoToast(
          t(origin.status === 'Active' ? 'posAdmin.scheduleAttendance.drag.movedPublished' : 'posAdmin.scheduleAttendance.drag.moved', params),
          async () => {
            await updateAttendanceSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, originDateKey))
          },
          origin.status === 'Active' ? 'warning' : 'success',
        )
      } else if (origin && originDateKey && isMove) {
        const created = await createAttendanceSchedule(buildTransferCreatePayload(origin, row.userGuid, dateKey))
        let downgraded = false
        if (origin.status === 'Active') {
          try {
            await updateAttendanceSchedule(created.scheduleGuid, buildTransferCreatePayload(origin, row.userGuid, dateKey))
          } catch (error) {
            console.error(error)
            downgraded = true
          }
        }
        try {
          await deleteAttendanceSchedule(origin.scheduleGuid)
        } catch (error) {
          console.error(error)
          message.warning(t('posAdmin.scheduleAttendance.drag.transferDeleteFailed'))
          return
        }
        showUndoToast(
          t(origin.status === 'Active' && !downgraded ? 'posAdmin.scheduleAttendance.drag.movedPublished' : 'posAdmin.scheduleAttendance.drag.moved', params),
          async () => {
            // 恢复已删除排班会变成草稿（后端不留原状态），有效班次要再改回有效
            await deleteAttendanceSchedule(created.scheduleGuid)
            await restoreAttendanceSchedule(origin.scheduleGuid)
            if (origin.status === 'Active') {
              await updateAttendanceSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, originDateKey))
            }
          },
          origin.status === 'Active' ? 'warning' : 'success',
        )
      }
    } catch (error) {
      console.error(error)
      message.error(t(source.kind === 'preset' || source.kind === 'copy' ? 'posAdmin.scheduleAttendance.drag.addFailed' : 'posAdmin.scheduleAttendance.drag.moveFailed', { reason: dragErrorMessage(error) }))
    } finally {
      dropBusyRef.current = false
      void loadSchedulesRef.current()
    }
  }

  // 编辑浮层里「改给其他员工」：日期、时间不变，直接复用拖拽换人的落地逻辑（新建给对方 + 删除原班次，5 秒内可撤销）
  const transferSchedule = async (schedule: AttendanceScheduleDto, targetUserGuid: string) => {
    setEditingCell(null)
    await runDrop(
      { kind: 'move', schedule, start: normalizeHm(schedule.startTime), end: normalizeHm(schedule.endTime) },
      rowFor(targetUserGuid),
      schedule.workDate.slice(0, 10),
    )
  }

  // 班次右上角「×」：现有删除接口（软删除，可在「已删除排班」找回）
  const deleteScheduleViaX = async (schedule: AttendanceScheduleDto) => {
    try {
      await deleteAttendanceSchedule(schedule.scheduleGuid)
      message.success(t('message.deleteSuccess'))
      setEditingCell(null)
    } catch (error) {
      console.error(error)
      message.error(dragErrorMessage(error))
    } finally {
      void loadSchedulesRef.current()
    }
  }

  // 某个格子对当前拖拽内容的落点：类名（可放 / 提醒 / 不能放 / 当前悬停）、事件、角标与「放到这里」提示
  const effectiveDragSource: DragSource | null = dragSource && dragSource.kind !== 'preset'
    ? { ...dragSource, kind: altCopy ? 'copy' : 'move' }
    : dragSource

  // 为员工找一个可用于落地 / 编辑的「行」：优先用已加载的，没有（例如已离职员工的历史班次）就按班次现拼一个
  const rowFor = (userGuid: string, schedule?: AttendanceScheduleDto): ScheduleRow => (
    rowByGuid.get(userGuid) ?? {
      rowKey: `${schedule?.storeCode ?? storeCode ?? ''}-${userGuid}`,
      storeCode: schedule?.storeCode ?? storeCode ?? '',
      storeName: schedule?.storeName,
      userGuid,
      userName: schedule?.userName ?? nameOf(userGuid),
      schedulesByDate: {},
    }
  )

  // 某一天（整列）对当前拖拽内容的落点：类名（可放 / 提醒 / 不能放 / 当前悬停）、事件、角标与「放到这里」占位。
  // 落点只决定「哪一天」：员工取自被拖的卡片本身；托盘里的常用班次放下后打开排班弹窗（预选这一天和这个时段）。
  const getDayDrop = (dateKey: string): WeekBoardDayDrop => {
    const source = effectiveDragSource
    if (!source) return {}
    const editable = access.canEditAttendanceSchedule && canEditStoreCode(storeCode)
    const origin = source.schedule
    const originUser = origin?.userGuid
    let verdict: DropVerdict | undefined
    if (editable) {
      if (source.kind === 'preset') {
        verdict = { state: 'ok' }
      } else if (origin && originUser) {
        verdict = getDropVerdict(
          source,
          { userGuid: originUser, dateKey, schedules: existingOf(originUser, dateKey), availability: availabilityOf(originUser, dateKey) },
          { userGuid: originUser, dateKey: origin.workDate.slice(0, 10) },
        )
      }
    }
    const droppable = verdict?.state === 'ok' || verdict?.state === 'warn'
    const hovered = droppable && hoverDropKey === dateKey

    const className = [
      verdict && verdict.state !== 'noop' ? `sce-col--drop-${verdict.state}` : '',
      hovered ? 'sce-col--drop-target' : '',
    ].filter(Boolean).join(' ')

    const props: WeekBoardDayDrop['props'] = {
      onDragOver: (event: DragEvent<HTMLDivElement>) => {
        if (!source) return
        if (source.kind !== 'preset' && event.altKey !== altCopy) setAltCopy(event.altKey)
        if (!droppable) {
          event.dataTransfer.dropEffect = 'none'
          return
        }
        // 必须 preventDefault 浏览器才允许在这里放下
        event.preventDefault()
        event.dataTransfer.dropEffect = source.kind === 'move' ? 'move' : 'copy'
        if (hoverDropKey !== dateKey) setHoverDropKey(dateKey)
      },
      onDragLeave: (event: DragEvent<HTMLDivElement>) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setHoverDropKey((prev) => (prev === dateKey ? null : prev))
        }
      },
      onDrop: (event: DragEvent<HTMLDivElement>) => {
        if (!droppable) return
        event.preventDefault()
        endDrag()
        if (source.kind === 'preset') {
          openBatchModal({ dateKeys: [dateKey], start: source.start, end: source.end })
        } else if (origin) {
          void runDrop(source, rowFor(origin.userGuid, origin), dateKey)
        }
      },
    }

    let flag: ReactElement | null = null
    if (verdict && verdict.state !== 'noop') {
      const flagKind = verdict.state === 'ok' ? (hovered ? 'ok' : null) : verdict.state
      const flagText = verdict.state === 'ok'
        ? t('posAdmin.scheduleAttendance.drag.flagOk')
        : verdict.state === 'bad'
          ? t('posAdmin.scheduleAttendance.drag.flagOverlap')
          : t(verdict.reason === 'unavailable' ? 'posAdmin.scheduleAttendance.drag.flagUnavailable' : 'posAdmin.scheduleAttendance.drag.flagOutside')
      if (flagKind) flag = <span className={`sce-drop-flag sce-drop-flag--${flagKind}`}>{flagText}</span>
    }
    const noteKey = source.kind === 'preset' ? 'dropPreset' : source.kind === 'copy' ? 'dropCopy' : 'dropMove'
    const slot = hovered ? (
      <div className="sce-slot">
        {t(`posAdmin.scheduleAttendance.drag.${noteKey}`, { name: origin ? nameOf(origin.userGuid) : '', date: dateKey.slice(5) })}
      </div>
    ) : null
    return { className, props, flag, slot }
  }

  // 把就地编辑浮层锚定在被点的单元格 / 班次块上；内容在打开时才渲染，关闭即销毁以重置表单
  const withCellEditor = (
    row: ScheduleRow,
    date: Dayjs,
    cell: EditingCell,
    schedule: AttendanceScheduleDto | undefined,
    trigger: ReactElement,
  ) => {
    const dateKey = date.format('YYYY-MM-DD')
    const occupied = getOccupiedDateKeys(row.schedulesByDate)
    const editorOpen = isSameEditingCell(editingCell, cell)
    const editor = () => (
          <ScheduleCellEditor
            employeeName={row.userName || '--'}
            dateLabel={`${date.format('MM-DD')} ${t(`posAdmin.scheduleAttendance.weekdays.${date.isoWeekday() - 1}`)}`}
            currentDateKey={dateKey}
            schedule={schedule}
            weekDays={weekDays.map((day, index) => {
              const key = day.format('YYYY-MM-DD')
              return {
                dateKey: key,
                weekdayLabel: t(`posAdmin.scheduleAttendance.weekdays.${index}`),
                occupied: occupied.has(key),
                isoIndex: index,
              }
            })}
            saving={cellSaving}
            onSave={(values) => void saveCellSchedule(row, dateKey, schedule, values)}
            transfer={schedule && schedule.status !== 'Cancelled' && access.canEditAttendanceSchedule && canEditStoreCode(schedule.storeCode) ? {
              options: storeUsers
                .filter((user) => user.userGuid !== schedule.userGuid)
                .map((user) => ({ value: user.userGuid, label: displayStoreUserName(user) })),
              // 已有打卡记录的班次不能换人：打卡、审批都挂在原员工身上
              disabledReason: isScheduleLocked({
                scheduleGuid: schedule.scheduleGuid,
                workDateKey: dateKey,
                todayKey,
                punchScheduleGuids: weekPunches.guids,
                canViewPunches: weekPunches.known,
              }) ? t('posAdmin.scheduleAttendance.cellEditor.transfer.locked') : undefined,
              onTransfer: (targetUserGuid: string) => void transferSchedule(schedule, targetUserGuid),
            } : undefined}
            onDelete={() => schedule && confirmDeleteSchedule(schedule)}
            onClose={() => setEditingCell(null)}
          />
    )
    if (phoneEnv.phone) {
      return (
        <>
          {cloneElement(trigger, { onClick: () => setEditingCell(cell) })}
          <Modal
            open={editorOpen}
            centered
            destroyOnHidden
            footer={null}
            closable={false}
            width={420}
            className="sce-editor-modal"
            onCancel={() => setEditingCell(null)}
          >
            {editor()}
          </Modal>
        </>
      )
    }
    return (
      <Popover
        open={editorOpen}
        trigger="click"
        placement="bottom"
        destroyOnHidden
        onOpenChange={(open) => {
          // 点别处关闭时只清掉自己，避免把刚点开的另一个格子也一起关掉
          setEditingCell((prev) => (open ? cell : isSameEditingCell(prev, cell) ? null : prev))
        }}
        content={editor()}
      >
        {trigger}
      </Popover>
    )
  }

  // 打开批量排班弹窗：可预填日期 / 员工 / 时段（点某天的「添加班次」「可上班未排」，或把常用班次拖到某天）
  const openBatchModal = (initial?: BatchModalInitial) => {
    if (!storeCode || !access.canEditAttendanceSchedule || !canEditStoreCode(storeCode)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }
    setEditingCell(null)
    setBatchInitial(initial ?? null)
    setBatchOpen(true)
  }

  // 一张班次卡片：第一行员工名（21 岁以下带年龄），第二行时间 + 工时；
  // 底色 = 员工，虚线斜纹 = 草稿，灰底划线 = 已取消。点开是原来的编辑浮层，可拖到别的天（⌥ 复制）。
  const renderCard = (schedule: AttendanceScheduleDto) => {
    const dateKey = schedule.workDate.slice(0, 10)
    const color = staffColors.get(schedule.userGuid) ?? FALLBACK_STAFF_COLOR
    const name = nameOf(schedule.userGuid)
    const age = employeeAges.get(schedule.userGuid)
    const canEditSchedule = !shellReadOnly && access.canEditAttendanceSchedule && canEditStoreCode(schedule.storeCode)
    const cardCell: EditingCell = { userGuid: schedule.userGuid, dateKey, scheduleGuid: schedule.scheduleGuid }
    // 班次与员工上报的时间冲突：卡片上加 ⚠，悬停说明原因（不阻止保存，仅提醒排班的人）
    const conflict = getScheduleAvailabilityConflict(schedule, availabilityOf(schedule.userGuid, dateKey))
    // 已有打卡记录（或无法确认且已到班）的班次锁定：不能拖动，「×」仍可用
    const locked = canEditSchedule && schedule.status !== 'Cancelled' && isScheduleLocked({
      scheduleGuid: schedule.scheduleGuid,
      workDateKey: dateKey,
      todayKey,
      punchScheduleGuids: weekPunches.guids,
      canViewPunches: weekPunches.known,
    })
    const canDrag = canEditSchedule && schedule.status !== 'Cancelled' && !locked
    const dragging = dragSource?.schedule?.scheduleGuid === schedule.scheduleGuid && !altCopy
    const mealBadge = getMealBadge(schedule.mealBreakCount)
    const mealPaid = mealBadge
      ? getMealSummary(normalizeHm(schedule.startTime), normalizeHm(schedule.endTime), toMealChoice(schedule.mealBreakCount))?.paidHours
      : undefined
    const shiftHours = getScheduleHours(schedule).hours
    const timeText = `${formatTime(schedule.startTime)}–${formatTime(schedule.endTime)}`
    const dim = highlighting && !isHighlighted(schedule.userGuid)
    const hi = highlighting && isHighlighted(schedule.userGuid)
    // 悬停提示：姓名、状态、冲突原因、手动用餐的计薪、备注、锁定原因
    const title = [
      name,
      statusLabel('schedule', schedule.status),
      conflict ? t(`posAdmin.scheduleAttendance.weekAvailability.${conflict === 'unavailable' ? 'conflictUnavailable' : 'conflictOutside'}`) : '',
      mealBadge && mealPaid !== undefined ? t('posAdmin.scheduleAttendance.cellEditor.meal.badgeTip', { hours: mealPaid }) : '',
      schedule.remark ?? '',
      locked ? t(weekPunches.known ? 'posAdmin.scheduleAttendance.drag.locked' : 'posAdmin.scheduleAttendance.drag.lockedUnknown') : '',
    ].filter(Boolean).join(' · ')
    // 用 div 而不是 button：部分浏览器（Firefox）里 button 不能被拖动
    const card = (
      <div
        role="button"
        tabIndex={canEditSchedule ? 0 : -1}
        aria-disabled={!canEditSchedule}
        draggable={canDrag}
        title={title}
        style={staffColorVars(color)}
        onDragStart={(event) => handleChipDragStart(event, schedule)}
        onDragEnd={endDrag}
        onKeyDown={(event) => {
          // 键盘 Enter / 空格等同点击（div 没有 button 的原生行为）
          if (canEditSchedule && (event.key === 'Enter' || event.key === ' ') && event.target === event.currentTarget) {
            event.preventDefault()
            event.currentTarget.click()
          }
        }}
        className={[
          'sce-card',
          `sce-card--${schedule.status}`,
          canEditSchedule ? 'sce-card--editable' : '',
          canDrag ? 'sce-card--draggable' : '',
          locked ? 'sce-card--locked' : '',
          dragging ? 'sce-card--drag-src' : '',
          isSameEditingCell(editingCell, cardCell) ? 'sce-card--active' : '',
          conflict ? 'sce-card--conflict' : '',
          dim ? 'sce-card--dim' : '',
          hi ? 'sce-card--hi' : '',
        ].filter(Boolean).join(' ')}
      >
        <div className="sce-card-line1">
          <b>{name}</b>
          {age !== undefined ? <span className="sce-card-age">{age}</span> : null}
          {schedule.status === 'Cancelled' ? <span className="sce-card-tag">{statusLabel('schedule', 'Cancelled')}</span> : null}
        </div>
        <div className="sce-card-line2">
          <span className="sce-card-time">{timeText}</span>
          {conflict ? <WarningOutlined className="sce-card-warn" /> : null}
          {locked ? <LockOutlined className="sce-card-lock" /> : null}
          {/* 手动指定过用餐次数的班次：工时加点状下划线，具体扣餐说明在悬停提示里 */}
          {shiftHours > 0 ? <span className={`sce-card-hours${mealBadge ? ' sce-card-hours--meal' : ''}`}>{formatHours(shiftHours)}</span> : null}
        </div>
        {/* 手机上不放「×」：误触风险大，删除走编辑弹窗里的删除按钮 */}
        {canEditSchedule && !phoneEnv.phone ? (
          // 点「×」只弹删除确认，不能触发外层「点班次打开编辑浮层」
          <span className="sce-chip-x-wrap" onClick={(event) => event.stopPropagation()}>
            <Popconfirm
              title={t('posAdmin.scheduleAttendance.drag.deleteTitle', { time: timeText })}
              description={t('posAdmin.scheduleAttendance.drag.deleteDesc')}
              okText={t('common.delete')}
              cancelText={t('common.cancel')}
              okButtonProps={{ danger: true }}
              onConfirm={() => deleteScheduleViaX(schedule)}
            >
              <span
                role="button"
                tabIndex={0}
                className="sce-card-x"
                aria-label={t('posAdmin.scheduleAttendance.drag.deleteAria')}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    event.stopPropagation()
                    event.currentTarget.click()
                  }
                }}
              >
                <CloseOutlined />
              </span>
            </Popconfirm>
          </span>
        ) : null}
      </div>
    )
    return (
      <div key={schedule.scheduleGuid}>
        {canEditSchedule ? withCellEditor(rowFor(schedule.userGuid, schedule), dayjs(dateKey), cardCell, schedule, card) : card}
      </div>
    )
  }

  // 每天一列的底部：「可上班未排 N 人」（点开是预选了这一天和这些人的排班弹窗）+ 悬停才出现的「＋ 添加班次」
  const renderDayFooter = (day: WeekBoardDay) => {
    const canAdd = !shellReadOnly && access.canEditAttendanceSchedule && canEditStoreCode(storeCode)
    const free = availableUnscheduledByDay.get(day.dateKey) ?? []
    return (
      <>
        {free.length ? (
          canAdd ? (
            <button type="button" className="sce-free-note" onClick={() => openBatchModal({ dateKeys: [day.dateKey], userGuids: free })}>
              {t('posAdmin.scheduleAttendance.board.availableUnscheduled', { count: free.length })}
            </button>
          ) : (
            <span className="sce-free-note is-static">{t('posAdmin.scheduleAttendance.board.availableUnscheduled', { count: free.length })}</span>
          )
        ) : null}
        {canAdd ? (
          <button type="button" className="sce-col-add" onClick={() => openBatchModal({ dateKeys: [day.dateKey] })}>
            <PlusOutlined /> {t('posAdmin.scheduleAttendance.actions.addShift')}
          </button>
        ) : null}
      </>
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

  // 每天一列的表头数据：日期 + 星期、当天「人数 · 工时」、节假日标签
  const boardDays: WeekBoardDay[] = weekDays.map((day, index) => {
    const dateKey = day.format('YYYY-MM-DD')
    const list = daySchedules.get(dateKey) ?? []
    const stat = summarizeDayList(list)
    const dayHolidays = holidaysByDate.get(dateKey) ?? []
    const params = { people: stat.people, hours: formatHours(stat.hours) }
    return {
      dateKey,
      dateLabel: day.format('MM-DD'),
      weekdayLabel: t(`posAdmin.scheduleAttendance.weekdays.${index}`),
      weekend: index >= 5,
      statText: t('posAdmin.scheduleAttendance.board.dayStat', params),
      statTip: t('posAdmin.scheduleAttendance.board.dayStatTip', params),
      holidays: dayHolidays.length ? <Space size={[4, 2]} wrap>{dayHolidays.map(renderHolidayHeaderTag)}</Space> : null,
      schedules: list,
    }
  })

  // 顶部员工图例：颜色对照 + 本周合计；点一下只高亮他的班次，再点取消
  const legendItems: WeekBoardLegendItem[] = staffLegend.map((entry) => ({
    userGuid: entry.userGuid,
    name: entry.name,
    color: staffColors.get(entry.userGuid) ?? FALLBACK_STAFF_COLOR,
    summary: t('posAdmin.scheduleAttendance.board.legendSummary', { hours: formatHours(entry.hours), shifts: entry.shifts }),
    active: legendSelected.has(entry.userGuid),
    dimmed: highlighting && !isHighlighted(entry.userGuid),
  }))
  const toggleLegend = (userGuid: string) => {
    setLegendSelected((prev) => {
      const next = new Set(prev)
      if (next.has(userGuid)) next.delete(userGuid)
      else next.add(userGuid)
      return next
    })
  }

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
      title: t('posAdmin.scheduleAttendance.meal.column'),
      key: 'mealPaid',
      width: 170,
      render: (_, record) => {
        const meal = buildAttendanceMealPaySummary(record)
        if (!meal) return '--'
        return (
          <Space size={[4, 3]} wrap>
            <Tag>{t('posAdmin.scheduleAttendance.meal.deduction', { duration: formatExceptionDuration(meal.deductionMinutes) })}</Tag>
            {meal.pendingAddBackMinutes > 0 ? (
              <Tag color="gold">{t('posAdmin.scheduleAttendance.meal.pendingAddBack', { duration: formatExceptionDuration(meal.pendingAddBackMinutes) })}</Tag>
            ) : null}
            {meal.approvedAddBackMinutes > 0 ? (
              <Tag color="green">{t('posAdmin.scheduleAttendance.meal.approvedAddBack', { duration: formatExceptionDuration(meal.approvedAddBackMinutes) })}</Tag>
            ) : null}
            {meal.paidMinutes !== undefined ? (
              <Typography.Text strong>{t('posAdmin.scheduleAttendance.meal.paid', { value: formatRecordMinutes(meal.paidMinutes) })}</Typography.Text>
            ) : null}
          </Space>
        )
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
              {t('posAdmin.scheduleAttendance.recordLabels.late', { duration: formatExceptionDuration(summary.lateMinutes) })}
            </Tag>
            <Tag color={summary.earlyLeaveMinutes ? 'red' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.earlyLeave', { duration: formatExceptionDuration(summary.earlyLeaveMinutes) })}
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
            <Tag>{t('posAdmin.scheduleAttendance.recordLabels.earlyArrival', { duration: formatExceptionDuration(summary.earlyOvertimeMinutes) })}</Tag>
            <Tag>{t('posAdmin.scheduleAttendance.recordLabels.lateDeparture', { duration: formatExceptionDuration(summary.lateOvertimeMinutes) })}</Tag>
            <Tag color={summary.candidateOvertimeMinutes ? 'gold' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.candidateOvertime', { duration: formatExceptionDuration(summary.candidateOvertimeMinutes) })}
            </Tag>
            <Tag color={summary.approvedOvertimeMinutes ? 'green' : 'default'}>
              {t('posAdmin.scheduleAttendance.recordLabels.approvedOvertime', { duration: formatExceptionDuration(summary.approvedOvertimeMinutes) })}
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
        // 接口单次最多返回 1000 条（按采集时间倒序）；满页就按偏移继续取下一页，直到取完，
        // 单日样本再密也能完整加载。仅在超过页数上限（防异常数据无限翻页）时才放弃。
        for (let page = 0; ; page += 1) {
          if (sampleRequestIdRef.current !== requestId) return
          if (page >= ATTENDANCE_LOCATION_SAMPLE_MAX_PAGES) {
            throw new Error(ATTENDANCE_LOCATION_SAMPLES_TRUNCATED)
          }
          const pageSamples = await getAttendanceLocationSamples({
            storeCode: record.storeCode,
            userGuid: record.userGuid,
            fromDate: range.fromDate,
            toDate: range.toDate,
            storeTimeZone: trajectoryTimeZone,
            offset: page * ATTENDANCE_LOCATION_SAMPLE_LIMIT,
          })
          for (const sample of pageSamples) samplesByGuid.set(sample.sampleGuid, sample)
          if (pageSamples.length < ATTENDANCE_LOCATION_SAMPLE_LIMIT) break
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
      {activeTab === 'schedules' ? (
        <Input
          allowClear
          prefix={<SearchOutlined />}
          placeholder={t('posAdmin.scheduleAttendance.board.searchPlaceholder')}
          style={{ width: 220 }}
          value={staffKeyword}
          onChange={(event) => setStaffKeyword(event.target.value)}
        />
      ) : (
        <Input
          allowClear
          placeholder={t('posAdmin.scheduleAttendance.filters.userGuid')}
          style={{ width: 220 }}
          value={userGuid}
          onChange={(event) => setUserGuid(event.target.value)}
        />
      )}
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
        // 新增「用餐与计薪」列（170px）后同步加宽，避免挤压已有列。
        scroll={{ x: 2070 }}
        size="small"
      />
    ),
  } : null

  // 周视图看板：普通页面和手机横屏看板共用一份（横屏看板里不放状态图例，省高度）
  const renderWeekBoard = (inShell: boolean) => (
    <WeekBoard
      days={boardDays}
      legend={legendItems}
      legendCaption={t('posAdmin.scheduleAttendance.board.legendCaption', { count: staffLegend.length })}
      statusLegend={inShell ? null : (
        <span className="sce-status-legend">
          <span><i className="is-Active" />{statusLabel('schedule', 'Active')}</span>
          <span><i className="is-Draft" />{statusLabel('schedule', 'Draft')}</span>
          <span><i className="is-Cancelled" />{statusLabel('schedule', 'Cancelled')}</span>
        </span>
      )}
      loading={schedules.loading || storeUsersLoading || scheduleHolidaysLoading}
      empty={!storeCode ? (
        <Empty description={t('posAdmin.scheduleAttendance.selectStoreToSchedule')} />
      ) : !weekSchedules.length && !schedules.loading ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={t('posAdmin.scheduleAttendance.board.emptyWeek')}
        >
          {!shellReadOnly && access.canEditAttendanceSchedule && canEditSelectedStore ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={() => openBatchModal()}>
              {t('posAdmin.scheduleAttendance.batch.openButton')}
            </Button>
          ) : null}
        </Empty>
      ) : undefined}
      onLegendClick={toggleLegend}
      renderCard={renderCard}
      getDayDrop={getDayDrop}
      renderDayFooter={renderDayFooter}
    />
  )

  const scheduleTabItems = [
    {
      key: 'schedules',
      label: t('posAdmin.scheduleAttendance.tabs.schedules'),
      children: (
        <>
          {storeCode ? (
            <div className="sce-strip">
              {storeUsersLoading ? (
                <Tag color="processing" icon={<LoadingOutlined />} style={{ marginInlineEnd: 0 }}>
                  {t('posAdmin.scheduleAttendance.cellEditor.loadingStaff')}
                </Tag>
              ) : (
                <Tag color="success" icon={<CheckCircleOutlined />} style={{ marginInlineEnd: 0 }}>
                  {t('posAdmin.scheduleAttendance.board.summary', { scheduled: staffLegend.length, total: storeUsers.length })}
                </Tag>
              )}
              {staffKeyword.trim() ? (
                <Typography.Text type="secondary">
                  {t('posAdmin.scheduleAttendance.board.keywordMatches', { count: keywordMatches.size })}
                </Typography.Text>
              ) : null}
              {/* 手机上没在看板里（竖着拿，或横屏时点过「退出」）：点一下进入横屏看板（Android 会锁定横屏，iPhone 用旋转兜底） */}
              {phoneEnv.phone && shellMode === 'none' ? (
                <Button size="small" type="primary" ghost icon={<FullscreenOutlined />} onClick={enterShell}>
                  {t('posAdmin.scheduleAttendance.board.landscapeEnter')}
                </Button>
              ) : null}
            </div>
          ) : null}
          {/* 拖拽托盘：5 个固定常用班次，拖到某一天会打开排班弹窗（已预选这一天和这个时段） */}
          {/* 手机触屏不能拖拽，托盘在手机上没用还会撑宽页面，不显示 */}
          {storeCode && access.canEditAttendanceSchedule && canEditSelectedStore && !phoneEnv.phone ? (
            <div className="sce-tray">
              <span className="sce-tray-label">{t('posAdmin.scheduleAttendance.drag.trayLabel')}</span>
              {SHIFT_PRESETS.map((preset) => (
                <span
                  key={`${preset.start}-${preset.end}`}
                  className="sce-tray-chip"
                  draggable
                  onDragStart={(event) => handlePresetDragStart(event, preset)}
                  onDragEnd={endDrag}
                >
                  <span className="sce-tray-grip" aria-hidden>⋮⋮</span>
                  {preset.start}–{preset.end}
                </span>
              ))}
              <span className="sce-tray-hint">{t('posAdmin.scheduleAttendance.drag.trayHint')}</span>
            </div>
          ) : null}
          {renderWeekBoard(false)}
        </>
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
          {/* 规格第 1 节新增参数（卡 B）：后端返回才显示，旧后端不受影响 */}
          {settings?.defaultPeriodWeeks !== undefined ? <AttendancePayrollSettingsFields /> : null}
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

  // 「考勤审核」放在「考勤记录」后面：整页复用审核工作台，外层只提供标签与待审数。
  const reviewTabItem = canReviewTab ? {
    key: 'review',
    label: (
      <span>
        {t('menu.attendanceReview')}
        {pendingReviewCount > 0 ? <Badge count={pendingReviewCount} overflowCount={99} size="small" style={{ marginInlineStart: 6 }} /> : null}
      </span>
    ),
    children: (
      <Suspense fallback={<div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}><Spin /></div>}>
        <AttendanceReviewPanel embedded />
      </Suspense>
    ),
  } : null
  const afterRecordsKeys = new Set<string>(['schedules', 'records'])
  const tabItems = [
    ...(canViewSchedulePart ? scheduleTabItems.filter((item) => afterRecordsKeys.has(item.key)) : []),
    ...(reviewTabItem ? [reviewTabItem] : []),
    ...(canViewSchedulePart ? scheduleTabItems.filter((item) => !afterRecordsKeys.has(item.key)) : []),
  ]

  return (
    <Card
      title={<Space><CalendarOutlined />{t('posAdmin.scheduleAttendance.title')}</Space>}
      // 考勤审核标签自带门店 / 日期 / 搜索筛选，这里的周排班筛选与操作按钮不适用，整块隐藏。
      extra={activeTab === 'review' ? undefined : (
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
              <Button icon={<DeleteOutlined />} disabled={!storeCode} onClick={() => setDeletedSchedulesOpen(true)}>
                {t('posAdmin.scheduleAttendance.deletedSchedules.button')}
              </Button>
              <Button type="primary" icon={<PlusOutlined />} disabled={!canEditSelectedStore} onClick={() => openBatchModal()}>{t('posAdmin.scheduleAttendance.batch.openButton')}</Button>
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
      )}
    >
      <Tabs activeKey={activeTab} onChange={handleTabChange} items={tabItems} />

      <DeletedSchedulesDrawer
        open={deletedSchedulesOpen}
        storeCode={storeCode}
        fromDate={weekStart.format('YYYY-MM-DD')}
        toDate={getIsoWeekEnd(weekStart).format('YYYY-MM-DD')}
        canRestore={canEditSelectedStore}
        onClose={() => setDeletedSchedulesOpen(false)}
        onRestored={() => loadActiveTab(1)}
      />

      {/* 手机横屏看板：铺满屏幕，顶部一条细栏（周切换、分店、排班、退出），其余全给周视图 */}
      {shellMode !== 'none' ? (
        <LandscapeShell mode={shellMode}>
          <div className="sce-shell-bar">
            <Button size="small" icon={<LeftOutlined />} aria-label={t('posAdmin.scheduleAttendance.board.prevWeek')} onClick={() => setSelectedWeek((prev) => prev.subtract(1, 'week'))} />
            <span className="sce-shell-week">{selectedWeekLabel}</span>
            <Button size="small" icon={<RightOutlined />} aria-label={t('posAdmin.scheduleAttendance.board.nextWeek')} onClick={() => setSelectedWeek((prev) => prev.add(1, 'week'))} />
            <span className="sce-shell-store">{storeCode ? storeNameMap.get(storeCode) : ''}</span>
            {shellReadOnly ? <span className="sce-shell-hint">{t('posAdmin.scheduleAttendance.board.rotatedHint')}</span> : null}
            <span style={{ flex: 1 }} />
            {!shellReadOnly && access.canEditAttendanceSchedule && canEditSelectedStore ? (
              <Button size="small" type="primary" icon={<PlusOutlined />} onClick={() => openBatchModal()}>
                {t('posAdmin.scheduleAttendance.batch.openButton')}
              </Button>
            ) : null}
            <Button size="small" icon={<CloseOutlined />} onClick={exitShell}>{t('posAdmin.scheduleAttendance.board.shellExit')}</Button>
          </div>
          <div className="sce-shell-body">{renderWeekBoard(true)}</div>
        </LandscapeShell>
      ) : null}

      {/* 批量排班：选员工 × 选日期 × 统一班次；创建成功后刷新周视图 */}
      <BatchScheduleModal
        open={batchOpen}
        storeCode={storeCode ?? ''}
        storeName={storeCode ? storeNameMap.get(storeCode) : undefined}
        weekLabel={selectedWeekLabel}
        days={batchDays}
        users={batchUsers}
        initial={batchInitial}
        existingOf={existingOf}
        availabilityOf={availabilityOf}
        onClose={() => setBatchOpen(false)}
        onChanged={() => void loadSchedulesRef.current()}
      />

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
    </Card>
  )
}
