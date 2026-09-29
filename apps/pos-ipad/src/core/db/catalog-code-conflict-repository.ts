import type { SqliteConnectionPort, SqlValue } from "@hb/pos-db/core/db/types";

import type {
  CatalogStoredItem,
  LocalCatalogMatch,
} from "./catalog-repository";

// 中文注释：每行 19 个绑定参数，批大小 50（950 参数）兼容 Android 11 SQLite 999 参数上限。
const MAX_BULK_INSERT_ROWS = 50;
const CODE_CONFLICT_ROW_PLACEHOLDER = `(${Array.from(
  { length: 19 },
  () => "?",
).join(", ")})`;

export type CatalogCodeConflictReplaceResult = Readonly<{
  storeCode: string;
  itemCount: number;
  lookupCodeCount: number;
}>;

/**
 * 一码多商品候选仓储。候选只按门店整体原子替换，独立于目录快照子树，
 * 因而不参与 snapshot 激活、退役或分批清理；读取按门店 + 规范化查询码点查主键前缀。
 */
export class SqliteCatalogCodeConflictRepository {
  public constructor(private readonly db: SqliteConnectionPort) {}

  /**
   * 同一独占事务内先删后插：任何校验或写入失败都整体回滚，旧候选原样保留。
   * 同一查询码内按输入顺序编号（服务端首条即目录胜出项），同商品重复行只保留首条。
   */
  public async replaceStoreConflicts(
    storeCode: string,
    items: readonly CatalogStoredItem[],
  ): Promise<CatalogCodeConflictReplaceResult> {
    const scopedStoreCode = requiredText(storeCode, "catalog store code");
    const rows = prepareConflictRows(scopedStoreCode, items);
    await this.db.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        "DELETE FROM catalog_code_conflicts WHERE store_code = ?",
        [scopedStoreCode],
      );
      for (
        let start = 0;
        start < rows.length;
        start += MAX_BULK_INSERT_ROWS
      ) {
        const chunk = rows.slice(start, start + MAX_BULK_INSERT_ROWS);
        await transaction.run(
          `INSERT INTO catalog_code_conflicts (
             store_code, lookup_code_normalized, product_code, candidate_order,
             reference_code, item_number, display_name, barcode, lookup_code,
             retail_price_cents, price_source, price_source_label,
             quantity_factor, tax_rate_basis_points, updated_at_iso,
             row_version, product_image, discount_rate, is_special_product
           ) VALUES ${chunk.map(() => CODE_CONFLICT_ROW_PLACEHOLDER).join(", ")}`,
          chunk.flatMap(conflictRowParameters),
        );
      }
    });
    return {
      storeCode: scopedStoreCode,
      itemCount: rows.length,
      lookupCodeCount: new Set(rows.map((row) => row.item.lookupCodeNormalized))
        .size,
    };
  }

  /** 单码点查；绝大多数单商品码返回空数组。调用方负责确认该码仍存在于当前目录。 */
  public async findCandidates(
    storeCode: string,
    lookupCode: string,
  ): Promise<readonly LocalCatalogMatch[]> {
    const scopedStoreCode = requiredText(storeCode, "catalog store code");
    const lookupCodeNormalized = normalizeLookupCode(lookupCode);
    if (!lookupCodeNormalized) return [];
    const rows = await this.db.getAll<CodeConflictRow>(
      `SELECT
         store_code, product_code, reference_code, item_number, display_name,
         barcode, lookup_code, lookup_code_normalized, retail_price_cents,
         price_source, price_source_label, quantity_factor,
         tax_rate_basis_points, updated_at_iso, row_version, product_image,
         discount_rate, is_special_product
       FROM catalog_code_conflicts
       WHERE store_code = ?
         AND lookup_code_normalized = ?
       ORDER BY candidate_order ASC, product_code ASC`,
      [scopedStoreCode, lookupCodeNormalized],
    );
    return rows.map(mapConflictRow);
  }
}

type PreparedConflictRow = Readonly<{
  storeCode: string;
  candidateOrder: number;
  item: CatalogStoredItem;
}>;

type CodeConflictRow = Readonly<{
  store_code: unknown;
  product_code: unknown;
  reference_code: unknown;
  item_number: unknown;
  display_name: unknown;
  barcode: unknown;
  lookup_code: unknown;
  lookup_code_normalized: unknown;
  retail_price_cents: unknown;
  price_source: unknown;
  price_source_label: unknown;
  quantity_factor: unknown;
  tax_rate_basis_points: unknown;
  updated_at_iso: unknown;
  row_version: unknown;
  product_image: unknown;
  discount_rate: unknown;
  is_special_product: unknown;
}>;

function prepareConflictRows(
  storeCode: string,
  items: readonly CatalogStoredItem[],
): readonly PreparedConflictRow[] {
  const seenProducts = new Set<string>();
  const nextOrderByLookup = new Map<string, number>();
  const rows: PreparedConflictRow[] = [];
  for (const item of items) {
    assertConflictItem(item);
    if (item.storeCode !== storeCode) {
      throw new Error("Catalog code conflict belongs to another store.");
    }
    const productKey = `${item.lookupCodeNormalized}\u0000${normalizeProductCode(item.productCode)}`;
    // 中文注释：服务端已按商品去重；大小写不同的同商品仍只保留决胜顺序中的首条。
    if (seenProducts.has(productKey)) continue;
    seenProducts.add(productKey);
    const candidateOrder = nextOrderByLookup.get(item.lookupCodeNormalized) ?? 0;
    nextOrderByLookup.set(item.lookupCodeNormalized, candidateOrder + 1);
    rows.push({ storeCode, candidateOrder, item });
  }
  return rows;
}

function conflictRowParameters(row: PreparedConflictRow): SqlValue[] {
  const item = row.item;
  return [
    row.storeCode,
    item.lookupCodeNormalized,
    item.productCode,
    row.candidateOrder,
    item.referenceCode,
    item.itemNumber,
    item.displayName,
    item.barcode,
    item.lookupCode,
    item.retailPriceCents,
    item.priceSource,
    item.priceSourceLabel,
    storedDecimal(item.quantityFactor),
    item.taxRateBasisPoints,
    item.updatedAtIso,
    item.rowVersion,
    item.productImage,
    item.discountRate === null ? null : storedDecimal(item.discountRate),
    item.isSpecialProduct ? 1 : 0,
  ];
}

function assertConflictItem(item: CatalogStoredItem): void {
  requiredText(item.storeCode, "catalog store code");
  requiredText(item.productCode, "catalog product code");
  requiredText(item.displayName, "catalog display name");
  requiredText(item.lookupCode, "catalog lookup code");
  requiredText(item.priceSourceLabel, "catalog price source label");
  if (
    !item.lookupCodeNormalized ||
    normalizeLookupCode(item.lookupCodeNormalized) !== item.lookupCodeNormalized
  ) {
    throw new Error("Catalog lookup code must already be normalized.");
  }
  if (!Number.isSafeInteger(item.retailPriceCents)) {
    throw new Error("Catalog retail price must be integer cents.");
  }
  requiredPriceSource(item.priceSource);
  storedDecimal(item.quantityFactor);
  if (
    item.taxRateBasisPoints !== null &&
    !Number.isSafeInteger(item.taxRateBasisPoints)
  ) {
    throw new Error("Invalid catalog tax rate.");
  }
  if (item.discountRate !== null) storedDecimal(item.discountRate);
}

function mapConflictRow(row: CodeConflictRow): LocalCatalogMatch {
  const lookupCodeNormalized = requiredText(
    row.lookup_code_normalized,
    "catalog normalized lookup code",
  );
  if (normalizeLookupCode(lookupCodeNormalized) !== lookupCodeNormalized) {
    throw new Error("Invalid catalog normalized lookup code.");
  }
  return {
    storeCode: requiredText(row.store_code, "catalog store code"),
    productCode: requiredText(row.product_code, "catalog product code"),
    referenceCode: optionalText(row.reference_code, "catalog reference code"),
    itemNumber: optionalText(row.item_number, "catalog item number"),
    displayName: requiredText(row.display_name, "catalog display name"),
    barcode: optionalText(row.barcode, "catalog barcode"),
    lookupCode: requiredText(row.lookup_code, "catalog lookup code"),
    lookupCodeNormalized,
    retailPriceCents: requiredInteger(
      row.retail_price_cents,
      "catalog retail price",
    ),
    priceSource: requiredPriceSource(row.price_source),
    priceSourceLabel: requiredText(
      row.price_source_label,
      "catalog price source label",
    ),
    quantityFactor: requiredFiniteNumber(
      row.quantity_factor,
      "catalog quantity factor",
    ),
    taxRateBasisPoints: optionalInteger(
      row.tax_rate_basis_points,
      "catalog tax rate",
    ),
    updatedAtIso: optionalText(row.updated_at_iso, "catalog update timestamp"),
    rowVersion: optionalText(row.row_version, "catalog row version"),
    productImage: optionalText(row.product_image, "catalog product image"),
    discountRate: optionalFiniteNumber(
      row.discount_rate,
      "catalog discount rate",
    ),
    isSpecialProduct: requiredBooleanInteger(
      row.is_special_product,
      "catalog special product flag",
    ),
  };
}

function normalizeLookupCode(value: string): string {
  return value.trim().toUpperCase();
}

function normalizeProductCode(value: string): string {
  return value.trim().toUpperCase();
}

function storedDecimal(value: number): string {
  const number = requiredFiniteNumber(value, "catalog decimal");
  return Object.is(number, -0) ? "0" : String(number);
}

function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`Invalid ${label}.`);
  }
  return value;
}

function optionalText(value: unknown, label: string): string | null {
  if (value === null || value === undefined) return null;
  return requiredText(value, label);
}

function requiredInteger(value: unknown, label: string): number {
  const number = Number(value);
  if (!Number.isSafeInteger(number)) {
    throw new Error(`Invalid ${label}.`);
  }
  return number;
}

function optionalInteger(value: unknown, label: string): number | null {
  if (value === null || value === undefined) return null;
  return requiredInteger(value, label);
}

function requiredFiniteNumber(value: unknown, label: string): number {
  const number =
    typeof value === "number"
      ? value
      : typeof value === "string"
        ? Number(value)
        : Number.NaN;
  if (!Number.isFinite(number)) {
    throw new Error(`Invalid ${label}.`);
  }
  return number;
}

function optionalFiniteNumber(value: unknown, label: string): number | null {
  if (value === null || value === undefined) return null;
  return requiredFiniteNumber(value, label);
}

function requiredPriceSource(value: unknown): 0 | 1 | 2 | 3 | 4 {
  const number = Number(value);
  if (
    number === 0 ||
    number === 1 ||
    number === 2 ||
    number === 3 ||
    number === 4
  ) {
    return number;
  }
  throw new Error("Invalid catalog price source.");
}

function requiredBooleanInteger(value: unknown, label: string): boolean {
  if (value === 0 || value === false) return false;
  if (value === 1 || value === true) return true;
  throw new Error(`Invalid ${label}.`);
}
