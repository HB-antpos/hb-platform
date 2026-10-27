import type { PromotionDefinition } from "@/core/contracts";
import { ActivePricingCartSession } from "@/features/sales/runtime/active-pricing-cart-session";

/**
 * 这是 SQLCipher active catalog snapshot 的最窄只读投影。定义 JSON 必须是
 * Hbpos CatalogPromotionRuleDto 的完整 JSON，不能由 UI、搜索结果或网络失败时的
 * 局部响应临时拼接。
 */
export type StoredPromotionSnapshot = Readonly<{
  snapshotId: string;
  storeCode: string;
  promotions: readonly Readonly<{
    promotionId: string;
    definitionJson: string;
  }>[];
}>;

/** 数据库适配器只能读取已激活目录；staging 和 retired 快照绝不能参与定价。 */
export interface ActivePromotionSnapshotPort {
  loadActive(input: Readonly<{ storeCode: string }>): Promise<StoredPromotionSnapshot | null>;
}

export type PromotionSnapshotLoadResult =
  | Readonly<{
      status: "loaded";
      snapshotId: string;
      ruleCount: number;
    }>
  | Readonly<{ status: "no-active-snapshot" }>
  | Readonly<{ status: "fallback" }>;

/**
 * WPF 在每次购物车变化后用本地规则重算。本类仅负责把同一 active catalog
 * 快照的规则一次性装入共享购物车；读取、解析或校验失败时不触碰旧规则和金额。
 */
export class ActivePromotionSnapshotLoader {
  public constructor(
    private readonly activeCart: ActivePricingCartSession,
    private readonly snapshots: ActivePromotionSnapshotPort,
  ) {}

  public async load(input: Readonly<{
    storeCode: string;
    asOfIso: string;
  }>): Promise<PromotionSnapshotLoadResult> {
    try {
      const storeCode = requiredText(input.storeCode, "promotion store code");
      const snapshot = await this.snapshots.loadActive({ storeCode });
      if (snapshot === null) return { status: "no-active-snapshot" };

      assertSnapshotScope(snapshot, storeCode);
      // 负固定价规则是总部配置错误：整条忽略（不参与互斥选择、不计入规则数），其余规则照常生效，
      // 不能因为一条坏规则让整份促销快照失效而丢掉全部折扣（对齐 WPF #742 D5）。
      const promotions = snapshot.promotions
        .filter((record) => !hasNegativeFixedPrice(record))
        .map(parsePromotionDefinition);
      assertUniquePromotionIds(promotions);
      // 中文注释：applyPromotionSnapshot 在副本重算成功后才发布；任何异常均保留旧金额。
      this.activeCart.applyPromotionSnapshot(promotions, input.asOfIso);
      return {
        status: "loaded",
        snapshotId: requiredText(snapshot.snapshotId, "promotion snapshot id"),
        ruleCount: promotions.length,
      };
    } catch {
      // 促销是价格优化而不是耐久交易边界。目录不完整或 SQLCipher 暂不可读时，
      // 保留上一份已验证规则，绝不能清空折扣后继续收银。
      return { status: "fallback" };
    }
  }
}

/**
 * 规则的固定价是否为负数。只在定义 JSON 可解析且 fixedPrice 是有限负数时返回 true；
 * 其它任何损坏仍交给严格解析抛错（整份快照回退），不能顺手放过。
 */
export function hasNegativeFixedPrice(
  record: Readonly<{ promotionId: string; definitionJson: string }>,
): boolean {
  try {
    const fixedPrice = parseObject(record.definitionJson, "promotion definition")
      .fixedPrice;
    return typeof fixedPrice === "number" && Number.isFinite(fixedPrice) && fixedPrice < 0;
  } catch {
    return false;
  }
}

/**
 * 将后端 decimal 表示严格转换为 AUD 整数分。超过半分精度的值直接拒绝，
 * 避免 JavaScript 浮点数把 WPF 的固定价悄悄改成另一金额。
 */
export function parsePromotionDefinition(
  record: Readonly<{ promotionId: string; definitionJson: string }>,
): PromotionDefinition {
  const promotionId = requiredText(record.promotionId, "stored promotion id");
  const source = parseObject(record.definitionJson, "promotion definition");
  const definitionId = requiredText(source.promotionId, "promotion definition id");
  if (definitionId !== promotionId) {
    throw new Error("Promotion row identity does not match its definition.");
  }
  const products = requiredArray(source.products, "promotion products").map(
    (value) => {
      const product = requiredObject(value, "promotion product");
      return {
        productCode: requiredText(product.productCode, "promotion product code"),
        unitWeight: requiredPositiveInteger(
          product.unitWeight,
          "promotion product unit weight",
        ),
      };
    },
  );

  return {
    id: promotionId,
    name: requiredText(source.name, "promotion name"),
    effectiveStartIso: requiredIso(
      source.effectiveStart,
      "promotion effective start",
    ),
    effectiveEndIso: requiredIso(
      source.effectiveEnd,
      "promotion effective end",
    ),
    isExclusive: requiredBoolean(source.isExclusive, "promotion exclusivity"),
    priority: requiredInteger(source.priority, "promotion priority"),
    applyQuantity: requiredPositiveInteger(
      source.applyQuantity,
      "promotion apply quantity",
    ),
    fixedPrice: {
      currency: "AUD",
      cents: decimalAudToCents(source.fixedPrice, "promotion fixed price"),
    },
    maxApplicationsPerOrder: optionalNonNegativeInteger(
      source.maxApplicationsPerOrder,
      "promotion maximum applications",
    ),
    products,
  };
}

function assertSnapshotScope(
  snapshot: StoredPromotionSnapshot,
  storeCode: string,
): void {
  requiredText(snapshot.snapshotId, "promotion snapshot id");
  if (requiredText(snapshot.storeCode, "promotion snapshot store") !== storeCode) {
    throw new Error("Promotion snapshot store does not match cashier scope.");
  }
  if (!Array.isArray(snapshot.promotions)) {
    throw new TypeError("Promotion snapshot rules are invalid.");
  }
}

function assertUniquePromotionIds(
  promotions: readonly PromotionDefinition[],
): void {
  const ids = new Set<string>();
  for (const promotion of promotions) {
    if (ids.has(promotion.id)) {
      throw new Error("Promotion snapshot contains a duplicate rule id.");
    }
    ids.add(promotion.id);
  }
}

function parseObject(value: string, label: string): Record<string, unknown> {
  try {
    return requiredObject(JSON.parse(requiredText(value, label)), label);
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new TypeError(`${label} is not valid JSON.`);
    }
    throw error;
  }
}

function requiredObject(value: unknown, label: string): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new TypeError(`${label} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function requiredArray(value: unknown, label: string): readonly unknown[] {
  if (!Array.isArray(value)) throw new TypeError(`${label} must be an array.`);
  return value;
}

function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new TypeError(`${label} must be non-blank.`);
  }
  return value.trim();
}

function requiredBoolean(value: unknown, label: string): boolean {
  if (typeof value !== "boolean") throw new TypeError(`${label} must be boolean.`);
  return value;
}

function requiredInteger(value: unknown, label: string): number {
  if (!Number.isSafeInteger(value)) throw new TypeError(`${label} must be an integer.`);
  return value as number;
}

function requiredPositiveInteger(value: unknown, label: string): number {
  const number = requiredInteger(value, label);
  if (number <= 0) throw new RangeError(`${label} must be positive.`);
  return number;
}

function optionalNonNegativeInteger(value: unknown, label: string): number | null {
  if (value === null || value === undefined) return null;
  const number = requiredInteger(value, label);
  if (number < 0) throw new RangeError(`${label} must not be negative.`);
  return number;
}

function requiredIso(value: unknown, label: string): string {
  const text = requiredText(value, label);
  const timestamp = Date.parse(text);
  if (!Number.isFinite(timestamp)) throw new TypeError(`${label} must be ISO timestamp.`);
  return new Date(timestamp).toISOString();
}

function decimalAudToCents(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0) {
    throw new TypeError(`${label} must be a non-negative finite number.`);
  }
  const scaled = value * 100;
  const cents = Math.round(scaled);
  const tolerance = Number.EPSILON * Math.max(1, Math.abs(scaled)) * 8;
  if (!Number.isSafeInteger(cents) || Math.abs(scaled - cents) > tolerance) {
    throw new RangeError(`${label} cannot be represented as integer cents.`);
  }
  return cents;
}
