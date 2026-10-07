import type {
  MaterializeReturnFulfilmentInput,
  SqliteReturnFulfilmentPlanStore,
  StoredReturnFulfilmentPlan,
} from "../db/sqlite-return-fulfilment-plan-store";

export const RETURN_CASH_DRAWER_REASON = "cash-return";

export type RenderedReturnReceipt = Readonly<{
  printerId: string;
  receiptBytes: Uint8Array;
}>;

export type ReturnFulfilmentMaterializeResult = Readonly<{
  actionId: string;
  status: "materialized" | "already-materialized";
}>;

export type ReturnFulfilmentDrainReport = Readonly<{
  materialized: number;
  failed: number;
  materializedActionIds: readonly string[];
  failedActionIds: readonly string[];
}>;

export type ReturnFulfilmentReceiptIdentity = Readonly<{
  actionId: string;
  returnOrderGuid: string;
  receiptKind: "refund-voucher" | "refund-receipt";
}>;

type ReturnFulfilmentPlanStorePort = Pick<
  SqliteReturnFulfilmentPlanStore,
  "get" | "listPending" | "materialize"
>;

export type ReturnFulfilmentRuntimeOptions = Readonly<{
  plans: ReturnFulfilmentPlanStorePort;
  /** drawer-only 计划只解析外设身份，不得借此渲染或生成小票。 */
  resolveDrawerPrinterId?(): Promise<string>;
  renderReceipt(
    identity: ReturnFulfilmentReceiptIdentity,
  ): Promise<RenderedReturnReceipt>;
}>;

/**
 * refund-receipt 计划的打印字节：普通退货小票在前；若退货单含新签发的退款券
 * （刷卡 + 退款券混合退款），把每张券面追加到同一冻结 print job（每张各自切纸）。
 * 不新增打印任务、不改迁移，物化重放仍由同一 plan 保证幂等；券面材料缺失时整体抛错，
 * plan 保持 pending 重试，绝不只打小票而漏打退款券。
 */
export async function renderRefundReceiptWithVouchers(input: Readonly<{
  renderReceipt(): Promise<RenderedReturnReceipt>;
  hasRefundVoucherTender(): Promise<boolean>;
  renderRefundVouchers(): Promise<RenderedReturnReceipt>;
}>): Promise<RenderedReturnReceipt> {
  const receipt = await input.renderReceipt();
  if (!(await input.hasRefundVoucherTender())) return receipt;
  const vouchers = await input.renderRefundVouchers();
  if (vouchers.printerId !== receipt.printerId) {
    throw new Error("Return receipt printer identity has diverged.");
  }
  const receiptBytes = new Uint8Array(
    receipt.receiptBytes.byteLength + vouchers.receiptBytes.byteLength,
  );
  receiptBytes.set(receipt.receiptBytes, 0);
  receiptBytes.set(vouchers.receiptBytes, receipt.receiptBytes.byteLength);
  return Object.freeze({ printerId: receipt.printerId, receiptBytes });
}

/**
 * 退货账本完成后的唯一履约物化边界。
 *
 * 本服务没有 return ledger 或 outbox 写接口：渲染及物化失败只会让冻结 plan
 * 保持 pending，绝不能反向修改退款结果或把 action 标为 Unknown。
 */
export class ReturnFulfilmentRuntime {
  public constructor(
    private readonly options: ReturnFulfilmentRuntimeOptions,
  ) {}

  public async materializeAction(
    actionIdInput: string,
  ): Promise<ReturnFulfilmentMaterializeResult> {
    const actionId = strictText(actionIdInput, "return action id", 128);
    const plan = await this.options.plans.get(actionId);
    if (!plan) throw new Error("Return fulfilment plan is missing.");
    assertPlanIdentity(plan, actionId);
    if (plan.materializedAtIso !== null) {
      return Object.freeze({
        actionId,
        status: "already-materialized",
      });
    }

    const receipt =
      plan.receiptKind === "none"
        ? null
        : normalizeRenderedReceipt(
            await this.options.renderReceipt(
              Object.freeze({
                actionId,
                returnOrderGuid: strictText(
                  plan.returnOrderGuid,
                  "return order guid",
                  128,
                ),
                receiptKind: plan.receiptKind,
              }),
            ),
          );
    const printerId =
      receipt?.printerId ??
      strictText(
        await this.options.resolveDrawerPrinterId?.(),
        "return printer id",
        128,
      );
    const materialization: MaterializeReturnFulfilmentInput = {
      actionId: plan.actionId,
      expectedReturnOrderGuid: plan.returnOrderGuid,
      expectedPrintJobId: plan.printJobId,
      expectedDrawerEventId: plan.drawerEventId,
      printerId,
      receiptBytes: receipt?.receiptBytes ?? null,
      // 冻结 plan 的 drawerRequired 只由现金 allocation 生成；非现金绝不传原因。
      drawerReason: plan.drawerRequired
        ? RETURN_CASH_DRAWER_REASON
        : null,
    };
    const materialized = await this.options.plans.materialize(
      materialization,
    );
    assertMaterializedIdentity(plan, materialized);
    return Object.freeze({
      actionId,
      status: "materialized",
    });
  }

  public async drainPending(
    limitInput?: number,
  ): Promise<ReturnFulfilmentDrainReport> {
    const pending =
      limitInput === undefined
        ? await this.options.plans.listPending()
        : await this.options.plans.listPending(normalizeLimit(limitInput));
    const materializedActionIds: string[] = [];
    const failedActionIds: string[] = [];

    // 顺序处理避免同一打印机并发物化；单项失败只保留该 plan，后续继续。
    for (const plan of pending) {
      const actionId = strictText(plan.actionId, "return action id", 128);
      try {
        await this.materializeAction(actionId);
        materializedActionIds.push(actionId);
      } catch {
        failedActionIds.push(actionId);
      }
    }
    return Object.freeze({
      materialized: materializedActionIds.length,
      failed: failedActionIds.length,
      materializedActionIds: Object.freeze(materializedActionIds),
      failedActionIds: Object.freeze(failedActionIds),
    });
  }
}

function assertPlanIdentity(
  plan: StoredReturnFulfilmentPlan,
  expectedActionId: string,
): void {
  if (
    strictText(plan.actionId, "return action id", 128) !== expectedActionId
  ) {
    throw new Error("Return fulfilment action identity has diverged.");
  }
  strictText(plan.returnOrderGuid, "return order guid", 128);
  const drawerEventId =
    plan.drawerEventId === null
      ? null
      : strictText(plan.drawerEventId, "return drawer event id", 128);
  const expectsPrint = plan.receiptKind !== "none";
  if (
    (plan.receiptKind !== "none" &&
      plan.receiptKind !== "refund-voucher" &&
      plan.receiptKind !== "refund-receipt") ||
    plan.printReceipt !== expectsPrint ||
    (plan.printJobId !== null) !== expectsPrint ||
    typeof plan.drawerRequired !== "boolean" ||
    plan.drawerRequired !== (drawerEventId !== null)
  ) {
    throw new Error("Return fulfilment plan flags are invalid.");
  }
  if (plan.printJobId !== null) {
    strictText(plan.printJobId, "return print job id", 128);
  }
  canonicalIso(plan.createdAtIso, "return fulfilment created time");
  if (plan.materializedAtIso !== null) {
    canonicalIso(
      plan.materializedAtIso,
      "return fulfilment materialized time",
    );
  }
}

function assertMaterializedIdentity(
  expected: StoredReturnFulfilmentPlan,
  actual: StoredReturnFulfilmentPlan,
): void {
  assertPlanIdentity(actual, expected.actionId);
  if (
    actual.returnOrderGuid !== expected.returnOrderGuid ||
    actual.printJobId !== expected.printJobId ||
    actual.drawerEventId !== expected.drawerEventId ||
    actual.receiptKind !== expected.receiptKind ||
    actual.printReceipt !== expected.printReceipt ||
    actual.drawerRequired !== expected.drawerRequired ||
    actual.materializedAtIso === null
  ) {
    throw new Error("Materialized return fulfilment identity has diverged.");
  }
}

function normalizeRenderedReceipt(
  value: unknown,
): RenderedReturnReceipt {
  if (!isRecord(value)) {
    throw new TypeError("Return rendered receipt is invalid.");
  }
  const printerId = strictText(
    value.printerId,
    "return printer id",
    128,
  );
  if (
    !(value.receiptBytes instanceof Uint8Array) ||
    value.receiptBytes.byteLength === 0
  ) {
    throw new TypeError("Return receipt bytes are invalid.");
  }
  return Object.freeze({
    printerId,
    receiptBytes: Uint8Array.from(value.receiptBytes),
  });
}

function normalizeLimit(value: number): number {
  if (!Number.isSafeInteger(value) || value < 1 || value > 100) {
    throw new TypeError("Return fulfilment drain limit is invalid.");
  }
  return value;
}

function strictText(value: unknown, label: string, max: number): string {
  if (typeof value !== "string") throw new TypeError(`Invalid ${label}.`);
  const normalized = value.trim();
  if (normalized.length === 0 || normalized.length > max) {
    throw new TypeError(`Invalid ${label}.`);
  }
  return normalized;
}

function canonicalIso(value: unknown, label: string): string {
  const normalized = strictText(value, label, 64);
  const milliseconds = Date.parse(normalized);
  if (!Number.isFinite(milliseconds)) throw new TypeError(`Invalid ${label}.`);
  return new Date(milliseconds).toISOString();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
