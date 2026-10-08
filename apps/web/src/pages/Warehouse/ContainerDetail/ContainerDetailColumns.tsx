/**
 * ContainerDetailColumns — 货柜明细表格列相关纯函数
 *
 * 职责边界：
 * - 提供表格列渲染所需的纯函数组件（renderNumericCell / renderOemPriceCell / renderImportPriceCell 等）
 * - 提供简单的标题包装器（renderCompactHeader / renderColumnTitle）
 * - 提取分类相关工具函数（renderContainerDetailCategoryCell / buildContainerDetailCategoryOptions / collectCategoryExpandedKeys）
 * - 所有函数均为纯函数或仅依赖外部工具，不依赖组件内部状态
 * - 不持有 columns 定义本身（columns 因深度依赖 filter/sort/editing 状态，仍保留在 index.tsx 中）
 */

import { ArrowDownOutlined, ArrowUpOutlined } from '@ant-design/icons'
import { Space, Tooltip } from 'antd'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { ContainerDetail } from '../../../types/container'
import { formatWarehouseCategoryNodeName, getWarehouseProductCategoryTooltip, type WarehouseCategoryLookup } from '../Products/categoryPath'
import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import {
  CONTAINER_DETAIL_ALL_CATEGORY_FILTER_KEY,
  CONTAINER_DETAIL_UNCATEGORIZED_FILTER_KEY,
  getContainerDetailCategoryTooltipRecord,
  getContainerDetailImportPriceTrend,
  getContainerDetailVisibleOemPrice,
  type ContainerDetailSortField,
} from './containerDetailLogic'
import { resolveContainerDetailCategoryEnglishName } from './containerDetailViewLogic'

// ---- 格式化辅助 ----

function formatNumber(value?: number, digits = 2) {
  return value == null ? '--' : value.toLocaleString('zh-CN', { maximumFractionDigits: digits, minimumFractionDigits: digits })
}

function formatCurrency(value?: number, symbol = '$', digits = 2) {
  const formatted = formatNumber(value, digits)
  return formatted === '--' ? formatted : `${symbol}${formatted}`
}

// ---- 单元格渲染器 ----

/** 数值类单元格统一包装：单行、不换行、右对齐样式。 */
export function renderNumericCell(value: ReactNode) {
  return <span className="container-detail-nowrap container-detail-numeric-cell">{value}</span>
}

/** 零售价只读单元格：新商品显示明细价，已有商品显示仓库实时价。 */
export function renderOemPriceCell(row: ContainerDetail) {
  const className = [
    'container-detail-nowrap',
    'container-detail-numeric-cell',
    'container-detail-oem-price-cell',
  ].filter(Boolean).join(' ')

  return <span className={className}>{formatCurrency(getContainerDetailVisibleOemPrice(row), '$')}</span>
}

/** 进口价格趋势图标（只读指示器，不参与业务逻辑）。 */
export function renderImportPriceTrend(row: ContainerDetail) {
  const trend = getContainerDetailImportPriceTrend(row)
  if (!trend) return null
  const className = trend === 'up' ? 'container-detail-import-price-trend-up' : 'container-detail-import-price-trend-down'
  const Icon = trend === 'up' ? ArrowUpOutlined : ArrowDownOutlined
  return <Icon className={className} />
}

/** 进口价格单元格：价格 + 趋势箭头。可选的 input 参数用于编辑态。 */
export function renderImportPriceCell(row: ContainerDetail, input?: ReactNode) {
  return (
    <Space size={4} wrap={false} className="container-detail-import-price-cell">
      {input ?? renderNumericCell(formatCurrency(row.进口价格, '$'))}
      {renderImportPriceTrend(row)}
    </Space>
  )
}

// ---- 分类相关 ----

/** 递归展开分类树到指定层级，返回所有应展开的 GUID。 */
export function collectCategoryExpandedKeys(nodes: WarehouseCategoryNode[], maxLevel: number, level = 1): string[] {
  return nodes.flatMap((node) => [
    level <= maxLevel ? node.categoryGUID : '',
    ...collectCategoryExpandedKeys(node.children || [], maxLevel, level + 1),
  ]).filter(Boolean)
}

/** 在分类树中按 GUID 查找节点。 */
export function findWarehouseCategory(nodes: WarehouseCategoryNode[], targetGuid?: string): WarehouseCategoryNode | undefined {
  if (!targetGuid) return undefined
  for (const node of nodes) {
    if (node.categoryGUID === targetGuid) return node
    const matched = findWarehouseCategory(node.children || [], targetGuid)
    if (matched) return matched
  }
  return undefined
}

/** 构造分类筛选下拉选项（含"全部分类"和"未分类"）。 */
export function buildContainerDetailCategoryOptions(
  nodes: WarehouseCategoryNode[],
  t: ReturnType<typeof useTranslation>['t'],
  language?: string,
  level = 0,
): Array<{ value: string; label: string }> {
  if (level === 0) {
    return [
      { value: CONTAINER_DETAIL_ALL_CATEGORY_FILTER_KEY, label: t('containers.filters.allCategories', '全部分类') },
      { value: CONTAINER_DETAIL_UNCATEGORIZED_FILTER_KEY, label: t('containers.filters.uncategorized', '未分类') },
      ...buildContainerDetailCategoryOptions(nodes, t, language, level + 1),
    ]
  }

  return nodes.flatMap((node) => [
    {
      value: node.categoryGUID,
      label: `${level > 1 ? `${'--'.repeat(level - 1)} ` : ''}${formatWarehouseCategoryNodeName(node, language)}`,
    },
    ...buildContainerDetailCategoryOptions(node.children || [], t, language, level + 1),
  ])
}

/** 分类列单元格渲染：显示名称 + Tooltip 完整路径；不随界面语言切换，一律显示英文。 */
export function renderContainerDetailCategoryCell(record: ContainerDetail, categoryLookup: WarehouseCategoryLookup) {
  const tooltipRecord = getContainerDetailCategoryTooltipRecord(record)
  const displayName = resolveContainerDetailCategoryEnglishName(tooltipRecord, categoryLookup) || '--'
  const tooltipTitle = getWarehouseProductCategoryTooltip(tooltipRecord, categoryLookup, 'en')

  return (
    <Tooltip title={tooltipTitle || displayName}>
      <span className="container-detail-two-line-text">{displayName}</span>
    </Tooltip>
  )
}

// ---- 标题包装器 ----

/** 紧凑列标题：适用于不需要排序/过滤的列。 */
export function renderCompactHeader(value: ReactNode) {
  return <span className="container-detail-header-title">{value}</span>
}

/** 带 data-column-key 的列标题：适用于可排序列（拖拽排序需要 key）。 */
export function renderColumnTitle(key: ContainerDetailSortField, value: ReactNode) {
  return <span data-column-key={key} className="container-detail-header-title">{value}</span>
}
