import type {
  ManualPaymentFinding,
  PaymentRecoveryRecord,
} from "./payment-recovery-types";

export type ManualVerificationDraft = Readonly<{
  finding: ManualPaymentFinding | null;
  amount: string;
  evidenceReference: string;
  note: string;
  confirmedByOperator: boolean;
  /** 终端实扣金额与订单不符时，“未收款”需额外确认：终端已扣款，须总部冲正。 */
  terminalChargeAcknowledged?: boolean;
}>;

export type ManualVerificationValidation = Readonly<{
  amountCents: number | null;
  errors: Readonly<{
    finding: "required" | "amount-mismatch-paid" | null;
    amount: "required" | "mismatch" | null;
    evidenceReference: "required" | null;
    note: "required" | null;
    operatorConfirmation: "required" | null;
    terminalChargeAcknowledgement: "required" | null;
  }>;
  valid: boolean;
}>;

export const EMPTY_MANUAL_VERIFICATION_DRAFT: ManualVerificationDraft = {
  finding: null,
  amount: "",
  evidenceReference: "",
  note: "",
  confirmedByOperator: false,
  terminalChargeAcknowledged: false,
};

export function validateManualVerification(
  record: PaymentRecoveryRecord,
  draft: ManualVerificationDraft,
): ManualVerificationValidation {
  const amountCents = parseAudCents(draft.amount);
  const amountRequired = draft.finding === "paid";
  const amountError = !amountRequired
    ? null
    : amountCents === null
      ? "required"
      : amountCents !== record.amountCents
        ? "mismatch"
        : null;
  const mismatch = (record.terminalAmountMismatchCents ?? null) !== null;
  const errors = {
    // 终端批准了不同金额：按订单金额确认“已收款”会账实不符，只能登记冲正（未收款）或继续等待。
    finding: draft.finding === null
      ? "required"
      : mismatch && draft.finding === "paid"
        ? "amount-mismatch-paid"
        : null,
    amount: amountError,
    evidenceReference: draft.evidenceReference.trim() && draft.evidenceReference.trim().length <= 256 && !/[\u0000-\u001f\u007f]/u.test(draft.evidenceReference) ? null : "required",
    note: normalizeVerificationNote(draft.note) && draft.note.length <= 1000 && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(draft.note) ? null : "required",
    operatorConfirmation: draft.confirmedByOperator ? null : "required",
    terminalChargeAcknowledgement:
      mismatch && draft.finding === "unpaid" && draft.terminalChargeAcknowledged !== true ? "required" : null,
  } as const;

  return {
    amountCents: amountRequired ? amountCents : null,
    errors,
    valid: Object.values(errors).every((error) => error === null),
  };
}

function parseAudCents(value: string): number | null {
  const normalized = value.trim().replace(/^AU\$/iu, "").replace(/,/gu, "");
  if (!/^(?:0|[1-9]\d*)(?:\.\d{1,2})?$/u.test(normalized)) return null;
  const [whole = "0", fraction = ""] = normalized.split(".");
  const cents = Number(whole) * 100 + Number(fraction.padEnd(2, "0"));
  return Number.isSafeInteger(cents) && cents >= 0 ? cents : null;
}

export function normalizeVerificationNote(note: string): string {
  return note.replace(/[\r\n\t]+/gu, " ").trim();
}
