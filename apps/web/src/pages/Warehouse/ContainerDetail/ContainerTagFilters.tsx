/**
 * ContainerTagFilters — 货柜明细标签筛选区域
 *
 * 职责边界：
 * - 渲染标签统计 Tag 行（全部/新商品/已有商品/商品类型/异常标签/上下架状态）
 * - 渲染多选 Select 下拉框
 * - 渲染已选标签展示区（可逐个关闭 + 一键清除）
 * - 纯展示组件，所有状态和回调由父组件通过 props 注入
 * - 不接管任何数据加载或标签统计计算逻辑
 */

import { Button, Space, Tag, Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import type { ContainerDetailTagFilter, ContainerDetailTagStats } from './containerDetailLogic'

export interface ContainerTagFiltersProps {
  /** 所有标签选项（含 label/color/统计数） */
  tagStatOptions: Array<{ value: ContainerDetailTagFilter; label: string; color?: string }>
  /** 标签统计数据；null 表示尚未加载或加载失败，数量显示为 -- */
  tagStats: ContainerDetailTagStats | null
  /** 当前选中的标签筛选值 */
  selectedTagFilters: ContainerDetailTagFilter[]
  /** 当前选中的标签选项（用于展示可关闭 Tag） */
  selectedTagOptions: Array<{ value: ContainerDetailTagFilter; label: string; color?: string }>
  /** 切换单个标签筛选 */
  onToggleTagFilter: (value: ContainerDetailTagFilter) => void
  /** 设置标签筛选（用于清除全部） */
  onSetTagFilters: (values: ContainerDetailTagFilter[]) => void
}

/**
 * 货柜明细标签筛选区域
 *
 * 包含两个子区域：
 * 1. 标签统计 Tag 行 — 点击可切换筛选
 * 2. 已选标签展示区 — 显示当前激活的筛选条件
 *
 * 注意：Select 下拉框位于父组件的顶部工具栏中，通过 tagSelectOptions / onSetTagFilters 集成。
 */
export default function ContainerTagFilters({
  tagStatOptions,
  tagStats,
  selectedTagFilters,
  selectedTagOptions,
  onToggleTagFilter,
  onSetTagFilters,
}: ContainerTagFiltersProps) {
  const { t } = useTranslation()

  return (
    <>
      {/* 标签统计 Tag 行 */}
      <Space className="container-detail-stats" wrap size={[8, 8]}>
        {tagStatOptions.map((option) => {
          const active = option.value === 'all' ? !selectedTagFilters.length : selectedTagFilters.includes(option.value)
          return (
            <Tag
              key={option.value}
              className={`container-detail-stat-tag ${active ? 'container-detail-stat-tag-active' : 'container-detail-stat-tag-muted'}`}
              color={option.color}
              role="button"
              tabIndex={0}
              aria-pressed={active}
              onClick={() => onToggleTagFilter(option.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault()
                  onToggleTagFilter(option.value)
                }
              }}
            >
              <span>{option.label}</span>
              <Typography.Text strong className="container-detail-stat-count">
                {tagStats ? tagStats[option.value] : '--'}
              </Typography.Text>
            </Tag>
          )
        })}
      </Space>

      {/* 已选标签展示区 */}
      {selectedTagOptions.length ? (
        <Space className="container-detail-selected-filters" wrap size={[6, 6]}>
          <Typography.Text type="secondary">{t('containers.text.selectedFilters')}</Typography.Text>
          {selectedTagOptions.map((option) => (
            <Tag
              key={option.value}
              color={option.color}
              closable
              onClose={(event) => {
                event.preventDefault()
                onToggleTagFilter(option.value)
              }}
            >
              {option.label}
            </Tag>
          ))}
          <Button type="link" size="small" onClick={() => onSetTagFilters([])}>
            {t('containers.actions.clearFilters')}
          </Button>
        </Space>
      ) : null}
    </>
  )
}
