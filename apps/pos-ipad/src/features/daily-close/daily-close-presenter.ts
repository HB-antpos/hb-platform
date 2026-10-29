import {
  resolveDailyCloseAccess,
  type DailyCloseAccess,
} from "@hb/pos-domain/features/daily-close/daily-close-authorization";
import {
  buildDailyCloseArchiveCommit,
  businessDateInTimeZone,
  dailyCloseBusinessDayScope,
  type DailyCloseIdentity,
} from "./daily-close-domain";

import {
  AUD_CASH_DENOMINATIONS_CENTS,
  auditActorPayload,
  normalizeDailyCloseCounts,
  type AuditRepositoryPort,
  type AudCashDenominationCents,
  type DailyCloseArchive,
  type DailyCloseDenominationCount,
  type DailyCloseRepositoryPort,
  type DailyCloseSummary,
} from "@/core/contracts";
import {
  buildDailyCloseReceipt,
  type DailyCloseReceiptDocument,
} from "@hb/pos-receipt-core/features/receipts/daily-close-receipt";
import type {
  ReceiptLocale,
  ReceiptPaper,
} from "@hb/pos-receipt-core/features/receipts/receipt-document";

export type DailyCloseStatusCode =
  | "invalid-business-date"
  | "load-failed"
  | "permission-required"
  | "reprint-cash-slip-failed"
  | "reprint-failed"
  | "reprint-printed"
  | "save-failed"
  | "saved-cash-slip-failed"
  | "saved-print-failed"
  | "saved-printed"
  | "select-archive-required";

export type DailyCloseState = Readonly<{
  access: DailyCloseAccess;
  activePane: "count" | "history";
  archives: readonly DailyCloseArchive[];
  businessDate: string;
  busy: boolean;
  coinsSubtotalCents: number;
  countedCashCents: number;
  counts: readonly DailyCloseDenominationCount[];
  kind: "idle" | "loading" | "ready" | "unauthorized" | "failed";
  notesSubtotalCents: number;
  selectedArchive: DailyCloseArchive | null;
  statusCode: DailyCloseStatusCode | null;
  summary: DailyCloseSummary | null;
  varianceCents: number;
}>;

export type DailyClosePrintJob = Readonly<{
  archive: DailyCloseArchive;
  document: DailyCloseReceiptDocument;
  reprint: boolean;
}>;

export interface DailyClosePrinterPort {
  print(job: DailyClosePrintJob): Promise<void>;
}

export type DailyClosePresenterOptions = Readonly<{
  businessTimeZone: string;
  createId(): string;
  /** 日结补打与归档保存解耦：审计故障不得反向改变已发生的硬件结果。 */
  audit: Pick<AuditRepositoryPort, "append">;
  identity: DailyCloseIdentity;
  initialBusinessDate?: string;
  now(): Date;
  printer: DailyClosePrinterPort;
  receiptLocale: ReceiptLocale;
  receiptPaper: ReceiptPaper;
  repository: DailyCloseRepositoryPort;
  brandName?: string;
  storeName: string;
  returnPolicy?: string;
  historyLimit?: number;
}>;

export class DailyClosePresenter {
  private readonly access: DailyCloseAccess;
  private readonly listeners = new Set<() => void>();
  private readonly historyLimit: number;
  private state: DailyCloseState;
  private destroyed = false;
  private loadGeneration = 0;
  private actionInFlight: Promise<void> | null = null;

  public constructor(private readonly options: DailyClosePresenterOptions) {
    this.access = resolveDailyCloseAccess(options.identity.permissions);
    this.historyLimit = normalizeHistoryLimit(options.historyLimit ?? 100);
    const businessDate =
      options.initialBusinessDate ??
      businessDateInTimeZone(options.now(), options.businessTimeZone);
    // 构造期先验证日期与可信门店/设备，避免 UI 首刷后才暴露组合错误。
    dailyCloseBusinessDayScope({
      businessDate,
      businessTimeZone: options.businessTimeZone,
      deviceCode: options.identity.deviceCode,
      storeCode: options.identity.storeCode,
    });
    const totals = countTotals(zeroCounts(), 0);
    this.state = Object.freeze({
      access: this.access,
      activePane: "count",
      archives: Object.freeze([]),
      businessDate,
      busy: false,
      ...totals,
      kind: this.access.canView ? "idle" : "unauthorized",
      selectedArchive: null,
      statusCode: this.access.canView ? null : "permission-required",
      summary: null,
    });
  }

  public readonly getState = (): DailyCloseState => this.state;

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

  public async load(): Promise<void> {
    if (this.destroyed) return;
    if (!this.access.canView) {
      this.patch({
        kind: "unauthorized",
        statusCode: "permission-required",
      });
      return;
    }
    const generation = ++this.loadGeneration;
    let scope;
    try {
      scope = dailyCloseBusinessDayScope({
        businessDate: this.state.businessDate,
        businessTimeZone: this.options.businessTimeZone,
        deviceCode: this.options.identity.deviceCode,
        storeCode: this.options.identity.storeCode,
      });
    } catch {
      this.patch({
        kind: "failed",
        statusCode: "invalid-business-date",
      });
      return;
    }
    this.patch({ kind: "loading", statusCode: null });
    try {
      const [summary, archives] = await Promise.all([
        this.options.repository.summarize(scope),
        this.options.repository.listArchives({
          businessDate: scope.businessDate,
          deviceCode: scope.deviceCode,
          limit: this.historyLimit,
          storeCode: scope.storeCode,
        }),
      ]);
      if (!this.isCurrentLoad(generation)) return;
      const selectedArchive =
        archives.find(
          (archive) =>
            archive.closeId === this.state.selectedArchive?.closeId,
        ) ??
        archives[0] ??
        null;
      this.patch({
        archives: Object.freeze([...archives]),
        kind: "ready",
        selectedArchive,
        statusCode: null,
        summary,
        ...countTotals(this.state.counts, summary.expectedCashCents),
      });
    } catch {
      if (!this.isCurrentLoad(generation)) return;
      this.patch({ kind: "failed", statusCode: "load-failed" });
    }
  }

  public setBusinessDate(businessDate: string): boolean {
    if (this.destroyed || this.state.busy) return false;
    let normalizedBusinessDate: string;
    try {
      normalizedBusinessDate = dailyCloseBusinessDayScope({
        businessDate,
        businessTimeZone: this.options.businessTimeZone,
        deviceCode: this.options.identity.deviceCode,
        storeCode: this.options.identity.storeCode,
      }).businessDate;
    } catch {
      this.patch({ statusCode: "invalid-business-date" });
      return false;
    }
    if (normalizedBusinessDate === this.state.businessDate) {
      this.patch({ statusCode: null });
      return true;
    }
    this.loadGeneration += 1;
    const totals = countTotals(zeroCounts(), 0);
    this.patch({
      activePane: "count",
      archives: Object.freeze([]),
      businessDate: normalizedBusinessDate,
      ...totals,
      kind: "idle",
      selectedArchive: null,
      statusCode: null,
      summary: null,
    });
    return true;
  }

  public setCount(
    denominationCents: number,
    quantity: number,
  ): boolean {
    if (
      this.destroyed ||
      this.state.busy ||
      !this.access.canSave ||
      !AUD_CASH_DENOMINATIONS_CENTS.includes(
        denominationCents as AudCashDenominationCents,
      ) ||
      !Number.isSafeInteger(quantity) ||
      quantity < 0
    ) {
      if (!this.access.canSave) {
        this.patch({ statusCode: "permission-required" });
      }
      return false;
    }
    const counts = normalizeDailyCloseCounts(
      this.state.counts.map((entry) => ({
        denominationCents: entry.denominationCents,
        quantity:
          entry.denominationCents === denominationCents
            ? quantity
            : entry.quantity,
      })),
    );
    this.patch({
      statusCode: null,
      ...countTotals(
        counts,
        this.state.summary?.expectedCashCents ?? 0,
      ),
    });
    return true;
  }

  public saveAndPrint(): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.actionInFlight) return this.actionInFlight;
    const operation = this.performSaveAndPrint().finally(() => {
      if (this.actionInFlight === operation) this.actionInFlight = null;
    });
    this.actionInFlight = operation;
    return operation;
  }

  public showCount(): void {
    if (!this.destroyed) this.patch({ activePane: "count" });
  }

  public showHistory(): void {
    if (!this.destroyed) this.patch({ activePane: "history" });
  }

  public selectArchive(closeId: string): void {
    if (this.destroyed) return;
    const selectedArchive =
      this.state.archives.find(
        (archive) => archive.closeId === closeId,
      ) ?? null;
    if (!selectedArchive) return;
    this.patch({ activePane: "history", selectedArchive, statusCode: null });
  }

  public reprintSelected(): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.actionInFlight) return this.actionInFlight;
    const operation = this.performReprint().finally(() => {
      if (this.actionInFlight === operation) this.actionInFlight = null;
    });
    this.actionInFlight = operation;
    return operation;
  }

  private async performSaveAndPrint(): Promise<void> {
    if (!this.access.canSave) {
      this.patch({ statusCode: "permission-required" });
      return;
    }
    if (!this.state.summary || this.state.kind !== "ready") {
      this.patch({ statusCode: "load-failed" });
      return;
    }
    this.patch({ busy: true, statusCode: null });
    const summary = this.state.summary;
    const savedAtIso = this.options.now().toISOString();
    const closeId = this.options.createId();
    const auditEventId = this.options.createId();
    const commit = buildDailyCloseArchiveCommit({
      auditEventId,
      closeId,
      counts: this.state.counts,
      savedAtIso,
      savedCashierId: this.options.identity.cashierId,
      savedCashierName: this.options.identity.cashierName,
      savedUserGuid: this.options.identity.userGuid,
      summary,
    });
    let archive: DailyCloseArchive;
    try {
      const saved = await this.options.repository.saveArchive(commit);
      archive = saved.archive;
    } catch {
      if (!this.destroyed) {
        this.patch({ busy: false, statusCode: "save-failed" });
      }
      return;
    }

    if (!this.destroyed) {
      const archives = Object.freeze([
        archive,
        ...this.state.archives.filter(
          (candidate) => candidate.closeId !== archive.closeId,
        ),
      ].slice(0, this.historyLimit));
      this.patch({
        activePane: "history",
        archives,
        selectedArchive: archive,
        ...countTotals(zeroCounts(), summary.expectedCashCents),
      });
    }

    const job = this.createPrintJob(archive, false);
    try {
      await this.options.printer.print(job);
      if (!this.destroyed) {
        this.patch({ busy: false, statusCode: "saved-printed" });
      }
    } catch (error) {
      // 中文注释：归档和审计已经提交；打印失败只改变安全状态码，绝不补偿删除。
      // 日结单已打印、仅现金单失败时如实告知（对齐 WPF #478），避免店员以为整张都没出。
      if (!this.destroyed) {
        this.patch({
          busy: false,
          statusCode: isCashSlipFailure(error)
            ? "saved-cash-slip-failed"
            : "saved-print-failed",
        });
      }
    }
  }

  private async performReprint(): Promise<void> {
    if (!this.access.canReprint) {
      this.patch({ statusCode: "permission-required" });
      return;
    }
    const archive = this.state.selectedArchive;
    if (!archive) {
      this.patch({ statusCode: "select-archive-required" });
      return;
    }
    this.patch({ busy: true, statusCode: null });
    const job = this.createPrintJob(archive, true);
    try {
      await this.options.printer.print(job);
      await this.appendReprintAudit(archive, "Succeeded");
      if (!this.destroyed) {
        this.patch({ busy: false, statusCode: "reprint-printed" });
      }
    } catch (error) {
      await this.appendReprintAudit(archive, "Failed");
      if (!this.destroyed) {
        this.patch({
          busy: false,
          statusCode: isCashSlipFailure(error)
            ? "reprint-cash-slip-failed"
            : "reprint-failed",
        });
      }
    }
  }

  private createPrintJob(
    archive: DailyCloseArchive,
    reprint: boolean,
  ): DailyClosePrintJob {
    return Object.freeze({
      archive,
      document: buildDailyCloseReceipt({
        archive,
        locale: this.options.receiptLocale,
        paper: this.options.receiptPaper,
        reprint,
        brandName: this.options.brandName,
        storeName: this.options.storeName,
        returnPolicy: this.options.returnPolicy,
      }),
      reprint,
    });
  }

  private async appendReprintAudit(
    archive: DailyCloseArchive,
    outcome: "Succeeded" | "Failed",
  ): Promise<void> {
    try {
      await this.options.audit.append([
        {
          eventId: this.options.createId(),
          eventType: "DAILY_CLOSE_REPRINT",
          occurredAtIso: this.options.now().toISOString(),
          orderGuid: null,
          correlationId: archive.closeId,
          payload: {
            action: "daily-close-reprint",
            status: outcome === "Succeeded" ? "Printed" : "Failed",
            reason: "daily-close-reprint",
            source: "ipad-pos",
            outcome,
            ...auditActorPayload({
              cashierId: this.options.identity.cashierId,
              cashierName: this.options.identity.cashierName,
              userGuid: this.options.identity.userGuid,
            }),
          },
        },
      ]);
    } catch {
      // 中文注释：硬件已完成或失败是不可逆事实；旁路员工审计只能尽力追加，不能覆盖打印结果。
    }
  }

  private isCurrentLoad(generation: number): boolean {
    return !this.destroyed && generation === this.loadGeneration;
  }

  private patch(patch: Partial<DailyCloseState>): void {
    if (this.destroyed) return;
    this.state = Object.freeze({ ...this.state, ...patch });
    for (const listener of this.listeners) listener();
  }
}

function zeroCounts(): readonly DailyCloseDenominationCount[] {
  return normalizeDailyCloseCounts([]);
}

function countTotals(
  counts: readonly DailyCloseDenominationCount[],
  expectedCashCents: number,
): Pick<
  DailyCloseState,
  | "coinsSubtotalCents"
  | "countedCashCents"
  | "counts"
  | "notesSubtotalCents"
  | "varianceCents"
> {
  const notesSubtotalCents = counts
    .filter((entry) => entry.denominationCents >= 500)
    .reduce((sum, entry) => safeAdd(sum, entry.subtotalCents), 0);
  const coinsSubtotalCents = counts
    .filter((entry) => entry.denominationCents < 500)
    .reduce((sum, entry) => safeAdd(sum, entry.subtotalCents), 0);
  const countedCashCents = safeAdd(
    notesSubtotalCents,
    coinsSubtotalCents,
  );
  return {
    coinsSubtotalCents,
    countedCashCents,
    counts: Object.freeze([...counts]),
    notesSubtotalCents,
    varianceCents: safeAdd(countedCashCents, -expectedCashCents),
  };
}

function safeAdd(left: number, right: number): number {
  const result = left + right;
  if (!Number.isSafeInteger(result)) {
    throw new TypeError("Daily close cash total is invalid.");
  }
  return result;
}

function normalizeHistoryLimit(value: number): number {
  if (!Number.isSafeInteger(value) || value < 1 || value > 500) {
    throw new TypeError("Daily close history limit is invalid.");
  }
  return value;
}

/** 日结单已打印、随后的现金单没有确认打印成功（打印层以此错误码区分）。 */
export const DAILY_CLOSE_CASH_SLIP_FAILED_CODE =
  "DAILY_CLOSE_CASH_SLIP_NOT_CONFIRMED";

function isCashSlipFailure(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    (error as { code?: unknown }).code === DAILY_CLOSE_CASH_SLIP_FAILED_CODE
  );
}
