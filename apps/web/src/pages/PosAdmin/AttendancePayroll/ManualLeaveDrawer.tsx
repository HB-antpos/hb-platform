import { Avatar, Button, DatePicker, Drawer, Input, InputNumber, Select, Spin, Switch, Tag, message } from 'antd'
import dayjs from 'dayjs'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { createAttendanceManualLeave, previewAttendanceManualLeave } from '../../../services/attendancePayrollService'
import { getAttendanceEmployees } from '../../../services/scheduleAttendanceService'
import type {
  AttendanceHoursDayHeaderDto,
  AttendanceHoursEmployeeDto,
  AttendanceManualLeavePreview,
  AttendanceManualLeaveType,
} from '../../../types/attendancePayroll'
import { avatarColorOf, initialsOf } from '../AttendanceReview/reviewLogic'
import type { ManualEntryPreset } from './ManualEntryDrawer'
import ManualEntryModeSwitch from './ManualEntryModeSwitch'
import {
  EMPTY_MANUAL_LEAVE_FORM,
  MANUAL_ENTRY_REASON_MAX,
  buildManualLeavePayload,
  collectSickEligibleDays,
  isManualLeaveDateDisabled,
  isManualLeaveFormReady,
  manualEntryErrorText,
  manualPreviewHoursText,
  type ManualEntryMode,
  type ManualLeaveForm,
} from './manualEntryLogic'

interface ManualLeaveDrawerProps {
  open: boolean
  storeCode: string
  storeName?: string
  /** 本期的日期表头（1 周 7 天 / 2 周 14 天）。 */
  days: readonly AttendanceHoursDayHeaderDto[]
  /** 工时统计里有数据的人：用来找「有排班没打卡」的班，以及识别临时工。 */
  employees: readonly AttendanceHoursEmployeeDto[]
  leaveType: AttendanceManualLeaveType
  preset?: ManualEntryPreset | null
  onClose: () => void
  /** 保存成功：外层据此重载工时与工资。 */
  onSaved: () => void
  /** 顶部切换模式（补工时 / 病假 / 年假）：外层换成对应的抽屉，并带上已选员工。 */
  onSwitchMode: (mode: ManualEntryMode, userGuid?: string) => void
}

type PreviewState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ok'; data: AttendanceManualLeavePreview }
  | { status: 'error'; text: string }

/** 预览防抖：连续改日期时只发最后一次。 */
const PREVIEW_DEBOUNCE_MS = 450

const EMPTY_FORM = EMPTY_MANUAL_LEAVE_FORM

export default function ManualLeaveDrawer(props: ManualLeaveDrawerProps) {
  const { open, storeCode, days, employees, leaveType, preset, onClose, onSaved, onSwitchMode } = props
  const { t } = useTranslation()
  const [staff, setStaff] = useState<{ userGuid: string; label: string }[]>([])
  const [userGuid, setUserGuid] = useState<string>()
  const [form, setForm] = useState<ManualLeaveForm>(EMPTY_FORM)
  const [reason, setReason] = useState('')
  const [preview, setPreview] = useState<PreviewState>({ status: 'idle' })
  const [saving, setSaving] = useState(false)
  // 保存失败的提示单独放：不能覆盖预览结果，否则没法不改日期直接重试
  const [saveError, setSaveError] = useState<string>()
  // 最近一次预览的输入签名：签名没变就不重复请求；返回时签名对不上说明已过期，丢弃。
  const signature = useRef('')
  const previewTimer = useRef<number>()
  const today = dayjs().format('YYYY-MM-DD')
  const periodStart = days[0]?.date

  const employee = useMemo(() => employees.find((item) => item.userGuid === userGuid), [employees, userGuid])
  const sickDays = useMemo(() => collectSickEligibleDays(employee, today), [employee, today])
  const isCasual = employee?.employmentType === 'Casual'

  // 每次打开 / 换模式：载入本店员工，套用预设，清空上次的填写。
  useEffect(() => {
    if (!open) return undefined
    let cancelled = false
    signature.current = ''
    window.clearTimeout(previewTimer.current)
    setPreview({ status: 'idle' })
    setSaveError(undefined)
    setReason('')
    setUserGuid(preset?.userGuid)
    const dates = [...(preset?.dates ?? [])].sort()
    setForm(leaveType === 'PaidSick'
      ? { ...EMPTY_FORM, sickDates: dates }
      : { ...EMPTY_FORM, range: dates.length > 0 ? [dates[0], dates[dates.length - 1]] : null })
    getAttendanceEmployees(storeCode)
      .then((list) => {
        if (cancelled) return
        setStaff(list.map((item) => ({ userGuid: item.userGuid, label: item.fullName || item.nickname || item.username })))
      })
      .catch(() => { if (!cancelled) setStaff([]) })
    return () => {
      cancelled = true
      window.clearTimeout(previewTimer.current)
    }
    // 只在打开 / 换模式时重置；employees 变化（重载工时）不应清掉正在填的内容
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, storeCode, preset, leaveType])

  // 实时预览：员工与日期选好后，防抖请求后端预览（带薪工时与每天明细）。
  useEffect(() => {
    if (!open || !userGuid || !isManualLeaveFormReady(leaveType, form)) {
      signature.current = ''
      setPreview((current) => (current.status === 'idle' ? current : { status: 'idle' }))
      return
    }
    // 预览用占位原因，真正保存时会带上填写的原因重新预览一次
    const payload = buildManualLeavePayload({ storeCode, userGuid, type: leaveType, form, reason: '—' })
    const next = JSON.stringify([payload.userGuid, payload.leaveType, payload.startDate, payload.endDate, payload.dates, payload.totalHours, payload.restsOnLeaveDays])
    if (signature.current === next) return
    signature.current = next
    setSaveError(undefined)
    setPreview({ status: 'loading' })
    window.clearTimeout(previewTimer.current)
    previewTimer.current = window.setTimeout(() => {
      previewAttendanceManualLeave(payload)
        .then((data) => { if (signature.current === next) setPreview({ status: 'ok', data }) })
        .catch((error: unknown) => {
          if (signature.current === next) setPreview({ status: 'error', text: manualEntryErrorText(error, t('attendancePayroll.manualLeave.previewFailed')) })
        })
    }, PREVIEW_DEBOUNCE_MS)
  }, [open, userGuid, leaveType, form, storeCode, t])

  const staffOptions = useMemo(() => staff.map((item) => ({ value: item.userGuid, label: item.label })), [staff])
  const canSave = Boolean(userGuid) && reason.trim().length > 0 && preview.status === 'ok' && !saving
  const totalHours = preview.status === 'ok' ? preview.data.paidHours : 0

  const save = async () => {
    if (!userGuid) return
    setSaving(true)
    setSaveError(undefined)
    try {
      // 先带真实原因重新预览拿版本号（版本号含原因），再提交；失败时预览结果保留，改正后可直接再点保存。
      const payload = buildManualLeavePayload({ storeCode, userGuid, type: leaveType, form, reason: reason.trim() })
      const fresh = await previewAttendanceManualLeave(payload)
      await createAttendanceManualLeave({ ...payload, previewRevision: fresh.previewRevision })
      message.success(t('attendancePayroll.manualLeave.saved', { type: t(`attendancePayroll.manualLeave.mode.${leaveType}`) }))
      onSaved()
      onClose()
    } catch (error) {
      setSaveError(manualEntryErrorText(error, t('attendancePayroll.manualLeave.saveFailed')))
    } finally {
      setSaving(false)
    }
  }

  const toggleSickDate = (date: string) =>
    setForm((current) => ({
      ...current,
      sickDates: current.sickDates.includes(date) ? current.sickDates.filter((item) => item !== date) : [...current.sickDates, date].sort(),
    }))

  const renderPreview = () => {
    if (saveError) {
      return <div className="atp-me-preview atp-me-preview-error">{t('attendancePayroll.manualEntry.saveFailedPrefix', { reason: saveError })}</div>
    }
    if (preview.status === 'idle') return null
    if (preview.status === 'loading') return <div className="atp-me-preview"><Spin size="small" /> {t('attendancePayroll.manualLeave.previewLoading')}</div>
    if (preview.status === 'error') return <div className="atp-me-preview atp-me-preview-error">{preview.text}</div>
    const { data } = preview
    return (
      <>
        <div className="atp-me-preview atp-me-preview-ok">
          {t('attendancePayroll.manualLeave.previewHours', { hours: manualPreviewHoursText(data.paidHours) })}
          <div className="atp-ml-days">
            {data.days.map((day) => (
              <span key={day.date} className={['atp-ml-day', day.hours > 0 ? '' : 'is-zero'].join(' ')}>
                {dayjs(day.date).format('MM-DD')} · {day.hours > 0 ? manualPreviewHoursText(day.hours) : '—'}
              </span>
            ))}
          </div>
        </div>
        {data.noCertificate ? <div className="atp-me-preview atp-me-preview-warn">{t('attendancePayroll.manualLeave.noCertificate')}</div> : null}
      </>
    )
  }

  const renderDates = () => {
    if (!userGuid) return <div className="atp-me-hint">{t('attendancePayroll.manualLeave.pickEmployee')}</div>
    if (leaveType === 'PaidSick') {
      if (sickDays.length === 0) return <div className="atp-me-preview atp-me-preview-warn">{t('attendancePayroll.manualLeave.sickNone')}</div>
      return (
        <div className="atp-me-days">
          {sickDays.map((day) => (
            <button
              key={day.date}
              type="button"
              className={['atp-me-chip', form.sickDates.includes(day.date) ? 'is-on' : ''].join(' ')}
              onClick={() => toggleSickDate(day.date)}
            >
              <span>{t(`attendancePayroll.weekdayShort.${dayjs(day.date).day()}`)} {dayjs(day.date).format('MM-DD')}</span>
              {day.start && day.end ? <small>{t('attendancePayroll.manualLeave.sickChip', { start: day.start, end: day.end })}</small> : null}
            </button>
          ))}
        </div>
      )
    }
    return (
      <DatePicker.RangePicker
        style={{ width: '100%' }}
        allowClear
        // 本期的工时数据还没就绪（补载中）时不让选：此刻没有周期起点，会把本期之前的日子也放开
        disabled={days.length === 0}
        placeholder={[t('attendancePayroll.manualLeave.rangeStart'), t('attendancePayroll.manualLeave.rangeEnd')]}
        value={form.range ? [dayjs(form.range[0]), dayjs(form.range[1])] : null}
        disabledDate={(date) => isManualLeaveDateDisabled(date.format('YYYY-MM-DD'), periodStart, today)}
        // 换区间后合计小时数回到默认（新区间的工作日 × 8 h）：旧的手填值对新区间没有意义
        onChange={(value) => setForm((current) => ({
          ...current,
          range: value?.[0] && value?.[1] ? [value[0].format('YYYY-MM-DD'), value[1].format('YYYY-MM-DD')] : null,
          totalHours: null,
        }))}
      />
    )
  }

  // 年假的合计小时数与「员工当天休息」开关：合计是整段区间的总数，后端平均摊到每个工作日。
  const renderAnnualOptions = () => {
    if (leaveType !== 'PaidAnnual' || !userGuid || !form.range) return null
    const defaultHours = preview.status === 'ok' ? preview.data.defaultPaidHours : null
    const shownHours = form.totalHours ?? defaultHours
    return (
      <>
        <div className="atp-me-field">
          <div className="atp-me-field-label">{t('attendancePayroll.manualLeave.totalHoursLabel')}</div>
          <div className="atp-me-hint atp-me-hint-top">{t('attendancePayroll.manualLeave.totalHoursHint')}</div>
          <InputNumber
            style={{ width: 180 }}
            min={0.01}
            step={0.5}
            precision={2}
            addonAfter="h"
            value={shownHours}
            disabled={defaultHours === null && form.totalHours === null}
            onChange={(value) => setForm((current) => ({
              ...current,
              // 清空输入框 = 回到默认；0 或负数不接受（min 已限制，这里兜底）
              totalHours: typeof value === 'number' && value > 0 ? value : null,
            }))}
          />
          {defaultHours !== null && form.totalHours !== null && form.totalHours !== defaultHours ? (
            <Button type="link" size="small" onClick={() => setForm((current) => ({ ...current, totalHours: null }))}>
              {t('attendancePayroll.manualLeave.totalHoursReset', { hours: manualPreviewHoursText(defaultHours) })}
            </Button>
          ) : null}
        </div>
        <div className="atp-me-field">
          <div className="atp-me-field-label">{t('attendancePayroll.manualLeave.restsLabel')}</div>
          <Switch
            checked={form.rests}
            checkedChildren={t('attendancePayroll.manualLeave.restsOn')}
            unCheckedChildren={t('attendancePayroll.manualLeave.restsOff')}
            onChange={(checked) => setForm((current) => ({ ...current, rests: checked }))}
          />
          <div className="atp-me-hint atp-me-hint-top">
            {form.rests ? t('attendancePayroll.manualLeave.restsOnHint') : t('attendancePayroll.manualLeave.restsOffHint')}
          </div>
        </div>
      </>
    )
  }

  return (
    <Drawer
      open={open}
      onClose={onClose}
      width={520}
      title={t('attendancePayroll.manualLeave.title')}
      destroyOnClose
      maskClosable={!saving}
      footer={(
        <div className="atp-me-footer">
          <span className="atp-me-total">
            {preview.status === 'ok'
              ? t('attendancePayroll.manualLeave.total', { hours: manualPreviewHoursText(totalHours) })
              : t('attendancePayroll.manualLeave.totalEmpty')}
          </span>
          <Button onClick={onClose} disabled={saving}>{t('attendancePayroll.manualEntry.cancel')}</Button>
          <Button type="primary" onClick={() => void save()} disabled={!canSave} loading={saving}>
            {t('attendancePayroll.manualLeave.save')}
          </Button>
        </div>
      )}
    >
      <div className="atp-me-subtitle">
        {t('attendancePayroll.manualEntry.subtitle', { store: props.storeName ?? storeCode, from: days[0]?.date.slice(5), to: days[days.length - 1]?.date.slice(5) })}
      </div>

      <ManualEntryModeSwitch value={leaveType} disabled={saving} onChange={(mode) => onSwitchMode(mode, userGuid)} />
      <div className="atp-me-hint atp-ml-type-hint">{t(`attendancePayroll.manualLeave.typeHint.${leaveType}`)}</div>

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
            // 换人后之前选的日期与预览作废（每个人的班不同）
            signature.current = ''
            window.clearTimeout(previewTimer.current)
            setPreview({ status: 'idle' })
            setSaveError(undefined)
            setForm(EMPTY_FORM)
          }}
          options={staffOptions}
          optionRender={(option) => (
            <div className="atp-me-option">
              <Avatar size={22} style={{ background: avatarColorOf(String(option.label)), fontSize: 10 }}>{initialsOf(String(option.label))}</Avatar>
              <span>{option.label}</span>
            </div>
          )}
        />
        {isCasual ? <div className="atp-me-preview atp-me-preview-warn">{t('attendancePayroll.manualLeave.ineligible')}</div> : null}
      </div>

      <div className="atp-me-field">
        <div className="atp-me-field-label">
          {leaveType === 'PaidSick' ? t('attendancePayroll.manualLeave.sickDays') : t('attendancePayroll.manualLeave.range')}
          {employee && leaveType === 'PaidSick' ? <Tag className="atp-tag" style={{ marginLeft: 6 }}>{sickDays.length}</Tag> : null}
        </div>
        {renderDates()}
        {renderPreview()}
      </div>

      {renderAnnualOptions()}

      <div className="atp-me-field">
        <div className="atp-me-field-label">{t('attendancePayroll.manualEntry.reason')} <span className="atp-me-required">*</span></div>
        <div className="atp-me-hint atp-me-hint-top">{t('attendancePayroll.manualEntry.reasonHint')}</div>
        <Input.TextArea
          value={reason}
          maxLength={MANUAL_ENTRY_REASON_MAX}
          showCount
          autoSize={{ minRows: 2, maxRows: 4 }}
          placeholder={t('attendancePayroll.manualLeave.reasonPlaceholder')}
          onChange={(event) => setReason(event.target.value)}
        />
      </div>
    </Drawer>
  )
}
