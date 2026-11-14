import {
  CASH_MOVEMENT_NATURAL_DIRECTIONS,
  CashMovementRepositoryError,
  CashMovementValidationError,
  buildCashMovement,
  isCashMovementReasonCode,
  validateCashMovementInput,
  type CashMovementRepositoryPort,
  type CashMovementVoidDenial,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";
import type { CashDrawerPort } from "@hb/pos-domain/core/contracts/drawer";
import type { PrinterPort } from "@hb/pos-domain/core/contracts/printer";
import {
  buildCashMovementReceipt,
  cashMovementReceiptToEscPosBytes,
} from "@hb/pos-receipt-core/features/receipts/cash-movement-receipt";
import type { ReceiptPaper } from "@hb/pos-receipt-core/features/receipts/receipt-document";

import type {
  CashMovementReprintResult,
  CashMovementRuntime,
  CashMovementTodayView,
  CashMovementWriteResult,
} from "../../features/cash-movement/cash-movement-runtime";
import {
  businessDateInTimeZone,
  dailyCloseBusinessDayScope,
} from "../../features/daily-close/daily-close-domain";
import type {
  AuthorizedOperationContext,
  OperationAuthorizationRequest,
  OperationAuthorizationResult,
} from "../../features/operation-authorization/operation-authorization-service";

/** 现金存入 / 取出 / 冲正的权限码（与服务端、WPF 一致）。 */
export const CASH_MOVEMENT_PERMISSIONS = Object.freeze({
  cashIn: "Permissions.PosTerminal.CashMovement.CashIn",
  cashOut: "Permissions.PosTerminal.CashMovement.CashOut",
  void: "Permissions.PosTerminal.CashMovement.Void",
} as const);

/** 可信收银员的冻结快照：权限只来自组合根，界面传不进来。 */
export type CashMovementSession = Readonly<{
  cashierId: string;
  cashierName: string;
  userGuid: string | null;
  storeCode: string;
  deviceCode: string;
  permissionCodes: readonly string[];
}>;

export type CashMovementHardwareSettings = Readonly<{
  printEnabled: boolean;
  drawerEnabled: boolean;
  peripheralId: string | null;
  paper: ReceiptPaper;
  brandName: string;
  storeName: string;
}>;

type CashMovementHardware = Pick<PrinterPort, "connect" | "print"> &
  Pick<CashDrawerPort, "open">;

export type ProductionCashMovementDependencies = Readonly<{
  repository: CashMovementRepositoryPort;
  /** 为 null（没有配置主管授权）时只能走当前收银员自己的权限，没权限直接拒绝。 */
  authorization: Readonly<{
    authorizeAndRun<T>(
      input: OperationAuthorizationRequest,
      operation: (context: AuthorizedOperationContext) => T | Promise<T>,
    ): Promise<OperationAuthorizationResult<T>>;
  }> | null;
  /** 每次动作开始与授权通过后各调用一次：会话失效（锁屏 / 换人 / 换设备）时抛错。 */
  session(): CashMovementSession;
  businessTimeZone: string;
  now(): Date;
  createId(): string;
  readHardwareSettings(): Promise<CashMovementHardwareSettings>;
  hardware: CashMovementHardware;
  /** 唤醒同步协调器上传；同步抛出或异步拒绝都已被吞掉，由调用方保证不影响保存结果。 */
  requestDrain(): unknown;
}>;

type Authorized<T> =
  | Readonly<{ ok: true; value: T }>
  | Readonly<{ ok: false; reason: "denied" | "cancelled" }>;

type MovementWrite = Readonly<{
  movement: LocalCashMovement;
  /** 冲正时的原记录（凭条要印 Voids 与原始信息）。 */
  original: LocalCashMovement | null;
}>;

type AuthorizedWrite = Readonly<{
  /** 授权通过后复核过的会话（凭条用它的门店编码）。 */
  session: CashMovementSession;
  write: MovementWrite;
}>;

const PERIPHERAL_ID_PATTERN = /^[A-Za-z0-9._:-]{1,128}$/u;

export function createCashMovementRuntime(
  deps: ProductionCashMovementDependencies,
): CashMovementRuntime {
  /** 授权 → 回调。回调抛出的错误（落库失败等）原样向上传播，授权失败转成结果。 */
  const authorizeAndRun = async <T>(
    permissionCode: string,
    action: string,
    run: (
      session: CashMovementSession,
      authorizedBy: Readonly<{ id: string; name: string }> | null,
    ) => Promise<T>,
  ): Promise<Authorized<T>> => {
    const requesting = deps.session();
    const execute = (
      authorizedBy: Readonly<{ id: string; name: string }> | null,
    ): Promise<T> => {
      // 授权通过（含主管扫码等待）之后再复核一次会话：期间锁屏或换人时不得再写库。
      const active = deps.session();
      if (active.cashierId !== requesting.cashierId) {
        throw new Error("CASH_MOVEMENT_SESSION_CHANGED");
      }
      return run(active, authorizedBy);
    };
    if (!deps.authorization) {
      if (!requesting.permissionCodes.includes(permissionCode)) {
        return { ok: false, reason: "denied" };
      }
      return { ok: true, value: await execute(null) };
    }
    const result = await deps.authorization.authorizeAndRun(
      {
        actionId: deps.createId(),
        permissionCode,
        screen: "cash-movement",
        action,
      },
      (context) => {
        const actor = context.authorizingActor;
        // 本人有权限时没有 authorizingActor；主管授权时把主管写进记录的授权人。
        return execute(
          actor
            ? { id: actor.cashierId, name: actor.cashierName ?? actor.cashierId }
            : null,
        );
      },
    );
    if (!result.authorized) {
      return {
        ok: false,
        reason: result.reason === "CANCELLED" || result.reason === "REVOKED"
          ? "cancelled"
          : "denied",
      };
    }
    return { ok: true, value: result.value };
  };

  const wakeUpload = (): void => {
    // 与日结存档后的写法一致：唤醒失败（含同步抛出）绝不能让“已记录”变成失败，上传由 pending 状态保证。
    try {
      const pending = deps.requestDrain();
      if (pending && typeof (pending as Promise<unknown>).catch === "function") {
        void (pending as Promise<unknown>).catch(() => undefined);
      }
    } catch {
      // 同步抛出的唤醒异常同样忽略。
    }
  };

  const scopeFor = (session: CashMovementSession, businessDate: string) =>
    dailyCloseBusinessDayScope({
      businessDate,
      businessTimeZone: deps.businessTimeZone,
      deviceCode: session.deviceCode,
      storeCode: session.storeCode,
    });

  /** 钱箱与凭条：本地记录已落库，这里任何一步失败都只影响返回的结果，绝不回滚。 */
  const runHardware = async (
    session: CashMovementSession,
    write: MovementWrite,
    options: Readonly<{ openDrawer: boolean; reprint: boolean }>,
  ): Promise<{ drawer: "opened" | "failed" | "skipped"; print: "printed" | "failed" | "skipped" }> => {
    let settings: CashMovementHardwareSettings;
    try {
      settings = await deps.readHardwareSettings();
    } catch {
      return { drawer: options.openDrawer ? "failed" : "skipped", print: "failed" };
    }
    const peripheralId =
      settings.peripheralId !== null &&
      PERIPHERAL_ID_PATTERN.test(settings.peripheralId)
        ? settings.peripheralId
        : null;
    const wantsDrawer = options.openDrawer && settings.drawerEnabled && peripheralId !== null;
    const wantsPrint = settings.printEnabled && peripheralId !== null;
    let drawer: "opened" | "failed" | "skipped" = wantsDrawer ? "failed" : "skipped";
    // 打印机没启用 / 没配置：对操作员按「没打印出来」提示（凭条要放进钱箱留底）。
    let print: "printed" | "failed" | "skipped" = wantsPrint ? "failed" : "skipped";
    if (peripheralId === null || (!wantsDrawer && !wantsPrint)) {
      return { drawer, print };
    }
    try {
      await deps.hardware.connect(peripheralId);
    } catch {
      return { drawer, print };
    }
    if (wantsDrawer) {
      try {
        const opened = await deps.hardware.open(deps.createId());
        drawer = opened.status === "completed" ? "opened" : "failed";
      } catch {
        drawer = "failed";
      }
    }
    if (wantsPrint) {
      try {
        const summary = await deps.repository.summarizePeriod(
          scopeFor(session, write.movement.businessDate),
        );
        const document = buildCashMovementReceipt({
          movement: write.movement,
          voidedOriginal: write.original
            ? {
                displayNumber: write.original.displayNumber,
                reasonCode: write.original.reasonCode,
                amountCents: write.original.amountCents,
                occurredAtIso: write.original.occurredAtIso,
              }
            : null,
          todayCashInCents: summary.cashInCents,
          todayCashOutCents: summary.cashOutCents,
          paper: settings.paper,
          reprint: options.reprint,
          brandName: settings.brandName,
          storeName: settings.storeName || session.storeCode,
          businessTimeZone: deps.businessTimeZone,
        });
        const result = await deps.hardware.print(
          `cash-movement:${write.movement.movementGuid}:${deps.createId()}`,
          cashMovementReceiptToEscPosBytes(document),
        );
        print = result.status === "printed" ? "printed" : "failed";
      } catch {
        print = "failed";
      }
    }
    return { drawer, print };
  };

  /** 落库之后的统一收尾：唤醒上传 → 开钱箱 → 打印；全部失败也只是提示。 */
  const finishWrite = async (
    session: CashMovementSession,
    write: MovementWrite,
  ): Promise<CashMovementWriteResult> => {
    wakeUpload();
    const hardware = await runHardware(session, write, {
      openDrawer: true,
      reprint: false,
    });
    return { kind: "saved", movement: write.movement, ...hardware };
  };

  const failureResult = (error: unknown): CashMovementWriteResult => {
    if (error instanceof CashMovementValidationError) {
      return { kind: "invalid", code: error.code };
    }
    if (error instanceof CashMovementRepositoryError) {
      if (error.code === "VOID_NOT_ALLOWED") {
        return { kind: "void-not-allowed", denial: error.denial as CashMovementVoidDenial | null };
      }
      if (error.code === "VOID_TARGET_NOT_FOUND") {
        return { kind: "void-not-allowed", denial: null };
      }
    }
    return { kind: "failed" };
  };

  return {
    async listToday(): Promise<CashMovementTodayView> {
      const session = deps.session();
      const businessDate = businessDateInTimeZone(deps.now(), deps.businessTimeZone);
      const scope = scopeFor(session, businessDate);
      const [movements, latestDailyCloseSavedAtIso, summary] = await Promise.all([
        deps.repository.listToday(scope),
        deps.repository.latestDailyCloseSavedAtIso(scope),
        deps.repository.summarizePeriod(scope),
      ]);
      deps.session();
      return {
        businessDate,
        businessTimeZone: deps.businessTimeZone,
        movements,
        latestDailyCloseSavedAtIso,
        cashInCents: summary.cashInCents,
        cashOutCents: summary.cashOutCents,
      };
    },

    async submit(input): Promise<CashMovementWriteResult> {
      // 先校验再授权：输入不合法时不能白白弹出主管授权。
      const checked = validateCashMovementInput({
        reasonCode: input.reasonCode,
        amountCents: input.amountCents,
        note: input.note,
      });
      if (!checked.ok) return { kind: "invalid", code: checked.code };
      if (!isCashMovementReasonCode(checked.reasonCode)) {
        return { kind: "invalid", code: "REASON_INVALID" };
      }
      const isIn = CASH_MOVEMENT_NATURAL_DIRECTIONS[checked.reasonCode] === "In";
      try {
        const authorized = await authorizeAndRun(
          isIn ? CASH_MOVEMENT_PERMISSIONS.cashIn : CASH_MOVEMENT_PERMISSIONS.cashOut,
          isIn ? "cash-in" : "cash-out",
          async (active, authorizedBy): Promise<AuthorizedWrite> => {
            const commit = buildCashMovement({
              movementGuid: input.movementGuid,
              auditEventId: deps.createId(),
              entry: {
                kind: "movement",
                reasonCode: checked.reasonCode,
                amountCents: checked.amountCents,
              },
              note: checked.note,
              occurredAtIso: deps.now().toISOString(),
              businessTimeZone: deps.businessTimeZone,
              storeCode: active.storeCode,
              deviceCode: active.deviceCode,
              cashier: {
                cashierId: active.cashierId,
                cashierName: active.cashierName,
                userGuid: active.userGuid,
              },
              authorizedBy,
            });
            // 本地落库是事实：append 成功之后的任何一步失败都不回滚。
            const appended = await deps.repository.append(commit);
            return {
              session: active,
              write: { movement: appended.movement, original: null },
            };
          },
        );
        if (!authorized.ok) return { kind: "denied", reason: authorized.reason };
        return await finishWrite(authorized.value.session, authorized.value.write);
      } catch (error) {
        return failureResult(error);
      }
    },

    async voidMovement(input): Promise<CashMovementWriteResult> {
      // 冲正备注 >= 2 字（界面已拦，这里兜底）；原因码与金额取自库里的原记录。
      const note = input.note.trim();
      try {
        const authorized = await authorizeAndRun(
          CASH_MOVEMENT_PERMISSIONS.void,
          "cash-movement-void",
          async (active, authorizedBy): Promise<AuthorizedWrite> => {
            const original = await deps.repository.getByGuid(input.originalMovementGuid);
            if (
              !original ||
              original.storeCode !== active.storeCode ||
              original.deviceCode !== active.deviceCode
            ) {
              throw new CashMovementRepositoryError("VOID_TARGET_NOT_FOUND");
            }
            const commit = buildCashMovement({
              movementGuid: input.movementGuid,
              auditEventId: deps.createId(),
              entry: { kind: "void", original },
              note,
              occurredAtIso: deps.now().toISOString(),
              businessTimeZone: deps.businessTimeZone,
              storeCode: active.storeCode,
              deviceCode: active.deviceCode,
              cashier: {
                cashierId: active.cashierId,
                cashierName: active.cashierName,
                userGuid: active.userGuid,
              },
              authorizedBy,
            });
            // 资格由库内触发器与事务内复核兜底（非今天 / 已冲正 / 其后已日结都会抛 VOID_NOT_ALLOWED）。
            const appended = await deps.repository.append(commit);
            return {
              session: active,
              write: { movement: appended.movement, original },
            };
          },
        );
        if (!authorized.ok) return { kind: "denied", reason: authorized.reason };
        return await finishWrite(authorized.value.session, authorized.value.write);
      } catch (error) {
        return failureResult(error);
      }
    },

    async reprint(movementGuid): Promise<CashMovementReprintResult> {
      try {
        const session = deps.session();
        const movement = await deps.repository.getByGuid(movementGuid);
        if (
          !movement ||
          movement.storeCode !== session.storeCode ||
          movement.deviceCode !== session.deviceCode
        ) {
          return { kind: "not-found" };
        }
        const original = movement.voidsMovementGuid
          ? await deps.repository.getByGuid(movement.voidsMovementGuid)
          : null;
        if (movement.voidsMovementGuid && !original) return { kind: "failed" };
        // 补打不开钱箱，凭条加 REPRINT 标记。
        const outcome = await runHardware(
          session,
          { movement, original },
          { openDrawer: false, reprint: true },
        );
        return { kind: outcome.print === "printed" ? "printed" : "failed" };
      } catch {
        return { kind: "failed" };
      }
    },
  };
}
