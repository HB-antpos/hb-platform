import { SearchOutlined } from '@ant-design/icons'
import { Empty, Input, Space, Tag, Tree, Typography } from 'antd'
import type { DataNode } from 'antd/es/tree'
import type { Key } from 'react'
import { useMemo, useState } from 'react'
import type { TFunction } from 'i18next'
import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import { formatWarehouseCategoryNodeName } from './categoryPath'

interface CategoryTreePickerProps {
  categories: WarehouseCategoryNode[]
  selectedKey?: string
  expandedKeys: string[]
  onExpand: (keys: string[]) => void
  onSelect: (key?: string) => void
  language?: string
  t: TFunction
  maxHeight?: number
}

interface FilterResult {
  nodes: WarehouseCategoryNode[]
  expandedKeys: string[]
}

export function normalizeSearchText(value: string) {
  return value.trim().toLowerCase()
}

function isNonEmptyText(value: string | undefined): value is string {
  return Boolean(value)
}

function buildSearchText(
  node: WarehouseCategoryNode,
  language: string | undefined,
  parentPath: string[],
) {
  const displayName = formatWarehouseCategoryNodeName(node, language)
  const rawNames = [node.categoryName, node.chineseName, displayName].filter(isNonEmptyText)
  const fullPath = [...parentPath, ...rawNames].join(' ')
  return fullPath.toLowerCase()
}

/** 按名称与父级路径搜索分类树；仓库商品页左侧分类面板也复用它，搜索语义保持一致。 */
export function filterCategoryTree(
  nodes: WarehouseCategoryNode[],
  keyword: string,
  language: string | undefined,
  parentPath: string[] = [],
): FilterResult {
  const filteredNodes: WarehouseCategoryNode[] = []
  const expandedKeys: string[] = []

  for (const node of nodes) {
    const displayName = formatWarehouseCategoryNodeName(node, language)
    const nextPath = [...parentPath, node.categoryName, node.chineseName, displayName].filter(isNonEmptyText)
    const childResult = filterCategoryTree(node.children || [], keyword, language, nextPath)
    const selfMatched = buildSearchText(node, language, parentPath).includes(keyword)

    if (selfMatched || childResult.nodes.length) {
      filteredNodes.push({
        ...node,
        children: childResult.nodes,
      })
      // 搜索时展开命中路径，保证匹配项和父级关系都直接可见。
      if (node.children?.length || childResult.nodes.length) {
        expandedKeys.push(node.categoryGUID)
      }
      expandedKeys.push(...childResult.expandedKeys)
    }
  }

  return { nodes: filteredNodes, expandedKeys }
}

function buildCategoryTreeData(
  nodes: WarehouseCategoryNode[],
  t: TFunction,
  language?: string,
): DataNode[] {
  return nodes.map((node) => ({
    key: node.categoryGUID,
    title: (
      <Space size={6}>
        <Typography.Text>{formatWarehouseCategoryNodeName(node, language)}</Typography.Text>
        {node.isActive ? <Tag color="success">{t('common.active')}</Tag> : <Tag>{t('common.inactive')}</Tag>}
      </Space>
    ),
    children: buildCategoryTreeData(node.children || [], t, language),
  }))
}

export default function CategoryTreePicker({
  categories,
  selectedKey,
  expandedKeys,
  onExpand,
  onSelect,
  language,
  t,
  maxHeight = 360,
}: CategoryTreePickerProps) {
  const [searchText, setSearchText] = useState('')
  const keyword = normalizeSearchText(searchText)
  const searchResult = useMemo(
    () => keyword ? filterCategoryTree(categories, keyword, language) : { nodes: categories, expandedKeys: [] },
    [categories, keyword, language],
  )
  const treeData = useMemo(() => buildCategoryTreeData(searchResult.nodes, t, language), [language, searchResult.nodes, t])
  const visibleExpandedKeys = keyword ? searchResult.expandedKeys : expandedKeys

  return (
    <Space direction="vertical" size={8} style={{ width: '100%' }}>
      <Input
        allowClear
        prefix={<SearchOutlined />}
        value={searchText}
        placeholder={t('warehouse.categories.searchPlaceholder', '搜索分类名称')}
        onChange={(event) => setSearchText(event.target.value)}
      />
      {treeData.length ? (
        <div style={{ maxHeight, overflow: 'auto', border: '1px solid #f0f0f0', borderRadius: 6, padding: 8 }}>
          <Tree
            blockNode
            selectedKeys={selectedKey ? [selectedKey] : []}
            expandedKeys={visibleExpandedKeys}
            onExpand={(keys: Key[]) => {
              if (!keyword) {
                onExpand(keys.map(String))
              }
            }}
            onSelect={(keys) => onSelect(typeof keys[0] === 'string' ? keys[0] : undefined)}
            treeData={treeData}
          />
        </div>
      ) : (
        <Empty description={t('warehouse.categories.noCategoryData', '暂无分类数据')} />
      )}
    </Space>
  )
}
