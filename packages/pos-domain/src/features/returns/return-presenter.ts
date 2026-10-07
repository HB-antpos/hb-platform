import {
  computeRequiredVoucherRefundCents,
  selectedLineAmountCents,
  type ReturnErrorCode,
  type ReturnSourceKind,
  type ReturnTenderMethod,
} from "./return-domain";
import {
  ReturnWorkflow,
  returnErrorCode,
  type ReturnExecutionOutcome,
  type ReturnWorkflowSnapshot,
} from "./return-workflow";

import type { UpdateOperationLeasePort } from "../../core/contracts/update-operation-lease";

export type ReturnPresenterPhase =
  | "search"
  | "loading"
  | "selecting"
  | "submitting"
  | "unknown"
  | "success"
  | "failed";

export type ReturnPresenterLine = Readonly<{
  id: string;
  sourceKind: ReturnSourceKind;
  displayName: string;
  itemNumber: string | null;
  availableQuantity: number;
  selectedQuantity: number;
  amountCents: number;
}>;

export type ReturnPresenterCapacity = Readonly<{
  method: ReturnTenderMethod;
  remainingCents: number;
  /** 该方式下至少有一笔额度可原路退回；全部为代替退款额度时为 false。 */
  originalRefundAvailable: boolean;
}>;

export type ReturnPresenterState = Readonly<{
  phase: ReturnPresenterPhase;
  mode: "receipt" | "no-receipt";
  busy: boolean;
  orderSummary: string | null;
  loadedFrom: "remote" | "local" | null;
  returnRecordsMayBeStale: boolean;
  lines: readonly ReturnPresenterLine[];
  capacities: readonly ReturnPresenterCapacity[];
  /** 为 null 表示未显式选择（有单退货默认按原支付方式退回）。 */
  preferredMethod: ReturnTenderMethod | null;
  selectedTotalCents: number;
  /** 代金券买的部分必须退代金券的最低金额（只读提示，计划阶段同口径强制）。 */
  requiredVoucherRefundCents: number;
  canConfirm: boolean;
  errorCode: ReturnErrorCode | null;
  result: Readonly<{
    returnOrderSummary: string;
    refundAmountCents: number;
  }> | null;
}>;

const INITIAL_STATE: ReturnPresenterState = {
  phase: "search",
  mode: "receipt",
  busy: false,
  orderSummary: null,
  loadedFrom: null,
  returnRecordsMayBeStale: false,
  lines: [],
  capacities: [],
  preferredMethod: null,
  selectedTotalCents: 0,
  requiredVoucherRefundCents: 0,
  canConfirm: false,
  errorCode: null,
  result: null,
};

export type ReturnPresenterOptions = Readonly<{
  operationLease?: UpdateOperationLeasePort;
}>;

/**
 * Screen 只消费此脱敏状态。可信订单、明细、容量和恢复键仅留在 workflow 私有图中。
 */
export class ReturnPresenter {
  private state: ReturnPresenterState = INITIAL_STATE;
  private readonly listeners = new Set<() => void>();
  private readonly publicIdBySelectionKey = new Map<string, string>();
  private readonly selectionKeyByPublicId = new Map<string, string>();
  private actionInFlight: Promise<boolean> | null = null;
  private destroyed = false;
  private nextPublicLineId = 1;

  public constructor(
    private readonly workflow: ReturnWorkflow,
    private readonly options: ReturnPresenterOptions = {},
  ) {
    const snapshot = workflow.getSnapshot();
    if (snapshot.status === "unknown") {
      this.syncDraft(snapshot, "unknown");
      this.patch({
        errorCode: "RETURN_UNKNOWN_RECOVERY_REQUIRED",
      });
    }
  }

  public readonly getState = (): ReturnPresenterState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public destroy(): void {
    this.destroyed = true;
    this.listeners.clear();
    this.publicIdBySelectionKey.clear();
    this.selectionKeyByPublicId.clear();
  }

  public loadReceipt(query: string): Promise<boolean> {
    return this.runExclusive(async () => {
      this.patch({ phase: "loading", errorCode: null, result: null });
      try {
        const snapshot = await this.workflow.loadReceipt(query);
        this.syncDraft(snapshot, "selecting");
        return true;
      } catch (error) {
        this.patch({
          phase: "search",
          errorCode: returnErrorCode(error, "RETURN_LOOKUP_FAILED"),
        });
        return false;
      }
    });
  }

  public beginNoReceipt(): boolean {
    if (this.actionInFlight || this.destroyed) return false;
    try {
      const snapshot = this.workflow.beginNoReceipt();
      this.resetPublicLineIds();
      this.syncDraft(snapshot, "selecting");
      return true;
    } catch (error) {
      this.patch({
        errorCode: returnErrorCode(error, "RETURN_SESSION_EXPIRED"),
      });
      return false;
    }
  }

  public addNoReceiptProduct(query: string): Promise<boolean> {
    return this.runExclusive(async () => {
      try {
        const snapshot = await this.workflow.addNoReceiptProduct(query);
        this.syncDraft(snapshot, "selecting");
        return true;
      } catch (error) {
        this.patch({
          phase: "selecting",
          errorCode: returnErrorCode(error, "RETURN_LOOKUP_FAILED"),
        });
        return false;
      }
    });
  }

  public addNoReceiptOpenItem(
    displayName: string,
    unitRefundCents: number,
  ): Promise<boolean> {
    return this.runExclusive(async () => {
      try {
        const snapshot = await this.workflow.addNoReceiptOpenItem({
          displayName,
          unitRefundCents,
        });
        this.syncDraft(snapshot, "selecting");
        return true;
      } catch (error) {
        this.patch({
          phase: "selecting",
          errorCode: returnErrorCode(error, "RETURN_OPEN_ITEM_INVALID"),
        });
        return false;
      }
    });
  }

  public incrementLine(publicLineId: string): boolean {
    const line = this.state.lines.find((candidate) => candidate.id === publicLineId);
    if (!line) return this.fail("RETURN_LINE_NOT_FOUND");
    return this.setLineQuantity(publicLineId, line.selectedQuantity + 1);
  }

  public decrementLine(publicLineId: string): boolean {
    const line = this.state.lines.find((candidate) => candidate.id === publicLineId);
    if (!line) return this.fail("RETURN_LINE_NOT_FOUND");
    return this.setLineQuantity(
      publicLineId,
      Math.max(0, line.selectedQuantity - 1),
    );
  }

  public setLineQuantity(
    publicLineId: string,
    quantity: number,
  ): boolean {
    if (this.actionInFlight || this.destroyed) {
      return this.fail("RETURN_OPERATION_IN_PROGRESS");
    }
    const selectionKey = this.selectionKeyByPublicId.get(publicLineId);
    if (!selectionKey) return this.fail("RETURN_LINE_NOT_FOUND");
    try {
      const snapshot = this.workflow.setQuantity(selectionKey, quantity);
      this.syncDraft(snapshot, "selecting");
      return true;
    } catch (error) {
      return this.fail(returnErrorCode(error, "RETURN_QUANTITY_INVALID"));
    }
  }

  public selectMethod(method: ReturnTenderMethod): boolean {
    if (this.actionInFlight || this.destroyed) {
      return this.fail("RETURN_OPERATION_IN_PROGRESS");
    }
    try {
      const snapshot = this.workflow.setPreferredMethod(method);
      this.syncDraft(snapshot, "selecting");
      return true;
    } catch (error) {
      return this.fail(returnErrorCode(error, "RETURN_SESSION_EXPIRED"));
    }
  }

  public confirm(): Promise<boolean> {
    return this.runExclusive(async () => {
      this.patch({ phase: "submitting", errorCode: null });
      try {
        const outcome = await this.workflow.confirm();
        this.applyOutcome(outcome);
        return outcome.status === "completed";
      } catch (error) {
        const code = returnErrorCode(error, "RETURN_EXECUTION_FAILED");
        const workflowStatus = this.workflow.getSnapshot().status;
        this.patch({
          phase:
            workflowStatus === "unknown"
              ? "unknown"
              : isExecutionFailure(code)
                ? "failed"
                : "selecting",
          errorCode: code,
        });
        return false;
      }
    });
  }

  public recoverUnknown(): Promise<boolean> {
    return this.runExclusive(async () => {
      this.patch({ phase: "submitting", errorCode: null });
      try {
        const outcome = await this.workflow.recoverUnknown();
        this.applyOutcome(outcome);
        return outcome.status === "completed";
      } catch (error) {
        const workflowStatus = this.workflow.getSnapshot().status;
        this.patch({
          phase: workflowStatus === "unknown" ? "unknown" : "failed",
          errorCode: returnErrorCode(error, "RETURN_RECOVERY_FAILED"),
        });
        return false;
      }
    });
  }

  public reset(): boolean {
    if (this.actionInFlight || this.destroyed) return false;
    try {
      const snapshot = this.workflow.reset();
      this.resetPublicLineIds();
      this.state = {
        ...INITIAL_STATE,
        mode: snapshot.caseKind,
      };
      this.emit();
      return true;
    } catch (error) {
      return this.fail(returnErrorCode(error, "RETURN_SESSION_EXPIRED"));
    }
  }

  private runExclusive(operation: () => Promise<boolean>): Promise<boolean> {
    if (this.destroyed || this.actionInFlight) return Promise.resolve(false);
    this.patch({ busy: true });
    const execute = () => operation();
    const pending = (
      this.options.operationLease
        ? this.options.operationLease.runOperation(execute)
        : execute()
    )
      .catch(() => {
        this.patch({
          phase: "failed",
          errorCode: "RETURN_EXECUTION_FAILED",
        });
        return false;
      })
      .finally(() => {
        if (this.actionInFlight === pending) {
          this.actionInFlight = null;
          if (!this.destroyed) this.patch({ busy: false });
        }
      });
    this.actionInFlight = pending;
    return pending;
  }

  private applyOutcome(outcome: ReturnExecutionOutcome): void {
    const snapshot = this.workflow.getSnapshot();
    if (outcome.status === "unknown") {
      this.syncDraft(snapshot, "unknown");
      this.patch({ errorCode: "RETURN_UNKNOWN_RECOVERY_REQUIRED" });
      return;
    }
    if (outcome.status === "declined") {
      this.syncDraft(snapshot, "failed");
      this.patch({ errorCode: "RETURN_EXECUTION_DECLINED" });
      return;
    }
    this.syncDraft(snapshot, "success");
    this.patch({
      result: {
        returnOrderSummary: maskIdentifier(outcome.returnOrderGuid),
        refundAmountCents: snapshot.selectedTotalCents,
      },
      errorCode: null,
    });
  }

  private syncDraft(
    snapshot: ReturnWorkflowSnapshot,
    phase: ReturnPresenterPhase,
  ): void {
    const lines = snapshot.lines.map((line) => {
      const id = this.publicLineId(line.selectionKey);
      return {
        id,
        sourceKind: line.sourceKind,
        displayName: line.displayName,
        itemNumber: line.itemNumber,
        availableQuantity: line.availableQuantity,
        selectedQuantity: line.selectedQuantity,
        amountCents: selectedLineAmountCents(
          line,
          line.selectedQuantity,
        ),
      };
    });
    const capacities = aggregateCapacities(snapshot);
    this.state = {
      phase,
      mode: snapshot.caseKind,
      busy: this.state.busy,
      orderSummary: snapshot.receiptLabel
        ? maskIdentifier(snapshot.receiptLabel)
        : null,
      loadedFrom: snapshot.loadedFrom,
      returnRecordsMayBeStale: snapshot.returnRecordsMayBeStale,
      lines,
      capacities,
      // 无单退货默认现金；有单退货未选择时保持 null（按原支付方式退回）。
      preferredMethod:
        snapshot.preferredMethod ??
        (snapshot.caseKind === "no-receipt" ? "cash" : null),
      selectedTotalCents: snapshot.selectedTotalCents,
      requiredVoucherRefundCents:
        snapshot.caseKind === "receipt"
          ? computeRequiredVoucherRefundCents(
              snapshot.selectedTotalCents,
              snapshot.voucherFundedBasis,
            )
          : 0,
      canConfirm:
        snapshot.selectedTotalCents > 0 &&
        snapshot.status === "draft",
      errorCode: snapshot.lastErrorCode,
      result: this.state.result,
    };
    this.emit();
  }

  private publicLineId(selectionKey: string): string {
    const existing = this.publicIdBySelectionKey.get(selectionKey);
    if (existing) return existing;
    const publicId = `return-line-${this.nextPublicLineId}`;
    this.nextPublicLineId += 1;
    this.publicIdBySelectionKey.set(selectionKey, publicId);
    this.selectionKeyByPublicId.set(publicId, selectionKey);
    return publicId;
  }

  private resetPublicLineIds(): void {
    this.publicIdBySelectionKey.clear();
    this.selectionKeyByPublicId.clear();
    this.nextPublicLineId = 1;
  }

  private fail(code: ReturnErrorCode): false {
    this.patch({ errorCode: code });
    return false;
  }

  private patch(patch: Partial<ReturnPresenterState>): void {
    if (this.destroyed) return;
    this.state = { ...this.state, ...patch };
    this.emit();
  }

  private emit(): void {
    for (const listener of [...this.listeners]) {
      try {
        listener();
      } catch {
        // 已卸载页面不能阻止其他订阅者观察退款状态。
      }
    }
  }
}

function aggregateCapacities(
  snapshot: ReturnWorkflowSnapshot,
): readonly ReturnPresenterCapacity[] {
  const totals = new Map<ReturnTenderMethod, number>();
  const originalRefundable = new Set<ReturnTenderMethod>();
  for (const capacity of snapshot.tenderCapacities) {
    const next = (totals.get(capacity.method) ?? 0) + capacity.remainingCents;
    if (!Number.isSafeInteger(next)) continue;
    totals.set(capacity.method, next);
    if (capacity.substituteOnly !== true) originalRefundable.add(capacity.method);
  }
  return (["cash", "card", "voucher", "installment"] as const)
    .filter((method) => totals.has(method))
    .map((method) => ({
      method,
      remainingCents: totals.get(method) ?? 0,
      originalRefundAvailable: originalRefundable.has(method),
    }));
}

function maskIdentifier(value: string): string {
  const normalized = value.trim().replace(/\s+/g, "");
  const suffix = normalized.slice(-6);
  return suffix ? `••••${suffix}` : "••••••";
}

function isExecutionFailure(code: ReturnErrorCode): boolean {
  return (
    code === "RETURN_EXECUTION_DECLINED" ||
    code === "RETURN_EXECUTION_FAILED" ||
    code === "RETURN_SESSION_EXPIRED"
  );
}
