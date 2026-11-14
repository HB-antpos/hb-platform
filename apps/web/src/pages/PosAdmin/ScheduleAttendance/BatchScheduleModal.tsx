import { CheckCircleFilled, ExclamationCircleFilled, LoadingOutlined, SearchOutlined, WarningOutlined } from '@ant-design/icons'
import { Button, Input, Modal, Segmented, TimePicker } from 'antd'
import dayjs from 'dayjs'
import type { Dayjs } from 'dayjs'
import type { CSSProperties, KeyboardEvent } from 'react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { createAttendanceSchedule } from '../../../services/scheduleAttendanceService'
import type { SaveAttendanceSchedulePayload } from '../../../types/scheduleAttendance'

import type { DayAvailability } from './availabilityLogic'
import {
  applyBatchOverrides,
  batchCellKey,
  buildBatchPayloads,
  buildBatchPlan,
  runWithConcurrency,
  summarizePlan,
  summarizeResults,
} from './batchScheduleLogic'
import type { BatchCell, BatchResultItem, ExistingShift } from './batchScheduleLogic'
import { DEFAULT_SHIFT, SHIFT_PRESETS, getMealSummary } from './scheduleCellEditorLogic'
import type { MealChoice } from './scheduleCellEditorLogic'
import type { StaffColor } from './staffColorLogic'
import { formatHours } from './weekSummaryLogic'

/** 批量排班：选员工 × 选日期 × 统一班次，预览结果后逐个提交（最多同时 4 个），做完逐条汇总。 */

export interface BatchModalUser {
  userGuid: string
  name: string
  /** 21 岁以下才有值。 */
  age?: number
  color: StaffColor
  /** 本周已排（未取消）的班次数与工时。 */
  weekShifts: number
  weekHours: number
  /** 本周上报「可上班」的星期标签（周一、周二…）。 */
  availableDays: string[]
  /** 本周上报「不能上班」的星期标签。 */
  unavailableDays: string[]
}

export interface BatchModalDay {
  dateKey: string
  dateLabel: string
  weekdayLabel: string
  /** 0=周一 … 6=周日。 */
  isoIndex: number
  holiday?: string
  /** 当天已有班次的人数（未取消）。 */
  scheduledCount: number
}

/** 打开弹窗时的预填：点某天的「添加班次」「可上班未排」，或把常用班次拖到某天。 */
export interface BatchModalInitial {
  dateKeys?: string[]
  userGuids?: string[]
  start?: string
  end?: string
}

interface BatchScheduleModalProps {
  open: boolean
  storeCode: string
  storeName?: string
  weekLabel: string
  days: BatchModalDay[]
  users: BatchModalUser[]
  initial: BatchModalInitial | null
  existingOf: (userGuid: string, dateKey: string) => readonly ExistingShift[]
  availabilityOf: (userGuid: string, dateKey: string) => DayAvailability | undefined
  onClose: () => void
  /** 有班次创建成功后调用（刷新周视图）。 */
  onChanged: () => void
}

type Phase = 'edit' | 'running' | 'result'

const CONCURRENCY = 4
const MEAL_CHOICES: MealChoice[] = ['auto', 0, 1, 2, 3]
const toTime = (value: string) => dayjs(value, 'HH:mm')
const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })
const colorVars = (color: StaffColor): CSSProperties => (
  { '--sce-bg': color.bg, '--sce-bar': color.bar, '--sce-ink': color.ink } as CSSProperties
)

export default function BatchScheduleModal({
  open,
  storeCode,
  storeName,
  weekLabel,
  days,
  users,
  initial,
  existingOf,
  availabilityOf,
  onClose,
  onChanged,
}: BatchScheduleModalProps) {
  const { t } = useTranslation()
  const [phase, setPhase] = useState<Phase>('edit')
  const [selectedUsers, setSelectedUsers] = useState<string[]>([])
  const [selectedDates, setSelectedDates] = useState<string[]>([])
  const [range, setRange] = useState<[Dayjs, Dayjs]>([toTime(DEFAULT_SHIFT.start), toTime(DEFAULT_SHIFT.end)])
  const [mealChoice, setMealChoice] = useState<MealChoice>('auto')
  const [remark, setRemark] = useState('')
  const [overrides, setOverrides] = useState<Map<string, boolean>>(() => new Map())
  const [keyword, setKeyword] = useState('')
  const [onlyAvailable, setOnlyAvailable] = useState(false)
  const [onlyMinor, setOnlyMinor] = useState(false)
  const [unscheduledFirst, setUnscheduledFirst] = useState(true)
  const [results, setResults] = useState<BatchResultItem[]>([])
  const [progress, setProgress] = useState({ done: 0, total: 0 })
  // 提交那一刻的快照：结果页要列出「跳过 / 不排」的格子，以及重试用的载荷
  const [snapshot, setSnapshot] = useState<{ skipped: BatchCell[]; excluded: BatchCell[]; payloads: SaveAttendanceSchedulePayload[] }>({ skipped: [], excluded: [], payloads: [] })
  const changedRef = useRef(false)

  // 每次打开都重置为预填状态
  useEffect(() => {
    if (!open) return
    setPhase('edit')
    setSelectedUsers(initial?.userGuids ?? [])
    setSelectedDates(initial?.dateKeys ?? [])
    setRange([toTime(initial?.start ?? DEFAULT_SHIFT.start), toTime(initial?.end ?? DEFAULT_SHIFT.end)])
    setMealChoice('auto')
    setRemark('')
    setOverrides(new Map())
    setKeyword('')
    setOnlyAvailable(false)
    setOnlyMinor(false)
    setUnscheduledFirst(true)
    setResults([])
    setProgress({ done: 0, total: 0 })
    setSnapshot({ skipped: [], excluded: [], payloads: [] })
    changedRef.current = false
  }, [open, initial])

  const start = range[0].format('HH:mm')
  const end = range[1].format('HH:mm')
  const userByGuid = useMemo(() => new Map(users.map((user) => [user.userGuid, user])), [users])
  const dayByKey = useMemo(() => new Map(days.map((day) => [day.dateKey, day])), [days])
  const selectedUserSet = useMemo(() => new Set(selectedUsers), [selectedUsers])
  const selectedDateSet = useMemo(() => new Set(selectedDates), [selectedDates])

  // ===== 左侧员工列表：筛选 + 排序 =====
  const availableCount = useMemo(() => users.filter((user) => user.availableDays.length > 0).length, [users])
  const minorCount = useMemo(() => users.filter((user) => user.age !== undefined).length, [users])
  const visibleUsers = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    return users
      .filter((user) => (!needle || user.name.toLowerCase().includes(needle))
        && (!onlyAvailable || user.availableDays.length > 0)
        && (!onlyMinor || user.age !== undefined))
      .sort((left, right) => {
        if (unscheduledFirst && (left.weekShifts === 0) !== (right.weekShifts === 0)) return left.weekShifts === 0 ? -1 : 1
        return collator.compare(left.name, right.name)
      })
  }, [keyword, onlyAvailable, onlyMinor, unscheduledFirst, users])

  const toggleUser = (userGuid: string) => {
    setSelectedUsers((prev) => (prev.includes(userGuid) ? prev.filter((item) => item !== userGuid) : [...prev, userGuid]))
  }
  const toggleDate = (dateKey: string) => {
    setSelectedDates((prev) => (prev.includes(dateKey) ? prev.filter((item) => item !== dateKey) : [...prev, dateKey]))
  }

  // ===== 预览计划 =====
  const orderedUsers = useMemo(
    () => users.filter((user) => selectedUserSet.has(user.userGuid)).sort((left, right) => collator.compare(left.name, right.name)),
    [selectedUserSet, users],
  )
  const orderedDates = useMemo(() => [...selectedDates].sort(), [selectedDates])
  const plan = useMemo(() => applyBatchOverrides(
    buildBatchPlan({
      userGuids: orderedUsers.map((user) => user.userGuid),
      dateKeys: orderedDates,
      start,
      end,
      existingOf,
      availabilityOf,
    }),
    overrides,
  ), [availabilityOf, end, existingOf, orderedDates, orderedUsers, overrides, start])
  const summary = useMemo(() => summarizePlan(plan), [plan])
  const cellOf = useMemo(() => new Map(plan.map((cell) => [batchCellKey(cell), cell])), [plan])
  const minorSelected = orderedUsers.filter((user) => user.age !== undefined).length

  const toggleCell = (cell: BatchCell) => {
    if (cell.state === 'skip') return
    setOverrides((prev) => new Map(prev).set(batchCellKey(cell), !cell.include))
  }
  const changeRange = (next: [Dayjs, Dayjs]) => {
    setRange(next)
    // 时间一变，哪些格子重叠 / 冲突都变了，之前手动点的取舍作废
    setOverrides(new Map())
  }

  const mealSummary = getMealSummary(start, end, mealChoice)

  // ===== 提交 =====
  const errorText = (error: unknown) => (
    error instanceof Error && error.message ? error.message : t('posAdmin.scheduleAttendance.messages.saveScheduleFailed')
  )

  const execute = useCallback(async (payloads: SaveAttendanceSchedulePayload[], previous: BatchResultItem[]) => {
    setPhase('running')
    setProgress({ done: 0, total: payloads.length })
    // 重试时保留之前成功的，只替换这次重试的那些
    const retryKeys = new Set(payloads.map((item) => `${item.userGuid}|${item.workDate}`))
    setResults(previous.filter((item) => !retryKeys.has(`${item.userGuid}|${item.dateKey}`)))
    await runWithConcurrency(
      payloads,
      CONCURRENCY,
      async (payload) => {
        let item: BatchResultItem
        try {
          await createAttendanceSchedule(payload)
          item = { userGuid: payload.userGuid, dateKey: payload.workDate, ok: true }
          changedRef.current = true
        } catch (error) {
          console.error(error)
          item = { userGuid: payload.userGuid, dateKey: payload.workDate, ok: false, error: errorText(error) }
        }
        setResults((prev) => [...prev, item])
      },
      () => setProgress((prev) => ({ ...prev, done: prev.done + 1 })),
    )
    setPhase('result')
    if (changedRef.current) onChanged()
    // errorText 只依赖 t，onChanged 由页面保证稳定
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [onChanged, t])

  const submit = () => {
    const payloads = buildBatchPayloads(plan, { storeCode, start, end, remark, mealChoice })
    if (!payloads.length) return
    setSnapshot({
      skipped: plan.filter((cell) => cell.state === 'skip'),
      excluded: plan.filter((cell) => cell.state !== 'skip' && !cell.include),
      payloads,
    })
    void execute(payloads, [])
  }

  const retry = (only?: BatchResultItem) => {
    const failedKeys = new Set(
      (only ? [only] : results.filter((item) => !item.ok)).map((item) => `${item.userGuid}|${item.dateKey}`),
    )
    const payloads = snapshot.payloads.filter((item) => failedKeys.has(`${item.userGuid}|${item.workDate}`))
    if (payloads.length) void execute(payloads, results)
  }

  const finish = () => {
    onClose()
  }

  const running = phase === 'running'
  const nameOf = (userGuid: string) => userByGuid.get(userGuid)?.name ?? userGuid
  const dateText = (dateKey: string) => {
    const day = dayByKey.get(dateKey)
    return day ? `${day.dateLabel} ${day.weekdayLabel}` : dateKey
  }

  const handleRowKey = (event: KeyboardEvent<HTMLElement>, userGuid: string) => {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault()
      toggleUser(userGuid)
    }
  }

  // ===== 渲染：编辑 =====
  const renderEdit = () => (
    <>
      <div className="sbm-body">
        <div className="sbm-left">
          <div className="sbm-label">
            <span>{t('posAdmin.scheduleAttendance.batch.stepEmployees')}</span>
            <small>{t('posAdmin.scheduleAttendance.batch.employeesCount', { selected: selectedUsers.length, total: users.length })}</small>
            <span className="sbm-quick">
              <button type="button" onClick={() => setSelectedUsers(visibleUsers.map((user) => user.userGuid))}>{t('posAdmin.scheduleAttendance.batch.selectAll')}</button>
              <button type="button" onClick={() => setSelectedUsers([])}>{t('posAdmin.scheduleAttendance.batch.clear')}</button>
            </span>
          </div>
          <div className="sbm-filters">
            <button type="button" className={`sbm-pill${unscheduledFirst ? ' is-on' : ''}`} onClick={() => setUnscheduledFirst((prev) => !prev)}>
              {t('posAdmin.scheduleAttendance.batch.filterUnscheduledFirst')}
            </button>
            <button type="button" className={`sbm-pill${onlyAvailable ? ' is-on' : ''}`} onClick={() => setOnlyAvailable((prev) => !prev)}>
              {t('posAdmin.scheduleAttendance.batch.filterAvailable', { count: availableCount })}
            </button>
            <button type="button" className={`sbm-pill${onlyMinor ? ' is-on' : ''}`} onClick={() => setOnlyMinor((prev) => !prev)}>
              {t('posAdmin.scheduleAttendance.batch.filterMinor', { count: minorCount })}
            </button>
          </div>
          <Input
            allowClear
            prefix={<SearchOutlined />}
            placeholder={t('posAdmin.scheduleAttendance.batch.searchPlaceholder')}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <div className="sbm-list" role="group" aria-label={t('posAdmin.scheduleAttendance.batch.stepEmployees')}>
            {visibleUsers.length ? visibleUsers.map((user) => {
              const on = selectedUserSet.has(user.userGuid)
              return (
                <div
                  key={user.userGuid}
                  role="checkbox"
                  aria-checked={on}
                  tabIndex={0}
                  className={`sbm-item${on ? ' is-on' : ''}`}
                  onClick={() => toggleUser(user.userGuid)}
                  onKeyDown={(event) => handleRowKey(event, user.userGuid)}
                >
                  <span className="sbm-check">{on ? '✓' : ''}</span>
                  <div className="sbm-item-main">
                    <b>
                      <i className="sbm-dot" style={{ background: user.color.bar }} />
                      <span className="sbm-name">{user.name}</span>
                      {user.age !== undefined ? <span className="sbm-age">{t('posAdmin.scheduleAttendance.fields.ageBadge', { age: user.age })}</span> : null}
                    </b>
                    <small>
                      {user.availableDays.length ? <span className="sbm-ok">{t('posAdmin.scheduleAttendance.batch.availableOn', { days: user.availableDays.join(' ') })}</span> : null}
                      {user.unavailableDays.length ? <span className="sbm-bad">{t('posAdmin.scheduleAttendance.batch.unavailableOn', { days: user.unavailableDays.join(' ') })}</span> : null}
                      {!user.availableDays.length && !user.unavailableDays.length ? <span>{t('posAdmin.scheduleAttendance.batch.noReport')}</span> : null}
                    </small>
                  </div>
                  <span className={`sbm-week${user.weekShifts ? '' : ' is-empty'}`}>
                    {user.weekShifts
                      ? t('posAdmin.scheduleAttendance.batch.weekScheduled', { shifts: user.weekShifts, hours: formatHours(user.weekHours) })
                      : t('posAdmin.scheduleAttendance.batch.unscheduled')}
                  </span>
                </div>
              )
            }) : <div className="sbm-empty">{t('posAdmin.scheduleAttendance.batch.noMatch')}</div>}
          </div>
          <div className="sbm-note">{t('posAdmin.scheduleAttendance.batch.employeeHint')}</div>
        </div>

        <div className="sbm-right">
          <section className="sbm-sec">
            <div className="sbm-label">
              <span>{t('posAdmin.scheduleAttendance.batch.stepDates')}</span>
              <small>{t('posAdmin.scheduleAttendance.batch.datesCount', { count: selectedDates.length })}</small>
              <span className="sbm-quick">
                <button type="button" onClick={() => setSelectedDates(days.filter((day) => day.isoIndex <= 4).map((day) => day.dateKey))}>{t('posAdmin.scheduleAttendance.batch.workdays')}</button>
                <button type="button" onClick={() => setSelectedDates(days.filter((day) => day.isoIndex >= 5).map((day) => day.dateKey))}>{t('posAdmin.scheduleAttendance.batch.weekend')}</button>
                <button type="button" onClick={() => setSelectedDates(days.map((day) => day.dateKey))}>{t('posAdmin.scheduleAttendance.batch.allDays')}</button>
                <button type="button" onClick={() => setSelectedDates([])}>{t('posAdmin.scheduleAttendance.batch.clear')}</button>
              </span>
            </div>
            <div className="sbm-days">
              {days.map((day) => (
                <button
                  key={day.dateKey}
                  type="button"
                  aria-pressed={selectedDateSet.has(day.dateKey)}
                  className={`sbm-day${selectedDateSet.has(day.dateKey) ? ' is-on' : ''}`}
                  onClick={() => toggleDate(day.dateKey)}
                >
                  <b>{day.dateLabel}<em>{day.weekdayLabel}</em></b>
                  <small>{t('posAdmin.scheduleAttendance.batch.scheduledCount', { count: day.scheduledCount })}</small>
                  {day.holiday ? <span className="sbm-holiday">{day.holiday}</span> : null}
                </button>
              ))}
            </div>
          </section>

          <section className="sbm-sec">
            <div className="sbm-label">
              <span>{t('posAdmin.scheduleAttendance.batch.stepShift')}</span>
              <small>{t('posAdmin.scheduleAttendance.batch.shiftHint')}</small>
            </div>
            <div className="sbm-presets">
              {SHIFT_PRESETS.map((preset) => (
                <button
                  key={`${preset.start}-${preset.end}`}
                  type="button"
                  className={`sbm-preset${preset.start === start && preset.end === end ? ' is-on' : ''}`}
                  onClick={() => changeRange([toTime(preset.start), toTime(preset.end)])}
                >
                  {`${preset.start}–${preset.end}`}
                </button>
              ))}
              <TimePicker.RangePicker
                format="HH:mm"
                allowClear={false}
                value={range}
                onChange={(value) => {
                  if (value?.[0] && value[1]) changeRange([value[0], value[1]])
                }}
              />
            </div>
            <div className="sbm-shift-row">
              <div className="sbm-meal">
                <span>{t('posAdmin.scheduleAttendance.cellEditor.meal.title')}</span>
                <Segmented
                  size="small"
                  value={mealChoice}
                  onChange={(value) => setMealChoice(value as MealChoice)}
                  options={MEAL_CHOICES.map((value) => ({
                    value,
                    label: value === 'auto'
                      ? t('posAdmin.scheduleAttendance.cellEditor.meal.auto')
                      : value === 0
                        ? t('posAdmin.scheduleAttendance.cellEditor.meal.none')
                        : t('posAdmin.scheduleAttendance.cellEditor.meal.times', { count: value }),
                  }))}
                />
              </div>
              <Input
                className="sbm-remark"
                value={remark}
                maxLength={200}
                placeholder={t('posAdmin.scheduleAttendance.batch.remarkPlaceholder')}
                onChange={(event) => setRemark(event.target.value)}
              />
            </div>
            {mealSummary ? (
              <div className="sbm-note">
                {t('posAdmin.scheduleAttendance.batch.hoursLine', { hours: mealSummary.hours, paid: mealSummary.paidHours })}
                {' · '}
                {mealSummary.isAuto
                  ? mealSummary.count === 0
                    ? t('posAdmin.scheduleAttendance.cellEditor.meal.autoNone')
                    : t('posAdmin.scheduleAttendance.cellEditor.meal.autoCount', { count: mealSummary.count })
                  : mealSummary.count === 0
                    ? t('posAdmin.scheduleAttendance.cellEditor.meal.manualNone')
                    : t('posAdmin.scheduleAttendance.cellEditor.meal.manual')}
              </div>
            ) : null}
          </section>

          <section className="sbm-sec">
            <div className="sbm-label">
              <span>{t('posAdmin.scheduleAttendance.batch.stepPreview')}</span>
              <small>{t('posAdmin.scheduleAttendance.batch.previewHint')}</small>
            </div>
            {orderedUsers.length && orderedDates.length ? (
              <div className="sbm-preview">
                <div className="sbm-preview-head">
                  <b>{t('posAdmin.scheduleAttendance.batch.previewTitle', { count: summary.create })}</b>
                  {summary.warnings ? <span className="sbm-key is-warn">{t('posAdmin.scheduleAttendance.batch.legendWarn', { count: summary.warnings })}</span> : null}
                  {summary.skipped ? <span className="sbm-key is-skip">{t('posAdmin.scheduleAttendance.batch.legendSkipped', { count: summary.skipped })}</span> : null}
                  {summary.excluded ? <span className="sbm-key is-no">{t('posAdmin.scheduleAttendance.batch.legendExcluded', { count: summary.excluded })}</span> : null}
                </div>
                <div className="sbm-matrix" style={{ gridTemplateColumns: `150px repeat(${orderedDates.length}, minmax(96px, 1fr))` }}>
                  <div className="sbm-mh">{t('posAdmin.scheduleAttendance.fields.employee')}</div>
                  {orderedDates.map((dateKey) => <div key={dateKey} className="sbm-mh">{dateText(dateKey)}</div>)}
                  {orderedUsers.map((user) => (
                    <div key={user.userGuid} className="sbm-mrow">
                      <div className="sbm-mname"><i className="sbm-dot" style={{ background: user.color.bar }} />{user.name}</div>
                      {orderedDates.map((dateKey) => {
                        const cell = cellOf.get(batchCellKey({ userGuid: user.userGuid, dateKey }))
                        if (!cell) return <div key={dateKey} />
                        return (
                          <button
                            key={dateKey}
                            type="button"
                            disabled={cell.state === 'skip'}
                            style={colorVars(user.color)}
                            title={cell.state === 'skip' ? t('posAdmin.scheduleAttendance.batch.cellExistingTip', { range: cell.existingRange }) : undefined}
                            className={`sbm-cell is-${cell.include ? (cell.state === 'warn' || cell.state === 'unavailable' ? 'warn' : 'new') : cell.state === 'skip' ? 'skip' : cell.state === 'unavailable' ? 'bad' : 'off'}`}
                            onClick={() => toggleCell(cell)}
                          >
                            {cell.include
                              ? <>{cell.state === 'warn' || cell.state === 'unavailable' ? <WarningOutlined /> : null}{`${start}–${end}`}</>
                              : cell.state === 'skip'
                                ? t('posAdmin.scheduleAttendance.batch.cellExisting')
                                : cell.state === 'unavailable'
                                  ? t('posAdmin.scheduleAttendance.batch.cellUnavailable')
                                  : t('posAdmin.scheduleAttendance.batch.cellExcluded')}
                          </button>
                        )
                      })}
                    </div>
                  ))}
                </div>
              </div>
            ) : (
              <div className="sbm-empty">{t('posAdmin.scheduleAttendance.batch.emptyPreview')}</div>
            )}
          </section>
        </div>
      </div>
      <div className="sbm-foot">
        <span className="sbm-foot-hint">
          {t('posAdmin.scheduleAttendance.batch.footerHint')}
          {minorSelected ? ` ${t('posAdmin.scheduleAttendance.batch.minorHint', { count: minorSelected })}` : ''}
        </span>
        <span style={{ flex: 1 }} />
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button type="primary" disabled={summary.create === 0} onClick={submit}>
          {t('posAdmin.scheduleAttendance.batch.create', { count: summary.create })}
        </Button>
      </div>
    </>
  )

  // ===== 渲染：提交中 =====
  const renderRunning = () => {
    const perUser = Array.from(
      snapshot.payloads.reduce((map, payload) => {
        const entry = map.get(payload.userGuid) ?? { total: 0, done: 0 }
        entry.total += 1
        map.set(payload.userGuid, entry)
        return map
      }, new Map<string, { total: number; done: number }>()),
    )
    results.forEach((item) => {
      const entry = perUser.find(([guid]) => guid === item.userGuid)?.[1]
      if (entry) entry.done += 1
    })
    return (
      <>
        <div className="sbm-run">
          <div className="sbm-run-top"><LoadingOutlined /><b>{progress.done} / {progress.total}</b><span>{t('posAdmin.scheduleAttendance.batch.runningHint')}</span></div>
          <div className="sbm-bar"><i style={{ width: `${progress.total ? Math.round((progress.done / progress.total) * 100) : 0}%` }} /></div>
          {perUser.map(([userGuid, entry]) => (
            <div key={userGuid} className="sbm-run-row">
              {entry.done >= entry.total ? <CheckCircleFilled className="is-ok" /> : entry.done > 0 ? <LoadingOutlined /> : <span className="sbm-wait">○</span>}
              <span>{nameOf(userGuid)}</span>
              <span className="sbm-run-state">
                {entry.done >= entry.total
                  ? t('posAdmin.scheduleAttendance.batch.userDone', { done: entry.done, total: entry.total })
                  : entry.done > 0
                    ? t('posAdmin.scheduleAttendance.batch.userDoing', { done: entry.done, total: entry.total })
                    : t('posAdmin.scheduleAttendance.batch.userWaiting')}
              </span>
            </div>
          ))}
        </div>
        <div className="sbm-foot">
          <span className="sbm-foot-hint">{t('posAdmin.scheduleAttendance.batch.runningFooter')}</span>
          <span style={{ flex: 1 }} />
          <Button type="primary" disabled loading>{t('posAdmin.scheduleAttendance.batch.runningButton')}</Button>
        </div>
      </>
    )
  }

  // ===== 渲染：结果 =====
  const renderResult = () => {
    const { created, failed } = summarizeResults(results)
    const failedItems = results.filter((item) => !item.ok)
    const createdItems = results.filter((item) => item.ok)
    const sortedItems = (items: BatchResultItem[]) => [...items].sort((left, right) => collator.compare(nameOf(left.userGuid), nameOf(right.userGuid)) || left.dateKey.localeCompare(right.dateKey))
    return (
      <>
        <div className="sbm-result">
          <div className="sbm-result-head">
            {failed ? <ExclamationCircleFilled className="is-warn" /> : <CheckCircleFilled className="is-ok" />}
            <div>
              <h4>
                {failed && created
                  ? t('posAdmin.scheduleAttendance.batch.resultPartial', { ok: created, failed })
                  : failed
                    ? t('posAdmin.scheduleAttendance.batch.resultNone', { failed })
                    : t('posAdmin.scheduleAttendance.batch.resultAllOk', { count: created })}
              </h4>
              <small>{t('posAdmin.scheduleAttendance.batch.resultSub', { skipped: snapshot.skipped.length, excluded: snapshot.excluded.length })}</small>
            </div>
          </div>
          {failedItems.length ? (
            <div className="sbm-group is-fail">
              <div className="sbm-group-head">{t('posAdmin.scheduleAttendance.batch.failedGroup')}<span>{failedItems.length}</span></div>
              {sortedItems(failedItems).map((item) => (
                <div key={`${item.userGuid}|${item.dateKey}`} className="sbm-fail-row">
                  <b>{nameOf(item.userGuid)}</b>
                  <span>{dateText(item.dateKey)}</span>
                  <span className="sbm-why">{item.error}</span>
                  <Button size="small" onClick={() => retry(item)}>{t('posAdmin.scheduleAttendance.batch.retry')}</Button>
                </div>
              ))}
            </div>
          ) : null}
          <details className="sbm-group">
            <summary className="sbm-group-head">{t('posAdmin.scheduleAttendance.batch.createdGroup')}<span>{createdItems.length}</span></summary>
            <div className="sbm-list-lines">
              {sortedItems(createdItems).map((item) => <span key={`${item.userGuid}|${item.dateKey}`}>{nameOf(item.userGuid)} · {dateText(item.dateKey)}</span>)}
            </div>
          </details>
          {snapshot.skipped.length + snapshot.excluded.length ? (
            <details className="sbm-group">
              <summary className="sbm-group-head">{t('posAdmin.scheduleAttendance.batch.skippedGroup')}<span>{snapshot.skipped.length + snapshot.excluded.length}</span></summary>
              <div className="sbm-list-lines">
                {snapshot.skipped.map((cell) => (
                  <span key={batchCellKey(cell)}>{nameOf(cell.userGuid)} · {dateText(cell.dateKey)} · {t('posAdmin.scheduleAttendance.batch.cellExistingTip', { range: cell.existingRange })}</span>
                ))}
                {snapshot.excluded.map((cell) => (
                  <span key={batchCellKey(cell)}>{nameOf(cell.userGuid)} · {dateText(cell.dateKey)} · {cell.state === 'unavailable' ? t('posAdmin.scheduleAttendance.batch.cellUnavailable') : t('posAdmin.scheduleAttendance.batch.cellExcluded')}</span>
                ))}
              </div>
            </details>
          ) : null}
          <div className="sbm-note">{t('posAdmin.scheduleAttendance.batch.resultRefreshed')}</div>
        </div>
        <div className="sbm-foot">
          <span className="sbm-foot-hint">{t('posAdmin.scheduleAttendance.batch.publishHint')}</span>
          <span style={{ flex: 1 }} />
          {failed ? <Button onClick={() => retry()}>{t('posAdmin.scheduleAttendance.batch.retryFailed', { count: failed })}</Button> : null}
          <Button type="primary" onClick={finish}>{t('posAdmin.scheduleAttendance.batch.done')}</Button>
        </div>
      </>
    )
  }

  return (
    <Modal
      open={open}
      centered
      destroyOnHidden
      width={phase === 'edit' ? 1120 : 640}
      className="sbm-modal"
      closable={!running}
      maskClosable={false}
      keyboard={!running}
      footer={null}
      onCancel={phase === 'result' ? finish : onClose}
      title={(
        <span className="sbm-title">
          {phase === 'edit' ? t('posAdmin.scheduleAttendance.batch.title') : phase === 'running' ? t('posAdmin.scheduleAttendance.batch.runningTitle') : t('posAdmin.scheduleAttendance.batch.resultTitle')}
          <small>{weekLabel}{storeName ? ` · ${storeName}` : ''}</small>
        </span>
      )}
    >
      {phase === 'edit' ? renderEdit() : phase === 'running' ? renderRunning() : renderResult()}
    </Modal>
  )
}
