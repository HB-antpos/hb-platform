import { useTranslation } from 'react-i18next'
import type { PlanogramGradeLetter } from '../../../types/planogram'
import { gradeShortKey, gradeTagStyle } from './gradeDisplay'

/**
 * 等级小标签（列表「等级」列、设计器商品卡片）：A～D 用等级主色的浅底 + 描边，未分级（null）是灰色的「未分级」。
 * 除颜色外一定带字母 / 文字，不只靠颜色区分。
 */
export default function GradeTag({ grade, className }: { grade: PlanogramGradeLetter | null | undefined; className?: string }) {
  const { t } = useTranslation()
  if (!grade) return <span className={`planogram-grade-tag is-none ${className ?? ''}`}>{t(gradeShortKey('none'))}</span>
  return (
    <span className={`planogram-grade-tag ${className ?? ''}`} style={gradeTagStyle(grade)} title={t(gradeShortKey(grade))}>
      {grade}
    </span>
  )
}
