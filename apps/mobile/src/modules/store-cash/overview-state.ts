// 现金池总览的展示状态判断。
// 现金池 = 期初 + 日结实点现金 − 存款 − 支出；备用金不计入（点钱前已取出），所以存完后余额应趋近 0。
// 日结未接入时 poolBalance 等字段是 null，必须显示「暂不可算」而不是 $0.00。
import { CASH_EXPENSE_CATEGORIES } from "./constants";
import type {
  CashCapabilities,
  CashContext,
  CashExpenseCategoryTotal,
  CashStoreSummary,
} from "./types";

export interface OverviewState {
  /** 日结数据是否已接入（上下文与分店总览都为 true 才算接入，取保守口径）。 */
  dailyCloseConnected: boolean;
  /** 现金池余额是否可显示数值。 */
  balanceAvailable: boolean;
  /** 需要先录入期初现金。 */
  openingMissing: boolean;
  /** 当前账号可以录入期初（缺期初且有存款/日结选择能力）。 */
  canRecordOpening: boolean;
  /** 有建议存款额且为正，可一键带入存款表单。 */
  suggestedDepositAvailable: boolean;
  depositOverdue: boolean;
  uncoveredDayCount: number;
  /** 最近 14 天没有日结存档的营业日（仅提示，可能是休息日）。 */
  hasMissingCloseDates: boolean;
}

export function resolveOverviewState(
  context: Pick<CashContext, "dailyCloseConnected">,
  summary: CashStoreSummary,
  capabilities: Pick<CashCapabilities, "canCreateDeposit">,
): OverviewState {
  const dailyCloseConnected = context.dailyCloseConnected && summary.dailyCloseConnected;
  return {
    dailyCloseConnected,
    balanceAvailable: dailyCloseConnected && summary.poolBalance != null,
    openingMissing: summary.openingMissing,
    canRecordOpening: summary.openingMissing && capabilities.canCreateDeposit,
    suggestedDepositAvailable:
      summary.suggestedDepositAmount != null && summary.suggestedDepositAmount > 0,
    depositOverdue: dailyCloseConnected && summary.depositOverdue,
    uncoveredDayCount: dailyCloseConnected ? summary.uncoveredDayCount : 0,
    hasMissingCloseDates: dailyCloseConnected && summary.missingCloseDates.length > 0,
  };
}

/**
 * 分类合计按固定类别顺序展示（现金工资、现金购物、T2、其他），未知类别码排在最后。
 * 只展示服务端返回的行：T2 的可见范围由服务端过滤，客户端不补、不删、不提示。
 */
export function orderExpenseCategoryTotals(
  totals: readonly CashExpenseCategoryTotal[],
): CashExpenseCategoryTotal[] {
  const rank = (category: string) => {
    const index = (CASH_EXPENSE_CATEGORIES as readonly string[]).indexOf(category);
    return index < 0 ? CASH_EXPENSE_CATEGORIES.length : index;
  };
  return [...totals].sort((a, b) => rank(a.category) - rank(b.category));
}

/** 是否把存款入口突出为主操作：有可存金额，或已逾期。 */
export function shouldHighlightDeposit(state: OverviewState): boolean {
  return state.suggestedDepositAvailable || state.depositOverdue;
}
