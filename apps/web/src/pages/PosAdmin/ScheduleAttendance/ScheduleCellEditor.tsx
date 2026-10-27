import { DeleteOutlined } from '@ant-design/icons'
import { Button, Input, Segmented, TimePicker } from 'antd'
import dayjs from 'dayjs'
import type { Dayjs } from 'dayjs'
import type { KeyboardEvent } from 'react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { AttendanceScheduleDto, AttendanceScheduleStatus } from '../../../types/scheduleAttendance'

import { getShiftHours, normalizeHm } from './scheduleCellEditorLogic'
import type { ShiftPreset } from './scheduleCellEditorLogic'
import './scheduleCellEditor.css'

export interface CellEditorValues {
  start: string
  end: string
  remark?: string
  status: AttendanceScheduleStatus
  /** 仅新建时可选：同一时段一并铺到本周其他日期。 */
  extraDateKeys: string[]
}

export interface CellEditorWeekDay {
  dateKey: string
  weekdayLabel: string
  /** 该员工当天已有未取消的班次，不能再一并铺入。 */
  occupied: boolean
  /** ISO 周序号 0=周一 … 6=周日，用于「工作日」快捷选择。 */
  isoIndex: number
}

interface ScheduleCellEditorProps {
  employeeName: string
  dateLabel: string
  currentDateKey: string
  /** 有值 = 编辑已有班次；无值 = 在空白格新建。 */
  schedule?: AttendanceScheduleDto
  presets: ShiftPreset[]
  defaultShift: { start: string; end: string }
  weekDays: CellEditorWeekDay[]
  saving: boolean
  onSave: (values: CellEditorValues, options: { advance: boolean }) => void
  onDelete: () => void
  onClose: () => void
}

const SCHEDULE_STATUSES: AttendanceScheduleStatus[] = ['Draft', 'Active', 'Cancelled']
const toTime = (value: string) => dayjs(value, 'HH:mm')

/** 周排班单元格就地编辑浮层：快捷班次 + 时间 + 备注，新建时可同时应用到本周其他日期。 */
export default function ScheduleCellEditor({
  employeeName,
  dateLabel,
  currentDateKey,
  schedule,
  presets,
  defaultShift,
  weekDays,
  saving,
  onSave,
  onDelete,
  onClose,
}: ScheduleCellEditorProps) {
  const { t } = useTranslation()
  const rootRef = useRef<HTMLDivElement>(null)
  const [range, setRange] = useState<[Dayjs, Dayjs]>(() => [
    toTime(schedule ? normalizeHm(schedule.startTime) : defaultShift.start),
    toTime(schedule ? normalizeHm(schedule.endTime) : defaultShift.end),
  ])
  const [remark, setRemark] = useState(schedule?.remark ?? '')
  const [status, setStatus] = useState<AttendanceScheduleStatus>(schedule?.status ?? 'Draft')
  const [extraDateKeys, setExtraDateKeys] = useState<string[]>([])

  // 打开后把焦点放到浮层根节点，Enter / Esc 才能直接生效，不用先点一下
  useEffect(() => {
    const node = rootRef.current
    if (!node) return undefined
    node.focus()
    // 保存后跳到下一格时，新浮层可能在首次对齐前还是隐藏状态，第一次 focus 会落空；稍后补一次（用户已点进浮层内就不抢焦点）
    const timer = window.setTimeout(() => {
      if (!node.contains(document.activeElement)) node.focus()
    }, 80)
    return () => window.clearTimeout(timer)
  }, [])

  const start = range[0].format('HH:mm')
  const end = range[1].format('HH:mm')
  const hours = getShiftHours(start, end)
  const selectableDays = useMemo(
    () => weekDays.filter((day) => day.dateKey !== currentDateKey && !day.occupied),
    [currentDateKey, weekDays],
  )

  const submit = (advance: boolean) => {
    if (saving) return
    onSave({ start, end, remark: remark.trim() || undefined, status, extraDateKeys }, { advance })
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    // 只在焦点就是浮层根节点时响应 Enter，避免截走时间选择器 / 按钮自己的 Enter
    if (event.key === 'Enter' && event.target === event.currentTarget) {
      event.preventDefault()
      submit(true)
      return
    }
    if (event.key === 'Escape') {
      // 焦点在时间选择器且下拉还开着时，第一次 Esc 只收起下拉
      const pickerHasDropdownOpen = (event.target as HTMLElement).closest('.ant-picker-focused')
        && document.querySelector('.ant-picker-dropdown:not(.ant-picker-dropdown-hidden)')
      if (pickerHasDropdownOpen) return
      event.stopPropagation()
      onClose()
    }
  }

  const toggleExtraDay = (dateKey: string) => {
    setExtraDateKeys((prev) => (prev.includes(dateKey) ? prev.filter((key) => key !== dateKey) : [...prev, dateKey]))
  }

  const presetLabel = (preset: ShiftPreset) => (
    preset.nameKey ? t(`posAdmin.scheduleAttendance.cellEditor.presetNames.${preset.nameKey}`) : undefined
  )

  return (
    <div ref={rootRef} className="sce-editor" tabIndex={-1} onKeyDown={handleKeyDown}>
      <div className="sce-editor-head">
        <span className="sce-editor-who">{employeeName}</span>
        <span className="sce-editor-when">
          {dateLabel} · {schedule ? t('posAdmin.scheduleAttendance.cellEditor.editing') : t('posAdmin.scheduleAttendance.cellEditor.creating')}
        </span>
      </div>

      <div className="sce-label"><span>{t('posAdmin.scheduleAttendance.cellEditor.presets')}</span></div>
      <div className="sce-presets">
        {presets.map((preset) => {
          const label = presetLabel(preset)
          const timeText = `${preset.start}–${preset.end}`
          const on = preset.start === start && preset.end === end
          return (
            <button
              key={`${preset.start}-${preset.end}`}
              type="button"
              className={`sce-preset${on ? ' sce-preset--on' : ''}`}
              onClick={() => setRange([toTime(preset.start), toTime(preset.end)])}
            >
              <span className="sce-preset-name">{label ?? timeText}</span>
              {label ? <span className="sce-preset-time">{timeText}</span> : null}
            </button>
          )
        })}
      </div>

      <div className="sce-label"><span>{t('posAdmin.scheduleAttendance.cellEditor.shiftTime')}</span></div>
      <div className="sce-time-row">
        <TimePicker.RangePicker
          format="HH:mm"
          allowClear={false}
          value={range}
          onChange={(value) => {
            if (value?.[0] && value[1]) setRange([value[0], value[1]])
          }}
        />
        {hours !== undefined ? (
          <span className="sce-duration">{t('posAdmin.scheduleAttendance.cellEditor.hours', { hours })}</span>
        ) : null}
      </div>

      {schedule ? (
        <>
          <div className="sce-label"><span>{t('common.status')}</span></div>
          <Segmented
            block
            value={status}
            onChange={(value) => setStatus(value as AttendanceScheduleStatus)}
            options={SCHEDULE_STATUSES.map((value) => ({
              value,
              label: t(`posAdmin.scheduleAttendance.status.schedule.${value}`),
            }))}
          />
        </>
      ) : null}

      <div className="sce-label">
        <span>{t('column.remarks')}</span>
        <span>{t('posAdmin.scheduleAttendance.cellEditor.optional')}</span>
      </div>
      <Input
        value={remark}
        maxLength={200}
        placeholder={t('posAdmin.scheduleAttendance.cellEditor.remarkPlaceholder')}
        onChange={(event) => setRemark(event.target.value)}
        onPressEnter={() => submit(true)}
      />

      {!schedule && weekDays.length > 1 ? (
        <>
          <div className="sce-label">
            <span>{t('posAdmin.scheduleAttendance.cellEditor.applyToOthers')}</span>
            <span className="sce-quick">
              <button
                type="button"
                onClick={() => setExtraDateKeys(selectableDays.filter((day) => day.isoIndex <= 4).map((day) => day.dateKey))}
              >
                {t('posAdmin.scheduleAttendance.cellEditor.workdays')}
              </button>
              <button type="button" onClick={() => setExtraDateKeys(selectableDays.map((day) => day.dateKey))}>
                {t('posAdmin.scheduleAttendance.cellEditor.allEmpty')}
              </button>
            </span>
          </div>
          <div className="sce-days">
            {weekDays.map((day) => {
              const locked = day.dateKey === currentDateKey
              const checked = extraDateKeys.includes(day.dateKey)
              return (
                <button
                  key={day.dateKey}
                  type="button"
                  disabled={locked || day.occupied}
                  title={day.occupied ? t('posAdmin.scheduleAttendance.cellEditor.occupied') : undefined}
                  className={`sce-day${locked ? ' sce-day--lock' : checked ? ' sce-day--on' : ''}`}
                  onClick={() => toggleExtraDay(day.dateKey)}
                >
                  {day.weekdayLabel}
                </button>
              )
            })}
          </div>
        </>
      ) : null}

      {!schedule ? (
        <div className="sce-hint">{t('posAdmin.scheduleAttendance.cellEditor.hintDraft')}</div>
      ) : schedule.status === 'Active' ? (
        <div className="sce-hint">{t('posAdmin.scheduleAttendance.cellEditor.hintActive')}</div>
      ) : null}

      <div className="sce-foot">
        {schedule ? (
          <Button danger type="text" icon={<DeleteOutlined />} disabled={saving} onClick={onDelete}>
            {t('common.delete')}
          </Button>
        ) : (
          <span className="sce-foot-tip">{t('posAdmin.scheduleAttendance.cellEditor.shortcuts')}</span>
        )}
        <span style={{ marginLeft: 'auto' }} />
        <Button onClick={onClose} disabled={saving}>{t('common.cancel')}</Button>
        <Button type="primary" loading={saving} onClick={() => submit(false)}>{t('common.save')}</Button>
      </div>
    </div>
  )
}
