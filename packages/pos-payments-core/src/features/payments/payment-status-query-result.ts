import type { PaymentProviderResult } from "@hb/pos-domain/core/contracts/payment";

/** 仅用于人工核实：本次 GET 已取得并校验原交易响应，不能由本地 Unknown 推断。 */
export type PaymentStatusQueryResult = PaymentProviderResult & Readonly<{
  queryVerified: boolean;
}>;
