import assert from "node:assert/strict";
import test from "node:test";

import { paymentRecoveryText, resolvePaymentRecoveryLocale } from "./payment-recovery-copy";
import {
  EMPTY_MANUAL_VERIFICATION_DRAFT,
  MANUAL_NOTE_PRESETS,
  appendManualNotePreset,
  isManualEvidencePlaceholder,
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

test("人工核实默认没有选择结果或操作人确认，不能提交；证据与备注都是选填，不再报必填", () => {
  const result = validateManualVerification(record, EMPTY_MANUAL_VERIFICATION_DRAFT);
  assert.equal(result.valid, false);
  assert.deepEqual(result.errors, {
    finding: "required",
    amount: null,
    note: null,
    operatorConfirmation: "required",
    terminalChargeAcknowledgement: null,
  });
});

test("选好结果并确认后，不写备注也能提交（人工确认为准）", () => {
  for (const finding of ["unpaid", "uncertain"] as const) {
    assert.equal(
      validateManualVerification(record, { ...EMPTY_MANUAL_VERIFICATION_DRAFT, finding, confirmedByOperator: true }).valid,
      true,
    );
  }
  assert.equal(
    validateManualVerification(record, {
      ...EMPTY_MANUAL_VERIFICATION_DRAFT, finding: "paid", amount: "0.99", confirmedByOperator: true,
    }).valid,
    true,
  );
});

test("备注预设：已扣款两条、未扣款三条，仍无法确认没有；追加不重复，中英文分隔符不同", () => {
  assert.deepEqual([...MANUAL_NOTE_PRESETS.paid], ["paidTerminalSuccess", "paidCustomerRecord"]);
  assert.deepEqual([...MANUAL_NOTE_PRESETS.unpaid], ["unpaidTerminalFailed", "unpaidTerminalOffline", "unpaidTerminalStuck"]);
  assert.deepEqual([...MANUAL_NOTE_PRESETS.uncertain], []);
  assert.equal(paymentRecoveryText("zh", "manual.preset.paidTerminalSuccess"), "刷卡机显示支付成功");
  assert.equal(paymentRecoveryText("zh", "manual.preset.paidCustomerRecord"), "顾客提供支付记录");
  assert.equal(paymentRecoveryText("zh", "manual.preset.unpaidTerminalFailed"), "刷卡机显示失败");
  assert.equal(paymentRecoveryText("zh", "manual.preset.unpaidTerminalOffline"), "刷卡机离线");
  assert.equal(paymentRecoveryText("zh", "manual.preset.unpaidTerminalStuck"), "刷卡机卡住了");
  assert.equal(paymentRecoveryText("en", "manual.preset.unpaidTerminalOffline"), "Terminal offline");

  assert.equal(appendManualNotePreset("", "刷卡机离线", "；"), "刷卡机离线");
  assert.equal(appendManualNotePreset("刷卡机离线", "刷卡机卡住了", "；"), "刷卡机离线；刷卡机卡住了");
  assert.equal(appendManualNotePreset("刷卡机离线；刷卡机卡住了", "刷卡机离线", "；"), "刷卡机离线；刷卡机卡住了");
  assert.equal(appendManualNotePreset("Terminal offline", "terminal offline", "; "), "Terminal offline");
  assert.equal(appendManualNotePreset("Terminal offline", "Terminal stuck", "; "), "Terminal offline; Terminal stuck");
  assert.equal(appendManualNotePreset("keep me", "   ", "; "), "keep me");
});

test("占位证据不在历史里显示", () => {
  assert.equal(isManualEvidencePlaceholder("-"), true);
  assert.equal(isManualEvidencePlaceholder(" - "), true);
  assert.equal(isManualEvidencePlaceholder(undefined), true);
  assert.equal(isManualEvidencePlaceholder("R-42"), false);
});

test("确认已扣款要求金额与原订单分毫一致", () => {
  const common = {
    ...EMPTY_MANUAL_VERIFICATION_DRAFT,
    finding: "paid" as const,
    note: "Checked the terminal settlement",
    confirmedByOperator: true,
  };
  assert.equal(validateManualVerification(record, { ...common, amount: "1.00" }).errors.amount, "mismatch");
  assert.deepEqual(validateManualVerification(record, { ...common, amount: "AU$0.99" }), {
    amountCents: 99,
    errors: {
      finding: null,
      amount: null,
      note: null,
      operatorConfirmation: null,
      terminalChargeAcknowledgement: null,
    },
    valid: true,
  });
});

test("确认未扣款或仍无法确认不伪造已收款金额，只要求主管验证前确认", () => {
  for (const finding of ["unpaid", "uncertain"] as const) {
    const result = validateManualVerification(record, {
      finding,
      amount: "999.99",
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
  const draft = { finding: "paid" as const, amount: "0.99",
    note: "已核对\n终端小票\t和交易记录", confirmedByOperator: true };
  assert.equal(validateManualVerification(record, draft).valid, true);
  assert.equal(normalizeVerificationNote(draft.note), "已核对 终端小票 和交易记录");
  assert.equal(validateManualVerification(record, { ...draft, note: "a".repeat(1001) }).valid, false);
  assert.equal(validateManualVerification(record, { ...draft, note: "bad\u0001note" }).errors.note, "invalid");
});

test("M34：终端批准不同金额时禁止确认已收款；未收款必须额外确认终端已扣款需冲正", () => {
  const mismatch: PaymentRecoveryRecord = { ...record, terminalAmountMismatchCents: 1_500 };
  const common = {
    ...EMPTY_MANUAL_VERIFICATION_DRAFT,
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
