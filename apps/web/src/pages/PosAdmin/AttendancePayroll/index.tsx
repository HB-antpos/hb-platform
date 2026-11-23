import {
  DownOutlined,
  CloudUploadOutlined,
  DownloadOutlined,
  LeftOutlined,
  LockOutlined,
  RightOutlined,
  SearchOutlined,
  ShopOutlined,
  SyncOutlined,
  UnlockOutlined,
} from '@ant-design/icons'
import { Badge, Button, Grid, Input, Modal, Popover, Segmented, Space, Tabs, Tag, Tooltip, Typography, message } from 'antd'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getAttendancePayPeriodStatuses,
  getAttendancePayrollHours,
  getAttendancePayrollPay,
  getAttendancePayRates,
  revokeAttendanceManualEntry,
  revokeAttendanceManualLeave,
  submitAttendancePayPeriod,
  unlockAttendancePayPeriod,
} from '../../../services/attendancePayrollService'
import { useAuthStore } from '../../../store/auth'
import type {
  AttendanceHoursResultDto,
  AttendancePayRateDto,
  AttendancePayResultDto,
} from '../../../types/attendancePayroll'
import StorePicker from '../ScheduleAttendance/StorePicker'
import {
  ALL_STORES_TOKEN,
  STORAGE_KEYS,
  getBrowserStorage,
  parseRecentStores,
  parseSidebarPreference,
  pushRecentStore,
  safeStorageGet,
  safeStorageSet,
  type SidebarPreference,
} from '../ScheduleAttendance/storePickerLogic'
import messagesEn from './attendancePayrollMessages.en.json'
import messagesZh from './attendancePayrollMessages.zh.json'
import HoursTab from './HoursTab'
import ManualEntryDrawer, { type ManualEntryPreset } from './ManualEntryDrawer'
import { manualEntryErrorText, pickHoursInView, type ManualEntryMode } from './manualEntryLogic'
import ManualLeaveDrawer from './ManualLeaveDrawer'
import MyobPayRateSyncDrawer from './MyobPayRateSyncDrawer'
import PayTab from './PayTab'
import RatesTab from './RatesTab'
import { exportPayrollWorkbook } from './payrollExport'
import { PERIOD_PRESETS, birthdayRateReviews, isoWeekNumber, periodEnd, presetPeriodStart, shiftPeriod, type PeriodPreset } from './payrollLogic'
import { useAttendanceStores } from './useAttendanceStores'
import { usePayrollMyobPush } from './usePayrollMyobPush'
import './attendancePayroll.css'

registerPageMessages({ zh: messagesZh, en: messagesEn })

type PayrollTabKey = 'hours' | 'pay' | 'rates'

// 快捷定位按钮文案：1 周统计期叫 上周 / 本周 / 下周；2 周统计期里「上周」会误导（实际是上一个两周期），改叫 上期 / 本期 / 下期。
// 键名写成完整字面量，页面文案契约测试（pageMessagesContract）靠它静态核对键是否存在。
const PRESET_LABEL_KEYS: Record<1 | 2, Record<PeriodPreset, string>> = {
  1: {
    previous: 'attendancePayroll.period.lastWeek',
    current: 'attendancePayroll.period.thisWeek',
    next: 'attendancePayroll.period.nextWeek',
  },
  2: {
    previous: 'attendancePayroll.period.lastPeriod',
    current: 'attendancePayroll.period.thisPeriod',
    next: 'attendancePayroll.period.nextPeriod',
  },
}

export default function AttendancePayrollPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const canViewHours = access.canViewAttendancePunches
  const canViewPay = access.canViewAttendancePayroll
  const canEdit = access.canEditAttendancePayroll
  const canUnlock = access.canUnlockAttendancePayroll
  const { storeOptions, storeCode, setStoreCode } = useAttendanceStores(true)
  const [activeTab, setActiveTab] = useState<PayrollTabKey>(canViewHours ? 'hours' : 'pay')
  // 期以后端为准：首次不传期起点 / 周数，后端返回今天所在期（周数取设置默认、两周按双周起始日对齐）；
  // 翻期在返回的起点上 ± 周数 × 7 天，切周数时把当前起点连同新周数发过去由后端重新对齐。
  // 写接口（提交、解锁、备注、核定）都用后端返回的 periodStart，避免 INVALID_PERIOD。
  const [requested, setRequested] = useState<{ periodStart?: string; weeks?: 1 | 2 }>({})
  const [resolvedPeriod, setResolvedPeriod] = useState<{ periodStart: string; weeks: 1 | 2 } | null>(null)
  const [includePending, setIncludePending] = useState(false)
  const [mergeStores, setMergeStores] = useState(false)
  const [hours, setHours] = useState<AttendanceHoursResultDto | null>(null)
  const [pay, setPay] = useState<AttendancePayResultDto | null>(null)
  const [rates, setRates] = useState<AttendancePayRateDto[]>([])
  const [rateKeyword, setRateKeyword] = useState('')
  // 「生日后时薪还没更新」的待办：个人时薪表标签上的红点、工时统计 / 工资表顶部的待办条共用。
  // 单独取一份、不带搜索关键字，红点数和待办条不会因为在时薪表里搜人而变；时薪保存 / 同步后 reviewVersion + 1 重取。
  const [reviewRates, setReviewRates] = useState<AttendancePayRateDto[]>([])
  const [reviewVersion, setReviewVersion] = useState(0)
  // 从 MYOB 同步时薪的抽屉（需要同时有「工资编辑」与「MYOB 员工绑定」权限，后端也是两个策略取「且」）
  const [syncOpen, setSyncOpen] = useState(false)
  const [loading, setLoading] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [unlockOpen, setUnlockOpen] = useState(false)
  const [unlockReason, setUnlockReason] = useState('')
  // 分店列表在左侧（与排班考勤页同一个组件、同一份「最近使用 / 上次所选 / 侧栏收起」记忆，两页切换时分店不丢）
  const storage = useMemo(() => getBrowserStorage(), [])
  const screens = Grid.useBreakpoint()
  const [recentStores, setRecentStores] = useState<string[]>(() => parseRecentStores(safeStorageGet(getBrowserStorage(), STORAGE_KEYS.recentStores)))
  const [sidebarPreference, setSidebarPreference] = useState<SidebarPreference>(() => parseSidebarPreference(safeStorageGet(getBrowserStorage(), STORAGE_KEYS.sidebarDock)))
  const [storePopoverOpen, setStorePopoverOpen] = useState(false)
  // 侧栏「🔒 / 未锁」：各店在所看周期是否已提交；提交 / 解锁后 lockVersion + 1 重取
  const [lockedStores, setLockedStores] = useState<Readonly<Record<string, boolean>>>({})
  const [lockVersion, setLockVersion] = useState(0)
  const rememberedApplied = useRef(false)

  // 首次拿到分店列表时，沿用排班考勤页上次选的分店（「全部分店」在这里不适用，工资按店算）
  useEffect(() => {
    if (rememberedApplied.current || storeOptions.length === 0) return
    rememberedApplied.current = true
    const remembered = safeStorageGet(storage, STORAGE_KEYS.lastStore)
    if (remembered && remembered !== ALL_STORES_TOKEN && storeOptions.some((option) => option.value === remembered)) {
      setStoreCode(remembered)
    }
  }, [setStoreCode, storage, storeOptions])

  const selectStore = useCallback((code: string | undefined) => {
    setStorePopoverOpen(false)
    if (!code) return
    setStoreCode(code)
    safeStorageSet(storage, STORAGE_KEYS.lastStore, code)
    setRecentStores((previous) => {
      const next = pushRecentStore(previous, code)
      safeStorageSet(storage, STORAGE_KEYS.recentStores, JSON.stringify(next))
      return next
    })
  }, [setStoreCode, storage])

  const setSidebarDock = useCallback((next: Exclude<SidebarPreference, null>) => {
    setSidebarPreference(next)
    setStorePopoverOpen(false)
    safeStorageSet(storage, STORAGE_KEYS.sidebarDock, next)
  }, [storage])

  // 宽屏（≥ 992px）默认停靠在左侧；用户点过「收起 / 固定」就按用户的；只有 1 家店时不需要列表
  const storeSidebarDocked = storeOptions.length > 1 && (sidebarPreference ? sidebarPreference === 'docked' : screens.lg === true)

  const periodQuery = useMemo(
    () => (storeCode ? { storeCode, ...requested, includePending, mergeStores } : null),
    [includePending, mergeStores, requested, storeCode],
  )

  const loadHours = useCallback(async () => {
    if (!periodQuery || !canViewHours) return
    setLoading(true)
    try {
      const result = await getAttendancePayrollHours(periodQuery)
      setHours(result)
      setResolvedPeriod({ periodStart: result.periodStart, weeks: result.weeks })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [canViewHours, periodQuery, t])

  const loadPay = useCallback(async () => {
    if (!periodQuery || !canViewPay) return
    setLoading(true)
    try {
      const result = await getAttendancePayrollPay(periodQuery)
      setPay(result)
      setResolvedPeriod({ periodStart: result.periodStart, weeks: result.weeks })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [canViewPay, periodQuery, t])

  // 以刚点选的周期为准（resolvedPeriod 要等工资数据回来才更新，会让时薪表先按旧周期请求一次）
  const ratesAsOf = requested.periodStart ?? resolvedPeriod?.periodStart
  const loadRates = useCallback(async () => {
    if (!storeCode || !canViewPay) return
    setLoading(true)
    try {
      // 按所看周期的起始日取「这一期适用的时薪」；期还没解析出来时按今天
      setRates(await getAttendancePayRates({ storeCode, keyword: rateKeyword || undefined, asOf: ratesAsOf }))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [canViewPay, rateKeyword, ratesAsOf, storeCode, t])

  useEffect(() => {
    if (activeTab === 'hours') void loadHours()
    if (activeTab === 'pay') void loadPay()
    if (activeTab === 'rates') {
      void loadRates()
      // 时薪抽屉要用本期应付工时做实时试算
      void loadPay()
    }
  }, [activeTab, loadHours, loadPay, loadRates])

  useEffect(() => {
    if (!storeCode || !canViewPay) {
      setReviewRates([])
      return undefined
    }
    let cancelled = false
    getAttendancePayRates({ storeCode })
      .then((list) => {
        if (!cancelled) setReviewRates(list.filter((rate) => rate.birthdayReviewHint))
      })
      // 待办只是附加提示：取不到就不显示，不弹错误打扰主流程
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [canViewPay, reviewVersion, storeCode])

  const reloadAll = useCallback(() => {
    if (activeTab === 'hours') void loadHours()
    else void loadPay()
    if (activeTab === 'rates') void loadRates()
    setLockVersion((value) => value + 1)
  }, [activeTab, loadHours, loadPay, loadRates])

  const weeks = resolvedPeriod?.weeks ?? requested.weeks ?? 1
  const periodStart = resolvedPeriod?.periodStart ?? requested.periodStart
  const changeWeeks = (value: 1 | 2) => setRequested({ periodStart, weeks: value })
  const shiftRequested = (direction: -1 | 1) => {
    if (periodStart) setRequested({ periodStart: shiftPeriod(periodStart, weeks, direction), weeks })
  }
  // 快捷定位 上周 / 本周 / 下周：相对「今天所在期」，和 ‹ › 相对当前所看期逐期翻页互补。
  // 以后端返回的当前期起点为基准推算（2 周的对齐由后端决定），因此要等首次加载出期起点后才可点。
  const presetStartOf = (preset: PeriodPreset) =>
    periodStart ? presetPeriodStart(periodStart, weeks, dayjs().format('YYYY-MM-DD'), preset) : undefined
  // 期起点还没加载出来（或加载失败）时 presetStartOf 也是 undefined，直接比较会让第一个按钮被误判为命中，所以先判空。
  const activePreset = periodStart ? PERIOD_PRESETS.find((preset) => presetStartOf(preset) === periodStart) : undefined
  const jumpToPreset = (preset: PeriodPreset) => {
    const target = presetStartOf(preset)
    if (target) setRequested({ periodStart: target, weeks })
  }

  // 各店在所看周期是否已锁定（侧栏提示用，取不到就不显示，不打扰主流程）
  const storeCodesKey = storeOptions.map((option) => option.value).join(',')
  useEffect(() => {
    if (!periodStart || !storeCodesKey) return undefined
    let cancelled = false
    getAttendancePayPeriodStatuses({ storeCodes: storeCodesKey.split(','), periodStart, weeks })
      .then((list) => {
        if (!cancelled) setLockedStores(Object.fromEntries(list.map((item) => [item.storeCode, item.locked])))
      })
      .catch(() => {
        if (!cancelled) setLockedStores({})
      })
    return () => {
      cancelled = true
    }
  }, [lockVersion, periodStart, storeCodesKey, weeks])

  const storeBadges = useMemo<Record<string, ReactNode>>(() => {
    const badges: Record<string, ReactNode> = {}
    Object.entries(lockedStores).forEach(([code, locked]) => {
      badges[code] = locked
        ? <LockOutlined className="atp-store-locked" aria-label={t('attendancePayroll.storeList.locked')} />
        : <span className="atp-store-open">{t('attendancePayroll.storeList.open')}</span>
    })
    return badges
  }, [lockedStores, t])

  const status = (activeTab === 'hours' ? hours : pay) ?? hours ?? pay
  const pendingCount = status?.pendingCount ?? 0
  const isSubmitted = status?.periodStatus === 'Submitted'
  // 手动补录工时：要「补卡与修改管理分店打卡」权限；本期已锁定时页面上不出现任何入口（后端同样拒绝）
  const manualEntryEnabled = access.canAdjustAttendancePunches && canViewHours && !isSubmitted && Boolean(storeCode)
  const [manualEntryOpen, setManualEntryOpen] = useState(false)
  const [manualEntryPreset, setManualEntryPreset] = useState<ManualEntryPreset | null>(null)
  // 补录抽屉的模式：补工时，或补带薪病假 / 年假 / 年假发放（两个抽屉按模式二选一打开）
  const [manualEntryMode, setManualEntryMode] = useState<ManualEntryMode>('hours')
  // 抽屉只能用「所看周期」的工时数据：在工资表页签翻期只重载工资数据，hours 可能还停在旧的一期，
  // 直接给抽屉会让日期范围 / 每天状态 / 员工排班全是旧期的（切到上周后日期只能选本周今天就是这个原因）。
  const hoursInView = pickHoursInView(hours, periodStart, weeks)
  useEffect(() => {
    // 工时统计页签自己会随翻期重载，不用补载；其它页签打开抽屉时，工时数据缺失或周期对不上就补载一次
    if (manualEntryOpen && activeTab !== 'hours' && !hoursInView) void loadHours()
  }, [activeTab, hoursInView, loadHours, manualEntryOpen])
  const openManualEntry = (preset?: ManualEntryPreset) => {
    setManualEntryPreset(preset ?? null)
    setManualEntryMode('hours')
    setManualEntryOpen(true)
  }
  /** 补录 / 撤销后：工时统计与工资表都要重算，两边一起重载。 */
  const reloadAfterManualEntry = () => {
    void loadHours()
    void loadPay()
  }
  /** 抽屉顶部切换模式：带上已选员工，日期 / 时间不带（两种模式要填的东西不一样）。 */
  const switchManualEntryMode = (mode: ManualEntryMode, userGuid?: string) => {
    setManualEntryPreset(userGuid ? { userGuid } : null)
    setManualEntryMode(mode)
  }
  /** 撤销补录建的假（工时表格子「补」角标气泡里的按钮）。 */
  const revokeManualLeave = async (leaveGuid: string) => {
    if (!storeCode) return
    try {
      await revokeAttendanceManualLeave({ storeCode, leaveGuid })
      message.success(t('attendancePayroll.manualLeave.revoked'))
      reloadAfterManualEntry()
    } catch (error) {
      message.error(manualEntryErrorText(error, t('attendancePayroll.manualLeave.revokeFailed')))
    }
  }
  const revokeManualEntry = async (userGuid: string, date: string) => {
    if (!storeCode) return
    try {
      await revokeAttendanceManualEntry({ storeCode, userGuid, workDate: date })
      message.success(t('attendancePayroll.manualEntry.revoked'))
      reloadAfterManualEntry()
    } catch (error) {
      message.error(manualEntryErrorText(error, t('attendancePayroll.manualEntry.revokeFailed')))
    }
  }
  // 工资表「推送到 MYOB」：预览接口要工资编辑权限，推送要 Myob.Timesheet.Push；只在工资表页签、按所看分店 / 周期
  const myobPush = usePayrollMyobPush({
    allowed: activeTab === 'pay' && canEdit && access.canPushMyobTimesheet,
    storeCode,
    periodStart,
    locked: isSubmitted,
    canGoBind: access.canBindMyobEmployees,
    canGoMapping: access.canManageMyobConnections,
  })
  const periodLabel = useMemo(() => {
    if (!periodStart) return '…'
    const firstWeek = isoWeekNumber(periodStart)
    const weekText = weeks === 2
      ? t('attendancePayroll.period.weeksRange', { from: firstWeek, to: isoWeekNumber(shiftPeriod(periodStart, 1, 1)) })
      : t('attendancePayroll.period.week', { week: firstWeek })
    return `${weekText} · ${dayjs(periodStart).format('MM-DD')} ~ ${dayjs(periodEnd(periodStart, weeks)).format('MM-DD')}`
  }, [periodStart, t, weeks])

  const handleSubmit = () => {
    if (!storeCode || !periodStart) return
    Modal.confirm({
      title: t('attendancePayroll.submit.confirmTitle'),
      content: t('attendancePayroll.submit.confirmContent', { period: periodLabel }),
      okText: t('attendancePayroll.submit.button'),
      onOk: async () => {
        setSubmitting(true)
        try {
          await submitAttendancePayPeriod({ storeCode, periodStart, weeks })
          message.success(t('attendancePayroll.submit.done'))
          reloadAll()
        } catch (error) {
          message.error(error instanceof Error ? error.message : t('attendancePayroll.submit.failed'))
        } finally {
          setSubmitting(false)
        }
      },
    })
  }

  const handleUnlock = async () => {
    if (!storeCode || !periodStart || !unlockReason.trim()) return
    setSubmitting(true)
    try {
      await unlockAttendancePayPeriod({ storeCode, periodStart, weeks, reason: unlockReason.trim() })
      message.success(t('attendancePayroll.unlock.done'))
      setUnlockOpen(false)
      setUnlockReason('')
      reloadAll()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.unlock.failed'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleExport = async () => {
    const storeName = storeOptions.find((option) => option.value === storeCode)?.label ?? storeCode ?? ''
    if (!periodStart && activeTab !== 'rates') return
    try {
      await exportPayrollWorkbook({
        tab: activeTab,
        storeName,
        periodStart: periodStart ?? '',
        weeks,
        hours,
        pay,
        rates,
        text: (key, values) => t(key, values),
      })
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('attendancePayroll.messages.exportFailed'))
    }
  }

  // 三个页签都按同一个周期看（时薪表 = 这一期适用的时薪）
  const periodControls = (
    <>
      <Segmented
        value={weeks}
        options={[
          { label: t('attendancePayroll.period.oneWeek'), value: 1 },
          { label: t('attendancePayroll.period.twoWeeks'), value: 2 },
        ]}
        onChange={(value) => changeWeeks(value as 1 | 2)}
      />
      <Space.Compact>
        <Button icon={<LeftOutlined />} aria-label={t('attendancePayroll.period.previous')} disabled={!periodStart} onClick={() => shiftRequested(-1)} />
        <Button className="atp-period-label">{periodLabel}</Button>
        <Button icon={<RightOutlined />} aria-label={t('attendancePayroll.period.next')} disabled={!periodStart} onClick={() => shiftRequested(1)} />
      </Space.Compact>
      <Space.Compact>
        {PERIOD_PRESETS.map((preset) => (
          <Button
            key={preset}
            className={`atp-period-preset${activePreset === preset ? ' is-active' : ''}`}
            aria-pressed={activePreset === preset}
            disabled={!periodStart}
            onClick={() => jumpToPreset(preset)}
          >
            {t(PRESET_LABEL_KEYS[weeks][preset])}
          </Button>
        ))}
      </Space.Compact>
    </>
  )

  const lockControl = activeTab !== 'rates' && canViewPay ? (
    isSubmitted ? (
      <Space>
        <Tag color="green" icon={<LockOutlined />}>{t('attendancePayroll.submit.locked')}</Tag>
        {canUnlock ? <Button icon={<UnlockOutlined />} onClick={() => setUnlockOpen(true)}>{t('attendancePayroll.unlock.button')}</Button> : null}
      </Space>
    ) : canEdit ? (
      <Tooltip title={pendingCount > 0 ? t('attendancePayroll.submit.pendingTip') : undefined}>
        <Button type="primary" icon={<LockOutlined />} disabled={pendingCount > 0 || !status} loading={submitting} onClick={handleSubmit}>
          {pendingCount > 0
            ? t('attendancePayroll.submit.buttonWithPending', { count: pendingCount })
            : t('attendancePayroll.submit.button')}
        </Button>
      </Tooltip>
    ) : null
  ) : null

  // 待办：红点数 = 全部未处理；工时统计 / 工资表的待办条只在「生日之后最近的一期」起出现（生日 ≤ 本期末），生日之前的旧期不提示。
  // 期起点还没加载出来时先不显示，避免首屏闪出一堆旧期不该有的条。
  const viewPeriodEnd = periodStart ? periodEnd(periodStart, weeks) : undefined
  const allRateReviews = useMemo(() => birthdayRateReviews(reviewRates), [reviewRates])
  const periodRateReviews = useMemo(
    () => (viewPeriodEnd ? birthdayRateReviews(reviewRates, viewPeriodEnd) : []),
    [reviewRates, viewPeriodEnd],
  )
  const refreshRateReviews = () => setReviewVersion((version) => version + 1)

  // 推送按钮：规则见 getPayrollPushButton（未锁定灰色提示、没映射 / 读取失败灰色说明、服务器没开放则不显示）
  const myobPushTips: Record<'needLock' | 'unmapped' | 'error', string> = {
    needLock: t('attendancePayroll.myobPush.needLock'),
    unmapped: t('attendancePayroll.myobPush.unmapped'),
    error: t('attendancePayroll.myobPush.error'),
  }
  const pushKind = myobPush.button.kind
  const myobPushButton = pushKind === 'hidden' ? null : (
    <Tooltip title={pushKind === 'needLock' || pushKind === 'unmapped' || pushKind === 'error' ? myobPushTips[pushKind] : undefined}>
      {/* 禁用按钮收不到鼠标事件，Tooltip 套在外层 span 上 */}
      <span>
        <Button
          icon={<CloudUploadOutlined />}
          loading={pushKind === 'loading'}
          disabled={pushKind !== 'ready'}
          onClick={myobPush.open}
        >
          {t('attendancePayroll.myobPush.button')}
        </Button>
      </span>
    </Tooltip>
  )

  const tabItems = [
    ...(canViewHours ? [{
      key: 'hours',
      label: t('attendancePayroll.tabs.hours'),
      children: (
        <HoursTab
          data={hours}
          loading={loading}
          weeks={weeks}
          includePending={includePending}
          mergeStores={mergeStores}
          canReview={access.canReviewAttendance}
          onMergeStoresChange={setMergeStores}
          onReload={() => void loadHours()}
          onOpenRates={canViewPay ? () => setActiveTab('rates') : undefined}
          rateReviews={periodRateReviews}
          manualEntry={manualEntryEnabled ? { onOpen: openManualEntry, onRevoke: revokeManualEntry, onRevokeLeave: revokeManualLeave } : undefined}
        />
      ),
    }] : []),
    ...(canViewPay ? [
      {
        key: 'pay',
        label: t('attendancePayroll.tabs.pay'),
        children: (
          <PayTab
            data={pay}
            loading={loading}
            weeks={weeks}
            canEdit={canEdit && !isSubmitted}
            onReload={() => void loadPay()}
            rateReviews={periodRateReviews}
            onOpenRates={() => setActiveTab('rates')}
            myobPush={myobPush.cells}
            onManualEntry={manualEntryEnabled ? (userGuid) => openManualEntry(userGuid ? { userGuid } : undefined) : undefined}
          />
        ),
      },
      {
        key: 'rates',
        // 红点数字 = 生日后时薪还没更新的人数（全部，不看当前期）
        label: (
          <Badge count={allRateReviews.length} size="small" offset={[10, -2]}>
            {t('attendancePayroll.tabs.rates')}
          </Badge>
        ),
        children: (
          <RatesTab
            rates={rates}
            pay={pay}
            loading={loading}
            storeCode={storeCode}
            canEdit={canEdit}
            onSaved={() => {
              void loadRates()
              void loadPay()
              refreshRateReviews()
            }}
          />
        ),
      },
    ] : []),
  ]

  // 侧栏收起（窄屏 / 用户点了收起）时：标题旁一个分店按钮，点开是同一份列表
  const storeName = storeOptions.find((option) => option.value === storeCode)?.label ?? storeCode
  const storeSwitcher = !storeSidebarDocked && storeOptions.length > 1 ? (
    <Popover
      trigger="click"
      placement="bottomLeft"
      arrow={false}
      destroyOnHidden
      open={storePopoverOpen}
      onOpenChange={setStorePopoverOpen}
      styles={{ body: { padding: 0 } }}
      content={(
        <StorePicker
          variant="floating"
          autoFocus
          options={storeOptions}
          value={storeCode}
          recent={recentStores}
          allowAll={false}
          badges={storeBadges}
          onSelect={selectStore}
          onToggleDock={() => setSidebarDock('docked')}
        />
      )}
    >
      <Button className="sce-store-button" icon={<ShopOutlined />}>
        <span className="sce-store-button-name">{storeName ?? t('attendancePayroll.selectStore')}</span>
        <DownOutlined className="sce-store-button-caret" />
      </Button>
    </Popover>
  ) : storeOptions.length === 1 ? (
    <Tag icon={<ShopOutlined />} className="atp-store-single">{storeOptions[0].label}</Tag>
  ) : null

  return (
    <div className={`atp-page atp-layout${storeSidebarDocked ? ' has-sidebar' : ''}`}>
      {storeSidebarDocked ? (
        <aside className="atp-store-aside">
          <StorePicker
            variant="docked"
            options={storeOptions}
            value={storeCode}
            recent={recentStores}
            allowAll={false}
            badges={storeBadges}
            onSelect={selectStore}
            onToggleDock={() => setSidebarDock('floating')}
          />
        </aside>
      ) : null}
      <div className="atp-layout-main">
        <div className="atp-header">
          <Space wrap size={12}>
            <Typography.Title level={4} className="atp-title">{t('attendancePayroll.title')}</Typography.Title>
            {storeSwitcher}
            {periodControls}
            {activeTab === 'hours' ? (
              <Segmented
                value={includePending ? 'all' : 'reviewed'}
                options={[
                  { label: t('attendancePayroll.reviewedOnly'), value: 'reviewed' },
                  { label: t('attendancePayroll.includePending'), value: 'all' },
                ]}
                onChange={(value) => setIncludePending(value === 'all')}
              />
            ) : null}
            {activeTab === 'rates' ? (
              <Input
                allowClear
                prefix={<SearchOutlined />}
                placeholder={t('attendancePayroll.searchPlaceholder')}
                style={{ width: 180 }}
                onPressEnter={(event) => setRateKeyword((event.target as HTMLInputElement).value.trim())}
                onChange={(event) => {
                  if (!event.target.value) setRateKeyword('')
                }}
              />
            ) : null}
          </Space>
          <Space wrap>
            {activeTab === 'rates' && canEdit && access.canBindMyobEmployees ? (
              <Button icon={<SyncOutlined />} disabled={!storeCode} onClick={() => setSyncOpen(true)}>{t('attendancePayroll.rates.sync.open')}</Button>
            ) : null}
            <Button icon={<DownloadOutlined />} onClick={() => void handleExport()}>{t('attendancePayroll.exportExcel')}</Button>
            {myobPushButton}
            {lockControl}
          </Space>
        </div>
        <Tabs activeKey={activeTab} items={tabItems} onChange={(key) => setActiveTab(key as PayrollTabKey)} />
      </div>

      {myobPush.modals}

      {manualEntryEnabled && storeCode ? (
        <>
          <ManualEntryDrawer
            open={manualEntryOpen && manualEntryMode === 'hours'}
            storeCode={storeCode}
            storeName={storeOptions.find((option) => option.value === storeCode)?.label}
            days={hoursInView?.days ?? []}
            employees={hoursInView?.employees ?? []}
            preset={manualEntryPreset}
            onClose={() => setManualEntryOpen(false)}
            onSaved={reloadAfterManualEntry}
            onSwitchMode={switchManualEntryMode}
          />
          <ManualLeaveDrawer
            open={manualEntryOpen && manualEntryMode !== 'hours'}
            storeCode={storeCode}
            storeName={storeOptions.find((option) => option.value === storeCode)?.label}
            days={hoursInView?.days ?? []}
            employees={hoursInView?.employees ?? []}
            leaveType={manualEntryMode === 'hours' ? 'PaidSick' : manualEntryMode}
            preset={manualEntryPreset}
            onClose={() => setManualEntryOpen(false)}
            onSaved={reloadAfterManualEntry}
            onSwitchMode={switchManualEntryMode}
          />
        </>
      ) : null}

      <MyobPayRateSyncDrawer
        open={syncOpen}
        storeCode={storeCode}
        onClose={() => setSyncOpen(false)}
        onApplied={() => {
          void loadRates()
          void loadPay()
          refreshRateReviews()
        }}
      />

      <Modal
        open={unlockOpen}
        title={t('attendancePayroll.unlock.title', { period: periodLabel })}
        okText={t('attendancePayroll.unlock.button')}
        okButtonProps={{ disabled: !unlockReason.trim(), loading: submitting }}
        onCancel={() => setUnlockOpen(false)}
        onOk={() => void handleUnlock()}
      >
        <Typography.Paragraph type="secondary">{t('attendancePayroll.unlock.hint')}</Typography.Paragraph>
        <Input.TextArea rows={3} value={unlockReason} placeholder={t('attendancePayroll.unlock.reasonPlaceholder')} onChange={(event) => setUnlockReason(event.target.value)} />
      </Modal>
    </div>
  )
}
