import { Button, Checkbox } from 'antd'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import type { MyobPreviewHint } from '../../../types/myob'

import { formatHoursCell, groupHints, splitHintMessage, type ReadinessCard } from './myobTimesheetLogic'

// 推送预览页与考勤工资页的推送弹窗共用的小部件：就绪卡、工时单元格、6 个口径的工时列。

// 就绪卡状态的读屏文字（键写成字面量，供页面文案契约测试扫描）
const READINESS_STATE_KEYS: Record<ReadinessCard['tone'], string> = {
  ok: 'myob.push.readiness.state.ok',
  bad: 'myob.push.readiness.state.bad',
  warn: 'myob.push.readiness.state.warn',
  muted: 'myob.push.readiness.state.muted',
}

// 6 个口径的工时列（键写成字面量，供页面文案契约测试扫描）
export const HOUR_COLUMNS = [
  { key: 'weekday', titleKey: 'myob.push.columns.weekday' },
  { key: 'saturday', titleKey: 'myob.push.columns.saturday' },
  { key: 'sunday', titleKey: 'myob.push.columns.sunday' },
  { key: 'publicHoliday', titleKey: 'myob.push.columns.publicHoliday' },
  { key: 'personalLeave', titleKey: 'myob.push.columns.personalLeave' },
  { key: 'holidayLeave', titleKey: 'myob.push.columns.holidayLeave' },
] as const

export function ReadinessItem({ card, canGoMapping, onGoMapping }: { card: ReadinessCard; canGoMapping: boolean; onGoMapping: () => void }) {
  const { t } = useTranslation()
  return (
    <li className={`myob-step myob-step-${card.tone}`} data-step={card.id}>
      <span className="myob-step-symbol" aria-hidden="true">
        {card.symbol}
      </span>
      <div className="myob-step-body">
        {/* 符号对读屏不可见，用隐藏文字把状态读出来 */}
        <span className="myob-sr-only">{t(READINESS_STATE_KEYS[card.tone])}</span>
        <b>{t(card.titleKey, card.titleValues)}</b>
        {card.subKey ? <span className="myob-step-sub">{t(card.subKey, card.subValues)}</span> : null}
        {card.subText ? <span className="myob-step-sub">{card.subText}</span> : null}
        {card.subItems?.map((item) => (
          <span className="myob-step-sub" key={item.label}>
            {t('myob.push.readiness.unlocked.item', { label: item.label, count: item.count })}
          </span>
        ))}
        {card.action === 'goMapping' && canGoMapping ? (
          <Button type="link" size="small" className="myob-step-action" onClick={onGoMapping}>
            {t('myob.push.readiness.category.go')}
          </Button>
        ) : null}
      </div>
    </li>
  )
}


export function HoursCell({ value }: { value: number }) {
  const zero = Math.abs(value) < 0.005
  return <span className={`myob-push-hours${zero ? ' myob-muted' : ''}`}>{formatHoursCell(value)}</span>
}

/** 提示项（有人本期有年假折现、生日满龄等）：按类型分组列出，推送前要勾选「我已知晓」。 */
export function PushHints({ hints, acknowledged, onAcknowledgedChange }: { hints: MyobPreviewHint[]; acknowledged: boolean; onAcknowledgedChange: (value: boolean) => void }) {
  const { t } = useTranslation()
  const hintGroups = useMemo(() => groupHints(hints), [hints])
  if (hints.length === 0) {
    return null
  }
  return (
    <div className="myob-push-hints" role="note">
      <b>{t('myob.push.hints.title', { count: hints.length })}</b>
      {hintGroups.map((group) => (
        <div key={group.code} className="myob-push-hint-group">
          <div className="myob-push-hint-group-title">{t(group.labelKey)}</div>
          <ul>
            {group.items.map((hint, index) => {
              const parts = splitHintMessage(hint)
              return (
                <li key={`${hint.userGuid}-${hint.code}-${index}`}>
                  {parts.before}
                  {parts.name ? <b>{parts.name}</b> : null}
                  {parts.separated ? t('myob.push.hints.nameSep') : null}
                  {parts.after}
                </li>
              )
            })}
          </ul>
        </div>
      ))}
      <Checkbox checked={acknowledged} onChange={(event) => onAcknowledgedChange(event.target.checked)}>
        {t('myob.push.hints.ack')}
      </Checkbox>
    </div>
  )
}
