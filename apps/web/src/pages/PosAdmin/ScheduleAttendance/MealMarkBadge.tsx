// 看板卡片 / 图例上的「午饭」小标识：叉勺图标，长班次无午饭时叠一道斜杠；自动含 2 次时旁边写 ×2。
import { MEAL_ICON_PATH, MEAL_SLASH_PATH } from './mealMarkLogic'
import type { MealMark } from './mealMarkLogic'

export function MealIcon({ slashed = false }: { slashed?: boolean }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
      <path d={MEAL_ICON_PATH} />
      {slashed ? <path d={MEAL_SLASH_PATH} stroke="#ad4e00" strokeWidth="2.6" /> : null}
    </svg>
  )
}

/** 卡片第一行右侧的标识；hidden 不渲染。label 给读屏用（「含午饭 1 次」「无午饭（手动不扣）」）。 */
export function MealMarkBadge({ mark, label }: { mark: MealMark; label: string }) {
  if (mark.kind === 'hidden') return null
  const classes = ['sce-card-meal', mark.manual ? 'sce-card-meal--manual' : '', mark.kind === 'none' ? 'sce-card-meal--none' : ''].filter(Boolean).join(' ')
  return (
    <span className={classes} role="img" aria-label={label}>
      <MealIcon slashed={mark.kind === 'none'} />
      {mark.count > 1 ? <b>×{mark.count}</b> : null}
    </span>
  )
}
