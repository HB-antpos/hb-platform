import {
  CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT,
  createAud,
  CustomerDisplaySnapshotSchema,
  type CartSnapshot,
  type CustomerDisplaySnapshot,
  type DisplayStatus,
  type ExternalCustomerDisplayPort,
} from "@/core/contracts";
import {
  normalizeAdvertisementCacheRootUri,
  normalizeLocalAdvertisementUri,
  normalizeLocalProductImageUri,
  normalizeProductImageCacheRootUri,
} from "@/core/peripherals/customer-display/local-advertisement-uri";

export type CustomerDisplayFrame = Readonly<{
  mode: CustomerDisplaySnapshot["mode"];
  cart: CartSnapshot | null;
  changeCents: number;
  advert: CustomerDisplaySnapshot["advert"];
}>;

export type CustomerDisplayPublishResult =
  | Readonly<{ status: "published"; revision: number }>
  | Readonly<{ status: "unchanged"; revision: number }>
  | Readonly<{
      status: "failed";
      revision: number;
      errorCode: "DISPLAY_PUBLISH_FAILED";
    }>;

export type CustomerDisplayEnableResult =
  | Readonly<{ status: "updated" }>
  | Readonly<{
      status: "failed";
      errorCode: "DISPLAY_ENABLE_FAILED";
    }>;

/**
 * 商品缩略图的同步查询口。peek 只读内存索引并返回已缓存的本地 file URI，
 * 未命中返回 null（实现方可借机异步触发下载），客显层因此永不联网。
 */
export interface CustomerDisplayProductImagePort {
  peek(line: Readonly<{ productCode: string; lookupCode: string }>): string | null;
}

export type CustomerDisplayPublisherOptions = Readonly<{
  advertisementCacheRootUri?: string | null;
  /** 商品缩略图缓存根目录；缺省时快照一律不带 imageUri。 */
  productImageCacheRootUri?: string | null;
  /** 缺省时快照不带 imageUri。 */
  productImageResolver?: CustomerDisplayProductImagePort | null;
}>;

type ProductImageProjection = Readonly<{
  resolver: CustomerDisplayProductImagePort;
  rootUri: string;
}>;

let producerSessionRevision = 0;
const CUSTOMER_DISPLAY_SNAPSHOT_ITEM_LIMIT = 100;

/**
 * 从共享购物车只投影客显白名单。银行卡、顾客、收银员和授权信息不在输入面中，
 * line sync provenance 也不会进入最终 snapshot。
 */
export function buildCustomerDisplaySnapshot(
  revision: number,
  frame: CustomerDisplayFrame,
  advertisementCacheRootUri?: string | null,
  visibleItemStart?: number,
  productImages?: ProductImageProjection | null,
): CustomerDisplaySnapshot {
  if (!Number.isSafeInteger(revision) || revision < 0) {
    throw new TypeError("Customer display revision must be non-negative.");
  }
  if (!Number.isSafeInteger(frame.changeCents)) {
    throw new TypeError("Customer display change must use safe integer cents.");
  }
  const advert = normalizeLocalAdvert(
    frame.advert,
    advertisementCacheRootUri ?? null,
  );
  const cart = frame.cart;
  const totalCents = cart?.actualAmount.cents ?? 0;
  const cartLines = cart?.lines ?? [];
  const projectedLines = cartLines.slice(0, CUSTOMER_DISPLAY_SNAPSHOT_ITEM_LIMIT);
  const windowStart =
    visibleItemStart ?? defaultVisibleItemStart(projectedLines.length);
  const candidate = {
    revision,
    mode: frame.mode,
    items: projectedLines.map((line, index) => ({
      name: line.displayName,
      quantity: line.quantity,
      unitPrice: createAud(line.unitPrice.cents),
      amount: createAud(line.actualAmount.cents),
      ...projectItemDetails(line),
      // 只为当前可见窗口内的行取图：窗口外的行客显看不到，不必触发下载；
      // 窗口移动后下一次发布会对新进入窗口的行再取一次。
      ...projectItemImage(
        line,
        index >= windowStart &&
          index < windowStart + CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT,
        productImages ?? null,
      ),
    })),
    summary: {
      itemQuantity: sumFixedQuantities(cartLines.map((line) => line.quantity)),
      skuCount: cartLines.length,
      subtotal: createAud(cart?.subtotal.cents ?? 0),
    },
    visibleItemStart: windowStart,
    // 与 WPF CustomerDisplayViewModel 一致：GST 是含税应付额中的 1/11。
    gst: createAud(roundRatioAwayFromZero(totalCents, 11)),
    discount: createAud(cart?.discount.cents ?? 0),
    total: createAud(totalCents),
    change: createAud(frame.changeCents),
    advert,
  };
  return freezeSnapshot(CustomerDisplaySnapshotSchema.parse(candidate));
}

/**
 * 发布失败只影响客显状态，不向主交易流程抛出。所有发布串行化，保证原生桥看到的
 * revision 严格递增；一次结果不确定后也永不复用旧 revision。
 */
export class CustomerDisplayPublisher {
  private lastPublishedRevision = 0;
  private lastPublishedFingerprint: string | null = null;
  private lastObservedCart: CartSnapshot | null = null;
  private queue: Promise<unknown> = Promise.resolve();
  private readonly advertisementCacheRootUri: string | null;
  private readonly productImages: ProductImageProjection | null;
  private visibleItemStart = 0;

  public constructor(
    private readonly display: ExternalCustomerDisplayPort,
    options: CustomerDisplayPublisherOptions = {},
  ) {
    this.advertisementCacheRootUri =
      options.advertisementCacheRootUri === null ||
      options.advertisementCacheRootUri === undefined
        ? null
        : normalizeAdvertisementCacheRootUri(
            options.advertisementCacheRootUri,
          );
    this.productImages =
      options.productImageResolver && options.productImageCacheRootUri
        ? Object.freeze({
            resolver: options.productImageResolver,
            rootUri: normalizeProductImageCacheRootUri(
              options.productImageCacheRootUri,
            ),
          })
        : null;
  }

  public publish(
    frame: CustomerDisplayFrame,
  ): Promise<CustomerDisplayPublishResult> {
    const operation = this.queue.then(
      () => this.publishNow(frame),
      () => this.publishNow(frame),
    );
    this.queue = operation;
    return operation;
  }

  public async setEnabled(
    enabled: boolean,
  ): Promise<CustomerDisplayEnableResult> {
    try {
      await this.display.setEnabled(enabled);
      return Object.freeze({ status: "updated" as const });
    } catch {
      return Object.freeze({
        status: "failed" as const,
        errorCode: "DISPLAY_ENABLE_FAILED" as const,
      });
    }
  }

  public async getStatus(): Promise<DisplayStatus> {
    try {
      return await this.display.getStatus();
    } catch {
      return "failed";
    }
  }

  public subscribe(listener: (status: DisplayStatus) => void): () => void {
    return this.display.subscribe(listener);
  }

  private async publishNow(
    frame: CustomerDisplayFrame,
  ): Promise<CustomerDisplayPublishResult> {
    const visibleItemStart = this.resolveVisibleItemStart(frame.cart);
    const draft = buildCustomerDisplaySnapshot(
      0,
      frame,
      this.advertisementCacheRootUri,
      visibleItemStart,
      this.productImages,
    );
    const fingerprint = snapshotFingerprint(draft);
    if (fingerprint === this.lastPublishedFingerprint) {
      return Object.freeze({
        status: "unchanged" as const,
        revision: this.lastPublishedRevision,
      });
    }
    const revision = allocateProducerSessionRevision();
    const snapshot = buildCustomerDisplaySnapshot(
      revision,
      frame,
      this.advertisementCacheRootUri,
      visibleItemStart,
      this.productImages,
    );
    try {
      await this.display.publish(snapshot);
      this.lastPublishedFingerprint = fingerprint;
      this.lastPublishedRevision = revision;
      return Object.freeze({
        status: "published" as const,
        revision,
      });
    } catch {
      return Object.freeze({
        status: "failed" as const,
        revision,
        errorCode: "DISPLAY_PUBLISH_FAILED" as const,
      });
    }
  }

  private resolveVisibleItemStart(cart: CartSnapshot | null): number {
    // 使用完整购物车判断变化，避免删除前 100 行时把边界补入项误判为新增。
    const currentLines = cart?.lines ?? [];
    const previousLines = this.lastObservedCart?.lines ?? [];
    const visibleItemCount = Math.min(
      currentLines.length,
      CUSTOMER_DISPLAY_SNAPSHOT_ITEM_LIMIT,
    );
    const maximumStart = defaultVisibleItemStart(visibleItemCount);
    let nextStart = Math.min(this.visibleItemStart, maximumStart);

    if (currentLines.length === 0) {
      nextStart = 0;
    } else if (previousLines.length === 0) {
      nextStart = maximumStart;
    } else {
      const targetIndex = recentlyChangedItemIndex(
        previousLines,
        currentLines,
      );
      if (targetIndex !== null && targetIndex < visibleItemCount) {
        if (targetIndex < nextStart) {
          nextStart = targetIndex;
        } else if (
          targetIndex >=
          nextStart + CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT
        ) {
          nextStart =
            targetIndex - CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT + 1;
        }
      }
      nextStart = Math.max(0, Math.min(nextStart, maximumStart));
    }

    this.lastObservedCart = cart;
    this.visibleItemStart = nextStart;
    return nextStart;
  }
}

type ProjectedCartLine = CartSnapshot["lines"][number];

const MAX_ITEM_CODE_LENGTH = 64;

/**
 * 对齐 WPF 客显商品行：货号、条码以及折扣前原价/折扣率。
 * 所有可选字段缺失时返回空对象，旧版消费方看到的快照不变。
 */
function projectItemDetails(line: ProjectedCartLine): {
  itemNumber?: string;
  lookupCode?: string;
  grossAmount?: ReturnType<typeof createAud>;
  discountRate?: string;
} {
  const details: {
    itemNumber?: string;
    lookupCode?: string;
    grossAmount?: ReturnType<typeof createAud>;
    discountRate?: string;
  } = {};
  const itemNumber = normalizeItemCode(line.itemNumber);
  if (itemNumber !== null) details.itemNumber = itemNumber;
  const lookupCode = normalizeItemCode(line.lookupCode);
  if (lookupCode !== null) details.lookupCode = lookupCode;

  const discountCents = line.discount.cents;
  if (Number.isSafeInteger(discountCents) && discountCents > 0) {
    // 金额符号约定：销售行 actualAmount = 折前 - 折扣（>=0），退货行折扣恒为 0 且
    // actualAmount 为负。折前金额 = |实收| + 折扣，符号与 actualAmount 一致。
    const actualCents = line.actualAmount.cents;
    const grossMagnitude = Math.abs(actualCents) + discountCents;
    if (Number.isSafeInteger(grossMagnitude) && grossMagnitude > 0) {
      const discountRate = formatDiscountRate(discountCents, grossMagnitude);
      // 折扣率四舍五入后为 0（例如大额订单上 1 分钱折扣）时展示 "-0%" 没有意义，整体省略。
      if (discountRate !== null) {
        details.grossAmount = createAud(
          actualCents < 0 ? -grossMagnitude : grossMagnitude,
        );
        details.discountRate = discountRate;
      }
    }
  }
  return details;
}

function normalizeItemCode(value: string | null | undefined): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (trimmed.length === 0) return null;
  // 契约上限 64；宁可截断也不能因为超长字段让整帧快照校验失败。
  return trimmed.slice(0, MAX_ITEM_CODE_LENGTH);
}

/**
 * 折扣率 = 折扣 / 折前 * 100，四舍五入到两位小数并去掉末尾 0（对应 WPF `{rate:0.##}`）。
 * 用 BigInt 整数运算，避免浮点误差；结果为 0 时返回 null。
 */
function formatDiscountRate(
  discountCents: number,
  grossCents: number,
): string | null {
  const discount = BigInt(discountCents);
  const gross = BigInt(grossCents);
  // 百分之一个百分点（0.01%）为单位，四舍五入（半数进位）。
  const hundredths = (discount * 20_000n + gross) / (2n * gross);
  if (hundredths <= 0n) return null;
  const whole = hundredths / 100n;
  const fraction = String(hundredths % 100n)
    .padStart(2, "0")
    .replace(/0+$/, "");
  return `${whole}${fraction.length > 0 ? `.${fraction}` : ""}`;
}

/**
 * 从注入的缩略图解析器同步取本地 file URI。解析器抛错或返回的地址不在
 * 商品图缓存目录内时一律当作无图，绝不让缩略图问题拖垮整帧客显快照。
 */
function projectItemImage(
  line: ProjectedCartLine,
  visible: boolean,
  productImages: ProductImageProjection | null,
): { imageUri?: string } {
  if (!visible || productImages === null) return {};
  try {
    const peeked = productImages.resolver.peek({
      productCode: line.productCode,
      lookupCode: line.lookupCode,
    });
    if (peeked === null || peeked === undefined) return {};
    return {
      imageUri: normalizeLocalProductImageUri(peeked, productImages.rootUri),
    };
  } catch {
    return {};
  }
}

function defaultVisibleItemStart(itemCount: number): number {
  return Math.max(0, itemCount - CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT);
}

function recentlyChangedItemIndex(
  previous: readonly ProjectedCartLine[],
  current: readonly ProjectedCartLine[],
): number | null {
  const previousById = new Map(
    previous.map((line) => [line.lineId, line] as const),
  );
  const currentIndexById = new Map(
    current.map((line, index) => [line.lineId, index] as const),
  );
  const changedCurrentIndexes: number[] = [];

  for (let index = 0; index < current.length; index += 1) {
    const line = current[index]!;
    const before = previousById.get(line.lineId);
    if (before === undefined || customerVisibleLineChanged(before, line)) {
      changedCurrentIndexes.push(index);
    }
  }

  for (let index = 0; index < previous.length; index += 1) {
    if (currentIndexById.has(previous[index]!.lineId)) continue;

    // 删除后优先显示原位置的下一项；删除末项时回到新的最后一项。
    let adjacentIndex: number | null = null;
    for (
      let successorIndex = index + 1;
      successorIndex < previous.length;
      successorIndex += 1
    ) {
      const currentIndex = currentIndexById.get(
        previous[successorIndex]!.lineId,
      );
      if (currentIndex !== undefined) {
        adjacentIndex = currentIndex;
        break;
      }
    }
    if (adjacentIndex !== null) {
      changedCurrentIndexes.push(adjacentIndex);
    } else if (current.length > 0) {
      changedCurrentIndexes.push(current.length - 1);
    }
  }

  // 同一快照存在多种商品变化时，以当前购物车顺序最靠后的目标为准。
  return changedCurrentIndexes.length === 0
    ? null
    : Math.max(...changedCurrentIndexes);
}

function customerVisibleLineChanged(
  before: ProjectedCartLine,
  after: ProjectedCartLine,
): boolean {
  return (
    before.displayName !== after.displayName ||
    before.quantity !== after.quantity ||
    before.unitPrice.currency !== after.unitPrice.currency ||
    before.unitPrice.cents !== after.unitPrice.cents ||
    // 行折扣本身就是最近商品操作；即使金额重算稍晚，也应把该行带入视野。
    before.discount.currency !== after.discount.currency ||
    before.discount.cents !== after.discount.cents ||
    before.actualAmount.currency !== after.actualAmount.currency ||
    before.actualAmount.cents !== after.actualAmount.cents
  );
}

function normalizeLocalAdvert(
  advert: CustomerDisplaySnapshot["advert"],
  advertisementCacheRootUri: string | null,
): CustomerDisplaySnapshot["advert"] {
  if (advert === null) return null;
  if (advertisementCacheRootUri === null) {
    throw new TypeError("Customer display local advertisement URI is invalid.");
  }
  const localUri = normalizeLocalAdvertisementUri(
    advert.localUri,
    advertisementCacheRootUri,
  );
  return Object.freeze({ kind: advert.kind, localUri });
}

function allocateProducerSessionRevision(): number {
  if (producerSessionRevision >= Number.MAX_SAFE_INTEGER) {
    throw new RangeError("Customer display revision is exhausted.");
  }
  producerSessionRevision += 1;
  return producerSessionRevision;
}

function roundRatioAwayFromZero(value: number, denominator: number): number {
  if (
    !Number.isSafeInteger(value) ||
    !Number.isSafeInteger(denominator) ||
    denominator <= 0
  ) {
    throw new TypeError("Customer display GST inputs are invalid.");
  }
  const sign = value < 0 ? -1 : 1;
  const absolute = Math.abs(value);
  let quotient = Math.floor(absolute / denominator);
  const remainder = absolute % denominator;
  if (remainder * 2 >= denominator) quotient += 1;
  const result = quotient * sign;
  if (!Number.isSafeInteger(result)) {
    throw new TypeError("Customer display GST exceeds safe integer cents.");
  }
  return result;
}

/**
 * 数量最多三位小数，先统一换算成千分位整数再求和，避免称重商品产生
 * 0.1 + 0.2 之类的二进制浮点尾差。
 */
function sumFixedQuantities(quantities: readonly string[]): string {
  let totalThousandths = 0n;

  for (const quantity of quantities) {
    const match = /^(-?)(\d+)(?:\.(\d{1,3}))?$/.exec(quantity);
    if (match === null) {
      throw new TypeError("Customer display quantity is invalid.");
    }

    const whole = BigInt(match[2]!);
    const fraction = BigInt((match[3] ?? "").padEnd(3, "0") || "0");
    const thousandths = whole * 1_000n + fraction;
    totalThousandths += match[1] === "-" ? -thousandths : thousandths;
  }

  const sign = totalThousandths < 0n ? "-" : "";
  const absolute = totalThousandths < 0n ? -totalThousandths : totalThousandths;
  const whole = absolute / 1_000n;
  const fraction = String(absolute % 1_000n)
    .padStart(3, "0")
    .replace(/0+$/, "");
  return `${sign}${whole}${fraction.length > 0 ? `.${fraction}` : ""}`;
}

function snapshotFingerprint(snapshot: CustomerDisplaySnapshot): string {
  const { revision: _revision, ...content } = snapshot;
  void _revision;
  return JSON.stringify(content);
}

function freezeSnapshot(
  snapshot: CustomerDisplaySnapshot,
): CustomerDisplaySnapshot {
  const frozen = Object.freeze({
    ...snapshot,
    items: Object.freeze(
      snapshot.items.map((item) =>
        Object.freeze({
          ...item,
          unitPrice:
            item.unitPrice === undefined
              ? undefined
              : Object.freeze({ ...item.unitPrice }),
          amount: Object.freeze({ ...item.amount }),
          // 可选字段缺省时不写入键，保持旧快照形态（deepEqual 也不受 undefined 键影响）。
          ...(item.grossAmount === undefined
            ? {}
            : { grossAmount: Object.freeze({ ...item.grossAmount }) }),
        }),
      ),
    ),
    summary:
      snapshot.summary === undefined
        ? undefined
        : Object.freeze({
            ...snapshot.summary,
            subtotal: Object.freeze({ ...snapshot.summary.subtotal }),
          }),
    gst: Object.freeze({ ...snapshot.gst }),
    discount: Object.freeze({ ...snapshot.discount }),
    total: Object.freeze({ ...snapshot.total }),
    change: Object.freeze({ ...snapshot.change }),
    advert:
      snapshot.advert === null
        ? null
        : Object.freeze({ ...snapshot.advert }),
  });
  return frozen as unknown as CustomerDisplaySnapshot;
}
