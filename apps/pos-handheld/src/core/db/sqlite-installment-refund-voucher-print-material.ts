import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import type { VoucherProtectedTokenPort } from "@/features/payments/voucher/voucher-payment-adapter";

export type InstallmentRefundVoucherPrintMaterial = Readonly<{
  voucherCode: string;
  /** 正数整数分币。 */
  amountCents: number;
}>;

export interface InstallmentRefundVoucherPrintMaterialPort {
  /**
   * 列出本机已完成的取消退款为该分期单签发的全部退款代金券。券码只在受保护 state 中
   * 解密后直接返回给券面编码，不进入 presenter、路由或日志；本机没有记录（如跨机取消）时返回空数组。
   */
  listApprovedRefundVouchers(
    installmentGuid: string,
    storeCode: string,
  ): Promise<readonly InstallmentRefundVoucherPrintMaterial[]>;
}

type ProtectedReferenceRow = Readonly<{
  protected_reference: unknown;
}>;

export class SqliteInstallmentRefundVoucherPrintMaterial
implements InstallmentRefundVoucherPrintMaterialPort {
  public constructor(
    private readonly connection: Pick<SqliteConnectionPort, "getAll">,
    private readonly tokens: Pick<VoucherProtectedTokenPort, "resolve">,
  ) {}

  public async listApprovedRefundVouchers(
    installmentGuidInput: string,
    storeCodeInput: string,
  ): Promise<readonly InstallmentRefundVoucherPrintMaterial[]> {
    const installmentGuid = exactText(installmentGuidInput);
    const storeCode = exactText(storeCodeInput);
    // 只认已完成（resolution=Completed）的取消退款动作；签券中途、待恢复的动作不出券面。
    const rows = await this.connection.getAll<ProtectedReferenceRow>(
      `SELECT state.protected_reference
       FROM installment_voucher_protected_states state
       INNER JOIN installment_actions action
         ON action.action_id = state.action_id
       WHERE action.installment_guid = ?
         AND action.store_code = ?
         AND action.action_kind = 'cancel-refund'
         AND action.resolution = 'Completed'
       ORDER BY state.updated_at_iso ASC, state.attempt_id ASC`,
      [installmentGuid, storeCode],
    );
    const vouchers: InstallmentRefundVoucherPrintMaterial[] = [];
    for (const row of rows) {
      if (typeof row.protected_reference !== "string") {
        throw new Error("Installment refund voucher reference is invalid.");
      }
      const state = await this.tokens.resolve(row.protected_reference);
      if (
        !state ||
        state.operation !== "refund" ||
        state.phase !== "approved" ||
        state.storeCode !== storeCode ||
        !state.voucherCode ||
        !Number.isSafeInteger(state.amountCents) ||
        state.amountCents === 0
      ) {
        continue;
      }
      vouchers.push(Object.freeze({
        voucherCode: state.voucherCode,
        amountCents: Math.abs(state.amountCents),
      }));
    }
    return Object.freeze(vouchers);
  }
}

function exactText(value: string): string {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (!normalized || normalized !== value || normalized.length > 128) {
    throw new Error("Installment refund voucher identity is invalid.");
  }
  return normalized;
}
