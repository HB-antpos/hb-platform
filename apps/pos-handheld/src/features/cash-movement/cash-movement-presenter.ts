import {
  CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH,
  canVoidCashMovement,
  validateCashMovementInput,
  type CashMovementReasonCode,
  type CashMovementSummary,
  type CashMovementValidationCode,
  type CashMovementVoidEligibility,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

import {
  CASH_MOVEMENT_QUICK_AMOUNTS_CENTS,
  amountTextToCents,
  applyAmountKey,
  centsToAmountText,
  type CashMovementKeypadKey,
} from "./cash-movement-amount";
import type { CashMovementServicePort } from "./cash-movement-runtime";

export type CashMovementTab = "in" | "out" | "records";

/** 存入只列前两项、取出只列后两项，默认选第一项。 */
export const CASH_MOVEMENT_REASONS_BY_TAB: Readonly<
  Record<"in" | "out", readonly CashMovementReasonCode[]>
> = Object.freeze({
  in: Object.freeze(["ChangeTopUp", "OtherIn"] as const),
  out: Object.freeze(["SafeDrop", "PaidOut"] as const),
});

export type CashMovementStatusCode =
  | "reason-required"
  | "amount-invalid"
  | "amount-too-large"
  | "note-required"
  | "note-too-long"
  | "void-note-required"
  | "saved"
  | "saved-drawer-failed"
  | "saved-print-failed"
  | "saved-drawer-print-failed"
  | "save-failed"
  | "authorization-denied"
  | "authorization-cancelled"
  | "void-saved"
  | "void-saved-print-failed"
  | "void-saved-drawer-failed"
  | "void-saved-drawer-print-failed"
  | "void-failed"
  | "void-not-allowed"
  | "reprint-printed"
  | "reprint-failed"
  | "load-failed";

export type CashMovementRecordKind = "in" | "out" | "void";

/** 今日记录里一行的展示模型（最新在前）。 */
export type CashMovementRecordView = Readonly<{
  movement: LocalCashMovement;
  kind: CashMovementRecordKind;
  /** 本条原记录已被冲正（界面显示「已冲正」）。 */
  isVoided: boolean;
  voidEligibility: CashMovementVoidEligibility;
}>;

export type CashMovementVoidDraft = Readonly<{
  movementGuid: string;
  note: string;
}>;

export type CashMovementState = Readonly<{
  kind: "idle" | "loading" | "ready" | "failed";
  tab: CashMovementTab;
  busy: boolean;
  reasonCode: CashMovementReasonCode;
  amountText: string;
  amountCents: number;
  note: string;
  records: readonly CashMovementRecordView[];
  summary: CashMovementSummary | null;
  rejectedCount: number;
  /** 门店营业时区（记录时间按它显示）；读取前为 null。 */
  businessTimeZone: string | null;
  voidDraft: CashMovementVoidDraft | null;
  statusCode: CashMovementStatusCode | null;
}>;

export type CashMovementPresenterOptions = Readonly<{
  service: CashMovementServicePort;
}>;

const VALIDATION_STATUS: Readonly<
  Partial<Record<CashMovementValidationCode, CashMovementStatusCode>>
> = {
  REASON_INVALID: "reason-required",
  AMOUNT_INVALID: "amount-invalid",
  AMOUNT_TOO_LARGE: "amount-too-large",
  NOTE_REQUIRED: "note-required",
  NOTE_TOO_LONG: "note-too-long",
};

/**
 * 钱箱弹窗的展示状态机（照日结 / 设置 presenter 的「patch + 订阅」风格）：
 * - 所有会产生副作用的动作都走 runAction：同一时刻只允许一个在途，重复点击拿到的是同一个 Promise，
 *   服务只会被调用一次，因此同一次提交不会生成第二个流水 guid；
 * - 表单校验用共享层 validateCashMovementInput，与服务端口径一致，备注限长靠输入框 maxLength 与校验，不静默截断；
 * - 保存成功后若开钱箱 / 打印失败，只改提示文案，记录已经入库不回滚。
 */
export class CashMovementPresenter {
  private readonly listeners = new Set<() => void>();
  private state: CashMovementState;
  private destroyed = false;
  private actionInFlight: Promise<void> | null = null;
  private loadGeneration = 0;

  public constructor(private readonly options: CashMovementPresenterOptions) {
    this.state = Object.freeze({
      kind: "idle",
      tab: "in",
      busy: false,
      reasonCode: CASH_MOVEMENT_REASONS_BY_TAB.in[0]!,
      amountText: "",
      amountCents: 0,
      note: "",
      records: Object.freeze([]),
      summary: null,
      rejectedCount: 0,
      businessTimeZone: null,
      voidDraft: null,
      statusCode: null,
    });
  }

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

  /** 读取今日记录与汇总（打开弹窗、切到「今日记录」、动作完成后都会调用）。 */
  public async load(options: Readonly<{ quiet?: boolean }> = {}): Promise<void> {
    if (this.destroyed) return;
    const generation = ++this.loadGeneration;
    const previousKind = this.state.kind;
    this.patch({ kind: previousKind === "ready" ? "ready" : "loading" });
    try {
      const [snapshot, summary] = await Promise.all([
        this.options.service.listToday(),
        this.options.service.summary(),
      ]);
      if (!this.isCurrentLoad(generation)) return;
      const records = snapshot.movements
        .map((movement): CashMovementRecordView => ({
          movement,
          kind:
            movement.voidsMovementGuid !== null ? "void" : movement.direction === "In" ? "in" : "out",
          isVoided: movement.voidedByMovementGuid !== null,
          // 冲正资格只用共享层的规则判断；库里的触发器与唯一索引再兜底一次。
          voidEligibility: canVoidCashMovement({
            movement,
            alreadyVoided: movement.voidedByMovementGuid !== null,
            todayBusinessDate: snapshot.businessDate,
            latestDailyCloseSavedAtIso: snapshot.latestDailyCloseSavedAtIso,
          }),
        }))
        // 服务按发生时间升序返回；界面最新在上。
        .reverse();
      this.patch({
        kind: "ready",
        records: Object.freeze(records),
        summary,
        businessTimeZone: snapshot.businessTimeZone,
        rejectedCount: records.filter(
          (record) => record.movement.uploadState === "rejected",
        ).length,
        // 状态码只在加载失败时被覆盖；成功加载保留动作产生的提示。
        statusCode:
          this.state.statusCode === "load-failed" ? null : this.state.statusCode,
      });
    } catch {
      if (!this.isCurrentLoad(generation)) return;
      // 动作（保存 / 冲正）成功后的刷新失败不能盖掉「已保存」的提示：记录已经入库。
      if (options.quiet) this.patch({ kind: previousKind });
      else this.patch({ kind: "failed", statusCode: "load-failed" });
    }
  }

  public setTab(tab: CashMovementTab): void {
    if (this.destroyed || this.state.busy || tab === this.state.tab) return;
    if (tab === "records") {
      this.patch({ tab, statusCode: null, voidDraft: null });
      void this.load();
      return;
    }
    // 存入 / 取出互换：表单整体复位并默认选第一项原因，避免把存入的金额带到取出。
    this.patch({
      tab,
      reasonCode: CASH_MOVEMENT_REASONS_BY_TAB[tab][0]!,
      amountText: "",
      amountCents: 0,
      note: "",
      statusCode: null,
      voidDraft: null,
    });
  }

  public selectReason(reasonCode: CashMovementReasonCode): void {
    if (this.destroyed || this.state.busy || this.state.tab === "records") return;
    if (!CASH_MOVEMENT_REASONS_BY_TAB[this.state.tab].includes(reasonCode)) return;
    this.patch({ reasonCode, statusCode: null });
  }

  public pressAmountKey(key: CashMovementKeypadKey): void {
    if (this.destroyed || this.state.busy || this.state.tab === "records") return;
    const amountText = applyAmountKey(this.state.amountText, key);
    this.patch({
      amountText,
      amountCents: amountTextToCents(amountText),
      statusCode: null,
    });
  }

  /** 快捷金额：直接替换当前金额。 */
  public setQuickAmount(cents: number): void {
    if (this.destroyed || this.state.busy || this.state.tab === "records") return;
    if (!CASH_MOVEMENT_QUICK_AMOUNTS_CENTS.includes(cents)) return;
    this.patch({
      amountText: centsToAmountText(cents),
      amountCents: cents,
      statusCode: null,
    });
  }

  /** 备注原样保存（输入框 maxLength 负责限长）；超长由校验报错，不截断。 */
  public setNote(note: string): void {
    if (this.destroyed || this.state.busy) return;
    this.patch({ note, statusCode: null });
  }

  public submit(): Promise<void> {
    return this.runAction(() => this.performSubmit());
  }

  public beginVoid(movementGuid: string): void {
    if (this.destroyed || this.state.busy) return;
    const record = this.state.records.find(
      (candidate) => candidate.movement.movementGuid === movementGuid,
    );
    // 不满足冲正资格的记录不能进入冲正流程（按钮本身也是禁用的）。
    if (!record || !record.voidEligibility.ok) return;
    this.patch({ voidDraft: { movementGuid, note: "" }, statusCode: null });
  }

  public setVoidNote(note: string): void {
    if (this.destroyed || this.state.busy || !this.state.voidDraft) return;
    this.patch({ voidDraft: { ...this.state.voidDraft, note }, statusCode: null });
  }

  public cancelVoid(): void {
    if (this.destroyed || this.state.busy) return;
    this.patch({ voidDraft: null, statusCode: null });
  }

  public confirmVoid(): Promise<void> {
    return this.runAction(() => this.performVoid());
  }

  public reprint(movementGuid: string): Promise<void> {
    return this.runAction(async () => {
      try {
        const outcome = await this.options.service.reprint(movementGuid);
        this.patch({
          statusCode: outcome.kind === "printed" ? "reprint-printed" : "reprint-failed",
        });
      } catch {
        this.patch({ statusCode: "reprint-failed" });
      }
    });
  }

  private async performSubmit(): Promise<void> {
    const { reasonCode, amountCents, note } = this.state;
    // 本地先校验：不满足就不去打扰服务（更不会请求主管授权）。
    const checked = validateCashMovementInput({ reasonCode, amountCents, note });
    if (!checked.ok) {
      this.patch({ statusCode: VALIDATION_STATUS[checked.code] ?? "save-failed" });
      return;
    }
    try {
      const outcome = await this.options.service.submit({
        reasonCode: checked.reasonCode,
        amountCents: checked.amountCents,
        note,
      });
      switch (outcome.kind) {
        case "saved":
          // 记录已入库：清空表单；开钱箱 / 打印失败只影响提示。
          this.patch({
            amountText: "",
            amountCents: 0,
            note: "",
            statusCode:
              outcome.drawer === "failed" && outcome.print === "failed"
                ? "saved-drawer-print-failed"
                : outcome.drawer === "failed"
                  ? "saved-drawer-failed"
                  : outcome.print === "failed"
                    ? "saved-print-failed"
                    : "saved",
          });
          await this.load({ quiet: true });
          return;
        case "invalid":
          this.patch({ statusCode: VALIDATION_STATUS[outcome.code] ?? "save-failed" });
          return;
        case "denied":
          this.patch({
            statusCode:
              outcome.reason === "cancelled"
                ? "authorization-cancelled"
                : "authorization-denied",
          });
          return;
        case "failed":
          this.patch({ statusCode: "save-failed" });
          return;
      }
    } catch {
      this.patch({ statusCode: "save-failed" });
    }
  }

  private async performVoid(): Promise<void> {
    const draft = this.state.voidDraft;
    if (!draft) return;
    // 冲正备注 >= 2 字：与普通记录同一套 trim 规则，原因码 / 金额只是占位，由服务按原记录复核。
    if (draft.note.trim().length < 2) {
      this.patch({ statusCode: "void-note-required" });
      return;
    }
    if (draft.note.trim().length > CASH_MOVEMENT_MAXIMUM_NOTE_LENGTH) {
      this.patch({ statusCode: "note-too-long" });
      return;
    }
    try {
      const outcome = await this.options.service.void({
        movementGuid: draft.movementGuid,
        note: draft.note,
      });
      switch (outcome.kind) {
        case "saved":
          this.patch({
            voidDraft: null,
            statusCode:
              outcome.drawer === "failed" && outcome.print === "failed"
                ? "void-saved-drawer-print-failed"
                : outcome.drawer === "failed"
                  ? "void-saved-drawer-failed"
                  : outcome.print === "failed"
                    ? "void-saved-print-failed"
                    : "void-saved",
          });
          await this.load({ quiet: true });
          return;
        case "invalid":
          this.patch({
            statusCode:
              outcome.code === "NOTE_REQUIRED"
                ? "void-note-required"
                : (VALIDATION_STATUS[outcome.code] ?? "void-failed"),
          });
          return;
        case "not-allowed":
          // 资格已变（例如刚保存了日结）：收起冲正表单并刷新，让按钮反映最新状态。
          this.patch({ voidDraft: null, statusCode: "void-not-allowed" });
          await this.load({ quiet: true });
          return;
        case "denied":
          this.patch({
            statusCode:
              outcome.reason === "cancelled"
                ? "authorization-cancelled"
                : "authorization-denied",
          });
          return;
        case "failed":
          this.patch({ statusCode: "void-failed" });
          return;
      }
    } catch {
      this.patch({ statusCode: "void-failed" });
    }
  }

  /** 同一时刻只允许一个在途动作；重复触发拿到同一个 Promise，不会重复调用服务。 */
  private runAction(action: () => Promise<void>): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.actionInFlight) return this.actionInFlight;
    this.patch({ busy: true, statusCode: null });
    const operation = action().finally(() => {
      if (this.actionInFlight === operation) {
        this.actionInFlight = null;
        this.patch({ busy: false });
      }
    });
    this.actionInFlight = operation;
    return operation;
  }

  private isCurrentLoad(generation: number): boolean {
    return !this.destroyed && generation === this.loadGeneration;
  }

  private patch(patch: Partial<CashMovementState>): void {
    if (this.destroyed) return;
    this.state = Object.freeze({ ...this.state, ...patch });
    for (const listener of this.listeners) listener();
  }
}
