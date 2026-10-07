/**
 * ContainerTagFilters — 货柜明细工具栏里的紧凑标签筛选
 *
 * 职责边界：
 * - 渲染三组紧凑控件：商品类型（多选下拉）、新商品/已有商品（分段）、上架/下架（分段），每项带统计数
 * - 「缺零售价 / 进口价缺失」两个价格标签放在页面的「提交前检查」条，不在这里重复
 * - 组内并集、组间交集的筛选口径不变（由 matchesContainerDetailSelectedTags 与后端统计负责），这里只替换组内选择
 * - 纯展示组件，所有状态和回调由父组件注入，不接管数据加载或统计计算
 */

import { Select } from 'antd'
import { useTranslation } from 'react-i18next'
import type { ContainerDetailTagFilter, ContainerDetailTagStats } from './containerDetailLogic'
import {
  CONTAINER_DETAIL_NEW_STATE_TAGS,
  CONTAINER_DETAIL_PRODUCT_TYPE_TAGS,
  CONTAINER_DETAIL_WAREHOUSE_STATUS_TAGS,
  getContainerDetailTagGroupSelection,
  replaceContainerDetailTagGroup,
  resolveContainerDetailTagSegmentValue,
  type ContainerDetailTagGroup,
} from './containerDetailViewLogic'

export interface ContainerTagFiltersProps {
  /** 标签统计数据；null 表示尚未加载或加载失败，数量显示为 -- */
  tagStats: ContainerDetailTagStats | null
  /** 当前选中的标签筛选值 */
  selectedTagFilters: ContainerDetailTagFilter[]
  /** 整体设置标签筛选（各组控件只替换自己那一组） */
  onSetTagFilters: (values: ContainerDetailTagFilter[]) => void
}

interface SegmentOption {
  value: ContainerDetailTagFilter
  label: string
}

interface TagSegmentProps {
  ariaLabel: string
  group: ContainerDetailTagGroup
  options: SegmentOption[]
  tagStats: ContainerDetailTagStats | null
  selectedTagFilters: ContainerDetailTagFilter[]
  onSetTagFilters: (values: ContainerDetailTagFilter[]) => void
}

/** 单选分段：选「全部」即清空本组；组内两个都选与不选等价，所以单选不会丢失任何筛选能力。 */
function TagSegment({ ariaLabel, group, options, tagStats, selectedTagFilters, onSetTagFilters }: TagSegmentProps) {
  const current = resolveContainerDetailTagSegmentValue(selectedTagFilters, group)

  return (
    <div className="wh-cdetail-seg" role="group" aria-label={ariaLabel}>
      {options.map((option) => {
        const active = option.value === current
        return (
          <button
            key={option.value}
            type="button"
            aria-pressed={active}
            className={`wh-cdetail-seg-item${active ? ' wh-cdetail-seg-item-active' : ''}`}
            onClick={() => {
              if (active) return
              onSetTagFilters(replaceContainerDetailTagGroup(
                selectedTagFilters,
                group,
                option.value === 'all' ? [] : [option.value],
              ))
            }}
          >
            <span>{option.label}</span>
            <span className="wh-cdetail-seg-count">{tagStats ? tagStats[option.value] : '--'}</span>
          </button>
        )
      })}
    </div>
  )
}

export default function ContainerTagFilters({
  tagStats,
  selectedTagFilters,
  onSetTagFilters,
}: ContainerTagFiltersProps) {
  const { t } = useTranslation()
  const productTypeLabels: Record<(typeof CONTAINER_DETAIL_PRODUCT_TYPE_TAGS)[number], string> = {
    normal: t('containers.productTypes.normal'),
    set: t('containers.productTypes.set'),
    multi: t('containers.productTypes.multiCode'),
    setChild: t('containers.productTypes.setChild'),
  }

  return (
    <>
      <Select<ContainerDetailTagFilter[]>
        mode="multiple"
        size="middle"
        className="wh-cdetail-type-filter"
        prefix={<span className="wh-cdetail-type-filter-prefix">{t('containers.fields.productType')}</span>}
        aria-label={t('warehouseUi.containerDetail.typeFilterAria')}
        placeholder={t('common.all')}
        allowClear
        maxTagCount="responsive"
        popupMatchSelectWidth={false}
        value={getContainerDetailTagGroupSelection(selectedTagFilters, CONTAINER_DETAIL_PRODUCT_TYPE_TAGS)}
        options={CONTAINER_DETAIL_PRODUCT_TYPE_TAGS.map((value) => ({ value, label: productTypeLabels[value] }))}
        optionRender={(option) => (
          <span className="wh-cdetail-type-option">
            <span>{option.label}</span>
            <span className="wh-cdetail-seg-count">
              {tagStats ? tagStats[option.value as ContainerDetailTagFilter] : '--'}
            </span>
          </span>
        )}
        onChange={(values) => onSetTagFilters(replaceContainerDetailTagGroup(selectedTagFilters, CONTAINER_DETAIL_PRODUCT_TYPE_TAGS, values))}
      />
      <TagSegment
        ariaLabel={t('warehouseUi.containerDetail.newStateFilterAria')}
        group={CONTAINER_DETAIL_NEW_STATE_TAGS}
        options={[
          { value: 'all', label: t('common.all') },
          { value: 'new', label: t('containers.tags.newProduct') },
          { value: 'existing', label: t('containers.tags.existing') },
        ]}
        tagStats={tagStats}
        selectedTagFilters={selectedTagFilters}
        onSetTagFilters={onSetTagFilters}
      />
      <TagSegment
        ariaLabel={t('warehouseUi.containerDetail.warehouseStatusFilterAria')}
        group={CONTAINER_DETAIL_WAREHOUSE_STATUS_TAGS}
        options={[
          { value: 'all', label: t('warehouseUi.containerDetail.warehouseStatusAll') },
          { value: 'active', label: t('common.activeUpper') },
          { value: 'inactive', label: t('common.inactiveUpper') },
        ]}
        tagStats={tagStats}
        selectedTagFilters={selectedTagFilters}
        onSetTagFilters={onSetTagFilters}
      />
    </>
  )
}
