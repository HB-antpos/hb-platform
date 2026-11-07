import { roundAudCash } from "./money";
import type { AuditEventDraft } from "./order";

export const AUD_CASH_DENOMINATIONS_CENTS = Object.freeze([
  10_000,
  5_000,
  2_000,
  1_000,
  500,
  200,
  100,
  50,
  20,
  10,
  5,
] as const);

export type AudCashDenominationCents =
  (typeof AUD_CASH_DENOMINATIONS_CENTS)[number];

export type DailyCloseTenderMethod = "cash" | "card" | "voucher";

export type DailyCloseTenderBreakdown = Readonly<{
  method: DailyCloseTenderMethod;
  salesCents: number;
  refundCents: number;
  netCents: number;
}>;

export type DailyCloseScope = Readonly<{
  businessDate: string;
  periodFromIso: string;
  periodToIso: string;
  storeCode: string;
  deviceCode: string;
}>;

export type DailyCloseSummary = DailyCloseScope &
  Readonly<{
    orderCount: number;
    returnQuantity: string;
    /** 销售订单的支付汇总（不含分期）；分期单列在 installmentPayments。 */
    tenders: readonly DailyCloseTenderBreakdown[];
    /**
     * 分期（Lay-by）在本机当天收到和退回的款项，按付款方式拆开（恰好 cash / card / voucher 各一条）：
     * 收款是定金与续付，退款是取消分期的退款。分期不是销售订单，所以不混进 tenders；
     * 但它同样进出钱箱，应有现金与上传服务端的支付方式合计都要把它算进去（对齐 WPF #1196）。
     */
    installmentPayments: readonly DailyCloseTenderBreakdown[];
    /** 分期付款笔数（现金、刷卡、代金券都算）。服务端用它放宽现金进位上限：分期现金也是逐笔进位。 */
    installmentPaymentCount: number;
    /**
     * 现金进位合计（分，带符号）：钱箱里实际收付的是 5 分进位后的现金，而 tender 记的是订单精确金额，
     * 两者每单可差 ±2 分。应有现金 = 现金净额 + 现金进位；对账必须拿进位后的金额比，否则每天都有几分钱的假差额。
     * 对齐 WPF #914 的 CashRoundingAmount。分期现金同样逐笔进位，计入这个合计。
     */
    cashRoundingCents: number;
    /** 应有现金 = 订单现金 tender 净额 + 分期现金净额 + cashRoundingCents。 */
    expectedCashCents: number;
  }>;

export type DailyCloseDenominationCount = Readonly<{
  denominationCents: AudCashDenominationCents;
  quantity: number;
  subtotalCents: number;
}>;

export type DailyCloseArchive = DailyCloseSummary &
  Readonly<{
    closeId: string;
    savedCashierId: string;
    savedCashierName: string;
    savedAtIso: string;
    denominations: readonly DailyCloseDenominationCount[];
    notesSubtotalCents: number;
    coinsSubtotalCents: number;
    countedCashCents: number;
    varianceCents: number;
  }>;

export type DailyCloseArchiveCommit = Readonly<{
  archive: DailyCloseArchive;
  audit: AuditEventDraft;
}>;

export interface DailyCloseRepositoryPort {
  summarize(scope: DailyCloseScope): Promise<DailyCloseSummary>;
  saveArchive(
    input: DailyCloseArchiveCommit,
  ): Promise<Readonly<{ replayed: boolean; archive: DailyCloseArchive }>>;
  getArchive(closeId: string): Promise<DailyCloseArchive | null>;
  listArchives(
    scope: Readonly<{
      storeCode: string;
      deviceCode: string;
      businessDate?: string | null;
      limit: number;
    }>,
  ): Promise<readonly DailyCloseArchive[]>;
}

/** 全为 0 的三种支付方式汇总：没有分期、历史存档等场景使用。 */
export function emptyDailyCloseTenders(): readonly DailyCloseTenderBreakdown[] {
  return Object.freeze(
    (["cash", "card", "voucher"] as const).map((method) =>
      Object.freeze({ method, salesCents: 0, refundCents: 0, netCents: 0 }),
    ),
  );
}

/**
 * 每个付款方式把“销售订单 + 分期”合并成一条：服务端按「实点现金 − (现金净额 + 进位)」核对差额，
 * 银行结算也按合计对，所以上传用合并口径；分期明细只在本机存档和小票上单列（对齐 WPF GetCombinedSummary）。
 */
export function combineDailyCloseTenders(
  orders: readonly DailyCloseTenderBreakdown[],
  installments: readonly DailyCloseTenderBreakdown[],
): readonly DailyCloseTenderBreakdown[] {
  return Object.freeze(
    (["cash", "card", "voucher"] as const).map((method) => {
      const order = orders.find((entry) => entry.method === method);
      const installment = installments.find((entry) => entry.method === method);
      const salesCents = (order?.salesCents ?? 0) + (installment?.salesCents ?? 0);
      const refundCents = (order?.refundCents ?? 0) + (installment?.refundCents ?? 0);
      const netCents = salesCents + refundCents;
      if (
        !Number.isSafeInteger(salesCents) ||
        !Number.isSafeInteger(refundCents) ||
        !Number.isSafeInteger(netCents)
      ) {
        throw new TypeError("Daily close combined tender exceeds safe integer bounds.");
      }
      return Object.freeze({ method, salesCents, refundCents, netCents });
    }),
  );
}

/**
 * 当日现金进位合计。入参是“每张订单的现金 tender 合计（销售为正、退款为负，分）”，
 * 每单按收银时同一规则（AUD 5 分、远离零）进位，进位后的金额减精确金额就是这张订单的进位。
 * 只看现金 tender，不依赖订单头里的收款/找零字段，历史订单同样可算（与 WPF ReadCashRoundingAmountAsync 一致）。
 */
export function sumCashRoundingCents(
  cashCentsByOrder: readonly number[],
): number {
  let total = 0;
  for (const cashCents of cashCentsByOrder) {
    if (!Number.isSafeInteger(cashCents)) {
      throw new TypeError("Daily close order cash total must be integer cents.");
    }
    const rounded = roundAudCash({ currency: "AUD", cents: cashCents }).cents;
    total += rounded - cashCents;
    if (!Number.isSafeInteger(total)) {
      throw new TypeError("Daily close cash rounding exceeds safe integer bounds.");
    }
  }
  return total;
}

export function normalizeDailyCloseCounts(
  input: readonly Readonly<{
    denominationCents: number;
    quantity: number;
  }>[],
): readonly DailyCloseDenominationCount[] {
  const byDenomination = new Map<number, number>();
  for (const entry of input) {
    if (
      !AUD_CASH_DENOMINATIONS_CENTS.includes(
        entry.denominationCents as AudCashDenominationCents,
      )
    ) {
      throw new TypeError("Daily close denomination is unsupported.");
    }
    if (!Number.isSafeInteger(entry.quantity) || entry.quantity < 0) {
      throw new TypeError(
        "Daily close quantity must be a non-negative integer.",
      );
    }
    if (byDenomination.has(entry.denominationCents)) {
      throw new TypeError("Daily close denomination is duplicated.");
    }
    byDenomination.set(entry.denominationCents, entry.quantity);
  }
  return Object.freeze(
    AUD_CASH_DENOMINATIONS_CENTS.map((denominationCents) => {
      const quantity = byDenomination.get(denominationCents) ?? 0;
      const subtotalCents = denominationCents * quantity;
      if (!Number.isSafeInteger(subtotalCents)) {
        throw new TypeError("Daily close denomination subtotal is invalid.");
      }
      return Object.freeze({
        denominationCents,
        quantity,
        subtotalCents,
      });
    }),
  );
}
