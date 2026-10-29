import { Tag } from 'antd'
import type { CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'

import type { MyobMatchBasis } from '../../../types/myob'
import {
  BASIS_FIELDS,
  getBasisResult,
  getEmployeeTypeKey,
  getInitials,
  getScoreTone,
  getSignalBadge,
} from './myobEmployeeLogic'

/** 匹配依据四个徽章：✓ 一致（绿）/ ✕ 不一致（红）/ – 无法比对（灰）；符号 + 屏幕阅读器文字，不只靠颜色。 */
export function BasisBadges({ basis }: { basis: MyobMatchBasis }) {
  const { t } = useTranslation()
  return (
    <div className="myob-basis">
      {BASIS_FIELDS.map(({ field, labelKey }) => {
        const badge = getSignalBadge(getBasisResult(basis, field))
        return (
          <span key={field} className={`myob-signal myob-signal-${badge.tone}`}>
            {t(labelKey)}
            <span aria-hidden="true">{badge.symbol}</span>
            <span className="sr-only" style={SR_ONLY}>
              {t(badge.stateKey)}
            </span>
          </span>
        )
      })}
    </div>
  )
}

// 仅屏幕阅读器可见的文字（项目里没有通用 sr-only 类，内联即可）
const SR_ONLY: CSSProperties = {
  position: 'absolute',
  width: 1,
  height: 1,
  margin: -1,
  padding: 0,
  overflow: 'hidden',
  clip: 'rect(0, 0, 0, 0)',
  whiteSpace: 'nowrap',
  border: 0,
}

/** 得分徽章：≥85 绿、≥40 琥珀、其余灰。 */
export function ScoreBadge({ score }: { score: number }) {
  const { t } = useTranslation()
  return (
    <span className={`myob-score myob-score-${getScoreTone(score)}`} aria-label={t('myob.employees.scoreAria', { score })}>
      {score}
    </span>
  )
}

/** 用工类型标签：已知类型显示中文，未知取值原样显示，没填不显示。 */
export function EmployeeTypeTag({ type }: { type: string | null | undefined }) {
  const { t } = useTranslation()
  if (!type) {
    return null
  }
  const key = getEmployeeTypeKey(type)
  return <Tag>{key ? t(key) : type}</Tag>
}

export function Avatar({ name }: { name: string }) {
  return (
    <span className="myob-avatar" aria-hidden="true">
      {getInitials(name)}
    </span>
  )
}
