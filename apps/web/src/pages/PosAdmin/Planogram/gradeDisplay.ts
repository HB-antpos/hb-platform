/**
 * 商品等级的显示约定（纯逻辑）：等级定义与颜色复用 types/productGrade.ts 的 PRODUCT_GRADE_CONFIG，
 * 这里只补「陈列图页面里怎么叫它 / 怎么上色」：A 核心 / B 关注 / C 停产 / D 清仓 / 未分级。
 */
import { PRODUCT_GRADE_CONFIG, type ProductGradeKey } from '../../../types/productGrade'
import { PLANOGRAM_GRADE_VALUES, type PlanogramGradeLetter, type PlanogramProductGrade } from '../../../types/planogram'

/** 等级过滤项的固定顺序：A B C D 未分级。 */
export const GRADE_FILTER_OPTIONS: readonly PlanogramProductGrade[] = PLANOGRAM_GRADE_VALUES

// 文案键必须以带引号的字面量出现：契约测试按这个形式静态扫描，才能发现缺失键。
/** 完整名：「A 核心」。 */
const GRADE_NAME_KEYS: Record<PlanogramProductGrade, string> = {
  A: 'planogram.grade.name.A',
  B: 'planogram.grade.name.B',
  C: 'planogram.grade.name.C',
  D: 'planogram.grade.name.D',
  none: 'planogram.grade.name.none',
}
/** 简称：「A」「未分级」（表格小标签、条件标签用）。 */
const GRADE_SHORT_KEYS: Record<PlanogramProductGrade, string> = {
  A: 'planogram.grade.short.A',
  B: 'planogram.grade.short.B',
  C: 'planogram.grade.short.C',
  D: 'planogram.grade.short.D',
  none: 'planogram.grade.short.none',
}

export function gradeNameKey(grade: PlanogramProductGrade): string {
  return GRADE_NAME_KEYS[grade]
}

export function gradeShortKey(grade: PlanogramProductGrade): string {
  return GRADE_SHORT_KEYS[grade]
}

/** 等级主色（取自 PRODUCT_GRADE_CONFIG）；「未分级」没有颜色。 */
export function gradeColor(grade: PlanogramGradeLetter): string {
  return PRODUCT_GRADE_CONFIG[grade as ProductGradeKey].color
}

function hexToRgba(hex: string, alpha: number): string {
  const match = /^#([0-9a-f]{6})$/i.exec(hex.trim())
  if (!match) return hex
  const value = parseInt(match[1], 16)
  return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`
}

export interface GradeTagStyle {
  color: string
  background: string
  borderColor: string
}

/** 小标签的配色：文字用等级主色，底色与描边是同一主色的浅化，不另造色板。 */
export function gradeTagStyle(grade: PlanogramGradeLetter): GradeTagStyle {
  const color = gradeColor(grade)
  return { color, background: hexToRgba(color, 0.08), borderColor: hexToRgba(color, 0.35) }
}
