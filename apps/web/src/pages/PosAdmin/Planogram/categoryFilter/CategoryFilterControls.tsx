import { Spin, TreeSelect } from 'antd'
import { useMemo, type CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'
import SupplierCategoryCascader from '../../ProductManagement/SupplierCategoryCascader'
import { GRADE_FILTER_OPTIONS, gradeColor, gradeNameKey, gradeShortKey } from '../gradeDisplay'
import type { WarehouseTreeNode } from './categoryFilterLogic'
import type { CategoryFilterApi, FilterLoadStatus } from './useCategoryFilter'

/**
 * 商品范围过滤的三个控件（陈列参数页与设计器商品库共用）：
 * - 仓库分类：TreeSelect 多选，选父类等于选了全部子类（服务端展开），可搜索；
 * - 本地供应商分类：供应商 → 分类级联（隐藏供应商 200，含「未归类」，加载失败可重试），复用商品管理页的级联组件；
 * - 商品等级：A 核心 / B 关注 / C 停产 / D 清仓 / 未分级 的多选标签，颜色取自 PRODUCT_GRADE_CONFIG。
 * 分类数据只在用户第一次打开下拉时加载（filter.ensureLoaded），加载失败有提示且不影响列表。
 */

interface ControlProps {
  filter: CategoryFilterApi
  /** bar = 筛选条里的带标签输入框；popover = 弹层里（标签在上方，由外层画）。 */
  variant?: 'bar' | 'popover'
  className?: string
  style?: CSSProperties
}

/** 数据没就绪时下拉里显示的内容：加载中转圈、失败给「点击重试」。 */
function LoadHint({ status, onRetry, emptyText }: { status: FilterLoadStatus; onRetry: () => void; emptyText?: string }) {
  const { t } = useTranslation()
  if (status === 'loading' || status === 'idle') {
    return <div className="planogram-filter-hint"><Spin size="small" /> {t('planogram.categoryFilter.loading')}</div>
  }
  if (status === 'error') {
    return (
      <div className="planogram-filter-hint is-error" role="alert">
        <a role="button" tabIndex={0} onClick={onRetry} onKeyDown={(event) => { if (event.key === 'Enter') onRetry() }}>
          {t('planogram.categoryFilter.loadFailedRetry')}
        </a>
      </div>
    )
  }
  return <div className="planogram-filter-hint">{emptyText}</div>
}

type TreeSelectNode = WarehouseTreeNode & { tagLabel: string; children?: TreeSelectNode[] }

/** 给每个节点补「已选标签」：完整路径（区分重名分类），有子类的再注明「含子类」。 */
function withTagLabels(nodes: readonly WarehouseTreeNode[], suffix: string): TreeSelectNode[] {
  return nodes.map((node) => ({
    ...node,
    tagLabel: node.hasChildren ? `${node.pathLabel}${suffix}` : node.pathLabel,
    children: node.children ? withTagLabels(node.children, suffix) : undefined,
  }))
}

export function WarehouseCategorySelect({ filter, variant = 'bar', className, style }: ControlProps) {
  const { t } = useTranslation()
  const { treeData, status, reload } = filter.warehouse
  const data = useMemo(() => withTagLabels(treeData, t('planogram.categoryFilter.withChildren')), [t, treeData])
  const label = t('planogram.categoryFilter.warehouse')
  return (
    <TreeSelect
      multiple
      showSearch
      allowClear
      className={`planogram-filter-select ${className ?? ''}`}
      style={style}
      size={variant === 'popover' ? 'small' : undefined}
      prefix={variant === 'bar' ? <span className="planogram-filter-label">{label}</span> : undefined}
      placeholder={t('planogram.categoryFilter.all')}
      aria-label={label}
      treeData={data}
      treeNodeFilterProp="title"
      treeNodeLabelProp="tagLabel"
      maxTagCount="responsive"
      value={filter.state.warehouseCategoryGuids}
      onChange={(values: string[]) => filter.setWarehouseCategories(values)}
      onOpenChange={(open) => { if (open) filter.ensureLoaded() }}
      loading={status === 'loading'}
      status={status === 'error' ? 'error' : undefined}
      popupMatchSelectWidth={false}
      styles={{ popup: { root: { minWidth: 300, maxHeight: 360, overflow: 'auto' } } }}
      // 选父类等于选了它下面的全部子类（服务端展开），在下拉顶部说明一句
      popupRender={(menu) => (
        <div>
          <div className="planogram-filter-pop-hint">{t('planogram.categoryFilter.warehouseHint')}</div>
          {menu}
        </div>
      )}
      notFoundContent={<LoadHint status={status} onRetry={reload} emptyText={t('planogram.categoryFilter.emptyTree')} />}
    />
  )
}

export function SupplierCategorySelect({ filter, variant = 'bar', className, style }: ControlProps) {
  const { t } = useTranslation()
  const status = filter.suppliers.status
  return (
    <SupplierCategoryCascader
      className={`planogram-filter-select${variant === 'bar' ? ' is-supplier' : ''} ${className ?? ''}`}
      style={style}
      size={variant === 'popover' ? 'small' : undefined}
      prefix={variant === 'bar' ? <span className="planogram-filter-label">{t('planogram.categoryFilter.supplier')}</span> : undefined}
      placeholder={t('planogram.categoryFilter.all')}
      options={filter.cascader.options}
      value={filter.cascader.value}
      onChange={filter.setSupplierPath}
      onOpen={filter.ensureLoaded}
      onLoadSupplier={filter.cascader.loadSupplier}
      onRetrySupplier={filter.cascader.retrySupplier}
      searchPlaceholder={t('planogram.categoryFilter.supplierSearch')}
      searchEmptyText={t('planogram.categoryFilter.supplierSearchEmpty')}
      notFoundContent={<LoadHint status={status} onRetry={filter.suppliers.reload} emptyText={t('planogram.categoryFilter.noSuppliers')} />}
    />
  )
}

/** 等级多选标签：圆点是等级主色（未分级没有圆点）；md 写完整名「A 核心」，sm（窄栏弹层）只写简称「A」；按钮语义 + aria-pressed，键盘可操作。 */
export function GradeChips({ filter, size = 'md' }: { filter: CategoryFilterApi; size?: 'md' | 'sm' }) {
  const { t } = useTranslation()
  const selected = new Set<string>(filter.state.grades)
  return (
    <span className={`planogram-grade-chips is-${size}`} role="group" aria-label={t('planogram.grade.label')}>
      {GRADE_FILTER_OPTIONS.map((grade) => {
        const on = selected.has(grade)
        const next = on ? filter.state.grades.filter((item) => item !== grade) : [...filter.state.grades, grade]
        return (
          <button
            key={grade}
            type="button"
            className={`planogram-grade-chip${on ? ' is-on' : ''}`}
            aria-pressed={on}
            onClick={() => filter.setGrades(next)}
          >
            {grade === 'none' ? null : <span className="planogram-grade-dot" style={{ background: gradeColor(grade) }} aria-hidden="true" />}
            {t(size === 'sm' ? gradeShortKey(grade) : gradeNameKey(grade))}
          </button>
        )
      })}
    </span>
  )
}
