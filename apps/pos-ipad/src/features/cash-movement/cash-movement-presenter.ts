import {
  CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH,
  CASH_MOVEMENT_NOTE_REQUIRED_REASONS,
  canVoidCashMovement,
  validateCashMovementInput,
  type CashMovementReasonCode,
  type CashMovementValidationCode,
  type CashMovementVoidEligibility,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

import type {
  CashMovementRuntime,
  CashMovementWriteResult,
} from "./cash-movement-runtime";

export type CashMovementTab = "in" | "out" | "records";

/** 存入弹窗只列前两项，取出弹窗只列后两项；每页默认选第一项。 */
export const CASH_IN_REASONS: readonly CashMovementReasonCode[] = Object.freeze([
  "ChangeTopUp",
  "OtherIn",
]);
export const CASH_OUT_REASONS: readonly CashMovementReasonCode[] = Object.freeze([
  "SafeDrop",
  "PaidOut",
]);

/** 金额快捷按钮（元）。 */
export const CASH_QUICK_AMOUNTS: readonly number[] = Object.freeze([
  20, 50, 100, 200, 500,
]);

/** 金额输入最多 9 位数字（整数位 + 小数位合计），小数最多 2 位。 */
export const CASH_AMOUNT_MAXIMUM_DIGITS = 9;

export type CashAmountKey =
  | "0"
  | "1"
  | "2"
  | "3"
  | "4"
  | "5"
  | "6"
  | "7"
  | "8"
  | "9"
  | "decimal"
  | "clear"
  | "backspace";

/**
 * 小键盘输入规则（纯函数）：
 * - 最多 2 位小数、最多 9 位数字；超出的按键被忽略，不截断已有内容；
 * - 整数部分不留多余前导 0（"0" 后再按数字 → 替换）；
 * - 以小数点开头自动补 0。
 */
export function applyCashAmountKey(text: string, key: CashAmountKey): string {
  if (key === "clear") return "";
  if (key === "backspace") return text.slice(0, -1);
  if (key === "decimal") {
    if (text.includes(".")) return text;
    return text.length === 0 ? "0." : `${text}.`;
  }
  const dotIndex = text.indexOf(".");
  if (dotIndex >= 0 && text.length - dotIndex - 1 >= 2) return text;
  const digitCount = text.replace(".", "").length;
  if (digitCount >= CASH_AMOUNT_MAXIMUM_DIGITS) return text;
  if (text === "0") return key;
  return `${text}${key}`;
}

/** 金额文本 → 整数分；空串或格式不对返回 null（不用浮点数，避免 0.1 + 0.2 之类的误差）。 */
export function parseCashAmountText(text: string): number | null {
  const match = /^(\d+)(?:\.(\d{0,2}))?$/u.exec(text);
  if (!match) return null;
  const dollars = Number(match[1]);
  const fraction = (match[2] ?? "").padEnd(2, "0");
  const cents = dollars * 100 + Number(fraction);
  return Number.isSafeInteger(cents) ? cents : null;
}

export type CashMovementStatusCode =
  | "saved"
  | "void-saved"
  | "denied"
  | "cancelled"
  | "save-failed"
  | "load-failed"
  | "reprint-printed"
  | "reprint-failed"
  | "void-not-allowed";

/** 保存成功后钱箱 / 打印失败的提示（记录已落库，只是告知）。 */
export type CashMovementHardwareWarning = "drawer" | "print";

export type CashMovementRecordKind = "in" | "out" | "void";

export type CashMovementRecordView = Readonly<{
  movement: LocalCashMovement;
  kind: CashMovementRecordKind;
  /** 带符号金额：存入为正、取出为负；冲正记录与被冲正的原记录符号相反。 */
  signedCents: number;
  /** 是否已被冲正（原记录显示「已冲正」）。 */
  voided: boolean;
  voidEligibility: CashMovementVoidEligibility;
}>;

export type CashMovementVoidDraft = Readonly<{
  /** 被冲正的原记录 guid。 */
  originalMovementGuid: string;
  note: string;
  validationCode: CashMovementValidationCode | "VOID_NOTE_REQUIRED" | null;
}>;

export type CashMovementFormValidation =
  | CashMovementValidationCode
  | "VOID_NOTE_REQUIRED";

export type CashMovementState = Readonly<{
  tab: CashMovementTab;
  reasonCode: CashMovementReasonCode;
  amountText: string;
  note: string;
  /** 点过确认后的校验失败码；任何编辑都会清掉。 */
  validationCode: CashMovementFormValidation | null;
  busy: boolean;
  statusCode: CashMovementStatusCode | null;
  statusNumber: string | null;
  hardwareWarnings: readonly CashMovementHardwareWarning[];
  loadState: "idle" | "loading" | "ready" | "failed";
  /** 最新在上。 */
  records: readonly CashMovementRecordView[];
  rejectedCount: number;
  todayCashInCents: number;
  todayCashOutCents: number;
  businessTimeZone: string | null;
  voidDraft: CashMovementVoidDraft | null;
}>;

export type CashMovementPresenterOptions = Readonly<{
  runtime: CashMovementRuntime;
  /** 为每一次提交生成 movementGuid（UUID）。 */
  createId(): string;
}>;

/**
 * 钱箱弹窗的状态与动作。风格同 daily-close-presenter：不可变状态 + patch + 订阅，
 * 所有会写库 / 打印的动作走 runAction 防重入（授权等待、落库、开箱、打印期间再点无效，
 * 同一次点击只会生成一个 movementGuid）。
 */
export class CashMovementPresenter {
  private readonly listeners = new Set<() => void>();
  private state: CashMovementState = Object.freeze({
    tab: "in" as const,
    reasonCode: CASH_IN_REASONS[0]!,
    amountText: "",
    note: "",
    validationCode: null,
    busy: false,
    statusCode: null,
    statusNumber: null,
    hardwareWarnings: Object.freeze([]),
    loadState: "idle" as const,
    records: Object.freeze([]),
    rejectedCount: 0,
    todayCashInCents: 0,
    todayCashOutCents: 0,
    businessTimeZone: null,
    voidDraft: null,
  });
  private destroyed = false;
  private actionInFlight: Promise<void> | null = null;
  private loadGeneration = 0;

  public constructor(private readonly options: CashMovementPresenterOptions) {}

  public readonly getState = (): CashMovementState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.loadGeneration += 1;
    this.listeners.clear();
  }

  /** 当前金额文本对应的整数分；空或非法为 null。 */
  public amountCents(): number | null {
    return parseCashAmountText(this.state.amountText);
  }

  // ───────────────────────── 表单 ─────────────────────────

  public setTab(tab: CashMovementTab): void {
    if (this.destroyed || this.state.busy || tab === this.state.tab) return;
    if (tab === "records") {
      this.patch({ tab, statusCode: null, hardwareWarnings: [] });
      void this.load();
      return;
    }
    const reasons = tab === "in" ? CASH_IN_REASONS : CASH_OUT_REASONS;
    // 切换存入 / 取出：原因回到该页第一项，金额与备注清空，避免把存入的内容带到取出。
    this.patch({
      tab,
      reasonCode: reasons[0]!,
      amountText: "",
      note: "",
      validationCode: null,
      statusCode: null,
      hardwareWarnings: [],
      voidDraft: null,
    });
  }

  public selectReason(reasonCode: CashMovementReasonCode): boolean {
    if (this.destroyed || this.state.busy) return false;
    const reasons = this.state.tab === "in" ? CASH_IN_REASONS : CASH_OUT_REASONS;
    if (this.state.tab === "records" || !reasons.includes(reasonCode)) return false;
    this.patch({ reasonCode, validationCode: null });
    return true;
  }

  public pressKey(key: CashAmountKey): void {
    if (this.destroyed || this.state.busy || this.state.tab === "records") return;
    this.patch({
      amountText: applyCashAmountKey(this.state.amountText, key),
      validationCode: null,
    });
  }

  /** 快捷金额（元）：直接替换当前金额。 */
  public quickAmount(dollars: number): void {
    if (this.destroyed || this.state.busy || this.state.tab === "records") return;
    if (!CASH_QUICK_AMOUNTS.includes(dollars)) return;
    this.patch({ amountText: String(dollars), validationCode: null });
  }

  /** 备注输入框已限长；这里再兜底，超长内容直接忽略，绝不静默截断后保存。 */
  public setNote(note: string): void {
    if (this.destroyed || this.state.busy) return;
    if (note.length > CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH) return;
    this.patch({ note, validationCode: null });
  }

  public noteRequired(): boolean {
    return CASH_MOVEMENT_NOTE_REQUIRED_REASONS.includes(this.state.reasonCode);
  }

  public dismissStatus(): void {
    if (this.destroyed) return;
    this.patch({ statusCode: null, statusNumber: null, hardwareWarnings: [] });
  }

  // ───────────────────────── 提交 ─────────────────────────

  public submit(): Promise<void> {
    return this.runAction(() => this.performSubmit());
  }

  private async performSubmit(): Promise<void> {
    const { tab, reasonCode, note } = this.state;
    if (tab === "records") return;
    const checked = validateCashMovementInput({
      reasonCode,
      amountCents: this.amountCents(),
      note,
    });
    if (!checked.ok) {
      this.patch({ validationCode: checked.code });
      return;
    }
    // 一次提交只生成一个 guid：runAction 保证授权等待 / 落库期间重复点击不会走到这里。
    const movementGuid = this.options.createId();
    this.patch({
      busy: true,
      validationCode: null,
      statusCode: null,
      statusNumber: null,
      hardwareWarnings: [],
    });
    let result: CashMovementWriteResult;
    try {
      result = await this.options.runtime.submit({
        movementGuid,
        reasonCode: checked.reasonCode,
        amountCents: checked.amountCents,
        note: checked.note,
      });
    } catch {
      result = { kind: "failed" };
    }
    if (result.kind === "saved") {
      // 记录已落库：清空表单，开钱箱 / 打印失败只提示。
      this.patch({
        amountText: "",
        note: "",
        ...this.savedStatus("saved", result),
      });
      await this.refreshAfterWrite();
    } else {
      this.patch(this.failureStatus(result));
    }
    this.patch({ busy: false });
  }

  // ───────────────────────── 冲正 ─────────────────────────

  public startVoid(movementGuid: string): boolean {
    if (this.destroyed || this.state.busy) return false;
    const record = this.state.records.find(
      (candidate) => candidate.movement.movementGuid === movementGuid,
    );
    if (!record || !record.voidEligibility.ok) return false;
    this.patch({
      voidDraft: {
        originalMovementGuid: movementGuid,
        note: "",
        validationCode: null,
      },
      statusCode: null,
      hardwareWarnings: [],
    });
    return true;
  }

  public cancelVoid(): void {
    if (this.destroyed || this.state.busy) return;
    this.patch({ voidDraft: null });
  }

  public setVoidNote(note: string): void {
    const draft = this.state.voidDraft;
    if (this.destroyed || this.state.busy || !draft) return;
    if (note.length > CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH) return;
    this.patch({ voidDraft: { ...draft, note, validationCode: null } });
  }

  public confirmVoid(): Promise<void> {
    return this.runAction(() => this.performVoid());
  }

  private async performVoid(): Promise<void> {
    const draft = this.state.voidDraft;
    if (!draft) return;
    const record = this.state.records.find(
      (candidate) =>
        candidate.movement.movementGuid === draft.originalMovementGuid,
    );
    // 冲正前再看一次资格（列表可能是旧的）：不满足就禁止，库里的触发器还会再兜底。
    if (!record || !record.voidEligibility.ok) {
      this.patch({ voidDraft: null, statusCode: "void-not-allowed" });
      return;
    }
    const checked = validateCashMovementInput({
      reasonCode: record.movement.reasonCode,
      amountCents: record.movement.amountCents,
      note: draft.note,
      isVoid: true,
    });
    if (!checked.ok) {
      this.patch({
        voidDraft: {
          ...draft,
          validationCode:
            checked.code === "NOTE_REQUIRED" ? "VOID_NOTE_REQUIRED" : checked.code,
        },
      });
      return;
    }
    const movementGuid = this.options.createId();
    this.patch({ busy: true, statusCode: null, hardwareWarnings: [] });
    let result: CashMovementWriteResult;
    try {
      result = await this.options.runtime.voidMovement({
        movementGuid,
        originalMovementGuid: draft.originalMovementGuid,
        note: checked.note ?? "",
      });
    } catch {
      result = { kind: "failed" };
    }
    if (result.kind === "saved") {
      this.patch({ voidDraft: null, ...this.savedStatus("void-saved", result) });
      await this.refreshAfterWrite();
    } else {
      this.patch(this.failureStatus(result));
      if (result.kind === "void-not-allowed") {
        this.patch({ voidDraft: null });
        await this.refreshAfterWrite();
      }
    }
    this.patch({ busy: false });
  }

  // ───────────────────────── 补打 ─────────────────────────

  public reprint(movementGuid: string): Promise<void> {
    return this.runAction(async () => {
      this.patch({
        busy: true,
        statusCode: null,
        statusNumber: null,
        hardwareWarnings: [],
      });
      let kind: "printed" | "failed" | "not-found" = "failed";
      try {
        kind = (await this.options.runtime.reprint(movementGuid)).kind;
      } catch {
        kind = "failed";
      }
      this.patch({
        busy: false,
        statusCode: kind === "printed" ? "reprint-printed" : "reprint-failed",
      });
    });
  }

  // ───────────────────────── 今日记录 ─────────────────────────

  public async load(): Promise<void> {
    if (this.destroyed) return;
    const generation = ++this.loadGeneration;
    this.patch({ loadState: "loading" });
    try {
      const view = await this.options.runtime.listToday();
      if (this.destroyed || generation !== this.loadGeneration) return;
      const records = view.movements
        .map((movement): CashMovementRecordView => {
          const voided = movement.voidedByMovementGuid !== null;
          const kind: CashMovementRecordKind =
            movement.voidsMovementGuid !== null
              ? "void"
              : movement.direction === "In"
                ? "in"
                : "out";
          // 符号按记录自身方向取：冲正记录的方向与原记录相反，所以符号也相反。
          const signedCents =
            movement.direction === "In"
              ? movement.amountCents
              : -movement.amountCents;
          return {
            movement,
            kind,
            signedCents,
            voided,
            voidEligibility: canVoidCashMovement({
              movement,
              alreadyVoided: voided,
              todayBusinessDate: view.businessDate,
              latestDailyCloseSavedAtIso: view.latestDailyCloseSavedAtIso,
            }),
          };
        })
        // 最新在上；发生时间相同按流水号（入库顺序）倒序。
        .reverse();
      this.patch({
        loadState: "ready",
        records: Object.freeze(records),
        rejectedCount: view.movements.filter(
          (movement) => movement.uploadState === "rejected",
        ).length,
        todayCashInCents: view.cashInCents,
        todayCashOutCents: view.cashOutCents,
        businessTimeZone: view.businessTimeZone,
      });
    } catch {
      if (this.destroyed || generation !== this.loadGeneration) return;
      this.patch({ loadState: "failed" });
    }
  }

  // ───────────────────────── 内部 ─────────────────────────

  /** 防重入：已有动作在途时返回它，不再启动第二个。 */
  private runAction(action: () => Promise<void>): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.actionInFlight) return this.actionInFlight;
    const operation = action()
      .catch(() => {
        // 动作内部已把失败转成状态；这里兜底保证 busy 不会卡住。
        if (!this.destroyed) {
          this.patch({ busy: false, statusCode: "save-failed" });
        }
      })
      .finally(() => {
        if (this.actionInFlight === operation) this.actionInFlight = null;
      });
    this.actionInFlight = operation;
    return operation;
  }

  private async refreshAfterWrite(): Promise<void> {
    // 写入已成功：刷新今日记录失败不能把它改写成失败，只在记录页显示读取失败。
    await this.load();
  }

  private savedStatus(
    statusCode: "saved" | "void-saved",
    result: Extract<CashMovementWriteResult, { kind: "saved" }>,
  ): Pick<
    CashMovementState,
    "statusCode" | "statusNumber" | "hardwareWarnings"
  > {
    const warnings: CashMovementHardwareWarning[] = [];
    if (result.drawer === "failed") warnings.push("drawer");
    if (result.print !== "printed") warnings.push("print");
    return {
      statusCode,
      statusNumber: result.movement.displayNumber,
      hardwareWarnings: Object.freeze(warnings),
    };
  }

  private failureStatus(
    result: Exclude<CashMovementWriteResult, { kind: "saved" }>,
  ): Pick<CashMovementState, "statusCode" | "statusNumber" | "hardwareWarnings"> {
    let statusCode: CashMovementStatusCode;
    switch (result.kind) {
      case "denied":
        statusCode = result.reason === "cancelled" ? "cancelled" : "denied";
        break;
      case "void-not-allowed":
        statusCode = "void-not-allowed";
        break;
      default:
        statusCode = "save-failed";
    }
    return {
      statusCode,
      statusNumber: null,
      hardwareWarnings: Object.freeze([]),
    };
  }

  private patch(patch: Partial<CashMovementState>): void {
    if (this.destroyed) return;
    this.state = Object.freeze({ ...this.state, ...patch });
    for (const listener of [...this.listeners]) listener();
  }
}

/** 整数分 → "$12.50"。 */
export function formatCashMovementMoney(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  const value = Math.abs(cents);
  return `${sign}$${Math.floor(value / 100)}.${String(value % 100).padStart(2, "0")}`;
}
