import { CloseOutlined, PlusOutlined } from '@ant-design/icons'
import { Avatar, Button, Drawer, Input, Select, Spin, Tag, TimePicker, Tooltip, message } from 'antd'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { createAttendanceManualEntry, previewAttendanceManualEntry } from '../../../services/attendancePayrollService'
import { getAttendanceEmployees } from '../../../services/scheduleAttendanceService'
import type {
  AttendanceHoursDayHeaderDto,
  AttendanceHoursEmployeeDto,
  AttendanceManualEntryPayload,
  AttendanceManualEntryPreview,
} from '../../../types/attendancePayroll'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import ManualEntryModeSwitch from './ManualEntryModeSwitch'
import {
  MANUAL_ENTRY_REASON_MAX,
  buildManualSegments,
  defaultManualRows,
  manualEntryDayState,
  manualEntryErrorText,
  manualPreviewHoursText,
  validateManualRows,
  type ManualEntryMode,
  type ManualEntryRow,
} from './manualEntryLogic'
import { round2, summarizeEmployeeHours } from './payrollLogic'

/** 打开抽屉时的预设：从格子 / 提示条 / 工资表行进来时带上员工与日期。 */
export interface ManualEntryPreset {
  userGuid?: string
  dates?: string[]
}

interface ManualEntryDrawerProps {
  open: boolean
  storeCode: string
  storeName?: string
  /** 本期的日期表头（1 周 7 天 / 2 周 14 天）。 */
  days: readonly AttendanceHoursDayHeaderDto[]
  /** 工时统计里有数据的人：用来判断每天是否已有打卡 / 带薪假，以及标「本期 0 h」。 */
  employees: readonly AttendanceHoursEmployeeDto[]
  preset?: ManualEntryPreset | null
  onClose: () => void
  /** 至少保存成功了一天：外层据此重载工时与工资。 */
  onSaved: () => void
  /** 顶部切换到「补带薪假」模式：外层换成补假抽屉，并带上已选员工；不传就不显示切换。 */
  onSwitchMode?: (mode: ManualEntryMode, userGuid?: string) => void
}

interface DayForm {
  rows: ManualEntryRow[]
  /** 用餐次数覆盖值；null = 按时长自动。只在要新建排班时显示。 */
  meal: number | null
}

type PreviewState =
  | { status: 'loading' }
  | { status: 'ok'; data: AttendanceManualEntryPreview }
  | { status: 'error'; text: string }

/** 预览防抖：连续改时间时只发最后一次。 */
const PREVIEW_DEBOUNCE_MS = 450

const toDayjsTime = (value: string) => (value ? dayjs(`2000-01-01T${value}`) : null)

export default function ManualEntryDrawer(props: ManualEntryDrawerProps) {
  const { open, storeCode, days, employees, preset, onClose, onSaved, onSwitchMode } = props
  const { t } = useTranslation()
  const [staff, setStaff] = useState<{ userGuid: string; label: string }[]>([])
  const [userGuid, setUserGuid] = useState<string>()
  const [selected, setSelected] = useState<string[]>([])
  const [forms, setForms] = useState<Record<string, DayForm>>({})
  const [previews, setPreviews] = useState<Record<string, PreviewState>>({})
  const [reason, setReason] = useState('')
  const [saving, setSaving] = useState(false)
  // 保存失败的提示单独放：不能覆盖预览结果，否则失败的那天不再算「可保存」，没法不改时间直接重试
  const [saveErrors, setSaveErrors] = useState<Record<string, string>>({})
  // 每天最近一次预览的输入签名：签名没变就不重复请求；返回时签名对不上说明已过期，丢弃。
  const signatures = useRef<Record<string, string>>({})
  // 每天各自的防抖定时器：不能放进 effect 的 cleanup 里清——effect 在 450ms 内因别的状态重跑时，
  // 签名没变就会跳过重新调度，这一天就永远停在「正在计算」。
  const previewTimers = useRef<Record<string, number>>({})
  const clearPreviewTimers = () => {
    Object.values(previewTimers.current).forEach((timer) => window.clearTimeout(timer))
    previewTimers.current = {}
  }
  const today = dayjs().format('YYYY-MM-DD')

  const employee = useMemo(() => employees.find((item) => item.userGuid === userGuid), [employees, userGuid])
  const dayIndex = useMemo(() => new Map(days.map((day, index) => [day.date, index])), [days])
  const dayOf = useCallback(
    (date: string) => {
      const index = dayIndex.get(date)
      return index === undefined ? undefined : employee?.days[index]
    },
    [dayIndex, employee],
  )

  // 每次打开：载入本店员工，套用预设，清空上次的填写。
  useEffect(() => {
    if (!open) return undefined
    let cancelled = false
    signatures.current = {}
    clearPreviewTimers()
    setPreviews({})
    setSaveErrors({})
    setReason('')
    setUserGuid(preset?.userGuid)
    const dates = preset?.dates ?? []
    setSelected(dates)
    const presetEmployee = employees.find((item) => item.userGuid === preset?.userGuid)
    setForms(Object.fromEntries(dates.map((date) => {
      const index = dayIndex.get(date)
      return [date, { rows: defaultManualRows(index === undefined ? undefined : presetEmployee?.days[index]), meal: null }]
    })))
    getAttendanceEmployees(storeCode)
      .then((list) => {
        if (cancelled) return
        setStaff(list.map((item) => ({ userGuid: item.userGuid, label: item.fullName || item.nickname || item.username })))
      })
      .catch(() => { if (!cancelled) setStaff([]) })
    return () => {
      cancelled = true
      clearPreviewTimers()
    }
    // 只在打开时重置；employees / dayIndex 变化（重载工时）不应清掉正在填的内容
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, storeCode, preset])

  const staffOptions = useMemo(() => {
    const zero = new Set(employees.filter((item) => summarizeEmployeeHours(item).total === 0).map((item) => item.userGuid))
    const known = new Set(employees.map((item) => item.userGuid))
    return staff.map((item) => ({
      value: item.userGuid,
      label: item.label,
      // 工时统计里没有这个人 = 本期一个工时都没有
      zero: zero.has(item.userGuid) || !known.has(item.userGuid),
    }))
  }, [employees, staff])

  const toggleDay = (date: string) => {
    setSelected((current) => {
      if (current.includes(date)) return current.filter((item) => item !== date)
      return [...current, date].sort()
    })
    setForms((current) => (current[date] ? current : { ...current, [date]: { rows: defaultManualRows(dayOf(date)), meal: null } }))
  }

  const updateForm = (date: string, patch: Partial<DayForm>) =>
    setForms((current) => ({ ...current, [date]: { ...(current[date] ?? { rows: defaultManualRows(), meal: null }), ...patch } }))

  const updateRow = (date: string, index: number, patch: Partial<ManualEntryRow>) => {
    const rows = (forms[date]?.rows ?? []).map((row, rowIndex) => (rowIndex === index ? { ...row, ...patch } : row))
    updateForm(date, { rows })
  }

  const buildPayload = useCallback(
    (date: string, form: DayForm, text: string): AttendanceManualEntryPayload => ({
      storeCode,
      userGuid: userGuid ?? '',
      workDate: date,
      segments: buildManualSegments(date, form.rows),
      mealBreakCount: form.meal,
      // 预览接口要求原因非空；这里的占位原因只用于预览，真正保存时会带上填写的原因重新预览一次
      reason: text.trim() || '—',
    }),
    [storeCode, userGuid],
  )

  // 实时预览：某天的班段填完整且通过本地校验后，防抖请求后端预览（工时、是否新建排班、加班候选）。
  useEffect(() => {
    if (!open || !userGuid) return
    for (const date of selected) {
      const form = forms[date]
      if (!form) continue
      if (validateManualRows(form.rows) !== null) {
        if (signatures.current[date]) {
          delete signatures.current[date]
          window.clearTimeout(previewTimers.current[date])
          setPreviews((current) => {
            const next = { ...current }
            delete next[date]
            return next
          })
        }
        continue
      }
      const signature = JSON.stringify([userGuid, date, form.rows, form.meal])
      if (signatures.current[date] === signature) continue
      signatures.current[date] = signature
      // 时间改了：之前的保存失败提示作废
      setSaveErrors((current) => {
        if (!(date in current)) return current
        const next = { ...current }
        delete next[date]
        return next
      })
      setPreviews((current) => ({ ...current, [date]: { status: 'loading' } }))
      window.clearTimeout(previewTimers.current[date])
      previewTimers.current[date] = window.setTimeout(() => {
        previewAttendanceManualEntry(buildPayload(date, form, ''))
          .then((data) => {
            if (signatures.current[date] === signature) setPreviews((current) => ({ ...current, [date]: { status: 'ok', data } }))
          })
          .catch((error: unknown) => {
            if (signatures.current[date] === signature) {
              setPreviews((current) => ({ ...current, [date]: { status: 'error', text: manualEntryErrorText(error, t('attendancePayroll.manualEntry.previewFailed')) } }))
            }
          })
      }, PREVIEW_DEBOUNCE_MS)
    }
  }, [open, userGuid, selected, forms, buildPayload, t])

  const readyDates = selected.filter((date) => previews[date]?.status === 'ok')
  const loadingAny = selected.some((date) => previews[date]?.status === 'loading')
  const totalHours = round2(readyDates.reduce((sum, date) => {
    const preview = previews[date]
    return sum + (preview?.status === 'ok' ? preview.data.paidHours : 0)
  }, 0))
  const canSave = Boolean(userGuid) && reason.trim().length > 0 && readyDates.length > 0 && !loadingAny && !saving

  const save = async () => {
    if (!userGuid) return
    setSaving(true)
    setSaveErrors({})
    const failed: string[] = []
    let savedCount = 0
    try {
      // 逐天保存：每天先带真实原因重新预览拿版本号，再提交；某天失败不影响其它天，失败的留在抽屉里。
      for (const date of readyDates) {
        const form = forms[date]
        try {
          const payload = buildPayload(date, form, reason)
          const fresh = await previewAttendanceManualEntry(payload)
          await createAttendanceManualEntry({ ...payload, previewRevision: fresh.previewRevision })
          savedCount += 1
          setSelected((current) => current.filter((item) => item !== date))
        } catch (error) {
          failed.push(date)
          // 预览结果保留：这一天仍算「可保存」，解锁 / 改正后可以直接再点保存重试
          setSaveErrors((current) => ({ ...current, [date]: manualEntryErrorText(error, t('attendancePayroll.manualEntry.saveFailed')) }))
        }
      }
    } finally {
      setSaving(false)
    }
    if (savedCount > 0) {
      onSaved()
      message.success(t('attendancePayroll.manualEntry.saved', { count: savedCount }))
    }
    if (failed.length === 0) onClose()
  }

  const renderPreview = (date: string) => {
    const preview = previews[date]
    if (!preview) return null
    if (saveErrors[date]) {
      return <div className="atp-me-preview atp-me-preview-error">{t('attendancePayroll.manualEntry.saveFailedPrefix', { reason: saveErrors[date] })}</div>
    }
    if (preview.status === 'loading') return <div className="atp-me-preview"><Spin size="small" /> {t('attendancePayroll.manualEntry.previewLoading')}</div>
    if (preview.status === 'error') return <div className="atp-me-preview atp-me-preview-error">{preview.text}</div>
    const { data } = preview
    return (
      <>
        <div className="atp-me-preview atp-me-preview-ok">
          {t('attendancePayroll.manualEntry.previewHours', { hours: manualPreviewHoursText(data.paidHours) })}
          {data.willCreateSchedule
            ? t('attendancePayroll.manualEntry.previewNewSchedule', { start: data.scheduleStart, end: data.scheduleEnd })
            : t('attendancePayroll.manualEntry.previewExistingSchedule', { start: data.scheduleStart, end: data.scheduleEnd })}
        </div>
        {data.candidateOvertimeMinutes > 0 ? (
          <div className="atp-me-preview atp-me-preview-warn">
            {t('attendancePayroll.manualEntry.previewOvertime', { minutes: data.candidateOvertimeMinutes })}
          </div>
        ) : null}
      </>
    )
  }

  const renderDay = (date: string) => {
    const form = forms[date]
    if (!form) return null
    const rowsError = validateManualRows(form.rows)
    const hasSchedule = Boolean(dayOf(date)?.hasSchedule)
    const preview = previews[date]
    const showMeal = !hasSchedule || (preview?.status === 'ok' && preview.data.willCreateSchedule)
    return (
      <div key={date} className="atp-me-day">
        <div className="atp-me-day-head">
          <strong>{t(`attendancePayroll.weekdayShort.${dayjs(date).day()}`)} {dayjs(date).format('MM-DD')}</strong>
          {dayOf(date)?.missingPunch ? <Tag color="orange">{t('attendancePayroll.manualEntry.missingTag')}</Tag> : null}
        </div>
        {form.rows.map((row, index) => (
          // eslint-disable-next-line react/no-array-index-key
          <div key={index} className="atp-me-row">
            <span className="atp-me-label">{index === 0 ? t('attendancePayroll.manualEntry.clockIn') : ''}</span>
            <TimePicker
              format="HH:mm"
              minuteStep={5}
              needConfirm={false}
              allowClear={false}
              value={toDayjsTime(row.start)}
              placeholder={t('attendancePayroll.manualEntry.startPlaceholder')}
              onChange={(_value, text) => updateRow(date, index, { start: typeof text === 'string' ? text : '' })}
            />
            <span className="atp-me-to">{t('attendancePayroll.manualEntry.to')}</span>
            <TimePicker
              format="HH:mm"
              minuteStep={5}
              needConfirm={false}
              allowClear={false}
              value={toDayjsTime(row.end)}
              placeholder={t('attendancePayroll.manualEntry.endPlaceholder')}
              onChange={(_value, text) => updateRow(date, index, { end: typeof text === 'string' ? text : '' })}
            />
            {form.rows.length > 1 ? (
              <Button
                type="text"
                size="small"
                icon={<CloseOutlined />}
                aria-label={t('attendancePayroll.manualEntry.removeSegment')}
                onClick={() => updateForm(date, { rows: form.rows.filter((_, rowIndex) => rowIndex !== index) })}
              />
            ) : null}
          </div>
        ))}
        <div className="atp-me-row">
          <span className="atp-me-label" />
          <Button type="link" size="small" icon={<PlusOutlined />} onClick={() => updateForm(date, { rows: [...form.rows, { start: '', end: '' }] })}>
            {t('attendancePayroll.manualEntry.addSegment')}
          </Button>
          {showMeal ? (
            <>
              <span className="atp-me-label atp-me-meal-label">{t('attendancePayroll.manualEntry.meal')}</span>
              <Select
                size="small"
                value={form.meal ?? 'auto'}
                style={{ width: 120 }}
                onChange={(value: number | 'auto') => updateForm(date, { meal: value === 'auto' ? null : value })}
                options={[
                  { value: 'auto', label: t('attendancePayroll.manualEntry.mealAuto') },
                  ...[0, 1, 2, 3].map((count) => ({ value: count, label: t('attendancePayroll.manualEntry.mealTimes', { count }) })),
                ]}
              />
            </>
          ) : null}
        </div>
        {rowsError && form.rows.some((row) => row.start || row.end)
          ? <div className="atp-me-preview atp-me-preview-error">{t(`attendancePayroll.manualEntry.rowsError.${rowsError}`)}</div>
          : renderPreview(date)}
      </div>
    )
  }

  return (
    <Drawer
      open={open}
      onClose={onClose}
      width={520}
      title={t('attendancePayroll.manualEntry.title')}
      destroyOnClose
      maskClosable={!saving}
      footer={(
        <div className="atp-me-footer">
          <span className="atp-me-total">
            {readyDates.length > 0
              ? t('attendancePayroll.manualEntry.total', { hours: manualPreviewHoursText(totalHours), count: readyDates.length })
              : t('attendancePayroll.manualEntry.totalEmpty')}
          </span>
          <Button onClick={onClose} disabled={saving}>{t('attendancePayroll.manualEntry.cancel')}</Button>
          <Button type="primary" onClick={() => void save()} disabled={!canSave} loading={saving}>
            {t('attendancePayroll.manualEntry.save', { count: readyDates.length })}
          </Button>
        </div>
      )}
    >
      <div className="atp-me-subtitle">
        {t('attendancePayroll.manualEntry.subtitle', { store: props.storeName ?? storeCode, from: days[0]?.date.slice(5), to: days[days.length - 1]?.date.slice(5) })}
      </div>

      {onSwitchMode ? <ManualEntryModeSwitch value="hours" disabled={saving} onChange={(mode) => onSwitchMode(mode, userGuid)} /> : null}

      <div className="atp-me-field">
        <div className="atp-me-field-label">{t('attendancePayroll.manualEntry.employee')}</div>
        <Select
          showSearch
          value={userGuid}
          placeholder={t('attendancePayroll.manualEntry.employeePlaceholder')}
          optionFilterProp="label"
          style={{ width: '100%' }}
          onChange={(value) => {
            setUserGuid(value)
            // 换人后之前的预览与已选日期作废（每个人每天的状态不同）
            signatures.current = {}
            clearPreviewTimers()
            setPreviews({})
            setSaveErrors({})
            setSelected([])
            setForms({})
          }}
          options={staffOptions.map((item) => ({
            value: item.value,
            label: item.label,
            zero: item.zero,
          }))}
          optionRender={(option) => (
            <div className="atp-me-option">
              <Avatar size={22} style={{ background: avatarColorOf(String(option.label)), fontSize: 10 }}>{initialsOf(String(option.label))}</Avatar>
              <span>{option.label}</span>
              {(option.data as { zero?: boolean }).zero ? <Tag color="orange" className="atp-tag">{t('attendancePayroll.manualEntry.zeroHours')}</Tag> : null}
            </div>
          )}
        />
      </div>

      <div className="atp-me-field">
        <div className="atp-me-field-label">{t('attendancePayroll.manualEntry.days')}</div>
        <div className="atp-me-days">
          {days.map((day) => {
            const state = userGuid ? manualEntryDayState(dayOf(day.date), day.date, today) : 'ok'
            const disabled = !userGuid || state !== 'ok'
            const chip = (
              <button
                key={day.date}
                type="button"
                disabled={disabled}
                className={['atp-me-chip', selected.includes(day.date) ? 'is-on' : '', state === 'hasPunches' ? 'is-has' : '', state === 'future' || state === 'leave' ? 'is-off' : ''].join(' ')}
                onClick={() => toggleDay(day.date)}
              >
                <span>{t(`attendancePayroll.weekdayShort.${dayjs(day.date).day()}`)}</span>
                <span>{dayjs(day.date).format('MM-DD')}</span>
              </button>
            )
            return state === 'ok' ? chip : (
              <Tooltip key={day.date} title={t(`attendancePayroll.manualEntry.dayState.${state}`)}>
                <span>{chip}</span>
              </Tooltip>
            )
          })}
        </div>
        <div className="atp-me-hint">{t('attendancePayroll.manualEntry.daysHint')}</div>
      </div>

      {selected.map(renderDay)}

      <div className="atp-me-field">
        <div className="atp-me-field-label">{t('attendancePayroll.manualEntry.reason')} <span className="atp-me-required">*</span></div>
        <div className="atp-me-hint atp-me-hint-top">{t('attendancePayroll.manualEntry.reasonHint')}</div>
        <Input.TextArea
          value={reason}
          maxLength={MANUAL_ENTRY_REASON_MAX}
          showCount
          autoSize={{ minRows: 2, maxRows: 4 }}
          placeholder={t('attendancePayroll.manualEntry.reasonPlaceholder')}
          onChange={(event) => setReason(event.target.value)}
        />
      </div>
    </Drawer>
  )
}
