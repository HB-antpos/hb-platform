import assert from 'node:assert/strict'
import i18next from 'i18next'
import { renderToStaticMarkup } from 'react-dom/server'
import { initReactI18next } from 'react-i18next'
import { PRODUCT_GRADE_CONFIG } from '../../../../types/productGrade'
import GradeTag from '../GradeTag'
import en from '../planogramMessages.en.json'
import zh from '../planogramMessages.zh.json'
import { createEmptyFilterState, withGrades } from './categoryFilterLogic'
import { GradeChips, SupplierCategorySelect, WarehouseCategorySelect } from './CategoryFilterControls'
import type { CategoryFilterApi } from './useCategoryFilter'

// 过滤控件的静态渲染契约：等级标签的按压状态 / 颜色 / 文案、仓库分类与供应商分类控件的前缀标签与占位。
// （下拉的交互——打开、选择、懒加载——由 useCategoryFilter 的 hook 测试与浏览器目测覆盖。）

await i18next.use(initReactI18next).init({ lng: 'zh', resources: { zh: { translation: zh }, en: { translation: en } } })

function fakeFilter(grades: string[] = []): CategoryFilterApi {
  const state = withGrades(createEmptyFilterState(), grades)
  return {
    state,
    scope: {},
    scopeKey: '',
    activeCount: 0,
    hasActive: false,
    setWarehouseCategories: () => {},
    setSupplierPath: () => {},
    setGrades: () => {},
    clear: () => {},
    chips: [],
    removeGroup: () => {},
    ensureLoaded: () => {},
    warehouse: { status: 'idle', treeData: [], reload: () => {} },
    suppliers: { status: 'idle', options: [], reload: () => {} },
    cascader: { options: [], value: undefined, loadSupplier: () => {}, retrySupplier: () => {} },
  }
}

// ---------- 等级标签 ----------
const none = renderToStaticMarkup(<GradeChips filter={fakeFilter()} />)
assert.equal(none.match(/aria-pressed="false"/g)?.length, 5, '没选等级：五个标签都是未按下')
assert.ok(none.includes('A 核心') && none.includes('B 关注') && none.includes('C 停产') && none.includes('D 清仓') && none.includes('未分级'), '完整名：A 核心 / B 关注 / C 停产 / D 清仓 / 未分级')
for (const grade of ['A', 'B', 'C', 'D'] as const) {
  assert.ok(none.includes(`background:${PRODUCT_GRADE_CONFIG[grade].color}`), `${grade} 的圆点颜色取自 PRODUCT_GRADE_CONFIG`)
}
assert.equal(none.match(/planogram-grade-dot/g)?.length, 4, '未分级没有圆点')

const picked = renderToStaticMarkup(<GradeChips filter={fakeFilter(['B', 'none'])} />)
assert.equal(picked.match(/aria-pressed="true"/g)?.length, 2, '选了 B 和未分级：两个按下')
assert.equal(picked.match(/is-on/g)?.length, 2)
assert.ok(picked.includes('role="group"') && picked.includes('aria-label="等级"'), '分组语义 + 读屏标签')

const small = renderToStaticMarkup(<GradeChips filter={fakeFilter()} size="sm" />)
assert.ok(small.includes('is-sm') && !small.includes('A 核心') && small.includes('>A<'), '窄栏弹层用简称（A / B / C / D / 未分级）')

// ---------- 等级小标签（列表「等级」列 / 设计器卡片） ----------
const tagA = renderToStaticMarkup(<GradeTag grade="A" />)
assert.ok(tagA.includes('>A<') && tagA.includes(`color:${PRODUCT_GRADE_CONFIG.A.color}`), 'A 级标签：字母 + 主色')
const tagNone = renderToStaticMarkup(<GradeTag grade={null} />)
assert.ok(tagNone.includes('is-none') && tagNone.includes('未分级'), '未分级是灰色文字标签，不只靠颜色')

// ---------- 仓库分类 / 供应商分类控件 ----------
const warehouse = renderToStaticMarkup(<WarehouseCategorySelect filter={fakeFilter()} />)
assert.ok(warehouse.includes('仓库分类') && warehouse.includes('全部'), '筛选条里的仓库分类：带前缀标签与「全部」占位')
assert.ok(warehouse.includes('ant-tree-select') && warehouse.includes('ant-select-multiple'), 'TreeSelect 多选')
const supplier = renderToStaticMarkup(<SupplierCategorySelect filter={fakeFilter()} />)
assert.ok(supplier.includes('本地供应商分类') && supplier.includes('ant-cascader') && supplier.includes('is-supplier'), '筛选条里的本地供应商分类：级联')
const popoverWarehouse = renderToStaticMarkup(<WarehouseCategorySelect filter={fakeFilter()} variant="popover" />)
assert.ok(!popoverWarehouse.includes('planogram-filter-label'), '弹层里标签画在控件上方，控件本身没有前缀')

await i18next.changeLanguage('en')
const enChips = renderToStaticMarkup(<GradeChips filter={fakeFilter(['A'])} />)
assert.ok(enChips.includes('A Core') && enChips.includes('No grade') && enChips.includes('aria-label="Grade"'), '英文界面')

console.log('CategoryFilterControls.test: ok')
