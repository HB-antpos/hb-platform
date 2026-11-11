import type {
  SpecialProductCategoriesRepositoryPort,
  SpecialProductCategory,
  SpecialProductCategoryAssignment,
  SpecialProductCategoryLocalSnapshot,
  SpecialProductCategorySelection,
  SpecialProductCategorySnapshot,
} from "@hb/pos-domain/core/contracts/special-product-categories";
import type { SpecialProductItem } from "@hb/pos-domain/core/contracts/special-products";

import { SqliteSpecialProductsRepository } from "./sqlite-special-products-repository";
import type { SqliteConnectionPort } from "./types";

/** 当前选中分类按分店记在 app_settings 里（现成的本机 KV，不新建表）；键名带门店编号。 */
const SELECTED_CATEGORY_KEY_PREFIX = "special_products_selected_category_v1:";
const SPECIAL_PRODUCT_PAGE_SIZE = 1_000;
const MAX_SPECIAL_PRODUCT_PAGES = 1_000;

type CategoryRow = Readonly<{
  category_id: unknown;
  name: unknown;
  color: unknown;
  sort_order: unknown;
}>;

type AssignmentRow = Readonly<{
  product_code: unknown;
  category_id: unknown;
  sort_order: unknown;
}>;

type ProductCodeRow = Readonly<{ product_code: unknown }>;

type SettingRow = Readonly<{ setting_value: unknown }>;

/**
 * 分店级特殊商品分类的本机快照仓储。
 * 快照永远整体覆盖（服务端 revision 在删除行时可能不变甚至回退，不能靠它做增量）；
 * 分类表与归属表在同一个独占事务里替换，校验失败则整体回滚、旧快照原样保留。
 */
export class SqliteSpecialProductCategoriesRepository
  implements SpecialProductCategoriesRepositoryPort
{
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  public async readSnapshot(
    storeCode: string,
  ): Promise<SpecialProductCategoryLocalSnapshot> {
    const store = boundedText(storeCode, "store code", 128);
    const categoryRows = await this.connection.getAll<CategoryRow>(
      `SELECT category_id, name, color, sort_order
       FROM local_special_product_categories
       WHERE store_code = ?
       ORDER BY sort_order, category_id`,
      [store],
    );
    const assignmentRows = await this.connection.getAll<AssignmentRow>(
      `SELECT product_code, category_id, sort_order
       FROM local_special_product_category_items
       WHERE store_code = ?
       ORDER BY category_id COLLATE NOCASE, sort_order, product_code COLLATE NOCASE`,
      [store],
    );
    return Object.freeze({
      categories: Object.freeze(categoryRows.map(mapCategory)),
      assignments: Object.freeze(assignmentRows.map(mapAssignment)),
    });
  }

  public async replaceSnapshot(
    snapshot: SpecialProductCategorySnapshot,
  ): Promise<void> {
    const store = boundedText(snapshot?.storeCode, "store code", 128);
    const categories = validateCategories(snapshot.categories);
    const assignments = validateAssignments(
      snapshot.assignments,
      new Set(categories.map((category) => category.categoryId)),
    );
    await this.connection.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        "DELETE FROM local_special_product_category_items WHERE store_code = ?",
        [store],
      );
      await transaction.run(
        "DELETE FROM local_special_product_categories WHERE store_code = ?",
        [store],
      );
      for (const category of categories) {
        await transaction.run(
          `INSERT INTO local_special_product_categories (
            store_code, category_id, name, color, sort_order
          ) VALUES (?, ?, ?, ?, ?)`,
          [
            store,
            category.categoryId,
            category.name,
            category.color,
            category.sortOrder,
          ],
        );
      }
      for (const assignment of assignments) {
        await transaction.run(
          `INSERT INTO local_special_product_category_items (
            store_code, product_code, category_id, sort_order
          ) VALUES (?, ?, ?, ?)`,
          [
            store,
            assignment.productCode,
            assignment.categoryId,
            assignment.sortOrder,
          ],
        );
      }
    });
  }

  public async listCategoryProducts(
    storeCode: string,
    categoryId: string,
  ): Promise<readonly SpecialProductItem[]> {
    const store = boundedText(storeCode, "store code", 128);
    const category = boundedText(categoryId, "category id", 128);
    // 只取本机仍是特殊商品的归属（与服务端「和 IsSpecialProduct = 1 求交集」同口径），
    // 顺序取分类内的服务器顺序。
    const rows = await this.connection.getAll<ProductCodeRow>(
      `SELECT assignment.product_code
       FROM local_special_product_category_items assignment
       INNER JOIN local_special_products special
         ON special.store_code = assignment.store_code
        AND special.product_code = assignment.product_code
       WHERE assignment.store_code = ? AND assignment.category_id = ?
       ORDER BY assignment.sort_order, assignment.product_code COLLATE NOCASE`,
      [store, category],
    );
    if (rows.length === 0) return Object.freeze([]);

    // 商品明细复用特殊商品仓储既有的行校验与映射，避免在这里复制一份。
    const products = new SqliteSpecialProductsRepository(this.connection);
    const byCode = new Map<string, SpecialProductItem>();
    for (let page = 0; page < MAX_SPECIAL_PRODUCT_PAGES; page += 1) {
      const items = await products.list(
        store,
        SPECIAL_PRODUCT_PAGE_SIZE,
        page * SPECIAL_PRODUCT_PAGE_SIZE,
      );
      for (const item of items) byCode.set(item.productCode, item);
      if (items.length < SPECIAL_PRODUCT_PAGE_SIZE) break;
    }
    const ordered: SpecialProductItem[] = [];
    for (const row of rows) {
      const item = byCode.get(
        boundedText(row.product_code, "product code", 128),
      );
      if (item) ordered.push(item);
    }
    return Object.freeze(ordered);
  }

  public async readSelectedCategory(
    storeCode: string,
  ): Promise<SpecialProductCategorySelection | null> {
    const key = selectedCategoryKey(storeCode);
    const row = await this.connection.getFirst<SettingRow>(
      "SELECT setting_value FROM app_settings WHERE setting_key = ?",
      [key],
    );
    if (!row || typeof row.setting_value !== "string") return null;
    try {
      return boundedText(row.setting_value, "selected category", 128);
    } catch {
      // 记录损坏按「没有记录」处理，页面回到「全部」。
      return null;
    }
  }

  public async saveSelectedCategory(
    storeCode: string,
    selection: SpecialProductCategorySelection,
  ): Promise<void> {
    const key = selectedCategoryKey(storeCode);
    const value = boundedText(selection, "selected category", 128);
    await this.connection.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
         VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET
           setting_value = excluded.setting_value,
           updated_at_iso = excluded.updated_at_iso`,
        [key, value, this.nowIso()],
      );
    });
  }
}

function selectedCategoryKey(storeCode: string): string {
  return `${SELECTED_CATEGORY_KEY_PREFIX}${boundedText(storeCode, "store code", 128)}`;
}

function validateCategories(
  input: readonly SpecialProductCategory[],
): readonly SpecialProductCategory[] {
  if (!Array.isArray(input)) {
    throw new TypeError("Special product categories are invalid.");
  }
  const seen = new Set<string>();
  return Object.freeze(
    input.map((category) => {
      const normalized = Object.freeze({
        categoryId: boundedText(category?.categoryId, "category id", 128),
        name: boundedText(category?.name, "category name", 128),
        color: optionalColor(category?.color),
        sortOrder: nonNegativeInteger(
          category?.sortOrder,
          "category sort order",
        ),
      });
      if (seen.has(normalized.categoryId)) {
        throw new TypeError("Special product category id is duplicated.");
      }
      seen.add(normalized.categoryId);
      return normalized;
    }),
  );
}

function validateAssignments(
  input: readonly SpecialProductCategoryAssignment[],
  categoryIds: ReadonlySet<string>,
): readonly SpecialProductCategoryAssignment[] {
  if (!Array.isArray(input)) {
    throw new TypeError("Special product category assignments are invalid.");
  }
  const seen = new Set<string>();
  return Object.freeze(
    input.map((assignment) => {
      const normalized = Object.freeze({
        productCode: boundedText(
          assignment?.productCode,
          "special product code",
          128,
        ),
        categoryId: boundedText(assignment?.categoryId, "category id", 128),
        sortOrder: nonNegativeInteger(
          assignment?.sortOrder,
          "assignment sort order",
        ),
      });
      // 一个商品在一家店只属于一个分类，归属必须指向本次快照里的分类。
      if (seen.has(normalized.productCode)) {
        throw new TypeError("Special product assignment is duplicated.");
      }
      seen.add(normalized.productCode);
      if (!categoryIds.has(normalized.categoryId)) {
        throw new TypeError("Special product assignment category is unknown.");
      }
      return normalized;
    }),
  );
}

function mapCategory(row: CategoryRow): SpecialProductCategory {
  return Object.freeze({
    categoryId: boundedText(row.category_id, "category id", 128),
    name: boundedText(row.name, "category name", 128),
    color: optionalColor(row.color),
    sortOrder: nonNegativeInteger(row.sort_order, "category sort order"),
  });
}

function mapAssignment(row: AssignmentRow): SpecialProductCategoryAssignment {
  return Object.freeze({
    productCode: boundedText(row.product_code, "special product code", 128),
    categoryId: boundedText(row.category_id, "category id", 128),
    sortOrder: nonNegativeInteger(row.sort_order, "assignment sort order"),
  });
}

function optionalColor(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "string" || !/^#[0-9a-fA-F]{6}$/u.test(value)) {
    throw new TypeError("Special product category color is invalid.");
  }
  return value;
}

function boundedText(value: unknown, label: string, maxLength: number): string {
  if (typeof value !== "string") {
    throw new TypeError(`Special product ${label} is invalid.`);
  }
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new TypeError(`Special product ${label} is invalid.`);
  }
  return normalized;
}

function nonNegativeInteger(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) {
    throw new TypeError(`Special product ${label} must be a non-negative integer.`);
  }
  return value;
}
