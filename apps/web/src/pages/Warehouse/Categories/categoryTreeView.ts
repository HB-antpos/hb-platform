import type { WarehouseCategoryNode } from '../../../services/warehouseCategoryService'

// 分类管理页左侧分类树的纯计算：统计、搜索过滤、路径查找。只依赖分类树接口返回的现有字段。

export interface CategoryTreeStats {
  /** 顶级分类数 */
  topLevel: number
  /** 全部分类数（含各级子分类） */
  total: number
}

export function countCategoryTree(nodes: WarehouseCategoryNode[]): CategoryTreeStats {
  const countAll = (items: WarehouseCategoryNode[]): number =>
    items.reduce((sum, node) => sum + 1 + countAll(node.children || []), 0)

  return { topLevel: nodes.length, total: countAll(nodes) }
}

function normalizeKeyword(keyword: string) {
  return keyword.trim().toLowerCase()
}

function nodeMatchesKeyword(node: WarehouseCategoryNode, normalizedKeyword: string) {
  return [node.categoryName, node.chineseName].some((text) =>
    Boolean(text && text.toLowerCase().includes(normalizedKeyword)),
  )
}

export interface FilteredCategoryTree {
  nodes: WarehouseCategoryNode[]
  /** 需要展开的节点：命中节点的全部祖先，保证命中项可见。 */
  expandedKeys: string[]
  matchCount: number
}

/**
 * 按英文名 / 中文名过滤分类树（前端过滤，不发请求）。
 * - 命中节点保留完整子树，便于继续往下点；
 * - 未命中但有命中后代的节点只保留通向命中项的分支，并作为祖先展开。
 */
export function filterCategoryTree(nodes: WarehouseCategoryNode[], keyword: string): FilteredCategoryTree {
  const normalizedKeyword = normalizeKeyword(keyword)
  if (!normalizedKeyword) {
    return { nodes, expandedKeys: [], matchCount: 0 }
  }

  const expandedKeys: string[] = []
  let matchCount = 0

  const visit = (items: WarehouseCategoryNode[]): WarehouseCategoryNode[] =>
    items.flatMap((node) => {
      const filteredChildren = visit(node.children || [])
      const matched = nodeMatchesKeyword(node, normalizedKeyword)
      if (matched) {
        matchCount += 1
      }
      if (filteredChildren.length) {
        // 后代里有命中项：当前节点是命中项的祖先，必须展开。
        expandedKeys.push(node.categoryGUID)
      }
      if (matched) {
        return [node]
      }
      return filteredChildren.length ? [{ ...node, children: filteredChildren }] : []
    })

  const filtered = visit(nodes)
  return { nodes: filtered, expandedKeys, matchCount }
}

/** 从根到目标分类的路径（含目标本身）；找不到时返回空数组。 */
export function findCategoryPath(nodes: WarehouseCategoryNode[], targetGuid?: string): WarehouseCategoryNode[] {
  if (!targetGuid) {
    return []
  }

  for (const node of nodes) {
    if (node.categoryGUID === targetGuid) {
      return [node]
    }
    const childPath = findCategoryPath(node.children || [], targetGuid)
    if (childPath.length) {
      return [node, ...childPath]
    }
  }

  return []
}

/** 目标分类的全部祖先 GUID（不含自身），用于新增/编辑后展开到该节点。 */
export function collectAncestorGuids(nodes: WarehouseCategoryNode[], targetGuid?: string): string[] {
  return findCategoryPath(nodes, targetGuid)
    .slice(0, -1)
    .map((node) => node.categoryGUID)
}

function collectNodesByName(nodes: WarehouseCategoryNode[], name: string): WarehouseCategoryNode[] {
  return nodes.flatMap((node) => [
    ...(node.categoryName.trim() === name ? [node] : []),
    ...collectNodesByName(node.children || [], name),
  ])
}

export interface ProductCategoryRef {
  categoryGuid?: string
  categoryName?: string
}

/**
 * 解析商品当前分类的路径名称（英文名，从根到叶）。
 * 仓库商品表格接口只返回分类名不返回 GUID，因此按名称回查分类树：
 * 先在当前选中的分类子树里找（同名分类一般分属不同父类），唯一命中才用完整路径；
 * 有重名无法确定时只显示分类名本身，不猜路径。
 */
export function resolveProductCategoryPath(
  nodes: WarehouseCategoryNode[],
  product: ProductCategoryRef,
  scopeGuid?: string,
): string[] {
  if (product.categoryGuid) {
    const path = findCategoryPath(nodes, product.categoryGuid)
    if (path.length) {
      return path.map((node) => node.categoryName)
    }
  }

  const name = product.categoryName?.trim()
  if (!name) {
    return []
  }

  const scopePath = findCategoryPath(nodes, scopeGuid)
  const scopeNode = scopePath[scopePath.length - 1]
  if (scopeNode) {
    const scopedMatches = collectNodesByName([scopeNode], name)
    if (scopedMatches.length === 1) {
      return findCategoryPath(nodes, scopedMatches[0].categoryGUID).map((node) => node.categoryName)
    }
  }

  const matches = collectNodesByName(nodes, name)
  if (matches.length === 1) {
    return findCategoryPath(nodes, matches[0].categoryGUID).map((node) => node.categoryName)
  }

  return [name]
}

/** 只显示路径末尾几级（默认两级），如 `Lighting › Solar`。 */
export function formatCategoryPathTail(names: string[], depth = 2, separator = ' › '): string {
  return names.slice(-Math.max(1, depth)).join(separator)
}
