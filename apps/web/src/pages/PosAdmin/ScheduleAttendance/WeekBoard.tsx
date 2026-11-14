import { Spin } from 'antd'
import type { CSSProperties, HTMLAttributes, ReactNode } from 'react'

import type { AttendanceScheduleDto } from '../../../types/scheduleAttendance'

import type { StaffColor } from './staffColorLogic'

/** 按天分列的周排班视图（纯展示）：顶部员工图例 + 每天一列。卡片内容、拖放判定由页面传入。 */

export interface WeekBoardDay {
  dateKey: string
  /** 例如 11-23 */
  dateLabel: string
  weekdayLabel: string
  weekend: boolean
  /** 例如「9 人 · 44h」。 */
  statText: string
  statTip: string
  holidays?: ReactNode
  /** 已按「开始时间 → 员工名」排好序。 */
  schedules: AttendanceScheduleDto[]
}

export interface WeekBoardLegendItem {
  userGuid: string
  name: string
  color: StaffColor
  /** 例如「12.5h · 5 班」。 */
  summary: string
  /** 被点选高亮。 */
  active: boolean
  /** 有高亮对象时，其余人变淡。 */
  dimmed: boolean
}

/** 某一天作为拖放落点时的状态（由页面按当前拖拽内容算出）。 */
export interface WeekBoardDayDrop {
  className?: string
  /** onDragOver / onDragLeave / onDrop。 */
  props?: HTMLAttributes<HTMLDivElement>
  /** 列顶的提示角标（能放 / 提醒 / 不能放）。 */
  flag?: ReactNode
  /** 悬停时「放在这里」的占位框。 */
  slot?: ReactNode
}

interface WeekBoardProps {
  days: WeekBoardDay[]
  legend: WeekBoardLegendItem[]
  legendCaption: string
  statusLegend: ReactNode
  loading: boolean
  /** 有值时代替整个看板（例如「本周还没有排班」）。 */
  empty?: ReactNode
  onLegendClick: (userGuid: string) => void
  renderCard: (schedule: AttendanceScheduleDto) => ReactNode
  getDayDrop: (dateKey: string) => WeekBoardDayDrop
  renderDayFooter: (day: WeekBoardDay) => ReactNode
}

const colorVars = (color: StaffColor): CSSProperties => (
  { '--sce-bg': color.bg, '--sce-bar': color.bar, '--sce-ink': color.ink } as CSSProperties
)

export default function WeekBoard({
  days,
  legend,
  legendCaption,
  statusLegend,
  loading,
  empty,
  onLegendClick,
  renderCard,
  getDayDrop,
  renderDayFooter,
}: WeekBoardProps) {
  return (
    <Spin spinning={loading}>
      {empty ?? (
        <div className="sce-board-wrap">
          <div className="sce-staff-legend">
            <span className="sce-staff-legend-caption">{legendCaption}</span>
            {legend.map((item) => (
              <button
                key={item.userGuid}
                type="button"
                aria-pressed={item.active}
                className={`sce-staff-pill${item.active ? ' is-on' : ''}${item.dimmed ? ' is-off' : ''}`}
                style={colorVars(item.color)}
                onClick={() => onLegendClick(item.userGuid)}
              >
                <i aria-hidden />
                <b>{item.name}</b>
                <small>{item.summary}</small>
              </button>
            ))}
            <span className="sce-staff-legend-spacer" />
            {statusLegend}
          </div>
          <div className="sce-board">
            {days.map((day) => {
              const drop = getDayDrop(day.dateKey)
              return (
                <div
                  key={day.dateKey}
                  className={`sce-col${day.weekend ? ' sce-col--weekend' : ''}${drop.className ? ` ${drop.className}` : ''}`}
                  {...drop.props}
                >
                  <div className="sce-col-head">
                    <div className="sce-col-date"><strong>{day.dateLabel}</strong><span>{day.weekdayLabel}</span></div>
                    <div className="sce-col-stat" title={day.statTip}>{day.statText}</div>
                    {day.holidays}
                  </div>
                  <div className="sce-col-body">
                    {drop.flag}
                    {day.schedules.map((schedule) => renderCard(schedule))}
                    {drop.slot}
                    {renderDayFooter(day)}
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}
    </Spin>
  )
}
