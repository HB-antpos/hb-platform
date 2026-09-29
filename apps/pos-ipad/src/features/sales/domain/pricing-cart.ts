import {
  createAud,
  type CartLineKind,
  type CartMode,
  type CartSnapshot,
  type LineSyncProvenance,
  type Money,
  type PriceSource,
  normalizeLineSyncProvenance,
} from "../../../core/contracts";
import { multiplyCentsAwayFromZero } from "@hb/pos-domain/core/contracts/money";

import type {
  AddCartItemInput,
  AddOpenItemInput,
  CartAddDisposition,
  MergeCompatibleCartLinesResult,
  PricingCartLineState,
  PricingCartDiscountSource,
  PricingCartOptions,
  PricingCartSnapshot,
  PricingCartStateSnapshot,
  PricingDiscountState,
  PromotionDefinition,
  PromotionProduct,
  QuickDiscountBasisPoints,
  RefreshCatalogItemInput,
} from "./types";

type MutablePricingLine = {
  lineId: string;
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  quantity: number;
  unitPriceCents: number;
  basePriceSource: Exclude<PriceSource, "promotion">;
  catalogDiscountBasisPoints: number;
  syncProvenance: LineSyncProvenance | undefined;
  kind: CartLineKind;
  returnSourceKey: string | null;
  originalOrderGuid: string | null;
  originalOrderDetailGuid: string | null;
  discountState: PricingDiscountState;
};

type PromotionUnit = Readonly<{
  line: MutablePricingLine;
  quantityIndex: number;
  selectedIndex: number;
}>;

type PromotionLineAllocation = {
  cents: number;
  promotionIds: string[];
};

const MAX_SAFE_INTEGER_BIGINT = BigInt(Number.MAX_SAFE_INTEGER);
const MIN_SAFE_INTEGER_BIGINT = BigInt(Number.MIN_SAFE_INTEGER);
const NONE_DISCOUNT: PricingDiscountState = { kind: "none" };

function assertSafeInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value)) {
    throw new TypeError(`${label} must be a safe integer`);
  }
}

function assertNonBlank(value: string, label: string): void {
  if (value.trim().length === 0) {
    throw new TypeError(`${label} must not be blank`);
  }
}

function assertAudCents(
  money: Money,
  label: string,
  minimum = Number.MIN_SAFE_INTEGER,
): number {
  if (money.currency !== "AUD" || !Number.isSafeInteger(money.cents)) {
    throw new TypeError(`${label} must use safe integer AUD cents`);
  }

  if (money.cents < minimum) {
    throw new RangeError(`${label} is below its minimum`);
  }

  return money.cents;
}

function assertCatalogDiscountBasisPoints(
  value: number,
  label: string,
): number {
  if (
    !Number.isSafeInteger(value) ||
    value < 0 ||
    value > 10_000
  ) {
    throw new RangeError(`${label} must be an integer between 0 and 10000`);
  }

  return value;
}

function bigIntToSafeInteger(value: bigint, label: string): number {
  if (
    value > MAX_SAFE_INTEGER_BIGINT ||
    value < MIN_SAFE_INTEGER_BIGINT
  ) {
    throw new RangeError(`${label} exceeds the safe integer range`);
  }

  return Number(value);
}

function multiplySafe(
  left: number,
  right: number,
  label: string,
): number {
  // 与 canonical SharedSaleCartV1 multiplyCents 一致：整数走 BigInt，
  // 称重小数按 C# decimal AwayFromZero 精确取整（0.29 * 50 = 15，不是 14）。
  return multiplyCentsAwayFromZero(left, right, label);
}

function sumSafe(values: Iterable<number>, label: string): number {
  let result = 0n;
  for (const value of values) {
    assertSafeInteger(value, label);
    result += BigInt(value);
  }

  return bigIntToSafeInteger(result, label);
}

/**
 * 对整数分币比例执行 MidpointRounding.AwayFromZero，避免先转浮点数。
 */
function roundProductRatio(
  left: number,
  right: number,
  denominator: number,
  label: string,
): number {
  assertSafeInteger(left, label);
  assertSafeInteger(right, label);
  if (!Number.isSafeInteger(denominator) || denominator <= 0) {
    throw new RangeError(`${label} denominator must be positive`);
  }

  let numerator = BigInt(left) * BigInt(right);
  const divisor = BigInt(denominator);
  const sign = numerator < 0n ? -1n : 1n;
  numerator = numerator < 0n ? -numerator : numerator;

  let quotient = numerator / divisor;
  const remainder = numerator % divisor;
  if (remainder * 2n >= divisor) {
    quotient += 1n;
  }

  return bigIntToSafeInteger(sign * quotient, label);
}

function normalizeLookupCode(value: string): string {
  return value.trim().toUpperCase();
}

function normalizeProductCode(value: string): string {
  return value.trim().toUpperCase();
}

function compareOrdinalIgnoreCase(left: string, right: string): number {
  const normalizedLeft = left.toUpperCase();
  const normalizedRight = right.toUpperCase();
  if (normalizedLeft < normalizedRight) {
    return -1;
  }
  if (normalizedLeft > normalizedRight) {
    return 1;
  }

  return left < right ? -1 : left > right ? 1 : 0;
}

function clonePromotionProduct(
  product: PromotionProduct,
): PromotionProduct {
  assertNonBlank(product.productCode, "promotion product code");
  assertSafeInteger(product.unitWeight, "promotion unit weight");
  return {
    productCode: product.productCode,
    unitWeight: product.unitWeight,
  };
}

function clonePromotion(
  promotion: PromotionDefinition,
): PromotionDefinition {
  assertNonBlank(promotion.id, "promotion id");
  assertNonBlank(promotion.name, "promotion name");
  assertSafeInteger(promotion.priority, "promotion priority");
  assertSafeInteger(promotion.applyQuantity, "promotion apply quantity");
  if (promotion.applyQuantity < 0) {
    throw new RangeError("promotion apply quantity must not be negative");
  }
  if (
    promotion.maxApplicationsPerOrder !== null &&
    (!Number.isSafeInteger(promotion.maxApplicationsPerOrder) ||
      promotion.maxApplicationsPerOrder < 0)
  ) {
    throw new RangeError(
      "promotion max applications must be a non-negative integer",
    );
  }

  const start = Date.parse(promotion.effectiveStartIso);
  const end = Date.parse(promotion.effectiveEndIso);
  if (!Number.isFinite(start) || !Number.isFinite(end) || start > end) {
    throw new TypeError("promotion effective date range is invalid");
  }

  return {
    id: promotion.id,
    name: promotion.name,
    effectiveStartIso: new Date(start).toISOString(),
    effectiveEndIso: new Date(end).toISOString(),
    isExclusive: promotion.isExclusive,
    priority: promotion.priority,
    applyQuantity: promotion.applyQuantity,
    fixedPrice: createAud(
      assertAudCents(promotion.fixedPrice, "promotion fixed price", 0),
    ),
    maxApplicationsPerOrder: promotion.maxApplicationsPerOrder,
    products: promotion.products.map(clonePromotionProduct),
  };
}

function canonicalPromotions(
  promotions: readonly PromotionDefinition[],
): PromotionDefinition[] {
  return promotions
    .map(clonePromotion)
    .sort(
      (left, right) =>
        right.priority - left.priority ||
        compareOrdinalIgnoreCase(left.id, right.id),
    );
}

function cloneDiscountState(
  discountState: PricingDiscountState,
): PricingDiscountState {
  switch (discountState.kind) {
    case "none":
      return NONE_DISCOUNT;
    case "manual-amount":
      return { kind: "manual-amount", cents: discountState.cents };
    case "manual-percent":
      return {
        kind: "manual-percent",
        basisPoints: discountState.basisPoints,
      };
    case "promotion":
      return {
        kind: "promotion",
        cents: discountState.cents,
        promotionIds: [...discountState.promotionIds],
      };
  }
}

function cloneLineState(line: MutablePricingLine): PricingCartLineState {
  return {
    lineId: line.lineId,
    productCode: line.productCode,
    itemNumber: line.itemNumber,
    lookupCode: line.lookupCode,
    displayName: line.displayName,
    quantity: line.quantity,
    unitPriceCents: line.unitPriceCents,
    basePriceSource: line.basePriceSource,
    // 零基线省略序列化字段，兼容仍在使用旧快照形状的支付恢复链路；
    // 非零目录基线必须落盘，restore 会把缺失字段按 0 解释。
    ...(line.catalogDiscountBasisPoints === 0
      ? {}
      : { catalogDiscountBasisPoints: line.catalogDiscountBasisPoints }),
    ...(line.syncProvenance === undefined
      ? {}
      : {
          syncProvenance: normalizeLineSyncProvenance(
            line.syncProvenance,
          ),
        }),
    kind: line.kind,
    returnSourceKey: line.returnSourceKey,
    originalOrderGuid: line.originalOrderGuid,
    originalOrderDetailGuid: line.originalOrderDetailGuid,
    discountState: cloneDiscountState(line.discountState),
  };
}

function cloneMutableLine(line: MutablePricingLine): MutablePricingLine {
  return {
    lineId: line.lineId,
    productCode: line.productCode,
    itemNumber: line.itemNumber,
    lookupCode: line.lookupCode,
    displayName: line.displayName,
    quantity: line.quantity,
    unitPriceCents: line.unitPriceCents,
    basePriceSource: line.basePriceSource,
    catalogDiscountBasisPoints: line.catalogDiscountBasisPoints,
    syncProvenance:
      line.syncProvenance === undefined
        ? undefined
        : normalizeLineSyncProvenance(line.syncProvenance),
    kind: line.kind,
    returnSourceKey: line.returnSourceKey,
    originalOrderGuid: line.originalOrderGuid,
    originalOrderDetailGuid: line.originalOrderDetailGuid,
    discountState: cloneDiscountState(line.discountState),
  };
}

function hasSameSyncProvenance(
  left: LineSyncProvenance | undefined,
  right: LineSyncProvenance,
): boolean {
  return (
    left !== undefined &&
    left.referenceCode === right.referenceCode &&
    left.priceSource === right.priceSource
  );
}

function hasSameOptionalSyncProvenance(
  left: LineSyncProvenance | undefined,
  right: LineSyncProvenance | undefined,
): boolean {
  if (left === undefined || right === undefined) {
    return left === right;
  }
  return (
    left.referenceCode === right.referenceCode &&
    left.priceSource === right.priceSource
  );
}

function validateMode(mode: CartMode): CartMode {
  if (mode !== "sale" && mode !== "return" && mode !== "installment") {
    throw new TypeError("cart mode is invalid");
  }
  return mode;
}

function canonicalIso(value: string, label: string): string {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    throw new TypeError(`${label} must be a valid ISO timestamp`);
  }
  return new Date(timestamp).toISOString();
}

export class PricingCart {
  private revision = 0;
  private mode: CartMode;
  private asOfIso: string;
  private promotions: PromotionDefinition[];
  private lines: MutablePricingLine[] = [];

  constructor(options: PricingCartOptions = {}) {
    this.mode = validateMode(options.mode ?? "sale");
    this.asOfIso = canonicalIso(
      options.asOfIso ?? new Date().toISOString(),
      "cart as-of timestamp",
    );
    this.promotions = canonicalPromotions(options.promotions ?? []);
  }

  static restore(snapshot: PricingCartStateSnapshot): PricingCart {
    if (!Number.isSafeInteger(snapshot.revision) || snapshot.revision < 0) {
      throw new TypeError("cart revision must be a non-negative integer");
    }

    const cart = new PricingCart({
      mode: snapshot.mode,
      asOfIso: snapshot.asOfIso,
      promotions: snapshot.promotions,
    });
    const seenLineIds = new Set<string>();
    cart.lines = snapshot.lines.map((line) => {
      assertNonBlank(line.lineId, "cart line id");
      if (seenLineIds.has(line.lineId)) {
        throw new TypeError(`duplicate cart line id: ${line.lineId}`);
      }
      seenLineIds.add(line.lineId);
      assertNonBlank(line.productCode, "cart product code");
      assertNonBlank(line.displayName, "cart display name");
      if (!PricingCart.isRestorableQuantity(line.quantity)) {
        throw new TypeError("cart line quantity must be a positive finite number");
      }
      assertSafeInteger(line.unitPriceCents, "cart unit price");
      if (line.unitPriceCents < 0) {
        throw new RangeError("cart unit price must not be negative");
      }
      if (
        line.basePriceSource !== "catalog" &&
        line.basePriceSource !== "manual" &&
        line.basePriceSource !== "open-item"
      ) {
        throw new TypeError("cart base price source is invalid");
      }
      const catalogDiscountBasisPoints =
        assertCatalogDiscountBasisPoints(
          line.catalogDiscountBasisPoints ?? 0,
          "cart catalog discount basis points",
        );
      if (
        catalogDiscountBasisPoints > 0 &&
        (line.kind !== "sale" || line.basePriceSource === "open-item")
      ) {
        throw new TypeError(
          line.basePriceSource === "open-item"
            ? "open-item lines cannot contain a catalog discount"
            : "return lines cannot contain a catalog discount",
        );
      }

      const restored: MutablePricingLine = {
        lineId: line.lineId,
        productCode: line.productCode,
        itemNumber: line.itemNumber,
        lookupCode: line.lookupCode,
        displayName: line.displayName,
        quantity: line.quantity,
        unitPriceCents: line.unitPriceCents,
        basePriceSource: line.basePriceSource,
        catalogDiscountBasisPoints,
        syncProvenance:
          line.syncProvenance === undefined
            ? undefined
            : normalizeLineSyncProvenance(line.syncProvenance),
        kind: line.kind,
        returnSourceKey: line.returnSourceKey,
        originalOrderGuid: line.originalOrderGuid,
        originalOrderDetailGuid: line.originalOrderDetailGuid,
        discountState: cloneDiscountState(line.discountState),
      };
      cart.validateRestoredDiscount(restored);
      return restored;
    });
    cart.revision = snapshot.revision;
    // 快照已经包含结账当时的促销结果；恢复时不得按新时刻重新定价。
    return cart;
  }

  /** 保持已有调用方的 string API；新代码应消费 disposition。 */
  addItem(input: AddCartItemInput): string {
    return this.addItemWithDisposition(input).lineId;
  }

  addItemWithDisposition(input: AddCartItemInput): CartAddDisposition {
    const quantity = input.quantity ?? 1;
    this.assertNewLineInput(input, quantity);
    const catalogDiscountBasisPoints =
      assertCatalogDiscountBasisPoints(
        input.catalogDiscountBasisPoints ?? 0,
        "cart catalog discount basis points",
      );
    const syncProvenance = normalizeLineSyncProvenance(
      input.syncProvenance,
    );
    const kind = input.kind ?? "sale";
    const normalizedLookup = normalizeLookupCode(input.lookupCode);
    const normalizedProduct = normalizeProductCode(input.productCode);

    if (kind === "sale") {
      // 一码多商品（套装码、共用条码）时同码可对应不同商品：与 WPF 一致，
      // 查询码与商品编码都相同才并入已有行，选了不同商品必须分行各按自身价格。
      const existing = this.lines.find(
        (line) =>
          line.kind === "sale" &&
          line.basePriceSource !== "open-item" &&
          normalizeLookupCode(line.lookupCode) === normalizedLookup &&
          normalizeProductCode(line.productCode) === normalizedProduct,
      );
      if (existing) {
        if (
          !hasSameSyncProvenance(
            existing.syncProvenance,
            syncProvenance,
          )
        ) {
          throw new TypeError(
            "cart line sync provenance conflicts with the existing lookup",
          );
        }
        const nextQuantity = sumSafe(
          [existing.quantity, quantity],
          "merged cart quantity",
        );
        this.assertGrossSafe(nextQuantity, existing.unitPriceCents);
        // 搜索/本地命中代表当前目录事实；同一安全身份即使价格未变，
        // 折扣基线变化或移除也必须在本次合并中生效。
        existing.catalogDiscountBasisPoints =
          catalogDiscountBasisPoints;
        existing.quantity = nextQuantity;
        this.normalizeDiscountAfterGrossChange(existing);
        this.finishMutation();
        return { lineId: existing.lineId, kind: "incremented" };
      }
    }

    return {
      lineId: this.appendItem(input, quantity, syncProvenance, kind),
      kind: "added",
    };
  }

  /**
   * 扫码只允许与最后一行连续合并，避免把中间已分开的销售决策重新折叠。
   */
  /** 保持已有调用方的 string API；新代码应消费 disposition。 */
  addScannedItem(input: AddCartItemInput): string {
    return this.addScannedItemWithDisposition(input).lineId;
  }

  addScannedItemWithDisposition(
    input: AddCartItemInput,
  ): CartAddDisposition {
    const quantity = input.quantity ?? 1;
    this.assertNewLineInput(input, quantity);
    const catalogDiscountBasisPoints =
      assertCatalogDiscountBasisPoints(
        input.catalogDiscountBasisPoints ?? 0,
        "cart catalog discount basis points",
      );
    const syncProvenance = normalizeLineSyncProvenance(
      input.syncProvenance,
    );
    const kind = input.kind ?? "sale";
    const unitPriceCents = assertAudCents(
      input.unitPrice,
      "cart unit price",
      0,
    );
    const priceSource = input.priceSource ?? "catalog";
    if (priceSource !== "catalog" && priceSource !== "manual") {
      throw new TypeError("cart item price source is invalid");
    }

    const lastLine = this.lines.at(-1);
    if (
      kind === "sale" &&
      lastLine?.kind === "sale" &&
      lastLine.basePriceSource !== "open-item" &&
      normalizeLookupCode(lastLine.lookupCode) ===
        normalizeLookupCode(input.lookupCode) &&
      normalizeProductCode(lastLine.productCode) ===
        normalizeProductCode(input.productCode) &&
      lastLine.unitPriceCents === unitPriceCents &&
      lastLine.basePriceSource === priceSource &&
      hasSameOptionalSyncProvenance(
        lastLine.syncProvenance,
        syncProvenance,
      ) &&
      (lastLine.discountState.kind === "none" ||
        lastLine.discountState.kind === "promotion")
    ) {
      const nextQuantity = sumSafe(
        [lastLine.quantity, quantity],
        "merged scanned cart quantity",
      );
      this.assertGrossSafe(nextQuantity, lastLine.unitPriceCents);
      // 连续扫码同样采用最新目录折扣；该赋值与数量变化共享一次 revision。
      lastLine.catalogDiscountBasisPoints =
        catalogDiscountBasisPoints;
      lastLine.quantity = nextQuantity;
      this.normalizeDiscountAfterGrossChange(lastLine);
      this.finishMutation();
      return { lineId: lastLine.lineId, kind: "incremented" };
    }

    return {
      lineId: this.appendItem(input, quantity, syncProvenance, kind),
      kind: "added",
    };
  }

  hasMergeCompatibleLines(): boolean {
    const baseline = this.snapshot();
    const compatibleGroups = this.compatibleLineGroups(this.lines).filter(
      (group) => this.groupPreservesCartAmounts(group),
    );
    if (compatibleGroups.length === 0) return false;

    const batched = this.simulateCompatibleGroupsMerge(
      this.lines,
      compatibleGroups,
    );
    if (this.hasSameCartAmounts(baseline, batched.snapshot)) {
      return true;
    }

    // 理论不变量被新促销算法打破时退回逐组验证，保持按钮预测与执行完全一致。
    return compatibleGroups.some((group) => {
      const candidate = this.simulateCompatibleGroupMerge(
        this.lines,
        group,
      );
      return (
        candidate !== null &&
        this.hasSameCartAmounts(baseline, candidate.snapshot)
      );
    });
  }

  /**
   * 先在候选车上逐组重算并核对三项金额；只有完全不改变金额的组才一次性提交。
   */
  mergeCompatibleLines(): MergeCompatibleCartLinesResult {
    const plan = this.planCompatibleLineMerge();
    if (plan.groups.length === 0) {
      return { groups: [], removedLineCount: 0 };
    }

    this.lines = plan.lines;
    this.finishMutation();
    return {
      groups: plan.groups,
      removedLineCount: plan.groups.reduce(
        (count, group) => count + group.removedLineIds.length,
        0,
      ),
    };
  }

  addOpenItem(input: AddOpenItemInput): string {
    const quantity = input.quantity ?? 1;
    this.assertNewLineInput(
      {
        ...input,
        lookupCode: input.lookupCode ?? input.productCode,
      },
      quantity,
    );
    const syncProvenance = normalizeLineSyncProvenance(
      input.syncProvenance,
    );
    this.assertUniqueLineId(input.lineId);
    const unitPriceCents = assertAudCents(
      input.unitPrice,
      "open item price",
      0,
    );
    this.assertGrossSafe(quantity, unitPriceCents);

    this.lines.push({
      lineId: input.lineId,
      productCode: input.productCode,
      itemNumber: input.itemNumber,
      lookupCode: input.lookupCode ?? input.productCode,
      displayName: input.displayName,
      quantity,
      unitPriceCents,
      basePriceSource: "open-item",
      syncProvenance,
      kind: "sale",
      returnSourceKey: null,
      originalOrderGuid: null,
      originalOrderDetailGuid: null,
      catalogDiscountBasisPoints: 0,
      discountState: NONE_DISCOUNT,
    });
    this.finishMutation();
    return input.lineId;
  }

  /**
   * 仅更新扫码时已确认的同身份销售行。手工价属于收银员决策，只同步目录元数据；
   * catalog 价则按服务端分币更新，并由 finishMutation 统一重算折扣与促销。
   */
  refreshCatalogItem(input: RefreshCatalogItemInput): readonly string[] {
    const expectedLookup = normalizeLookupCode(input.expected.lookupCode);
    const nextLookup = normalizeLookupCode(input.item.lookupCode);
    if (
      normalizeProductCode(input.expected.productCode) !==
        normalizeProductCode(input.item.productCode) ||
      input.expected.referenceCode !== input.item.referenceCode ||
      expectedLookup !== nextLookup ||
      expectedLookup.length === 0
    ) {
      return [];
    }
    assertNonBlank(input.item.displayName, "catalog display name");
    assertSafeInteger(input.item.retailPriceCents, "catalog retail price");
    if (input.item.retailPriceCents < 0) {
      throw new RangeError("catalog retail price must not be negative");
    }
    const catalogDiscountBasisPoints =
      assertCatalogDiscountBasisPoints(
        input.item.catalogDiscountBasisPoints ?? 0,
        "catalog discount basis points",
      );

    const updatedLineIds: string[] = [];
    for (const line of this.lines) {
      if (
        line.kind !== "sale" ||
        line.basePriceSource === "open-item" ||
        normalizeLookupCode(line.lookupCode) !== expectedLookup ||
        normalizeProductCode(line.productCode) !==
          normalizeProductCode(input.expected.productCode) ||
        line.syncProvenance?.referenceCode !==
          input.expected.referenceCode
      ) {
        continue;
      }

      const metadataChanged =
        line.itemNumber !== input.item.itemNumber ||
        normalizeLookupCode(line.lookupCode) !== nextLookup ||
        line.displayName !== input.item.displayName ||
        line.syncProvenance?.priceSource !== input.item.priceSource;
      const catalogPriceChanged =
        line.basePriceSource === "catalog" &&
        line.unitPriceCents !== input.item.retailPriceCents;
      const catalogDiscountChanged =
        line.catalogDiscountBasisPoints !== catalogDiscountBasisPoints;
      if (
        !metadataChanged &&
        !catalogPriceChanged &&
        !catalogDiscountChanged
      ) {
        continue;
      }

      if (metadataChanged) {
        line.productCode = input.item.productCode;
        line.itemNumber = input.item.itemNumber;
        line.lookupCode = input.item.lookupCode;
        line.displayName = input.item.displayName;
        line.syncProvenance = normalizeLineSyncProvenance({
          referenceCode: input.item.referenceCode,
          priceSource: input.item.priceSource,
        });
      }
      if (catalogPriceChanged) {
        this.assertGrossSafe(line.quantity, input.item.retailPriceCents);
        line.unitPriceCents = input.item.retailPriceCents;
        this.normalizeDiscountAfterGrossChange(line);
      }
      if (catalogDiscountChanged) {
        line.catalogDiscountBasisPoints = catalogDiscountBasisPoints;
      }
      updatedLineIds.push(line.lineId);
    }

    if (updatedLineIds.length > 0) this.finishMutation();
    return updatedLineIds;
  }

  removeLine(lineId: string): boolean {
    const index = this.lines.findIndex((line) => line.lineId === lineId);
    if (index < 0) {
      return false;
    }

    this.lines.splice(index, 1);
    this.finishMutation();
    return true;
  }

  increaseLine(lineId: string): boolean {
    const line = this.editableLine(lineId);
    if (!line) {
      return false;
    }

    const nextQuantity = sumSafe(
      [line.quantity, 1],
      "cart line quantity",
    );
    this.assertGrossSafe(nextQuantity, line.unitPriceCents);
    line.quantity = nextQuantity;
    this.normalizeDiscountAfterGrossChange(line);
    this.finishMutation();
    return true;
  }

  decreaseLine(lineId: string): boolean {
    const line = this.editableLine(lineId);
    if (!line) {
      return false;
    }

    if (line.quantity <= 1) {
      this.lines.splice(this.lines.indexOf(line), 1);
    } else {
      line.quantity -= 1;
      this.normalizeDiscountAfterGrossChange(line);
    }
    this.finishMutation();
    return true;
  }

  setLineQuantity(lineId: string, quantity: number): boolean {
    const line = this.editableLine(lineId);
    if (!line || !PricingCart.isPositiveQuantity(quantity)) {
      return false;
    }

    this.assertGrossSafe(quantity, line.unitPriceCents);
    line.quantity = quantity;
    this.normalizeDiscountAfterGrossChange(line);
    this.finishMutation();
    return true;
  }

  setLineUnitPrice(lineId: string, unitPrice: Money): boolean {
    const line = this.editableLine(lineId);
    if (
      !line ||
      unitPrice.currency !== "AUD" ||
      !Number.isSafeInteger(unitPrice.cents) ||
      unitPrice.cents < 0
    ) {
      return false;
    }

    this.assertGrossSafe(line.quantity, unitPrice.cents);
    line.unitPriceCents = unitPrice.cents;
    if (line.basePriceSource !== "open-item") {
      line.basePriceSource = "manual";
    }
    this.normalizeDiscountAfterGrossChange(line);
    this.finishMutation();
    return true;
  }

  setLineDiscountAmount(lineId: string, discount: Money): boolean {
    const line = this.editableLine(lineId);
    if (
      !line ||
      discount.currency !== "AUD" ||
      !Number.isSafeInteger(discount.cents) ||
      discount.cents < 0 ||
      discount.cents > this.lineGross(line)
    ) {
      return false;
    }

    line.discountState =
      discount.cents === 0
        ? NONE_DISCOUNT
        : { kind: "manual-amount", cents: discount.cents };
    this.finishMutation();
    return true;
  }

  setLineDiscountPercentBps(
    lineId: string,
    basisPoints: number,
  ): boolean {
    const line = this.editableLine(lineId);
    if (
      !line ||
      !Number.isSafeInteger(basisPoints) ||
      basisPoints < 0 ||
      basisPoints > 10_000
    ) {
      return false;
    }

    line.discountState =
      basisPoints === 0
        ? NONE_DISCOUNT
        : { kind: "manual-percent", basisPoints };
    this.finishMutation();
    return true;
  }

  applyQuickLineDiscount(
    lineId: string,
    basisPoints: QuickDiscountBasisPoints,
  ): boolean {
    if (
      basisPoints !== 1_000 &&
      basisPoints !== 2_000 &&
      basisPoints !== 3_000 &&
      basisPoints !== 4_000 &&
      basisPoints !== 5_000
    ) {
      return false;
    }
    return this.setLineDiscountPercentBps(lineId, basisPoints);
  }

  setOrderDiscountAmount(discount: Money): boolean {
    if (
      this.lines.length === 0 ||
      this.lines.some((line) => line.kind === "return") ||
      discount.currency !== "AUD" ||
      !Number.isSafeInteger(discount.cents) ||
      discount.cents < 0
    ) {
      return false;
    }

    const totalGross = this.totalGross();
    if (discount.cents > totalGross) {
      return false;
    }

    this.applyOrderDiscount(discount.cents, totalGross);
    this.finishMutation();
    return true;
  }

  setOrderDiscountPercentBps(basisPoints: number): boolean {
    if (
      this.lines.length === 0 ||
      this.lines.some((line) => line.kind === "return") ||
      !Number.isSafeInteger(basisPoints) ||
      basisPoints < 0 ||
      basisPoints > 10_000
    ) {
      return false;
    }

    const totalGross = this.totalGross();
    const discountCents = roundProductRatio(
      totalGross,
      basisPoints,
      10_000,
      "order percentage discount",
    );
    this.applyOrderDiscount(
      discountCents,
      totalGross,
      basisPoints > 0,
    );
    this.finishMutation();
    return true;
  }

  applyQuickOrderDiscount(
    basisPoints: QuickDiscountBasisPoints,
  ): boolean {
    if (
      basisPoints !== 1_000 &&
      basisPoints !== 2_000 &&
      basisPoints !== 3_000 &&
      basisPoints !== 4_000 &&
      basisPoints !== 5_000
    ) {
      return false;
    }
    return this.setOrderDiscountPercentBps(basisPoints);
  }

  setPromotions(
    promotions: readonly PromotionDefinition[],
    asOfIso: string,
  ): void {
    this.promotions = canonicalPromotions(promotions);
    this.asOfIso = canonicalIso(asOfIso, "cart as-of timestamp");
    this.finishMutation();
  }

  snapshot(): PricingCartSnapshot {
    const lines = this.lines.map((line) => {
      const gross = this.lineGross(line);
      const discountSource = this.discountSource(line, gross);
      const discount = this.lineDiscount(line, gross, discountSource);
      const actual =
        line.kind === "return" ? -gross : gross - discount;
      return {
        lineId: line.lineId,
        productCode: line.productCode,
        itemNumber: line.itemNumber,
        lookupCode: line.lookupCode,
        displayName: line.displayName,
        quantity: String(line.quantity),
        unitPrice: createAud(line.unitPriceCents),
        discount: createAud(discount),
        actualAmount: createAud(actual),
        discountSource,
        priceSource:
          discountSource === "promotion"
            ? ("promotion" as const)
            : line.basePriceSource,
        ...(line.syncProvenance === undefined
          ? {}
          : {
              syncProvenance: normalizeLineSyncProvenance(
                line.syncProvenance,
              ),
            }),
        kind: line.kind,
        returnSourceKey: line.returnSourceKey,
        originalOrderGuid: line.originalOrderGuid,
        originalOrderDetailGuid: line.originalOrderDetailGuid,
      };
    });

    return {
      revision: this.revision,
      mode: this.mode,
      lines,
      subtotal: createAud(
        sumSafe(
          this.lines.map((line) => {
            const gross = this.lineGross(line);
            return line.kind === "return" ? -gross : gross;
          }),
          "cart subtotal",
        ),
      ),
      discount: createAud(
        sumSafe(
          this.lines.map((line) => this.lineDiscount(line)),
          "cart discount",
        ),
      ),
      actualAmount: createAud(
        sumSafe(
          lines.map((line) => line.actualAmount.cents),
          "cart actual amount",
        ),
      ),
    };
  }

  stateSnapshot(): PricingCartStateSnapshot {
    return {
      revision: this.revision,
      mode: this.mode,
      asOfIso: this.asOfIso,
      promotions: this.promotions.map(clonePromotion),
      lines: this.lines.map(cloneLineState),
    };
  }

  private static isPositiveQuantity(quantity: number): boolean {
    return Number.isSafeInteger(quantity) && quantity > 0;
  }

  /** 恢复入口接受 frozen SharedSaleCartV1 的正有限小数（称重商品）；
   *  普通加购/改数量仍保持整数语义（isPositiveQuantity）。 */
  private static isRestorableQuantity(quantity: number): boolean {
    return Number.isFinite(quantity) && quantity > 0;
  }

  private appendItem(
    input: AddCartItemInput,
    quantity: number,
    syncProvenance: LineSyncProvenance,
    kind: CartLineKind,
  ): string {
    this.assertUniqueLineId(input.lineId);
    const unitPriceCents = assertAudCents(
      input.unitPrice,
      "cart unit price",
      0,
    );
    const priceSource = input.priceSource ?? "catalog";
    if (priceSource !== "catalog" && priceSource !== "manual") {
      throw new TypeError("cart item price source is invalid");
    }
    const catalogDiscountBasisPoints =
      assertCatalogDiscountBasisPoints(
        input.catalogDiscountBasisPoints ?? 0,
        "cart catalog discount basis points",
      );
    if (kind !== "sale" && catalogDiscountBasisPoints > 0) {
      throw new TypeError(
        "return and open-item lines cannot contain a catalog discount",
      );
    }
    this.assertGrossSafe(quantity, unitPriceCents);

    this.lines.push({
      lineId: input.lineId,
      productCode: input.productCode,
      itemNumber: input.itemNumber,
      lookupCode: input.lookupCode,
      displayName: input.displayName,
      quantity,
      unitPriceCents,
      basePriceSource: priceSource,
      syncProvenance,
      kind,
      returnSourceKey: input.returnSourceKey ?? null,
      originalOrderGuid: input.originalOrderGuid ?? null,
      originalOrderDetailGuid: input.originalOrderDetailGuid ?? null,
      catalogDiscountBasisPoints,
      discountState: NONE_DISCOUNT,
    });
    this.finishMutation();
    return input.lineId;
  }

  private planCompatibleLineMerge(): Readonly<{
    lines: MutablePricingLine[];
    groups: MergeCompatibleCartLinesResult["groups"];
  }> {
    const baseline = this.snapshot();
    let workingLines = this.lines.map(cloneMutableLine);
    const candidateGroups = this.compatibleLineGroups(workingLines).filter(
      (group) => this.groupPreservesCartAmounts(group),
    );
    if (candidateGroups.length === 0) {
      return { lines: workingLines, groups: [] };
    }
    const batched = this.simulateCompatibleGroupsMerge(
      workingLines,
      candidateGroups,
    );
    if (this.hasSameCartAmounts(baseline, batched.snapshot)) {
      return { lines: batched.lines, groups: batched.groups };
    }

    const mergedGroups: {
      keptLineId: string;
      removedLineIds: string[];
    }[] = [];
    for (const group of candidateGroups) {
      const candidate = this.simulateCompatibleGroupMerge(
        workingLines,
        group,
      );
      if (
        candidate === null ||
        !this.hasSameCartAmounts(baseline, candidate.snapshot)
      ) {
        continue;
      }

      workingLines = candidate.lines;
      mergedGroups.push(candidate.group);
    }

    return { lines: workingLines, groups: mergedGroups };
  }

  private simulateCompatibleGroupsMerge(
    lines: readonly MutablePricingLine[],
    groups: readonly (readonly MutablePricingLine[])[],
  ): Readonly<{
    lines: MutablePricingLine[];
    snapshot: CartSnapshot;
    groups: MergeCompatibleCartLinesResult["groups"];
  }> {
    const candidate = new PricingCart({
      mode: this.mode,
      asOfIso: this.asOfIso,
      promotions: this.promotions,
    });
    candidate.revision = this.revision;
    candidate.lines = lines.map(cloneMutableLine);
    const candidateById = new Map(
      candidate.lines.map((line) => [line.lineId, line] as const),
    );
    const removedLineIds = new Set<string>();
    const mergedGroups: {
      keptLineId: string;
      removedLineIds: string[];
    }[] = [];

    for (const group of groups) {
      const mergedDiscount = this.mergedDiscountState(group);
      if (mergedDiscount === null) continue;
      const keptLineId = group[0]!.lineId;
      const removedIds = group.slice(1).map((line) => line.lineId);
      const kept = candidateById.get(keptLineId);
      if (!kept) continue;
      kept.quantity = sumSafe(
        group.map((line) => line.quantity),
        "merged compatible cart quantity",
      );
      candidate.assertGrossSafe(kept.quantity, kept.unitPriceCents);
      kept.discountState = mergedDiscount;
      for (const lineId of removedIds) removedLineIds.add(lineId);
      mergedGroups.push({
        keptLineId,
        removedLineIds: removedIds,
      });
    }

    candidate.lines = candidate.lines.filter(
      (line) => !removedLineIds.has(line.lineId),
    );
    candidate.refreshPromotionDiscounts();
    return {
      lines: candidate.lines.map(cloneMutableLine),
      snapshot: candidate.snapshot(),
      groups: mergedGroups,
    };
  }

  private compatibleLineGroups(
    lines: readonly MutablePricingLine[],
  ): MutablePricingLine[][] {
    const groupsByKey = new Map<string, MutablePricingLine[]>();
    for (const line of lines) {
      if (
        line.kind !== "sale" ||
        line.basePriceSource === "open-item" ||
        // 冻结共享挂单允许称重小数；现有兼容合并使用整数 BigInt 求和，
        // 因此小数行保持原样，避免销售页能力探测同步抛错。
        !Number.isSafeInteger(line.quantity)
      ) {
        continue;
      }
      const provenance =
        line.syncProvenance === undefined
          ? ["missing"]
          : [
              "present",
              line.syncProvenance.referenceCode,
              line.syncProvenance.priceSource,
            ];
      const discountKey =
        line.discountState.kind === "none" ||
        line.discountState.kind === "promotion"
          ? ["automatic"]
          : line.discountState.kind === "manual-amount"
            ? ["manual-amount"]
            : [
                "manual-percent",
                line.discountState.basisPoints,
              ];
      const key = JSON.stringify([
        normalizeLookupCode(line.lookupCode),
        normalizeProductCode(line.productCode),
        line.unitPriceCents,
        line.basePriceSource,
        line.catalogDiscountBasisPoints,
        provenance,
        discountKey,
      ]);
      const group = groupsByKey.get(key);
      if (group) {
        group.push(line);
      } else {
        groupsByKey.set(key, [line]);
      }
    }
    return [...groupsByKey.values()].filter((group) => group.length > 1);
  }

  private simulateCompatibleGroupMerge(
    lines: readonly MutablePricingLine[],
    groupLines: readonly MutablePricingLine[],
  ): Readonly<{
    lines: MutablePricingLine[];
    snapshot: CartSnapshot;
    group: {
      keptLineId: string;
      removedLineIds: string[];
    };
  }> | null {
    const mergedDiscount = this.mergedDiscountState(groupLines);
    if (mergedDiscount === null) return null;
    const keptLineId = groupLines[0]!.lineId;
    const removedLineIds = groupLines
      .slice(1)
      .map((line) => line.lineId);
    const removed = new Set(removedLineIds);
    const candidate = new PricingCart({
      mode: this.mode,
      asOfIso: this.asOfIso,
      promotions: this.promotions,
    });
    candidate.revision = this.revision;
    candidate.lines = lines
      .filter((line) => !removed.has(line.lineId))
      .map(cloneMutableLine);
    const kept = candidate.lines.find(
      (line) => line.lineId === keptLineId,
    );
    if (!kept) return null;
    kept.quantity = sumSafe(
      groupLines.map((line) => line.quantity),
      "merged compatible cart quantity",
    );
    candidate.assertGrossSafe(kept.quantity, kept.unitPriceCents);
    kept.discountState = mergedDiscount;
    candidate.refreshPromotionDiscounts();
    return {
      lines: candidate.lines.map(cloneMutableLine),
      snapshot: candidate.snapshot(),
      group: { keptLineId, removedLineIds },
    };
  }

  private hasSameCartAmounts(
    left: CartSnapshot,
    right: CartSnapshot,
  ): boolean {
    return (
      left.subtotal.cents === right.subtotal.cents &&
      left.discount.cents === right.discount.cents &&
      left.actualAmount.cents === right.actualAmount.cents
    );
  }

  /**
   * 同商品、价格及来源的行合并后小计恒定；固定折扣可直接求和，
   * 自动促销按相同总数量重算也保持订单级折扣。百分比折扣只需防守分币舍入差。
   */
  private groupPreservesCartAmounts(
    lines: readonly MutablePricingLine[],
  ): boolean {
    const mergedDiscount = this.mergedDiscountState(lines);
    if (mergedDiscount === null) return false;

    if (mergedDiscount.kind === "none") {
      const catalogDiscountBasisPoints =
        lines[0]!.catalogDiscountBasisPoints;
      if (catalogDiscountBasisPoints === 0) return true;
      const currentDiscount = sumSafe(
        lines.map((line) => this.lineDiscount(line)),
        "compatible catalog discount",
      );
      const mergedQuantity = sumSafe(
        lines.map((line) => line.quantity),
        "compatible catalog quantity",
      );
      const mergedGross = multiplySafe(
        mergedQuantity,
        lines[0]!.unitPriceCents,
        "compatible catalog gross",
      );
      const mergedDiscountCents = roundProductRatio(
        mergedGross,
        catalogDiscountBasisPoints,
        10_000,
        "compatible catalog discount",
      );
      return currentDiscount === mergedDiscountCents;
    }
    if (mergedDiscount.kind !== "manual-percent") return true;

    const currentDiscount = sumSafe(
      lines.map((line) => this.lineDiscount(line)),
      "compatible percentage discount",
    );
    const mergedQuantity = sumSafe(
      lines.map((line) => line.quantity),
      "compatible percentage quantity",
    );
    const mergedGross = multiplySafe(
      mergedQuantity,
      lines[0]!.unitPriceCents,
      "compatible percentage gross",
    );
    const mergedDiscountCents = roundProductRatio(
      mergedGross,
      mergedDiscount.basisPoints,
      10_000,
      "compatible percentage discount",
    );
    return currentDiscount === mergedDiscountCents;
  }

  private mergedDiscountState(
    lines: readonly MutablePricingLine[],
  ): PricingDiscountState | null {
    if (
      lines.every(
        (line) =>
          line.discountState.kind === "none" ||
          line.discountState.kind === "promotion",
      )
    ) {
      return NONE_DISCOUNT;
    }
    if (
      lines.every(
        (line) => line.discountState.kind === "manual-amount",
      )
    ) {
      return {
        kind: "manual-amount",
        cents: sumSafe(
          lines.map((line) =>
            line.discountState.kind === "manual-amount"
              ? line.discountState.cents
              : 0,
          ),
          "merged fixed cart discount",
        ),
      };
    }
    if (
      lines.every(
        (line) => line.discountState.kind === "manual-percent",
      )
    ) {
      const first = lines[0]!.discountState;
      if (
        first.kind === "manual-percent" &&
        lines.every(
          (line) =>
            line.discountState.kind === "manual-percent" &&
            line.discountState.basisPoints === first.basisPoints,
        )
      ) {
        return {
          kind: "manual-percent",
          basisPoints: first.basisPoints,
        };
      }
    }
    return null;
  }

  private assertNewLineInput(
    input: {
      lineId: string;
      productCode: string;
      lookupCode: string;
      displayName: string;
    },
    quantity: number,
  ): void {
    assertNonBlank(input.lineId, "cart line id");
    assertNonBlank(input.productCode, "cart product code");
    assertNonBlank(input.displayName, "cart display name");
    if (!PricingCart.isPositiveQuantity(quantity)) {
      throw new TypeError("cart item quantity must be a positive integer");
    }
  }

  private assertUniqueLineId(lineId: string): void {
    if (this.lines.some((line) => line.lineId === lineId)) {
      throw new TypeError(`duplicate cart line id: ${lineId}`);
    }
  }

  private editableLine(lineId: string): MutablePricingLine | undefined {
    const line = this.lines.find((candidate) => candidate.lineId === lineId);
    return line?.kind === "return" ? undefined : line;
  }

  private assertGrossSafe(quantity: number, unitPriceCents: number): void {
    multiplySafe(quantity, unitPriceCents, "cart line gross");
  }

  private lineGross(line: MutablePricingLine): number {
    return multiplySafe(
      line.quantity,
      line.unitPriceCents,
      "cart line gross",
    );
  }

  private discountSource(
    line: MutablePricingLine,
    gross = this.lineGross(line),
  ): PricingCartDiscountSource {
    if (line.kind === "return") {
      return "none";
    }

    if (
      (line.discountState.kind === "manual-amount" &&
        line.discountState.cents <= gross) ||
      (line.discountState.kind === "manual-percent" &&
        line.discountState.basisPoints > 0)
    ) {
      return "manual";
    }
    if (
      line.basePriceSource !== "open-item" &&
      line.catalogDiscountBasisPoints > 0
    ) {
      return "catalog";
    }
    if (
      line.discountState.kind === "promotion" &&
      line.discountState.cents > 0
    ) {
      return "promotion";
    }
    return "none";
  }

  private lineDiscount(
    line: MutablePricingLine,
    gross = this.lineGross(line),
    source = this.discountSource(line, gross),
  ): number {
    switch (source) {
      case "none":
        return 0;
      case "catalog":
        return Math.min(
          gross,
          roundProductRatio(
            gross,
            line.catalogDiscountBasisPoints,
            10_000,
            "catalog percentage discount",
          ),
        );
      case "manual":
        if (line.discountState.kind === "manual-amount") {
          return Math.min(gross, line.discountState.cents);
        }
        if (line.discountState.kind === "manual-percent") {
          return Math.min(
            gross,
            roundProductRatio(
              gross,
              line.discountState.basisPoints,
              10_000,
              "line percentage discount",
            ),
          );
        }
        return 0;
      case "promotion":
        return Math.min(
          gross,
          line.discountState.kind === "promotion"
            ? Math.max(0, line.discountState.cents)
            : 0,
        );
    }
  }

  private normalizeDiscountAfterGrossChange(
    line: MutablePricingLine,
  ): void {
    const gross = this.lineGross(line);
    if (line.discountState.kind === "manual-amount") {
      const cents = Math.min(gross, line.discountState.cents);
      // manual-amount: 0 是整单折扣分摊为零的覆盖哨兵，不能归一化为 none；
      // 用户主动清除行折扣仍由 setLineDiscountAmount(0) 写入 none。
      line.discountState = { kind: "manual-amount", cents };
    } else if (line.discountState.kind === "promotion") {
      line.discountState = NONE_DISCOUNT;
    }
  }

  private finishMutation(): void {
    this.refreshPromotionDiscounts();
    this.revision = sumSafe([this.revision, 1], "cart revision");
  }

  private totalGross(): number {
    return sumSafe(
      this.lines.map((line) => {
        const gross = this.lineGross(line);
        return line.kind === "return" ? -gross : gross;
      }),
      "cart gross total",
    );
  }

  private applyOrderDiscount(
    discountCents: number,
    totalGross: number,
    preserveManualOverrideWhenZero = false,
  ): void {
    let remainingDiscount = Math.min(
      totalGross,
      Math.max(0, discountCents),
    );
    const discountable = this.lines.filter(
      (line) => this.lineGross(line) > 0,
    );

    if (remainingDiscount === 0) {
      for (const line of discountable) {
        // 非零整单百分比可能舍入为 0 分；此时仍是明确的人工覆盖，
        // 只有用户输入 0 才清除并恢复 catalog 折扣。
        line.discountState = preserveManualOverrideWhenZero
          ? { kind: "manual-amount", cents: 0 }
          : NONE_DISCOUNT;
      }
      return;
    }

    for (let index = 0; index < discountable.length; index += 1) {
      const line = discountable[index]!;
      const gross = this.lineGross(line);
      const proposed =
        index === discountable.length - 1
          ? remainingDiscount
          : roundProductRatio(
              discountCents,
              gross,
              totalGross,
              "order discount allocation",
            );
      const lineDiscount = Math.min(
        gross,
        remainingDiscount,
        Math.max(0, proposed),
      );
      // 即使按比例分到 0 分，也必须保留人工整单覆盖的来源，
      // 否则 catalog 折扣会重新生效并突破订单折扣总额。
      line.discountState = { kind: "manual-amount", cents: lineDiscount };
      remainingDiscount -= lineDiscount;
    }
  }

  private validateRestoredDiscount(line: MutablePricingLine): void {
    const gross = this.lineGross(line);
    const state = line.discountState;
    if (line.kind === "return" && state.kind !== "none") {
      throw new TypeError("return line cannot contain a discount");
    }

    if (
      state.kind === "manual-amount" ||
      state.kind === "promotion"
    ) {
      assertSafeInteger(state.cents, "restored line discount");
      if (state.cents < 0 || state.cents > gross) {
        throw new RangeError("restored line discount is out of range");
      }
    } else if (
      state.kind === "manual-percent" &&
      (!Number.isSafeInteger(state.basisPoints) ||
        state.basisPoints < 0 ||
        state.basisPoints > 10_000)
    ) {
      throw new RangeError(
        "restored line discount percentage is out of range",
      );
    }

    if (
      state.kind === "promotion" &&
      (line.catalogDiscountBasisPoints > 0 ||
        line.basePriceSource === "open-item" ||
        state.promotionIds.some((id) => id.trim().length === 0))
    ) {
      throw new TypeError(
        line.catalogDiscountBasisPoints > 0
          ? "catalog discount cannot coexist with restored promotion discount"
          : "restored promotion discount is invalid",
      );
    }
  }

  private refreshPromotionDiscounts(): void {
    for (const line of this.lines) {
      if (line.discountState.kind === "promotion") {
        line.discountState = NONE_DISCOUNT;
      }
    }

    const eligibleLines = this.lines.filter((line) => {
      const gross = this.lineGross(line);
      const hasManualDiscount =
        line.discountState.kind === "manual-amount" ||
        (line.discountState.kind === "manual-percent" &&
          line.discountState.basisPoints > 0);
      return (
        line.kind === "sale" &&
        line.basePriceSource !== "open-item" &&
        !hasManualDiscount &&
        line.catalogDiscountBasisPoints === 0 &&
        line.unitPriceCents > 0 &&
        gross > 0
      );
    });
    if (eligibleLines.length === 0) {
      return;
    }

    const asOf = Date.parse(this.asOfIso);
    const applicableRules = this.promotions.filter((promotion) => {
      if (
        promotion.applyQuantity <= 0 ||
        promotion.products.length === 0 ||
        Date.parse(promotion.effectiveStartIso) > asOf ||
        Date.parse(promotion.effectiveEndIso) < asOf
      ) {
        return false;
      }

      const productCodes = new Set(
        promotion.products.map((product) =>
          normalizeProductCode(product.productCode),
        ),
      );
      return eligibleLines.some((line) =>
        productCodes.has(normalizeProductCode(line.productCode)),
      );
    });
    if (applicableRules.length === 0) {
      return;
    }

    const exclusive = applicableRules.find(
      (promotion) => promotion.isExclusive,
    );
    const rulesToEvaluate = exclusive
      ? [exclusive]
      : applicableRules.filter((promotion) => !promotion.isExclusive);
    const allocations = new Map<
      MutablePricingLine,
      PromotionLineAllocation
    >();

    for (const promotion of rulesToEvaluate) {
      this.evaluatePromotion(promotion, eligibleLines, allocations);
    }

    for (const [line, allocation] of allocations) {
      if (allocation.cents <= 0) {
        continue;
      }
      line.discountState = {
        kind: "promotion",
        cents: Math.min(this.lineGross(line), allocation.cents),
        promotionIds: [...allocation.promotionIds],
      };
    }
  }

  private evaluatePromotion(
    promotion: PromotionDefinition,
    eligibleLines: readonly MutablePricingLine[],
    allocations: Map<MutablePricingLine, PromotionLineAllocation>,
  ): void {
    const productWeights = new Map<string, number>();
    for (const product of promotion.products) {
      const productCode = normalizeProductCode(product.productCode);
      if (productCode.length > 0) {
        productWeights.set(
          productCode,
          product.unitWeight > 0 ? product.unitWeight : 1,
        );
      }
    }
    if (productWeights.size === 0) {
      return;
    }

    const units: PromotionUnit[] = [];
    for (const line of eligibleLines) {
      const unitWeight = productWeights.get(
        normalizeProductCode(line.productCode),
      );
      if (unitWeight === undefined) {
        continue;
      }

      for (
        let quantityIndex = 0;
        quantityIndex < line.quantity;
        quantityIndex += 1
      ) {
        for (
          let weightIndex = 0;
          weightIndex < unitWeight;
          weightIndex += 1
        ) {
          units.push({
            line,
            quantityIndex,
            selectedIndex: units.length,
          });
        }
      }
    }

    let applicationCount = Math.floor(
      units.length / promotion.applyQuantity,
    );
    if (promotion.maxApplicationsPerOrder !== null) {
      applicationCount = Math.min(
        applicationCount,
        promotion.maxApplicationsPerOrder,
      );
    }

    for (
      let applicationIndex = 0;
      applicationIndex < applicationCount;
      applicationIndex += 1
    ) {
      const start = applicationIndex * promotion.applyQuantity;
      this.addPromotionGroupDiscount(
        promotion,
        units.slice(start, start + promotion.applyQuantity),
        allocations,
      );
    }
  }

  private addPromotionGroupDiscount(
    promotion: PromotionDefinition,
    selectedUnits: readonly PromotionUnit[],
    allocations: Map<MutablePricingLine, PromotionLineAllocation>,
  ): void {
    const physicalUnits = new Map<
      string,
      PromotionUnit & { firstSelectedIndex: number }
    >();
    selectedUnits.forEach((unit, selectedIndex) => {
      const key = `${unit.line.lineId}\u0000${unit.quantityIndex}`;
      if (!physicalUnits.has(key)) {
        physicalUnits.set(key, {
          ...unit,
          firstSelectedIndex: selectedIndex,
        });
      }
    });

    const sortedPhysicalUnits = [...physicalUnits.values()].sort(
      (left, right) =>
        left.quantityIndex - right.quantityIndex ||
        left.firstSelectedIndex - right.firstSelectedIndex,
    );
    const groupedLines = new Map<
      MutablePricingLine,
      {
        amount: number;
        sortOrder: number;
        firstSelectedIndex: number;
      }
    >();
    for (const unit of sortedPhysicalUnits) {
      const current = groupedLines.get(unit.line);
      if (current) {
        current.amount = sumSafe(
          [current.amount, unit.line.unitPriceCents],
          "promotion group amount",
        );
        current.sortOrder = Math.min(
          current.sortOrder,
          unit.quantityIndex,
        );
      } else {
        groupedLines.set(unit.line, {
          amount: unit.line.unitPriceCents,
          sortOrder: unit.quantityIndex,
          firstSelectedIndex: unit.firstSelectedIndex,
        });
      }
    }

    const candidates = [...groupedLines.entries()]
      .map(([line, group]) => {
        const currentDiscount = allocations.get(line)?.cents ?? 0;
        return {
          line,
          ...group,
          capacity: Math.max(
            0,
            this.lineGross(line) - currentDiscount,
          ),
        };
      })
      .filter((candidate) => candidate.capacity > 0)
      .sort(
        (left, right) =>
          left.sortOrder - right.sortOrder ||
          left.firstSelectedIndex - right.firstSelectedIndex,
      );
    if (candidates.length === 0) {
      return;
    }

    const groupTotal = sumSafe(
      candidates.map((candidate) => candidate.amount),
      "promotion group total",
    );
    let remainingDiscount =
      groupTotal - promotion.fixedPrice.cents;
    if (remainingDiscount <= 0) {
      return;
    }

    for (
      let index = 0;
      index < candidates.length && remainingDiscount > 0;
      index += 1
    ) {
      const candidate = candidates[index]!;
      const remainingAmount = sumSafe(
        candidates
          .slice(index)
          .map((remaining) => remaining.amount),
        "promotion remaining amount",
      );
      if (remainingAmount <= 0) {
        break;
      }

      const proposed =
        index === candidates.length - 1
          ? remainingDiscount
          : roundProductRatio(
              remainingDiscount,
              candidate.amount,
              remainingAmount,
              "promotion discount allocation",
            );
      const lineDiscount = Math.min(
        remainingDiscount,
        candidate.capacity,
        Math.max(0, proposed),
      );
      if (lineDiscount <= 0) {
        continue;
      }

      const allocation = allocations.get(candidate.line) ?? {
        cents: 0,
        promotionIds: [],
      };
      allocation.cents = sumSafe(
        [allocation.cents, lineDiscount],
        "promotion line discount",
      );
      if (!allocation.promotionIds.includes(promotion.id)) {
        allocation.promotionIds.push(promotion.id);
      }
      allocations.set(candidate.line, allocation);
      remainingDiscount -= lineDiscount;
    }
  }
}
