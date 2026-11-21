import type { LocalCatalogMatch } from "./catalog-repository";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

const NO_ACTIVE_SNAPSHOT_GENERATION = "__HBPOS_NO_ACTIVE_CATALOG__";

export type CatalogLookupOverlayWriteResult =
  | "applied"
  | "stale-generation";

export type CatalogLookupOverlayUpsert = Readonly<{
  baseSnapshotId: string | null;
  item: LocalCatalogMatch;
}>;

export type CatalogLookupOverlayTombstone = Readonly<{
  baseSnapshotId: string | null;
  storeCode: string;
  lookupCodeNormalized: string;
}>;

export class SqliteCatalogLookupOverlayRepository {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  public getActiveSnapshotId(): Promise<string | null> {
    return readActiveSnapshotId(this.db);
  }

  public async upsert(
    input: CatalogLookupOverlayUpsert,
  ): Promise<CatalogLookupOverlayWriteResult> {
    assertMatch(input.item);
    const baseSnapshotId = generationKey(input.baseSnapshotId);
    return this.db.withExclusiveTransaction(async (transaction) => {
      if (
        !sameGeneration(
          await readActiveSnapshotId(transaction),
          input.baseSnapshotId,
        )
      ) {
        return "stale-generation";
      }
      await transaction.run(
        `INSERT INTO catalog_lookup_overlays (
           base_snapshot_id, store_code, lookup_code_normalized, record_kind,
           product_code, reference_code, item_number, display_name, barcode,
           lookup_code, retail_price_cents, price_source, price_source_label,
           quantity_factor, tax_rate_basis_points, updated_at_iso, row_version,
           product_image, discount_rate, is_special_product, verified_at_iso
         ) VALUES (
           ?, ?, ?, 'item', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?
         )
         ON CONFLICT (
           base_snapshot_id, store_code, lookup_code_normalized
         ) DO UPDATE SET
           record_kind = 'item',
           product_code = excluded.product_code,
           reference_code = excluded.reference_code,
           item_number = excluded.item_number,
           display_name = excluded.display_name,
           barcode = excluded.barcode,
           lookup_code = excluded.lookup_code,
           retail_price_cents = excluded.retail_price_cents,
           price_source = excluded.price_source,
           price_source_label = excluded.price_source_label,
           quantity_factor = excluded.quantity_factor,
           tax_rate_basis_points = excluded.tax_rate_basis_points,
           updated_at_iso = excluded.updated_at_iso,
           row_version = excluded.row_version,
           product_image = excluded.product_image,
           discount_rate = excluded.discount_rate,
           is_special_product = excluded.is_special_product,
           verified_at_iso = excluded.verified_at_iso`,
        [
          baseSnapshotId,
          input.item.storeCode,
          input.item.lookupCodeNormalized,
          input.item.productCode,
          input.item.referenceCode,
          input.item.itemNumber,
          input.item.displayName,
          input.item.barcode,
          input.item.lookupCode,
          input.item.retailPriceCents,
          input.item.priceSource,
          input.item.priceSourceLabel,
          storedDecimal(input.item.quantityFactor),
          input.item.taxRateBasisPoints,
          input.item.updatedAtIso,
          input.item.rowVersion,
          input.item.productImage,
          input.item.discountRate === null
            ? null
            : storedDecimal(input.item.discountRate),
          input.item.isSpecialProduct ? 1 : 0,
          requiredText(this.nowIso(), "catalog verification timestamp"),
        ],
      );
      return "applied";
    });
  }

  public async tombstone(
    input: CatalogLookupOverlayTombstone,
  ): Promise<CatalogLookupOverlayWriteResult> {
    const storeCode = requiredText(input.storeCode, "catalog store code");
    const lookupCodeNormalized = requiredNormalizedLookupCode(
      input.lookupCodeNormalized,
    );
    const baseSnapshotId = generationKey(input.baseSnapshotId);
    return this.db.withExclusiveTransaction(async (transaction) => {
      if (
        !sameGeneration(
          await readActiveSnapshotId(transaction),
          input.baseSnapshotId,
        )
      ) {
        return "stale-generation";
      }
      await transaction.run(
        `INSERT INTO catalog_lookup_overlays (
           base_snapshot_id, store_code, lookup_code_normalized,
           record_kind, verified_at_iso
         ) VALUES (?, ?, ?, 'tombstone', ?)
         ON CONFLICT (
           base_snapshot_id, store_code, lookup_code_normalized
         ) DO UPDATE SET
           record_kind = 'tombstone',
           product_code = NULL,
           reference_code = NULL,
           item_number = NULL,
           display_name = NULL,
           barcode = NULL,
           lookup_code = NULL,
           retail_price_cents = NULL,
           price_source = NULL,
           price_source_label = NULL,
           quantity_factor = NULL,
           tax_rate_basis_points = NULL,
           updated_at_iso = NULL,
           row_version = NULL,
           product_image = NULL,
           discount_rate = NULL,
           is_special_product = NULL,
           verified_at_iso = excluded.verified_at_iso`,
        [
          baseSnapshotId,
          storeCode,
          lookupCodeNormalized,
          requiredText(this.nowIso(), "catalog verification timestamp"),
        ],
      );
      return "applied";
    });
  }

  public async findExact(
    storeCode: string,
    lookupCode: string,
  ): Promise<LocalCatalogMatch | null> {
    const scopedStoreCode = requiredText(storeCode, "catalog store code");
    const lookupCodeNormalized = normalizeLookupCode(lookupCode);
    if (!lookupCodeNormalized) return null;
    const row = await this.db.getFirst<CatalogLookupRow>(
      `${combinedCatalogSql(true)}
       SELECT
         candidate.*,
         active_scope.active_count,
         CASE
           WHEN active_scope.active_count > 1 THEN 1
           ELSE 0
         END AS integrity_error
       FROM active_scope
       LEFT JOIN candidate
         ON candidate.lookup_code_normalized = ?4
       ORDER BY candidate.source_priority ASC
       LIMIT 1`,
      [
        NO_ACTIVE_SNAPSHOT_GENERATION,
        scopedStoreCode,
        scopedStoreCode,
        lookupCodeNormalized,
      ],
    );
    assertActiveScope(row);
    return hasCatalogCandidate(row) ? mapMatch(row) : null;
  }

  public async searchByName(
    storeCode: string,
    query: string,
    limit: number,
    offset = 0,
  ): Promise<readonly LocalCatalogMatch[]> {
    if (
      !Number.isSafeInteger(limit) ||
      limit <= 0 ||
      !Number.isSafeInteger(offset) ||
      offset < 0
    ) {
      throw new Error("Invalid catalog overlay search page.");
    }
    const scopedStoreCode = requiredText(storeCode, "catalog store code");
    const pattern = `%${escapeLike(query.trim())}%`;
    const rows = await this.db.getAll<CatalogLookupRow>(
      `${combinedCatalogSql()},
       matching AS (
         SELECT
           candidate.*,
           ROW_NUMBER() OVER (
             ORDER BY
               candidate.display_name COLLATE NOCASE ASC,
               COALESCE(candidate.item_number, '') COLLATE NOCASE ASC,
               candidate.lookup_code_normalized ASC
           ) AS result_order
         FROM candidate
         WHERE (
          candidate.display_name LIKE ? ESCAPE '\\'
          OR candidate.product_code LIKE ? ESCAPE '\\'
          OR COALESCE(candidate.item_number, '') LIKE ? ESCAPE '\\'
          OR candidate.lookup_code LIKE ? ESCAPE '\\'
         )
         ORDER BY
           candidate.display_name COLLATE NOCASE ASC,
           COALESCE(candidate.item_number, '') COLLATE NOCASE ASC,
           candidate.lookup_code_normalized ASC
         LIMIT ? OFFSET ?
       ),
       result AS (
         SELECT
           matching.store_code,
           matching.product_code,
           matching.reference_code,
           matching.item_number,
           matching.display_name,
           matching.barcode,
           matching.lookup_code,
           matching.lookup_code_normalized,
           matching.retail_price_cents,
           matching.price_source,
           matching.price_source_label,
           matching.quantity_factor,
           matching.tax_rate_basis_points,
           matching.updated_at_iso,
           matching.row_version,
           matching.product_image,
           matching.discount_rate,
           matching.is_special_product,
           matching.source_priority,
           active_scope.active_count,
           0 AS integrity_error,
           matching.result_order
         FROM matching
         CROSS JOIN active_scope

         UNION ALL

         SELECT
           NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
           NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
           active_scope.active_count,
           1 AS integrity_error,
           -1 AS result_order
         FROM active_scope
         WHERE active_scope.active_count > 1
       )
       SELECT *
       FROM result
       ORDER BY integrity_error DESC, result_order ASC`,
      [
        NO_ACTIVE_SNAPSHOT_GENERATION,
        scopedStoreCode,
        scopedStoreCode,
        pattern,
        pattern,
        pattern,
        pattern,
        limit,
        offset,
      ],
    );
    assertActiveScope(rows[0] ?? null);
    return rows.map(mapMatch);
  }

  public cleanupOldGenerations(): Promise<number> {
    return this.db.withExclusiveTransaction(async (transaction) => {
      const baseSnapshotId = await readActiveSnapshotId(transaction);
      const result = await transaction.run(
        `DELETE FROM catalog_lookup_overlays
         WHERE base_snapshot_id <> ?`,
        [generationKey(baseSnapshotId)],
      );
      return result.changes;
    });
  }
}

type ActiveSnapshotRow = Readonly<{
  snapshot_id: unknown;
  generation_id: unknown;
}>;

type CatalogLookupRow = Readonly<{
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
  source_priority: unknown;
  active_count: unknown;
  integrity_error: unknown;
}>;

async function readActiveSnapshotId(
  db: SqliteConnectionPort,
): Promise<string | null> {
  const rows = await db.getAll<ActiveSnapshotRow>(
    `SELECT
       snapshot_id,
       COALESCE(NULLIF(generation_id, ''), snapshot_id) AS generation_id
     FROM catalog_snapshots
     WHERE state = 'active'
     ORDER BY snapshot_id
     LIMIT 2`,
  );
  if (rows.length > 1) {
    throw new Error("Catalog contains multiple active snapshots.");
  }
  return rows[0]
    ? requiredText(rows[0].generation_id, "active catalog generation id")
    : null;
}

function combinedCatalogSql(exactLookup = false): string {
  // 精确查码必须在两个候选分支内限制 lookup code；若只在 CTE 外过滤，
  // SQLite 会先物化整店目录，TC26 的 SQLCipher 冷缓存会放大到秒级。
  const overlayExactPredicate = exactLookup
    ? "AND overlays.lookup_code_normalized = ?4"
    : "";
  const itemExactPredicate = exactLookup
    ? "AND items.lookup_code_normalized = ?4"
    : "";
  const overlayStoreParameter = exactLookup ? "?2" : "?";
  const itemStoreParameter = exactLookup ? "?3" : "?";
  return `
    WITH active_rows AS (
      SELECT
        snapshot_id,
        COALESCE(NULLIF(generation_id, ''), snapshot_id) AS generation_id
      FROM catalog_snapshots
      WHERE state = 'active'
      ORDER BY snapshot_id
      LIMIT 2
    ),
    active_scope AS (
      SELECT
        COALESCE(MAX(generation_id), ?) AS generation_key,
        CASE
          WHEN COUNT(*) = 1 THEN MAX(snapshot_id)
          ELSE NULL
        END AS snapshot_id,
        COUNT(*) AS active_count
      FROM active_rows
    ),
    candidate AS (
      SELECT
        overlays.store_code,
        overlays.product_code,
        overlays.reference_code,
        overlays.item_number,
        overlays.display_name,
        overlays.barcode,
        overlays.lookup_code,
        overlays.lookup_code_normalized,
        overlays.retail_price_cents,
        overlays.price_source,
        overlays.price_source_label,
        overlays.quantity_factor,
        overlays.tax_rate_basis_points,
        overlays.updated_at_iso,
        overlays.row_version,
        overlays.product_image,
        overlays.discount_rate,
        overlays.is_special_product,
        0 AS source_priority
      FROM active_scope
      CROSS JOIN catalog_lookup_overlays overlays
      WHERE active_scope.active_count <= 1
        AND overlays.base_snapshot_id = active_scope.generation_key
        AND overlays.store_code = ${overlayStoreParameter}
        AND overlays.record_kind = 'item'
        ${overlayExactPredicate}

      UNION ALL

      SELECT
        items.store_code,
        items.product_code,
        items.reference_code,
        items.item_number,
        items.display_name,
        items.barcode,
        items.lookup_code,
        items.lookup_code_normalized,
        items.retail_price_cents,
        items.price_source,
        items.price_source_label,
        items.quantity_factor,
        items.tax_rate_basis_points,
        items.updated_at_iso,
        items.row_version,
        items.product_image,
        items.discount_rate,
        items.is_special_product,
        1 AS source_priority
      FROM active_scope
      CROSS JOIN catalog_items items
      WHERE active_scope.active_count = 1
        AND items.snapshot_id = active_scope.snapshot_id
        AND items.store_code = ${itemStoreParameter}
        AND items.is_active = 1
        ${itemExactPredicate}
        AND NOT EXISTS (
          SELECT 1
          FROM catalog_lookup_overlays shadow
          WHERE shadow.base_snapshot_id = active_scope.generation_key
            AND shadow.store_code = items.store_code
            AND shadow.lookup_code_normalized =
              items.lookup_code_normalized
        )
    )`;
}

function assertActiveScope(row: CatalogLookupRow | null): void {
  if (row && Number(row.integrity_error) === 1) {
    throw new Error("Catalog contains multiple active snapshots.");
  }
}

function hasCatalogCandidate(
  row: CatalogLookupRow | null,
): row is CatalogLookupRow {
  return row !== null && row.lookup_code_normalized !== null;
}

function mapMatch(row: CatalogLookupRow): LocalCatalogMatch {
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
    updatedAtIso: optionalText(
      row.updated_at_iso,
      "catalog update timestamp",
    ),
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

function assertMatch(item: LocalCatalogMatch): void {
  requiredText(item.storeCode, "catalog store code");
  requiredText(item.productCode, "catalog product code");
  requiredText(item.displayName, "catalog display name");
  requiredText(item.lookupCode, "catalog lookup code");
  requiredNormalizedLookupCode(item.lookupCodeNormalized);
  requiredInteger(item.retailPriceCents, "catalog retail price");
  requiredPriceSource(item.priceSource);
  requiredText(item.priceSourceLabel, "catalog price source label");
  requiredFiniteNumber(item.quantityFactor, "catalog quantity factor");
  if (item.taxRateBasisPoints !== null) {
    requiredInteger(item.taxRateBasisPoints, "catalog tax rate");
  }
  if (item.discountRate !== null) {
    requiredFiniteNumber(item.discountRate, "catalog discount rate");
  }
}

function generationKey(snapshotId: string | null): string {
  if (snapshotId === null) return NO_ACTIVE_SNAPSHOT_GENERATION;
  const value = requiredText(snapshotId, "catalog base snapshot id");
  if (value === NO_ACTIVE_SNAPSHOT_GENERATION) {
    throw new Error("Catalog snapshot id uses a reserved generation.");
  }
  return value;
}

function sameGeneration(
  currentSnapshotId: string | null,
  expectedSnapshotId: string | null,
): boolean {
  return currentSnapshotId === expectedSnapshotId;
}

function normalizeLookupCode(value: string): string {
  return value.trim().toUpperCase();
}

function requiredNormalizedLookupCode(value: string): string {
  const normalized = normalizeLookupCode(value);
  if (!normalized || normalized !== value) {
    throw new Error("Catalog lookup code must already be normalized.");
  }
  return normalized;
}

function escapeLike(value: string): string {
  return value.replace(/[\\%_]/gu, (character) => `\\${character}`);
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
