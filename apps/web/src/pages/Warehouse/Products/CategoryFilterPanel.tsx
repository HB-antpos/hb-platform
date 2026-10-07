import { SearchOutlined } from '@ant-design/icons'
import { Button, Empty, Input, Spin, Tree } from 'antd'
import type { DataNode } from 'antd/es/tree'
import type { TFunction } from 'i18next'
import type { Key } from 'react'
import { useMemo, useState } from 'react'
import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import { ALL_PRODUCTS_FILTER_KEY, UNCATEGORIZED_PRODUCTS_FILTER_KEY } from '../Categories/categoryProductFilters'
import { formatWarehouseCategoryNodeName } from './categoryPath'
import { filterCategoryTree, normalizeSearchText } from './CategoryTreePicker'

interface CategoryFilterPanelProps {
  categories: WarehouseCategoryNode[]
  loading: boolean
  /** 当前分类筛选值：ALL_PRODUCTS_FILTER_KEY、UNCATEGORIZED_PRODUCTS_FILTER_KEY 或分类 GUID。 */
  value: string
  expandedKeys: string[]
  onExpand: (keys: string[]) => void
  onChange: (value: string) => void
  /** 有分类管理权限时才传，面板头部显示「管理分类」入口。 */
  onManage?: () => void
  language?: string
  t: TFunction
}

function buildPanelTreeData(nodes: WarehouseCategoryNode[], language?: string): DataNode[] {
  return nodes.map((node) => {
    const name = formatWarehouseCategoryNodeName(node, language)
    return {
      key: node.categoryGUID,
      // 停用分类仍可筛选（商品可能还挂在上面），只用灰色区分，不再逐个加「启用/停用」标签。
      title: (
        <span className={`warehouse-products-category-node${node.isActive ? '' : ' is-inactive'}`} title={name}>
          {name}
        </span>
      ),
      children: node.children?.length ? buildPanelTreeData(node.children, language) : undefined,
    }
  })
}

/**
 * 仓库商品页左侧分类面板：固定的「全部商品」「未分类」入口 + 可搜索分类树，点节点即筛选。
 * 替代原先顶部的分类下拉与「只看未分类」开关；窄屏时由页面改回工具栏里的分类下拉。
 * 接口不返回各分类的商品数，所以节点不显示数量。
 */
export default function CategoryFilterPanel({
  categories,
  loading,
  value,
  expandedKeys,
  onExpand,
  onChange,
  onManage,
  language,
  t,
}: CategoryFilterPanelProps) {
  const [searchText, setSearchText] = useState('')
  const keyword = normalizeSearchText(searchText)
  // 搜索语义与批量分类弹窗的分类树一致：匹配中英文名称与父级路径，并自动展开命中路径。
  const searchResult = useMemo(
    () => keyword ? filterCategoryTree(categories, keyword, language) : { nodes: categories, expandedKeys: [] },
    [categories, keyword, language],
  )
  const treeData = useMemo(() => buildPanelTreeData(searchResult.nodes, language), [language, searchResult.nodes])
  const visibleExpandedKeys = keyword ? searchResult.expandedKeys : expandedKeys
  const isAll = value === ALL_PRODUCTS_FILTER_KEY
  const isUncategorized = value === UNCATEGORIZED_PRODUCTS_FILTER_KEY
  const selectedCategoryKeys = !isAll && !isUncategorized ? [value] : []

  return (
    <aside className="warehouse-products-category-panel" aria-label={t('warehouseUi.products.categoryPanelTitle')}>
      <div className="warehouse-products-category-head">
        <h2 className="warehouse-products-category-title">{t('warehouseUi.products.categoryPanelTitle')}</h2>
        {onManage ? (
          <Button type="link" size="small" className="warehouse-products-category-manage" onClick={onManage}>
            {t('containers.actions.manageCategories', '管理分类')}
          </Button>
        ) : null}
      </div>
      <Input
        size="small"
        allowClear
        prefix={<SearchOutlined />}
        className="warehouse-products-category-search"
        value={searchText}
        placeholder={t('warehouseUi.products.categorySearchPlaceholder')}
        aria-label={t('warehouseUi.products.categorySearchPlaceholder')}
        onChange={(event) => setSearchText(event.target.value)}
      />
      <div className="warehouse-products-category-fixed">
        <button
          type="button"
          aria-pressed={isAll}
          className={`warehouse-products-category-entry${isAll ? ' is-active' : ''}`}
          onClick={() => onChange(ALL_PRODUCTS_FILTER_KEY)}
        >
          {t('warehouseUi.products.allProducts')}
        </button>
        <button
          type="button"
          aria-pressed={isUncategorized}
          className={`warehouse-products-category-entry${isUncategorized ? ' is-active' : ''}`}
          onClick={() => onChange(UNCATEGORIZED_PRODUCTS_FILTER_KEY)}
        >
          {t('warehouseUi.products.uncategorized')}
        </button>
      </div>
      <div className="warehouse-products-category-tree">
        {loading && !categories.length ? (
          <div className="warehouse-products-category-loading"><Spin size="small" /></div>
        ) : treeData.length ? (
          <Tree
            blockNode
            selectedKeys={selectedCategoryKeys}
            expandedKeys={visibleExpandedKeys}
            onExpand={(keys: Key[]) => {
              // 搜索时展开状态由命中路径决定，不覆盖用户平时的手动展开。
              if (!keyword) {
                onExpand(keys.map(String))
              }
            }}
            onSelect={(keys) => {
              // 再次点击已选中的节点时 antd 会给出空数组，这里保持当前分类不变。
              const nextKey = keys[0]
              if (typeof nextKey === 'string' && nextKey !== value) {
                onChange(nextKey)
              }
            }}
            treeData={treeData}
          />
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={keyword ? t('warehouseUi.products.categorySearchEmpty') : t('warehouse.categories.noCategoryData', '暂无分类数据')}
          />
        )}
      </div>
    </aside>
  )
}
