/**
 * 分店级「特殊商品分类」的领域契约（收银端只读展示 + 在线调整归属 / 分类内顺序）。
 *
 * 设计取舍：这里全部是纯新增、独立于 `special-products.ts` 的端口——iPad 与手持 POS 共用
 * packages，手持本期不做分类，因此不能给既有 `SpecialProductsRemotePort` /
 * `SpecialProductsRepositoryPort` 增加必选成员，只在 iPad 的 presenter 与装配里使用本文件。
 */
import type { SpecialProductItem } from "./special-products";

/** 一个分类：顺序（sortOrder）由手机 App 维护，全店收银机一致。 */
export type SpecialProductCategory = Readonly<{
  categoryId: string;
  name: string;
  /** 分类自带颜色（#RRGGBB），仅用于色点；缺省表示用主题中性色。 */
  color: string | null;
  sortOrder: number;
}>;

/**
 * 商品归属。只会出现「当前仍是特殊商品」的商品；没出现的特殊商品 = 未分类。
 * sortOrder 是分类内的服务器统一顺序。
 */
export type SpecialProductCategoryAssignment = Readonly<{
  productCode: string;
  categoryId: string;
  sortOrder: number;
}>;

/** 服务端返回的整店分类快照；三个接口的成功响应都是它，客户端整体覆盖本地。 */
export type SpecialProductCategorySnapshot = Readonly<{
  storeCode: string;
  generatedAt: string;
  /** 注意：删除行时 revision 可能不变甚至回退，不能靠它判断「没变化」。 */
  revision: string;
  categories: readonly SpecialProductCategory[];
  assignments: readonly SpecialProductCategoryAssignment[];
}>;

/** 本地保存的快照（不含 generatedAt / revision：落库只为离线显示，每次都整体覆盖）。 */
export type SpecialProductCategoryLocalSnapshot = Readonly<{
  categories: readonly SpecialProductCategory[];
  assignments: readonly SpecialProductCategoryAssignment[];
}>;

/** 当前选中的分类视图：'all' = 全部，'uncategorized' = 未分类，其余是分类 id。 */
export type SpecialProductCategorySelection = string;
export const SPECIAL_PRODUCT_CATEGORY_ALL = "all";
export const SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED = "uncategorized";

/** 服务端在「分类内顺序」与现状不一致时返回的稳定错误码（HTTP 409）。 */
export const SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE =
  "SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT";
/** 两台收银机同时首次归类同一商品（HTTP 409）：本机数据已陈旧，需刷新快照后重新操作。 */
export const SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT_CODE =
  "SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT";
/** 分类已被删除 / 不属于本店（HTTP 404）。 */
export const SPECIAL_PRODUCT_CATEGORY_NOT_FOUND_CODE =
  "SPECIAL_PRODUCT_CATEGORY_NOT_FOUND";

export interface SpecialProductCategoriesRemotePort {
  /** 读取整店分类快照（实时读库，不走目录缓存）。 */
  getCategories(
    input: Readonly<{ storeCode: string }>,
  ): Promise<SpecialProductCategorySnapshot>;
  /** 把一个特殊商品移到某分类（追加到末尾）；categoryId 为 null = 回到「未分类」。 */
  assign(
    input: Readonly<{
      storeCode: string;
      productCode: string;
      categoryId: string | null;
    }>,
  ): Promise<SpecialProductCategorySnapshot>;
  /** 重排某分类内的商品顺序；productCodes 必须是该分类全部商品的一个排列。 */
  setOrder(
    input: Readonly<{
      storeCode: string;
      categoryId: string;
      productCodes: readonly string[];
    }>,
  ): Promise<SpecialProductCategorySnapshot>;
}

export interface SpecialProductCategoriesRepositoryPort {
  /** 读取本机保存的快照（没有时两个数组都为空）。 */
  readSnapshot(
    storeCode: string,
  ): Promise<SpecialProductCategoryLocalSnapshot>;
  /** 整体覆盖本机快照：分类表与归属表在同一个事务里完成，失败则保持旧数据。 */
  replaceSnapshot(snapshot: SpecialProductCategorySnapshot): Promise<void>;
  /** 某分类下的商品（只含本机仍是特殊商品的），按服务器分类内顺序。 */
  listCategoryProducts(
    storeCode: string,
    categoryId: string,
  ): Promise<readonly SpecialProductItem[]>;
  /** 本机记住的「当前选中分类」（按分店），没有记录时返回 null。 */
  readSelectedCategory(
    storeCode: string,
  ): Promise<SpecialProductCategorySelection | null>;
  saveSelectedCategory(
    storeCode: string,
    selection: SpecialProductCategorySelection,
  ): Promise<void>;
}

/** 结构化判断：远端抛出的错误是否带指定的稳定错误码（pos-domain 不依赖 api-client，不能用 instanceof）。 */
export function hasSpecialProductCategoryErrorCode(
  error: unknown,
  code: string,
): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    (error as Readonly<{ code?: unknown }>).code === code
  );
}
