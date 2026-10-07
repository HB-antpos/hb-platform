import { readFileSync } from 'node:fs'
import path from 'node:path'

// 分类管理页重设计的源码契约：保护自动加载、请求竞态、目标分类与筛选分离、删除限制等行为意图。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function readBlock(source: string, startMarker: string, endMarker: string) {
  const start = source.indexOf(startMarker)
  assert(start >= 0, `找不到代码块起点：${startMarker}`)
  const end = source.indexOf(endMarker, start + startMarker.length)
  return source.slice(start, end > 0 ? end : source.length)
}

const pageSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/Warehouse/Categories/index.tsx'),
  'utf8',
).replace(/\r\n/g, '\n')

// 1) 默认选中「全部商品」，选中节点/关键词/供应商变化即查第 1 页，不再需要「查询」按钮。
assert(
  pageSource.includes('useState<string>(ALL_PRODUCTS_FILTER_KEY)'),
  '左侧默认应选中「全部商品」，进入页面即加载商品',
)
assert(
  pageSource.includes('}, [selectedKey, productKeyword, productSupplierCode])'),
  '选中节点、关键词、供应商变化应自动查询',
)
assert(!pageSource.includes("t('common.query')"), '商品区不应再保留「查询」按钮')
assert(
  pageSource.includes('window.setTimeout(() => setProductKeyword(productKeywordInput.trim()), PRODUCT_SEARCH_DEBOUNCE_MS)'),
  '商品关键词应防抖后再查询',
)

// 2) 商品列表请求竞态：只采纳最后一次请求的结果。
const loadProductsBlock = readBlock(pageSource, 'const loadProducts = async', 'const loadSuppliers = async')
assert(loadProductsBlock.includes('productRequestSeqRef.current = requestSeq'), '商品请求缺少请求序号')
assert(
  loadProductsBlock.includes('if (requestSeq !== productRequestSeqRef.current) {\n        return\n      }'),
  '旧请求晚到时不能覆盖当前列表',
)
assert(loadProductsBlock.includes('searchText: query.keyword || undefined'), '关键词应同时匹配货号和商品名称（表格接口全局搜索）')
assert(
  loadProductsBlock.includes("categoryGuid: filterMode.type === 'category' ? filterMode.categoryGuid : undefined"),
  '选中具体分类时应按分类 GUID 查询',
)

// 3) 批量移动：目标分类与左侧筛选完全分离，调用原批量更新接口，成功后清空勾选并刷新。
const batchAssignBlock = readBlock(pageSource, 'const handleBatchAssign = async', 'const renderProductCell')
assert(batchAssignBlock.includes('const targetCategoryGuid = moveTargetGuid'), '批量移动的目标应取独立的目标分类')
assert(!batchAssignBlock.includes('selectedKey'), '批量移动目标不能跟随左侧选中项')
assert(
  batchAssignBlock.includes('await batchAssignProducts(targetCategoryGuid, selectedProductCodes.map(String))'),
  '批量移动应调用原批量更新接口',
)
assert(batchAssignBlock.includes('setSelectedProductCodes([])'), '移动成功后应清空勾选')
assert(batchAssignBlock.includes('await loadProducts(currentProductQuery())'), '移动成功后应刷新当前页商品')
assert(pageSource.includes('preserveSelectedRowKeys: true'), '翻页应保留勾选')

// 4) 删除：有子分类时禁用；没有子分类时保留二次确认，由后端拒绝有商品的分类并原样提示。
assert(pageSource.includes('{childCount > 0 ? ('), '有子分类时删除按钮应走禁用分支')
assert(
  pageSource.includes("<Tooltip title={t('warehouseUi.categories.deleteDisabledHasChildren')}>"),
  '删除禁用时应说明原因',
)
assert(
  pageSource.includes("title={t('warehouse.categories.confirmDelete')}") &&
    pageSource.includes("description={t('warehouse.categories.deleteBlockedHint')}"),
  '可删除时应保留确认弹层与后端拦截说明',
)
assert(
  pageSource.includes("message.error(error instanceof Error ? error.message : t('warehouse.categories.deleteFailed'))"),
  '后端拒绝删除时应提示后端返回的原因',
)

// 5) 新建/编辑弹窗：父类排除自身与子孙，新增子分类有提示。
assert(
  pageSource.includes('new Set([selectedCategory.categoryGUID, ...collectDescendantKeys(selectedCategory)])'),
  '编辑时父类选项应排除自身与子孙',
)
assert(pageSource.includes("t('warehouse.categories.addingChildFor'"), '新增子分类时应提示正在为哪个分类新增')

// 6) 分类树搜索为前端过滤，并展开命中项祖先。
assert(pageSource.includes('filterCategoryTree(categories, treeKeyword)'), '分类树搜索应在前端过滤')
assert(
  pageSource.includes('expandedKeys={isTreeSearching ? searchExpandedKeys : expandedKeys}'),
  '搜索时应使用命中项祖先的展开状态，退出搜索后恢复原展开状态',
)

console.log('categoriesPage.uiContract.test: ok')
