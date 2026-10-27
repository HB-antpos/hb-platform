import type {
  ManualPaymentFinding,
  PaymentRecoveryRecord,
} from "./payment-recovery-types";

export type ManualVerificationDraft = Readonly<{
  finding: ManualPaymentFinding | null;
  amount: string;
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
    /** 备注选填；只有写了内容才校验长度与控制字符。 */
    note: "invalid" | null;
    operatorConfirmation: "required" | null;
    terminalChargeAcknowledgement: "required" | null;
  }>;
  valid: boolean;
}>;

export const EMPTY_MANUAL_VERIFICATION_DRAFT: ManualVerificationDraft = {
  finding: null,
  amount: "",
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
    // 证据输入已取消（用户 10-24 决定：人工确认为准，只留选填备注）；空备注合法，写了内容才校验。
    note: !normalizeVerificationNote(draft.note) ||
      (draft.note.length <= 1000 && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(draft.note))
      ? null
      : "invalid",
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

/**
 * 数据库的证据 / 备注列仍有 NOT NULL + 非空 CHECK，改约束要重建表；证据输入取消后由这里补占位值，
 * 账本和同步契约保持不变。占位证据在历史里不显示（见 isManualEvidencePlaceholder）。
 */
export const MANUAL_EVIDENCE_NOT_ENTERED = "-";
export const MANUAL_NOTE_NOT_ENTERED = "Supervisor confirmed (no note entered).";

export function isManualEvidencePlaceholder(value: string | undefined | null): boolean {
  return !value || value.trim() === MANUAL_EVIDENCE_NOT_ENTERED;
}

/** 备注预设：点一下把常用说法追加到备注里，主管仍可继续编辑。 */
export type ManualNotePresetId =
  | "paidTerminalSuccess"
  | "paidCustomerRecord"
  | "unpaidTerminalFailed"
  | "unpaidTerminalOffline"
  | "unpaidTerminalStuck";

export const MANUAL_NOTE_PRESETS: Readonly<Record<ManualPaymentFinding, readonly ManualNotePresetId[]>> = {
  paid: ["paidTerminalSuccess", "paidCustomerRecord"],
  unpaid: ["unpaidTerminalFailed", "unpaidTerminalOffline", "unpaidTerminalStuck"],
  uncertain: [],
};

/** 追加预设：备注为空直接写入；已包含同一句则不重复；否则用语言对应的分隔符连接。 */
export function appendManualNotePreset(note: string, preset: string, separator: string): string {
  const current = normalizeVerificationNote(note);
  const text = normalizeVerificationNote(preset);
  if (!text) return note;
  if (!current) return text;
  if (current.toLowerCase().includes(text.toLowerCase())) return current;
  return `${current}${separator}${text}`;
}
