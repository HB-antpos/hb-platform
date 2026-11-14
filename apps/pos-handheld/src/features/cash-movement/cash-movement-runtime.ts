import {
  CASH_MOVEMENT_NATURAL_DIRECTIONS,
  CashMovementRepositoryError,
  CashMovementValidationError,
  buildCashMovement,
  canVoidCashMovement,
  validateCashMovementInput,
  type CashMovementRepositoryPort,
  type CashMovementSummary,
  type CashMovementValidationCode,
  type CashMovementVoidDenial,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";
import {
  buildCashMovementReceipt,
  cashMovementReceiptToEscPosBytes,
} from "@hb/pos-receipt-core/features/receipts/cash-movement-receipt";
import type { ReceiptPaper } from "@hb/pos-receipt-core/features/receipts/receipt-document";

import {
  businessDateInTimeZone,
  dailyCloseBusinessDayScope,
} from "../daily-close/daily-close-domain";
import type {
  AuthorizedOperationContext,
  OperationAuthorizationFailureReason,
  OperationAuthorizationResult,
} from "../operation-authorization/operation-authorization-service";

/** 现金进出的权限码（与 pos-terminal-permissions.ts 一致）。 */
export const CASH_MOVEMENT_CASH_IN_PERMISSION =
  "Permissions.PosTerminal.CashMovement.CashIn";
export const CASH_MOVEMENT_CASH_OUT_PERMISSION =
  "Permissions.PosTerminal.CashMovement.CashOut";
export const CASH_MOVEMENT_VOID_PERMISSION =
  "Permissions.PosTerminal.CashMovement.Void";

export type CashMovementSideEffectResult = "ok" | "skipped" | "failed";

export type CashMovementSubmitInput = Readonly<{
  reasonCode: string;
  amountCents: number;
  note: string;
}>;

export type CashMovementVoidInput = Readonly<{
  movementGuid: string;
  note: string;
}>;

export type CashMovementDeniedReason = "cancelled" | "denied";

export type CashMovementSubmitOutcome =
  | Readonly<{
      kind: "saved";
      movement: LocalCashMovement;
      drawer: CashMovementSideEffectResult;
      print: CashMovementSideEffectResult;
    }>
  | Readonly<{ kind: "invalid"; code: CashMovementValidationCode }>
  | Readonly<{ kind: "denied"; reason: CashMovementDeniedReason }>
  | Readonly<{ kind: "failed" }>;

export type CashMovementVoidOutcome =
  | Readonly<{
      kind: "saved";
      movement: LocalCashMovement;
      drawer: CashMovementSideEffectResult;
      print: CashMovementSideEffectResult;
    }>
  | Readonly<{ kind: "invalid"; code: CashMovementValidationCode }>
  | Readonly<{
      kind: "not-allowed";
      reason: CashMovementVoidDenial | "NOT_FOUND";
    }>
  | Readonly<{ kind: "denied"; reason: CashMovementDeniedReason }>
  | Readonly<{ kind: "failed" }>;

export type CashMovementReprintOutcome = Readonly<{
  kind: "printed" | "failed" | "not-found";
}>;

/** 今日记录快照：记录升序（最新在后）；冲正资格由展示层用同一批输入自行判断。 */
export type CashMovementTodaySnapshot = Readonly<{
  businessDate: string;
  businessTimeZone: string;
  movements: readonly LocalCashMovement[];
  /** 今天最近一次保存日结的时间；没有为 null（canVoidCashMovement 的输入）。 */
  latestDailyCloseSavedAtIso: string | null;
}>;

/** 展示层（presenter）依赖的服务端口：授权、落库、开箱、打印都封装在组合根一侧。 */
export interface CashMovementServicePort {
  listToday(): Promise<CashMovementTodaySnapshot>;
  summary(): Promise<CashMovementSummary>;
  submit(input: CashMovementSubmitInput): Promise<CashMovementSubmitOutcome>;
  void(input: CashMovementVoidInput): Promise<CashMovementVoidOutcome>;
  reprint(movementGuid: string): Promise<CashMovementReprintOutcome>;
}

/** 组合根暴露给路由的最小面：开关读取 / 订阅 + presenter 工厂；可信身份与硬件留在组合根。 */
export type PosCashMovementRuntimeService = Readonly<{
  /** 销售页工具栏「钱箱」入口是否显示；只控制入口，不影响任何记录的汇总、上传与日结。 */
  isCashMovementEnabled(): boolean;
  subscribeCashMovementEnabled(listener: () => void): () => void;
  createPresenter(): import("./cash-movement-presenter").CashMovementPresenter;
}>;

export type CashMovementTrustedSession = Readonly<{
  cashierId: string;
  cashierName: string | null;
  userGuid: string | null;
  permissionCodes: readonly string[];
  storeCode: string;
  deviceCode: string;
}>;

export type CashMovementAuthorizationPort = Readonly<{
  authorizeAndRun<T>(
    input: Readonly<{
      actionId: string;
      permissionCode: string;
      screen: string;
      action: string;
      forceSupervisor?: boolean;
    }>,
    operation: (context: AuthorizedOperationContext) => T | Promise<T>,
  ): Promise<OperationAuthorizationResult<T>>;
}>;

export type CashMovementHardwarePort = Readonly<{
  /** 开钱箱：设置里没开钱箱返回 skipped；脉冲失败返回 failed。永不抛出。 */
  openDrawer(eventId: string): Promise<CashMovementSideEffectResult>;
  /** 打印一张凭条：设置里没开打印返回 skipped；未确认打印成功返回 failed。永不抛出。 */
  printSlip(jobId: string, bytes: Uint8Array): Promise<CashMovementSideEffectResult>;
  receiptContext(): Promise<
    Readonly<{ paper: ReceiptPaper; brandName: string; storeName: string }>
  >;
}>;

export type CashMovementRuntimeDependencies = Readonly<{
  repository: CashMovementRepositoryPort;
  /** null = 没有主管授权能力：只能走当前收银员自己的权限，没有权限直接拒绝。 */
  authorization: CashMovementAuthorizationPort | null;
  /** 取当前可信会话（已校验设备范围）；会话失效时抛错。 */
  session(): CashMovementTrustedSession;
  businessTimeZone: string;
  createId(): string;
  now(): Date;
  /** 唤醒同步协调器上传；失败（含同步抛出）一律忽略，上传由 pending 状态保证。 */
  requestDrain(): void;
  hardware: CashMovementHardwarePort;
}>;

/**
 * 现金进出的运行时服务。保存顺序固定：
 * 校验 → 权限 / 主管授权 → buildCashMovement + append（本地落库即事实）→ 唤醒上传 → 开钱箱 → 打印凭条。
 * 落库之后任何一步失败都只影响对应的提示，绝不回滚记录；审计由 append 在同一事务里写入。
 */
export function createCashMovementRuntimeService(
  deps: CashMovementRuntimeDependencies,
): CashMovementServicePort {
  const scopeOf = (session: CashMovementTrustedSession) =>
    dailyCloseBusinessDayScope({
      businessDate: businessDateInTimeZone(deps.now(), deps.businessTimeZone),
      businessTimeZone: deps.businessTimeZone,
      deviceCode: session.deviceCode,
      storeCode: session.storeCode,
    });

  /** 权限 / 主管授权的统一入口；回调里再次核对会话仍然有效。 */
  const authorize = <T>(
    session: CashMovementTrustedSession,
    permissionCode: string,
    action: string,
    operation: (
      authorizedBy: Readonly<{ id: string; name: string }> | null,
    ) => Promise<T>,
  ): Promise<OperationAuthorizationResult<T>> => {
    const run = async (
      context: AuthorizedOperationContext | null,
    ): Promise<T> => {
      // 授权期间（可能在等主管扫码）收银员被锁屏 / 换人：失败关闭，不落库。
      const active = deps.session();
      if (
        active.cashierId !== session.cashierId ||
        active.storeCode !== session.storeCode ||
        active.deviceCode !== session.deviceCode
      ) {
        throw new Error("CASH_MOVEMENT_SESSION_CHANGED");
      }
      const actor = context?.authorizingActor;
      return operation(
        actor
          ? { id: actor.cashierId, name: actor.cashierName ?? actor.cashierId }
          : null,
      );
    };
    if (!deps.authorization) {
      if (!session.permissionCodes.includes(permissionCode)) {
        return Promise.resolve({ authorized: false, reason: "PERMISSION_DENIED" });
      }
      return run(null).then((value) => ({ authorized: true as const, value }));
    }
    return deps.authorization.authorizeAndRun(
      {
        actionId: deps.createId(),
        permissionCode,
        screen: "cash-movement",
        action,
      },
      (context) => run(context),
    );
  };

  const cashierOf = (session: CashMovementTrustedSession) => ({
    cashierId: session.cashierId,
    cashierName: session.cashierName ?? session.cashierId,
    userGuid: session.userGuid,
  });

  const deniedReason = (
    reason: OperationAuthorizationFailureReason,
  ): CashMovementDeniedReason => (reason === "CANCELLED" ? "cancelled" : "denied");

  /** 凭条：失败只返回 failed，不抛出。 */
  const printSlip = async (
    movement: LocalCashMovement,
    session: CashMovementTrustedSession,
    reprint: boolean,
  ): Promise<CashMovementSideEffectResult> => {
    try {
      const [receipt, summary, voidedOriginal] = await Promise.all([
        deps.hardware.receiptContext(),
        deps.repository.summarizePeriod(scopeOf(session)),
        movement.voidsMovementGuid
          ? deps.repository.getByGuid(movement.voidsMovementGuid)
          : Promise.resolve(null),
      ]);
      if (movement.voidsMovementGuid && !voidedOriginal) return "failed";
      const document = buildCashMovementReceipt({
        movement,
        voidedOriginal,
        todayCashInCents: summary.cashInCents,
        todayCashOutCents: summary.cashOutCents,
        paper: receipt.paper,
        reprint,
        brandName: receipt.brandName,
        storeName: receipt.storeName || movement.storeCode,
        businessTimeZone: deps.businessTimeZone,
      });
      return await deps.hardware.printSlip(
        `cash-movement:${movement.movementGuid}:${deps.createId()}`,
        cashMovementReceiptToEscPosBytes(document),
      );
    } catch {
      return "failed";
    }
  };

  const wakeUpload = (): void => {
    try {
      deps.requestDrain();
    } catch {
      // 上传由 pending 状态保证，之后的启动 / 前台 / 联网 / 定时唤醒仍会补传。
    }
  };

  return {
    async listToday() {
      const session = deps.session();
      const scope = scopeOf(session);
      const [movements, latestDailyCloseSavedAtIso] = await Promise.all([
        deps.repository.listToday(scope),
        deps.repository.latestDailyCloseSavedAtIso(scope),
      ]);
      return Object.freeze({
        businessDate: scope.businessDate,
        businessTimeZone: deps.businessTimeZone,
        movements,
        latestDailyCloseSavedAtIso,
      });
    },

    async summary() {
      return deps.repository.summarizePeriod(scopeOf(deps.session()));
    },

    async submit(input) {
      let session: CashMovementTrustedSession;
      try {
        session = deps.session();
      } catch {
        return { kind: "failed" };
      }
      // 先校验再授权：表单有误时不打扰主管。
      const checked = validateCashMovementInput({
        reasonCode: input.reasonCode,
        amountCents: input.amountCents,
        note: input.note,
      });
      if (!checked.ok) return { kind: "invalid", code: checked.code };
      const permissionCode =
        CASH_MOVEMENT_NATURAL_DIRECTIONS[checked.reasonCode] === "In"
          ? CASH_MOVEMENT_CASH_IN_PERMISSION
          : CASH_MOVEMENT_CASH_OUT_PERMISSION;
      const action =
        CASH_MOVEMENT_NATURAL_DIRECTIONS[checked.reasonCode] === "In"
          ? "cash-in"
          : "cash-out";

      let saved: LocalCashMovement;
      try {
        const result = await authorize(
          session,
          permissionCode,
          action,
          async (authorizedBy) => {
            const commit = buildCashMovement({
              movementGuid: deps.createId(),
              auditEventId: deps.createId(),
              entry: {
                kind: "movement",
                reasonCode: checked.reasonCode,
                amountCents: checked.amountCents,
              },
              note: checked.note,
              occurredAtIso: deps.now().toISOString(),
              businessTimeZone: deps.businessTimeZone,
              storeCode: session.storeCode,
              deviceCode: session.deviceCode,
              cashier: cashierOf(session),
              authorizedBy,
            });
            return (await deps.repository.append(commit)).movement;
          },
        );
        if (!result.authorized) {
          return { kind: "denied", reason: deniedReason(result.reason) };
        }
        saved = result.value;
      } catch (error) {
        if (error instanceof CashMovementValidationError) {
          return { kind: "invalid", code: error.code };
        }
        return { kind: "failed" };
      }

      // 以下都发生在「已落库」之后：失败只改变提示，不回滚记录。
      wakeUpload();
      const drawer = await deps.hardware
        .openDrawer(saved.movementGuid)
        .catch((): CashMovementSideEffectResult => "failed");
      const print = await printSlip(saved, session, false);
      return { kind: "saved", movement: saved, drawer, print };
    },

    async void(input) {
      let session: CashMovementTrustedSession;
      try {
        session = deps.session();
      } catch {
        return { kind: "failed" };
      }
      let original: LocalCashMovement | null;
      let latestSavedAt: string | null;
      let todayBusinessDate: string;
      try {
        const scope = scopeOf(session);
        todayBusinessDate = scope.businessDate;
        [original, latestSavedAt] = await Promise.all([
          deps.repository.getByGuid(input.movementGuid),
          deps.repository.latestDailyCloseSavedAtIso(scope),
        ]);
      } catch {
        return { kind: "failed" };
      }
      if (
        !original ||
        original.storeCode !== session.storeCode ||
        original.deviceCode !== session.deviceCode
      ) {
        return { kind: "not-allowed", reason: "NOT_FOUND" };
      }
      const eligibility = canVoidCashMovement({
        movement: original,
        alreadyVoided: original.voidedByMovementGuid !== null,
        todayBusinessDate,
        latestDailyCloseSavedAtIso: latestSavedAt,
      });
      if (!eligibility.ok) {
        return { kind: "not-allowed", reason: eligibility.reason };
      }
      const checked = validateCashMovementInput({
        reasonCode: original.reasonCode,
        amountCents: original.amountCents,
        note: input.note,
        isVoid: true,
      });
      if (!checked.ok) return { kind: "invalid", code: checked.code };

      const target = original;
      let saved: LocalCashMovement;
      try {
        const result = await authorize(
          session,
          CASH_MOVEMENT_VOID_PERMISSION,
          "cash-movement-void",
          async (authorizedBy) => {
            const commit = buildCashMovement({
              movementGuid: deps.createId(),
              auditEventId: deps.createId(),
              entry: { kind: "void", original: target },
              note: checked.note,
              occurredAtIso: deps.now().toISOString(),
              businessTimeZone: deps.businessTimeZone,
              storeCode: session.storeCode,
              deviceCode: session.deviceCode,
              cashier: cashierOf(session),
              authorizedBy,
            });
            return (await deps.repository.append(commit)).movement;
          },
        );
        if (!result.authorized) {
          return { kind: "denied", reason: deniedReason(result.reason) };
        }
        saved = result.value;
      } catch (error) {
        if (error instanceof CashMovementValidationError) {
          return { kind: "invalid", code: error.code };
        }
        // 库里的触发器 / 唯一索引兜底：并发冲正或刚好保存了日结。
        if (
          error instanceof CashMovementRepositoryError &&
          error.code === "VOID_NOT_ALLOWED" &&
          error.denial
        ) {
          return { kind: "not-allowed", reason: error.denial };
        }
        return { kind: "failed" };
      }
      wakeUpload();
      // 与 WPF 一致：冲正也走「开钱箱 → 打印」（冲正后现金要放回 / 取出钱箱）；失败只提示，不回滚记录。
      const drawer = await deps.hardware
        .openDrawer(saved.movementGuid)
        .catch((): CashMovementSideEffectResult => "failed");
      const print = await printSlip(saved, session, false);
      return { kind: "saved", movement: saved, drawer, print };
    },

    async reprint(movementGuid) {
      let session: CashMovementTrustedSession;
      try {
        session = deps.session();
      } catch {
        return { kind: "failed" };
      }
      let movement: LocalCashMovement | null;
      try {
        movement = await deps.repository.getByGuid(movementGuid);
      } catch {
        return { kind: "failed" };
      }
      if (
        !movement ||
        movement.storeCode !== session.storeCode ||
        movement.deviceCode !== session.deviceCode
      ) {
        return { kind: "not-found" };
      }
      return {
        kind: (await printSlip(movement, session, true)) === "ok" ? "printed" : "failed",
      };
    },
  };
}
