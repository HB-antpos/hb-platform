import assert from "node:assert/strict";
import test from "node:test";

import { paymentRecoveryText, resolvePaymentRecoveryLocale } from "./payment-recovery-copy";
import {
  EMPTY_MANUAL_VERIFICATION_DRAFT,
  validateManualVerification,
  normalizeVerificationNote,
} from "./payment-recovery-presenter";
import type { PaymentRecoveryRecord } from "./payment-recovery-types";

const record: PaymentRecoveryRecord = {
  id: "attempt-1",
  orderGuid: "order-1",
  occurredAtIso: "2026-09-11T01:00:00.000Z",
  amountCents: 99,
  status: "result-unknown",
  terminalName: "Lane 1",
  transactionReference: "TXN-1",
  receiptReference: null,
  lines: [],
  events: [],
};

test("人工核实默认没有选择结果或操作人确认，不能提交", () => {
  const result = validateManualVerification(record, EMPTY_MANUAL_VERIFICATION_DRAFT);
  assert.equal(result.valid, false);
  assert.deepEqual(result.errors, {
    finding: "required",
    amount: null,
    evidenceReference: "required",
    note: "required",
    operatorConfirmation: "required",
    terminalChargeAcknowledgement: null,
  });
});

test("确认已扣款要求金额与原订单分毫一致", () => {
  const common = {
    ...EMPTY_MANUAL_VERIFICATION_DRAFT,
    finding: "paid" as const,
    evidenceReference: "receipt-42",
    note: "Checked the terminal settlement",
    confirmedByOperator: true,
  };
  assert.equal(validateManualVerification(record, { ...common, amount: "1.00" }).errors.amount, "mismatch");
  assert.deepEqual(validateManualVerification(record, { ...common, amount: "AU$0.99" }), {
    amountCents: 99,
    errors: {
      finding: null,
      amount: null,
      evidenceReference: null,
      note: null,
      operatorConfirmation: null,
      terminalChargeAcknowledgement: null,
    },
    valid: true,
  });
});

test("确认未扣款或仍无法确认不伪造已收款金额，但仍要求凭证、备注和主管验证前确认", () => {
  for (const finding of ["unpaid", "uncertain"] as const) {
    const result = validateManualVerification(record, {
      finding,
      amount: "999.99",
      evidenceReference: "TERMINAL-JOURNAL-9",
      note: "Verified against terminal journal",
      confirmedByOperator: true,
    });
    assert.equal(result.valid, true);
    assert.equal(result.amountCents, null);
  }
});

test("中英文都明确区分人工结论、支付方批准和未知结果防重扣", () => {
  assert.equal(resolvePaymentRecoveryLocale("zh-CN"), "zh");
  assert.equal(resolvePaymentRecoveryLocale("en-AU"), "en");
  assert.match(paymentRecoveryText("zh", "statusHint.manual-paid"), /不代表支付方批准/u);
  assert.match(paymentRecoveryText("zh", "error.RECOVERY_ACTION_FAILED"), /勿重复扣款/u);
  assert.match(paymentRecoveryText("en", "statusHint.manual-paid"), /not a provider approval/iu);
  assert.match(paymentRecoveryText("en", "error.RECOVERY_ACTION_FAILED"), /do not charge again/iu);
});

test("多行备注提交前规范化，超长或控制字符拒绝", () => {
  const draft = { finding: "paid" as const, amount: "0.99", evidenceReference: "R1",
    note: "已核对\n终端小票\t和交易记录", confirmedByOperator: true };
  assert.equal(validateManualVerification(record, draft).valid, true);
  assert.equal(normalizeVerificationNote(draft.note), "已核对 终端小票 和交易记录");
  assert.equal(validateManualVerification(record, { ...draft, note: "a".repeat(1001) }).valid, false);
  assert.equal(validateManualVerification(record, { ...draft, evidenceReference: "a".repeat(257) }).valid, false);
  assert.equal(validateManualVerification(record, { ...draft, evidenceReference: "R1\nR2" }).valid, false);
});

test("M34：终端批准不同金额时禁止确认已收款；未收款必须额外确认终端已扣款需冲正", () => {
  const mismatch: PaymentRecoveryRecord = { ...record, terminalAmountMismatchCents: 1_500 };
  const common = {
    ...EMPTY_MANUAL_VERIFICATION_DRAFT,
    evidenceReference: "receipt-42",
    note: "Terminal shows a different amount",
    confirmedByOperator: true,
  };
  // 即使金额栏填了订单金额，也不能确认已收款。
  const paid = validateManualVerification(mismatch, { ...common, finding: "paid", amount: "0.99" });
  assert.equal(paid.valid, false);
  assert.equal(paid.errors.finding, "amount-mismatch-paid");

  const unpaid = validateManualVerification(mismatch, { ...common, finding: "unpaid" });
  assert.equal(unpaid.valid, false);
  assert.equal(unpaid.errors.terminalChargeAcknowledgement, "required");
  assert.equal(
    validateManualVerification(mismatch, { ...common, finding: "unpaid", terminalChargeAcknowledged: true }).valid,
    true,
  );
  // 仍未知不要求冲正确认；无金额不符的记录也不受影响。
  assert.equal(validateManualVerification(mismatch, { ...common, finding: "uncertain" }).valid, true);
  assert.equal(validateManualVerification(record, { ...common, finding: "unpaid" }).valid, true);
});

test("M34：中英文警告都带出终端实扣金额与订单金额，并明确不能确认已收款", () => {
  for (const locale of ["en", "zh"] as const) {
    const text = paymentRecoveryText(locale, "mismatch.warning", { charged: "AU$15.00", order: "AU$10.00" });
    assert.ok(text.includes("AU$15.00") && text.includes("AU$10.00"));
  }
  assert.match(paymentRecoveryText("en", "mismatch.warning", { charged: "x", order: "y" }), /Do not take payment again/u);
  assert.match(paymentRecoveryText("zh", "mismatch.warning", { charged: "x", order: "y" }), /请勿再次收款/u);
});
