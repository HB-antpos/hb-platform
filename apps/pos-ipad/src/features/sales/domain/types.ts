import type {
  CartLineKind,
  CartMode,
  CartSnapshot,
  LineSyncProvenance,
  Money,
  PriceSource,
  PricingCartStateSnapshot,
  PromotionDefinition,
} from "../../../core/contracts";

export type {
  PricingCartLineState,
  PricingCartStateSnapshot,
  PricingDiscountState,
  PromotionDefinition,
  PromotionProduct,
} from "../../../core/contracts";

export const QUICK_DISCOUNT_BASIS_POINTS = [
  1_000,
  2_000,
  3_000,
  4_000,
  5_000,
] as const;

export type QuickDiscountBasisPoints =
  (typeof QUICK_DISCOUNT_BASIS_POINTS)[number];

export type AddCartItemInput = Readonly<{
  lineId: string;
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  quantity?: number;
  unitPrice: Money;
  catalogDiscountBasisPoints?: number;
  syncProvenance: LineSyncProvenance;
  priceSource?: Exclude<PriceSource, "promotion" | "open-item">;
  kind?: CartLineKind;
  returnSourceKey?: string | null;
  originalOrderGuid?: string | null;
  originalOrderDetailGuid?: string | null;
}>;

/**
 * 领域层唯一确认的加购结果。上层不得由 lineId 或布尔值反推是否发生了合并。
 */
export type CartAddDisposition = Readonly<{
  lineId: string;
  kind: "added" | "incremented";
}>;

export type AddOpenItemInput = Readonly<{
  lineId: string;
  productCode: string;
  itemNumber: string | null;
  lookupCode?: string;
  displayName: string;
  quantity?: number;
  unitPrice: Money;
  syncProvenance: LineSyncProvenance;
}>;

export type RefreshCatalogItemInput = Readonly<{
  expected: Readonly<{
    productCode: string;
    referenceCode: string | null;
    lookupCode: string;
  }>;
  item: Readonly<{
    productCode: string;
    referenceCode: string | null;
    itemNumber: string | null;
    lookupCode: string;
    displayName: string;
    retailPriceCents: number;
    catalogDiscountBasisPoints?: number;
    priceSource: LineSyncProvenance["priceSource"];
  }>;
}>;

export type PricingCartDiscountSource =
  | "none"
  | "catalog"
  | "manual"
  | "promotion";

export type PricingCartSnapshotLine = CartSnapshot["lines"][number] &
  Readonly<{
    discountSource: PricingCartDiscountSource;
  }>;

export type PricingCartSnapshot = Omit<CartSnapshot, "lines"> &
  Readonly<{
    lines: readonly PricingCartSnapshotLine[];
  }>;

export type PricingCartOptions = Readonly<{
  mode?: CartMode;
  asOfIso?: string;
  promotions?: readonly PromotionDefinition[];
  /**
   * 可选的当前时间来源。提供后，每次重算自动促销前都会先取一次当前时间作为
   * 「促销是否在生效区间内」的判断点；不提供则一直使用 asOfIso（测试与账务回放保持确定性）。
   */
  nowIso?: () => string;
}>;

/** PricingCart.restore 的可选参数：只携带时钟，恢复出的购物车要继续按当前时间判断促销。 */
export type PricingCartRestoreOptions = Readonly<{
  nowIso?: () => string;
}>;

export type PricingCartResult = Readonly<{
  state: PricingCartStateSnapshot;
  cart: PricingCartSnapshot;
}>;

export type MergeCompatibleCartLinesResult = Readonly<{
  groups: readonly Readonly<{
    keptLineId: string;
    removedLineIds: readonly string[];
  }>[];
  removedLineCount: number;
}>;

export type CashSettlement = Readonly<{
  cashDue: Money;
  normalizedCashTendered: Money;
  change: Money;
  roundingAdjustment: Money;
}>;
