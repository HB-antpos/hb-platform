import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'
import {
  collectAncestorGuids,
  countCategoryTree,
  filterCategoryTree,
  findCategoryPath,
  formatCategoryPathTail,
  resolveProductCategoryPath,
} from './categoryTreeView'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${message}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

function node(
  categoryGUID: string,
  categoryName: string,
  chineseName?: string,
  children: WarehouseCategoryNode[] = [],
): WarehouseCategoryNode {
  return { categoryGUID, categoryName, chineseName, isActive: true, children }
}

const tree: WarehouseCategoryNode[] = [
  node('home', 'Home & Living', '家居生活', [
    node('kitchen', 'Kitchen', '厨房'),
    node('lighting', 'Lighting', '灯具', [
      node('string', 'String Lights', '灯串'),
      node('solar', 'Solar', '太阳能灯'),
    ]),
  ]),
  node('garden', 'Garden & Outdoor', '园艺户外', [node('garden-solar', 'Solar', '太阳能装饰')]),
  node('party', 'Party Supplies', '派对用品'),
]

// 统计：顶级 3 个，全部 8 个。
assertEqual(countCategoryTree(tree), { topLevel: 3, total: 8 }, '分类统计应同时给出顶级数和全部分类数')
assertEqual(countCategoryTree([]), { topLevel: 0, total: 0 }, '空树统计应为 0')

// 搜索：空关键词原样返回，不展开任何节点。
const untouched = filterCategoryTree(tree, '   ')
assert(untouched.nodes === tree, '空关键词不应复制或裁剪分类树')
assertEqual(untouched.expandedKeys, [], '空关键词不应强制展开节点')

// 搜索中文名：只保留命中分支，并展开命中项的全部祖先。
const lampResult = filterCategoryTree(tree, '灯串')
assertEqual(lampResult.matchCount, 1, '中文名应可命中')
assertEqual(lampResult.nodes.map((item) => item.categoryGUID), ['home'], '只保留通向命中项的顶级分支')
assertEqual(
  lampResult.nodes[0].children?.map((item) => item.categoryGUID),
  ['lighting'],
  '未命中的兄弟分支（Kitchen）应被裁掉',
)
assertEqual(
  lampResult.nodes[0].children?.[0].children?.map((item) => item.categoryGUID),
  ['string'],
  '命中节点的兄弟（Solar）应被裁掉',
)
assertEqual([...lampResult.expandedKeys].sort(), ['home', 'lighting'], '命中节点的祖先都应展开')
assert(tree[0].children?.length === 2, '过滤不能修改原始分类树')

// 搜索英文名（不区分大小写），命中节点保留完整子树。
const lightingResult = filterCategoryTree(tree, 'LIGHTING')
assertEqual(lightingResult.matchCount, 1, '英文名搜索应不区分大小写')
assertEqual(
  lightingResult.nodes[0].children?.[0].children?.map((item) => item.categoryGUID),
  ['string', 'solar'],
  '命中节点应保留完整子树，便于继续展开',
)

// 同名分类在多个父类下都应命中。
const solarResult = filterCategoryTree(tree, 'solar')
assertEqual(solarResult.matchCount, 2, '同名分类都应计入命中')
assertEqual(solarResult.nodes.map((item) => item.categoryGUID), ['home', 'garden'], '同名分类各自的分支都应保留')

assertEqual(filterCategoryTree(tree, '不存在').nodes, [], '无命中时返回空树')

// 路径与祖先。
assertEqual(
  findCategoryPath(tree, 'solar').map((item) => item.categoryGUID),
  ['home', 'lighting', 'solar'],
  '路径应从根到目标',
)
assertEqual(findCategoryPath(tree, 'missing'), [], '找不到的分类返回空路径')
assertEqual(collectAncestorGuids(tree, 'solar'), ['home', 'lighting'], '祖先不含目标本身')
assertEqual(collectAncestorGuids(tree, 'party'), [], '顶级分类没有祖先')

// 商品当前分类路径：有 GUID 时按 GUID；只有名称时唯一命中才给路径。
assertEqual(
  resolveProductCategoryPath(tree, { categoryGuid: 'string', categoryName: 'String Lights' }),
  ['Home & Living', 'Lighting', 'String Lights'],
  '有分类 GUID 时应给出完整路径',
)
assertEqual(
  resolveProductCategoryPath(tree, { categoryName: 'Kitchen' }),
  ['Home & Living', 'Kitchen'],
  '名称唯一时应按名称回查出路径',
)
assertEqual(
  resolveProductCategoryPath(tree, { categoryName: 'Solar' }),
  ['Solar'],
  '全树重名且没有范围时不能猜路径，只显示名称',
)
assertEqual(
  resolveProductCategoryPath(tree, { categoryName: 'Solar' }, 'garden'),
  ['Garden & Outdoor', 'Solar'],
  '在当前选中分类子树内唯一命中时应给出该子树下的路径',
)
assertEqual(
  resolveProductCategoryPath(tree, { categoryName: 'Retired' }),
  ['Retired'],
  '分类树里没有的名称原样显示',
)
assertEqual(resolveProductCategoryPath(tree, {}), [], '未分类商品没有路径')

assertEqual(formatCategoryPathTail(['Home & Living', 'Lighting', 'Solar']), 'Lighting › Solar', '默认显示末两级')
assertEqual(formatCategoryPathTail(['Kitchen']), 'Kitchen', '只有一级时只显示自身')
assertEqual(formatCategoryPathTail([]), '', '空路径返回空串')

console.log('categoryTreeView.test: ok')
