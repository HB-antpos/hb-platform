import assert from 'node:assert/strict'
import { PRODUCT_GRADE_CONFIG } from '../../../types/productGrade'
import { GRADE_FILTER_OPTIONS, gradeColor, gradeNameKey, gradeShortKey, gradeTagStyle } from './gradeDisplay'

// 等级显示约定：颜色复用 PRODUCT_GRADE_CONFIG，筛选项固定顺序 A B C D 未分级，文案键字面量齐全。

assert.deepEqual([...GRADE_FILTER_OPTIONS], ['A', 'B', 'C', 'D', 'none'], '筛选项顺序固定：A B C D 未分级')
for (const grade of ['A', 'B', 'C', 'D'] as const) {
  assert.equal(gradeColor(grade), PRODUCT_GRADE_CONFIG[grade].color, `${grade} 的颜色取自 PRODUCT_GRADE_CONFIG`)
  const style = gradeTagStyle(grade)
  assert.equal(style.color, PRODUCT_GRADE_CONFIG[grade].color)
  assert.ok(style.background.startsWith('rgba(') && style.borderColor.startsWith('rgba('), '底色与描边是同一主色的浅化')
}
// A 核心：#722ED1 = rgb(114, 46, 209)
assert.equal(gradeTagStyle('A').background, 'rgba(114, 46, 209, 0.08)')
assert.equal(gradeTagStyle('D').borderColor, 'rgba(245, 34, 45, 0.35)')

assert.equal(gradeNameKey('A'), 'planogram.grade.name.A')
assert.equal(gradeNameKey('none'), 'planogram.grade.name.none')
assert.equal(gradeShortKey('C'), 'planogram.grade.short.C')
assert.equal(gradeShortKey('none'), 'planogram.grade.short.none')

console.log('gradeDisplay.test: ok')
