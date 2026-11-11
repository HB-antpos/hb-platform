import {
  SPECIAL_PRODUCT_CATEGORY_ALL,
  SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED,
  type SpecialProductCategory,
  type SpecialProductCategoryAssignment,
  type SpecialProductCategorySelection,
} from "@hb/pos-domain/core/contracts/special-product-categories";

import type { SpecialProductItem } from "@/core/contracts";

/** 分类栏上各行的数量；byCategory 以分类 id 为键。 */
export type SpecialProductCategoryCounts = Readonly<{
  all: number;
  uncategorized: number;
  byCategory: Readonly<Record<string, number>>;
}>;

export type SpecialProductsCategoryView = Readonly<{
  /** 实际生效的选中项（已处理「分类被删除 / 未分类为空 / 没有分类」的回退）。 */
  selectedCategoryId: SpecialProductCategorySelection;
  /** 页面网格要显示的商品：先看搜索，没有搜索词时按选中分类。 */
  visibleItems: readonly SpecialProductItem[];
  counts: SpecialProductCategoryCounts;
}>;

export const EMPTY_SPECIAL_PRODUCT_CATEGORY_COUNTS: SpecialProductCategoryCounts =
  Object.freeze({ all: 0, uncategorized: 0, byCategory: Object.freeze({}) });

/**
 * 由「本机全部特殊商品 + 分类快照 + 选中项 + 搜索词」派生页面视图（纯函数，便于单测）。
 *
 * 规则（与产品约定一致）：
 * - 归属只在「商品当前仍是本机特殊商品」时才算（取消标记不删归属行，重新标记回到原分类）；
 * - 没有任何分类 → 一切与分类功能上线前完全一致：选中项恒为全部、列表 = 本机顺序；
 * - 搜索词非空 → 始终在「全部」特殊商品里过滤，不受当前分类限制，顺序沿用本机顺序；
 * - 选中真实分类 → 该分类的商品，按服务器分类内顺序（sortOrder，再按商品编码稳定排序）；
 * - 选中「全部」「未分类」 → 本机顺序（不提供服务器排序）。
 */
export function deriveSpecialProductsCategoryView(
  input: Readonly<{
    items: readonly SpecialProductItem[];
    categories: readonly SpecialProductCategory[];
    assignments: readonly SpecialProductCategoryAssignment[];
    selection: SpecialProductCategorySelection;
    search: string;
  }>,
): SpecialProductsCategoryView {
  const { items, categories, assignments, search } = input;
  const categoryIds = new Set(categories.map((category) => category.categoryId));
  const itemCodes = new Set(items.map((item) => item.productCode));

  // 只保留「指向存在的分类、且商品当前仍是特殊商品」的归属。
  const effective = assignments.filter(
    (assignment) =>
      categoryIds.has(assignment.categoryId) &&
      itemCodes.has(assignment.productCode),
  );
  const categoryByProduct = new Map<string, string>();
  const byCategory: Record<string, number> = {};
  for (const category of categories) byCategory[category.categoryId] = 0;
  for (const assignment of effective) {
    categoryByProduct.set(assignment.productCode, assignment.categoryId);
    byCategory[assignment.categoryId] =
      (byCategory[assignment.categoryId] ?? 0) + 1;
  }
  const counts: SpecialProductCategoryCounts = Object.freeze({
    all: items.length,
    uncategorized: items.length - categoryByProduct.size,
    byCategory: Object.freeze(byCategory),
  });

  // 选中项回退：没有分类 / 分类被删除 / 「未分类」已空（分类栏里该行会隐藏）→ 全部。
  let selectedCategoryId: SpecialProductCategorySelection =
    SPECIAL_PRODUCT_CATEGORY_ALL;
  if (categories.length > 0) {
    if (
      input.selection === SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED
        ? counts.uncategorized > 0
        : categoryIds.has(input.selection)
    ) {
      selectedCategoryId = input.selection;
    }
  }

  const tokens = searchTokens(search);
  if (tokens.length > 0) {
    return Object.freeze({
      selectedCategoryId,
      visibleItems: Object.freeze(
        items.filter((item) => matchesSearch(item, tokens)),
      ),
      counts,
    });
  }

  let visibleItems: readonly SpecialProductItem[];
  if (selectedCategoryId === SPECIAL_PRODUCT_CATEGORY_ALL) {
    visibleItems = items;
  } else if (selectedCategoryId === SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED) {
    visibleItems = items.filter(
      (item) => !categoryByProduct.has(item.productCode),
    );
  } else {
    const byCode = new Map(items.map((item) => [item.productCode, item]));
    visibleItems = effective
      .filter((assignment) => assignment.categoryId === selectedCategoryId)
      .sort(
        (left, right) =>
          left.sortOrder - right.sortOrder ||
          left.productCode.localeCompare(right.productCode),
      )
      .map((assignment) => byCode.get(assignment.productCode)!);
  }
  return Object.freeze({
    selectedCategoryId,
    // 「全部」直接复用 items 引用，避免每次状态变化都复制整张列表、让网格无谓重绘。
    visibleItems:
      visibleItems === items ? items : Object.freeze([...visibleItems]),
    counts,
  });
}

/** 搜索词按空白拆成多个关键词，要求全部命中（不区分大小写）。 */
function searchTokens(search: string): readonly string[] {
  return search
    .toLocaleLowerCase()
    .split(/\s+/u)
    .filter((token) => token.length > 0);
}

/** 名称 / 货号 / 条码 / 商品编码（含卡片上显示的查找码、参考码）任一命中即算该关键词命中。 */
function matchesSearch(
  item: SpecialProductItem,
  tokens: readonly string[],
): boolean {
  const haystack = [
    item.displayName,
    item.itemNumber,
    item.barcode,
    item.productCode,
    item.lookupCode,
    item.referenceCode,
  ]
    .filter((value): value is string => typeof value === "string")
    .join("\n")
    .toLocaleLowerCase();
  return tokens.every((token) => haystack.includes(token));
}

/**
 * 把「视图子集」的新顺序写回完整顺序：子集成员依次填回它们在完整顺序里占的位置，
 * 非成员的相对位置不动。子集 = 完整列表时等价于直接用新顺序，所以「全部」视图的
 * 本机排序行为与分类功能上线前完全一致；「未分类」视图只重排未分类商品。
 */
export function applySubsetOrder(
  fullOrder: readonly string[],
  subsetNewOrder: readonly string[],
): string[] {
  const subset = new Set(subsetNewOrder);
  const queue = [...subsetNewOrder];
  return fullOrder.map((code) => (subset.has(code) ? queue.shift()! : code));
}
