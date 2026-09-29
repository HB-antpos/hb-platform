import type { StoreOrderCategoryNode, StoreOrderProductQuery } from "@/modules/shop/types";

export interface HomeProductQueryInput {
  storeCode?: string | null;
  keyword: string;
  categoryGUID?: string;
  grade?: string;
  pageNumber: number;
  pageSize: number;
}

export interface HomeSearchPageState {
  keyword: string;
  pageNumber: number;
  returnPageNumber: number | null;
}

export type HomeSearchPageAction =
  | { type: "apply"; input: string }
  | { type: "clear" };

export interface VisibleCategoryRow {
  node: StoreOrderCategoryNode;
  depth: number;
  hasChildren: boolean;
  isExpanded: boolean;
}

export function buildHomeProductQuery(input: HomeProductQueryInput): StoreOrderProductQuery {
  const keyword = input.keyword.trim();

  return {
    storeCode: input.storeCode ?? undefined,
    // Home 单搜索框只查货号/条码；不传 productName，避免扩大成更慢的商品名模糊搜索。
    itemNumber: keyword || undefined,
    categoryGUID: input.categoryGUID,
    grade: input.grade,
    pageNumber: input.pageNumber,
    pageSize: input.pageSize,
    sortBy: "Default",
  };
}

export function resolveHomeSearchPageState(
  state: HomeSearchPageState,
  action: HomeSearchPageAction,
): HomeSearchPageState {
  if (action.type === "clear") {
    const hasAppliedKeyword = Boolean(state.keyword.trim());

    return {
      keyword: "",
      pageNumber: hasAppliedKeyword ? (state.returnPageNumber ?? 1) : state.pageNumber,
      returnPageNumber: null,
    };
  }

  const nextKeyword = action.input.trim();
  if (!nextKeyword) {
    return resolveHomeSearchPageState(state, { type: "clear" });
  }
  if (nextKeyword === state.keyword) {
    return state;
  }

  return {
    keyword: nextKeyword,
    pageNumber: 1,
    // 关键逻辑：只在首次进入搜索时保存原页，后续修改关键词继续复用同一返回点。
    returnPageNumber: state.keyword.trim() ? state.returnPageNumber : state.pageNumber,
  };
}

/**
 * 再次提交与当前已生效关键词相同的搜索：页码状态不变、查询键不变，React Query 不会自动重新请求，
 * 调用方需显式 refetch，否则门店在零结果页上架后再点搜索仍看到旧结果（Web ShopHome 用 location.key 解决同一问题）。
 */
export function isSameKeywordResubmit(appliedKeyword: string, input: string) {
  const nextKeyword = input.trim();
  return Boolean(nextKeyword) && nextKeyword === appliedKeyword.trim();
}

export function buildCategoryNameMap(tree: StoreOrderCategoryNode[]) {
  const map = new Map<string, string>();
  const stack = [...tree];

  while (stack.length) {
    const node = stack.pop()!;
    map.set(node.categoryGUID, node.categoryName);
    stack.push(...(node.children ?? []));
  }

  return map;
}

export function flattenVisibleCategories(
  tree: StoreOrderCategoryNode[],
  expandedCategoryGUIDs: string[]
): VisibleCategoryRow[] {
  const expanded = new Set(expandedCategoryGUIDs);
  const rows: VisibleCategoryRow[] = [];

  function visit(nodes: StoreOrderCategoryNode[], depth: number) {
    for (const node of nodes) {
      const hasChildren = Boolean(node.children?.length);
      const isExpanded = expanded.has(node.categoryGUID);
      rows.push({ node, depth, hasChildren, isExpanded });

      if (hasChildren && isExpanded) {
        visit(node.children ?? [], depth + 1);
      }
    }
  }

  visit(tree, 0);
  return rows;
}
