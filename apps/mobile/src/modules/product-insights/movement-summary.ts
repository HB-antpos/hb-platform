import type {
  ProductInsightDailySales,
  ProductInsightMovement,
  ProductInsightOrder,
  ProductInsightRange,
  StoreProductInsight,
} from "./types";

/** 页面提供的预设统计周期（天）；90 天与后端默认区间一致。 */
export const PRODUCT_INSIGHT_PRESET_DAYS = [30, 90, 180] as const;
export type ProductInsightPresetDays =
  (typeof PRODUCT_INSIGHT_PRESET_DAYS)[number];
export const DEFAULT_PRODUCT_INSIGHT_PRESET: ProductInsightPresetDays = 90;

const DAY_MS = 24 * 60 * 60 * 1000;

/** 后端日期可能带时间（2026-10-30T00:00:00），统一截成业务日 YYYY-MM-DD。 */
export function dayKey(value: string) {
  return value.slice(0, 10);
}

function dayValue(day: string) {
  return Date.parse(`${dayKey(day)}T00:00:00Z`);
}

export function addDays(day: string, count: number) {
  return new Date(dayValue(day) + count * DAY_MS).toISOString().slice(0, 10);
}

/** 两个业务日相差的天数（to - from）。 */
export function diffDays(from: string, to: string) {
  return Math.round((dayValue(to) - dayValue(from)) / DAY_MS);
}

/** 区间天数含首尾：同一天为 1 天，与后端 CountDays 口径一致。 */
export function countRangeDays(range: ProductInsightRange) {
  return diffDays(range.startDate, range.endDate) + 1;
}

/** 以门店「今天」（后端默认区间的结束日）为终点倒推预设周期。 */
export function buildPresetRange(
  endDate: string,
  days: ProductInsightPresetDays,
): ProductInsightRange {
  return { startDate: addDays(endDate, -(days - 1)), endDate: dayKey(endDate) };
}

export type ProductInsightPeriod =
  | { kind: "preset"; days: ProductInsightPresetDays }
  | { kind: "custom"; range: ProductInsightRange };

/**
 * 解析请求区间：默认 90 天不传参数（由后端按门店时区定「今天」并刷新锚点）；
 * 其他预设以最近一次默认请求返回的结束日为锚点倒推，自定义原样传。
 */
export function resolvePeriodRange(
  period: ProductInsightPeriod,
  anchorEndDate: string | null,
): ProductInsightRange | undefined {
  if (period.kind === "custom") return period.range;
  if (period.days === DEFAULT_PRODUCT_INSIGHT_PRESET || !anchorEndDate)
    return undefined;
  return buildPresetRange(anchorEndDate, period.days);
}

/** 「进」的来源：本地商品取本地进货单，仓库商品取仓库出库（配货）。 */
export function inboundMovements(data: StoreProductInsight) {
  return data.sourceType === "warehouse"
    ? data.warehouse.deliveries
    : data.purchases.records;
}

function inboundQuantity(data: StoreProductInsight) {
  return data.sourceType === "warehouse"
    ? data.warehouse.deliveredQuantity
    : data.purchases.quantity;
}

function ratio(numerator: number, denominator: number) {
  return denominator > 0 ? numerator / denominator : null;
}

function isPendingOrder(order: ProductInsightOrder) {
  return order.status !== "completed";
}

export interface ProductMovementSummary {
  rangeDays: number;
  inQuantity: number;
  inDocumentCount: number;
  /** 区间内最近一次进货 / 配货；区间内没有时回退到历史记录。 */
  lastInDate: string | null;
  lastInIsHistorical: boolean;
  daysSinceLastIn: number | null;
  salesQuantity: number;
  salesAmount: number;
  activeSalesDays: number;
  /** 售罄率 = 区间销量 / 区间进货；没有进货时为 null。 */
  sellThroughRate: number | null;
  netQuantity: number;
  dailyAverage: number;
  averagePrice: number | null;
  orderedQuantity: number;
  pendingQuantity: number;
  pendingOrderCount: number;
  earliestPendingDate: string | null;
  /** 配货满足率 = 区间订单已配 / 区间订单订货（仅仓库商品）。 */
  fulfillmentRate: number | null;
}

export function buildMovementSummary(
  data: StoreProductInsight,
): ProductMovementSummary {
  const rangeDays = countRangeDays(data.range);
  const inbound = inboundMovements(data);
  const inQuantity = inboundQuantity(data);
  const inDays = inbound.map((item) => dayKey(item.date)).sort();
  const historical =
    data.sourceType === "warehouse"
      ? data.warehouse.lastDelivery
      : data.purchases.lastRecord;
  const lastInDate = inDays[inDays.length - 1] ?? (historical ? dayKey(historical.date) : null);
  const salesQuantity = data.sales.quantity;
  const orders = data.warehouse.orders;
  const pending = orders.filter(isPendingOrder);
  const orderedTotal = orders.reduce((sum, item) => sum + item.quantity, 0);
  const deliveredTotal = orders.reduce(
    (sum, item) => sum + item.deliveredQuantity,
    0,
  );
  return {
    rangeDays,
    inQuantity,
    inDocumentCount:
      data.sourceType === "warehouse"
        ? new Set(inbound.map((item) => item.documentNo)).size
        : data.purchases.documentCount,
    lastInDate,
    lastInIsHistorical: !inDays.length && Boolean(historical),
    daysSinceLastIn: lastInDate
      ? Math.max(0, diffDays(lastInDate, data.range.endDate))
      : null,
    salesQuantity,
    salesAmount: data.sales.amount,
    activeSalesDays: data.sales.records.filter((item) => item.quantity !== 0)
      .length,
    sellThroughRate: ratio(salesQuantity, inQuantity),
    netQuantity: inQuantity - salesQuantity,
    dailyAverage: rangeDays > 0 ? salesQuantity / rangeDays : 0,
    averagePrice: ratio(data.sales.amount, salesQuantity),
    orderedQuantity: data.warehouse.orderedQuantity,
    pendingQuantity: pending.reduce(
      (sum, item) => sum + Math.max(0, item.quantity - item.deliveredQuantity),
      0,
    ),
    pendingOrderCount: pending.length,
    earliestPendingDate:
      pending.map((item) => dayKey(item.date)).sort()[0] ?? null,
    fulfillmentRate: ratio(deliveredTotal, orderedTotal),
  };
}

export interface ProductMovementTrendBucket {
  startDate: string;
  endDate: string;
  salesQuantity: number;
  inQuantity: number;
  cumulativeSales: number;
  cumulativeIn: number;
  /** 最后一桶天数不足时为 true，图上淡色显示，避免误读为销量下滑。 */
  partial: boolean;
}

export interface ProductMovementTrend {
  bucketDays: number;
  buckets: ProductMovementTrendBucket[];
}

/** 最多约 26 根柱：180 天内按 7 天一桶，更长区间按 7 的倍数放宽。 */
export function buildMovementTrend(
  data: StoreProductInsight,
): ProductMovementTrend {
  const days = Math.max(1, countRangeDays(data.range));
  const bucketDays = Math.max(7, Math.ceil(days / 26 / 7) * 7);
  const count = Math.ceil(days / bucketDays);
  const buckets = Array.from({ length: count }, (_, index) => {
    const startDate = addDays(data.range.startDate, index * bucketDays);
    const last = index === count - 1;
    return {
      startDate,
      endDate: last
        ? dayKey(data.range.endDate)
        : addDays(startDate, bucketDays - 1),
      salesQuantity: 0,
      inQuantity: 0,
      cumulativeSales: 0,
      cumulativeIn: 0,
      partial: last && days % bucketDays !== 0,
    };
  });
  const indexOf = (date: string) =>
    Math.floor(diffDays(data.range.startDate, date) / bucketDays);
  for (const item of data.sales.records) {
    const bucket = buckets[indexOf(item.date)];
    if (bucket) bucket.salesQuantity += item.quantity;
  }
  for (const item of inboundMovements(data)) {
    const bucket = buckets[indexOf(item.date)];
    if (bucket) bucket.inQuantity += item.quantity;
  }
  let cumulativeSales = 0;
  let cumulativeIn = 0;
  for (const bucket of buckets) {
    cumulativeSales += bucket.salesQuantity;
    cumulativeIn += bucket.inQuantity;
    bucket.cumulativeSales = cumulativeSales;
    bucket.cumulativeIn = cumulativeIn;
  }
  return { bucketDays, buckets };
}

export interface ProductMovementBatch {
  key: string;
  /** null 表示没有对应进货：区间第一批之前的销售，或整个区间都没有进货。 */
  date: string | null;
  documents: ProductInsightMovement[];
  inQuantity: number;
  startDate: string;
  endDate: string;
  days: number;
  salesQuantity: number;
  salesAmount: number;
  salesDays: number;
  sellThroughRate: number | null;
  latest: boolean;
  /** 本批期间的每日销售，新日期在前。 */
  sales: ProductInsightDailySales[];
}

/**
 * 按进货批次归集销售：同一天的进货单合成一批，
 * 每批覆盖「本批到货日 ~ 下一批到货前一天」，最后一批覆盖到区间结束日。
 * 各批销售合计必然等于区间销售合计，返回顺序为新批次在前。
 */
export function buildPurchaseBatches(
  data: StoreProductInsight,
): ProductMovementBatch[] {
  const endDate = dayKey(data.range.endDate);
  const groups = new Map<string, ProductInsightMovement[]>();
  for (const item of inboundMovements(data)) {
    const day = dayKey(item.date);
    groups.set(day, [...(groups.get(day) ?? []), item]);
  }
  const inboundDays = [...groups.keys()].sort();
  const sales = [...data.sales.records].sort((a, b) =>
    dayKey(b.date).localeCompare(dayKey(a.date)),
  );
  const build = (
    date: string | null,
    startDate: string,
    periodEnd: string,
    latest: boolean,
  ): ProductMovementBatch => {
    const documents = date ? (groups.get(date) ?? []) : [];
    const inQuantity = documents.reduce((sum, item) => sum + item.quantity, 0);
    // 销量为 0 的日记录不展示，也不计入销售日。
    const periodSales = sales.filter((item) => {
      const day = dayKey(item.date);
      return item.quantity !== 0 && day >= startDate && day <= periodEnd;
    });
    const salesQuantity = periodSales.reduce(
      (sum, item) => sum + item.quantity,
      0,
    );
    return {
      key: date ?? `before:${startDate}`,
      date,
      documents,
      inQuantity,
      startDate,
      endDate: periodEnd,
      days: diffDays(startDate, periodEnd) + 1,
      salesQuantity,
      salesAmount: periodSales.reduce((sum, item) => sum + item.amount, 0),
      salesDays: periodSales.length,
      sellThroughRate: date ? ratio(salesQuantity, inQuantity) : null,
      latest,
      sales: periodSales,
    };
  };
  const batches = inboundDays.map((day, index) =>
    build(
      day,
      day,
      index + 1 < inboundDays.length
        ? addDays(inboundDays[index + 1], -1)
        : endDate,
      index === inboundDays.length - 1,
    ),
  );
  const startDate = dayKey(data.range.startDate);
  const firstInbound = inboundDays[0];
  if (!firstInbound || firstInbound > startDate) {
    const before = build(
      null,
      startDate,
      firstInbound ? addDays(firstInbound, -1) : endDate,
      false,
    );
    // 第一批之前没有销售就不显示空组；整个区间无进货时保留一组承载全部销售。
    if (before.sales.length || !firstInbound) batches.unshift(before);
  }
  return batches.reverse();
}

export type ProductMovementFlowFilter = "all" | "in" | "sales";

export type ProductMovementFlowEntry =
  | { type: "sale"; key: string; date: string; record: ProductInsightDailySales }
  | { type: "in"; key: string; date: string; record: ProductInsightMovement }
  | { type: "order"; key: string; date: string; record: ProductInsightOrder };

export interface ProductMovementFlowMonth {
  month: string;
  inQuantity: number;
  orderQuantity: number;
  salesQuantity: number;
  salesAmount: number;
  entries: ProductMovementFlowEntry[];
}

// 同一天倒序显示时，到货排在当天销售下面（先到货后卖）。
const ENTRY_ORDER = { sale: 0, in: 1, order: 2 } as const;

/** 按日期把进货（仓库商品为订货 + 配货）和每日销售混排，按月分组，新日期在前。 */
export function buildDailyFlow(
  data: StoreProductInsight,
  filter: ProductMovementFlowFilter,
): ProductMovementFlowMonth[] {
  const entries: ProductMovementFlowEntry[] = [];
  if (filter !== "in") {
    for (const record of data.sales.records) {
      if (record.quantity === 0) continue;
      const date = dayKey(record.date);
      entries.push({ type: "sale", key: `sale:${date}`, date, record });
    }
  }
  if (filter !== "sales") {
    for (const record of inboundMovements(data)) {
      entries.push({
        type: "in",
        key: `in:${record.id}`,
        date: dayKey(record.date),
        record,
      });
    }
    if (data.sourceType === "warehouse") {
      for (const record of data.warehouse.orders) {
        entries.push({
          type: "order",
          key: `order:${record.id}`,
          date: dayKey(record.date),
          record,
        });
      }
    }
  }
  entries.sort(
    (a, b) =>
      b.date.localeCompare(a.date) ||
      ENTRY_ORDER[a.type] - ENTRY_ORDER[b.type],
  );
  const months: ProductMovementFlowMonth[] = [];
  for (const entry of entries) {
    const month = entry.date.slice(0, 7);
    let group = months[months.length - 1] as ProductMovementFlowMonth | undefined;
    if (group?.month !== month) {
      group = {
        month,
        inQuantity: 0,
        orderQuantity: 0,
        salesQuantity: 0,
        salesAmount: 0,
        entries: [],
      };
      months.push(group);
    }
    group.entries.push(entry);
    if (entry.type === "sale") {
      group.salesQuantity += entry.record.quantity;
      group.salesAmount += entry.record.amount;
    } else if (entry.type === "in") group.inQuantity += entry.record.quantity;
    else group.orderQuantity += entry.record.quantity;
  }
  return months;
}
