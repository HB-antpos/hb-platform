import type { AuditActorSnapshot } from "@hb/pos-domain/core/contracts/audit-actor";
import {
  CurrentCashierSession,
  type TrustedCashierLease,
  type TrustedCashierSession,
} from "./current-cashier-session";
import type { ReturnFulfilmentRuntime } from "./return-fulfilment-runtime";
import type { PaymentAcknowledgementRuntimePort } from "@hb/pos-payments-core/features/payments/payment-acknowledgement-service";

import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type { PosDatabase } from "@/core/db/pos-database";
import type {
  PosRepositoryBundle,
  SensitivePayloadEncryptor,
} from "@/core/db/sqlite-repositories";
import type { UpdateOperationLeasePort } from "@/features/app-updates/update-transition-lease-coordinator";
import {
  OperationAuthorizationService,
  type AuthorizedOperationContext,
  type OperationAuthorizationRequest,
  type OperationAuthorizationResult,
} from "@/features/operation-authorization";
import {
  DurableReturnExecutionOrchestrator,
  type ReturnSupervisorResolutionPort,
  type DurableOfflineCashRefundPort,
  type DurableOnlineReturnRefundPort,
  type DurableReturnLine,
  type DurableReturnRecoveryAction,
  type ReturnExecutionLedgerPort,
  type ReturnRecoveryListPort,
  type ReturnRecoveryScope,
  type ReturnTrustedIdentityPort,
  type TrustedReturnIdentity,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import {
  CanonicalReturnFingerprint,
  CatalogLocalReturnAdapter,
  DurableCapacityVaultAdapter,
  OrderRepositoryLocalReturnLookup,
  ReturnLineMaterialCache,
} from "@/features/returns/adapters/production-return-support";
import {
  ReturnLookupAdapter,
  type ReturnHistoryApiPort,
} from "@/features/returns/adapters/return-lookup-adapter";
import {
  ReturnFeatureError,
  type NoReceiptReturnItem,
  type ReceiptReturnContext,
  type ReceiptReturnLine,
  type ReturnRefundLine,
} from "@hb/pos-domain/features/returns/return-domain";
import {
  ReturnPresenter,
} from "@hb/pos-domain/features/returns/return-presenter";
import {
  ReturnWorkflow,
  type ReturnConnectivityPort,
  type ReturnExecutionCommand,
  type ReturnExecutionOutcome,
  type ReturnExecutionPort,
  type ReturnLookupPort,
  type ReturnWorkflowOptions,
  type ReturnRecoveryHydration,
  type ReturnSupervisorResolutionAuthorization,
  type ReturnSupervisorResolutionCommand,
  type ReturnWorkflowSnapshot,
} from "@hb/pos-domain/features/returns/return-workflow";

export const POS_RETURN_PERMISSIONS = Object.freeze({
  view: "Permissions.PosTerminal.Returns.View",
  addReceiptLine: "Permissions.PosTerminal.Returns.AddReceiptLine",
  addNoReceiptItem:
    "Permissions.PosTerminal.Returns.AddNoReceiptItem",
  confirm: "Permissions.PosTerminal.Returns.Confirm",
});

export interface PosReturnAuthorizationPort {
  activateRequestingCashier(
    identity: Parameters<
      OperationAuthorizationService["activateRequestingCashier"]
    >[0],
  ): void;
  authorizeAndRun<T>(
    input: OperationAuthorizationRequest,
    operation: (
      context: AuthorizedOperationContext,
    ) => T | Promise<T>,
  ): Promise<OperationAuthorizationResult<T>>;
}

export type PosReturnRuntimeService = Readonly<{
  /**
   * View 也遵循 WPF 主管覆盖流程；拒绝时 Promise 失败且绝不构造 presenter。
   */
  createPresenter(): Promise<ReturnPresenter>;
  /** route 只能读取同一可信收银员作用域下的脱敏布尔信号。 */
  hasRecoveryRequired(): Promise<boolean>;
}>;

export type ProductionReturnRuntimeDependencies = Readonly<{
  database: Pick<
    PosDatabase,
    | "catalogSnapshots"
    | "returnCapacityVault"
    | "returnExecutionLedger"
  >;
  repositories: Pick<PosRepositoryBundle, "orders">;
  encryptor: SensitivePayloadEncryptor;
  currentCashier: CurrentCashierSession;
  terminal: Readonly<{ storeCode: string; deviceCode: string }>;
  authorization: PosReturnAuthorizationPort;
  historyApi: ReturnHistoryApiPort;
  connectivity: ReturnConnectivityPort;
  cashRefund: DurableOfflineCashRefundPort;
  onlineRefund: DurableOnlineReturnRefundPort;
  fulfilment: Pick<
    ReturnFulfilmentRuntime,
    "materializeAction" | "drainPending"
  >;
  /** 同一 payment_attempts durable ACK 队列；未配置时保留既有退货行为。 */
  acknowledgements?: PaymentAcknowledgementRuntimePort | undefined;
  /** 退款结果未知的主管结案落点；缺省时该端不开放结案（失败关闭，仍只能重试恢复）。 */
  supervisorResolution?: ReturnSupervisorResolutionPort | undefined;
  /** 主管结案后补发后端会话 supervisorResolved ACK；失败只留队列，不影响结案结果。 */
  supervisorAcknowledgements?: Readonly<{ drain(): Promise<unknown> }> | undefined;
  sha256Hex(material: string): Promise<string>;
  createId(): string;
  nowIso(): string;
  operationLease?: UpdateOperationLeasePort;
}>;

export type ProductionReturnRecoveryProbeDependencies = Readonly<{
  database: Pick<PosDatabase, "returnExecutionLedger">;
  encryptor: SensitivePayloadEncryptor;
  currentCashier: CurrentCashierSession;
  terminal: Readonly<{ storeCode: string; deviceCode: string }>;
  createId(): string;
}>;

export type ProductionReturnRecoveryProbe = Readonly<{
  hasRecoveryRequired(): Promise<boolean>;
}>;

export class PosReturnRuntimeError extends Error {
  public constructor(
    public readonly code:
      | "RETURN_VIEW_FORBIDDEN"
      | "RETURN_SESSION_UNAVAILABLE"
      | "RETURN_RECOVERY_STATE_INVALID",
  ) {
    super(code);
    this.name = "PosReturnRuntimeError";
  }
}

/**
 * 设置与更新门禁只读取当前终端的退货恢复布尔值；不依赖主管授权、history API
 * 或完整退货 feature 是否可用，ledger 异常继续上抛给调用方 fail closed。
 */
export function createProductionReturnRecoveryProbe(
  input: ProductionReturnRecoveryProbeDependencies,
): ProductionReturnRecoveryProbe {
  const terminal = Object.freeze({
    storeCode: requiredText(input.terminal.storeCode),
    deviceCode: requiredText(input.terminal.deviceCode),
  });
  const ledger = input.database.returnExecutionLedger(input.encryptor, {
    createTenderGuid: () => runtimeId(input),
    createAuditEventId: () => runtimeId(input),
  });
  return createScopedReturnRecoveryProbe(
    ledger,
    input.currentCashier,
    terminal,
  );
}

type PendingPresenterCreation = Readonly<{
  epoch: number;
  promise: Promise<ReturnPresenter>;
}>;

/**
 * 生产退货组合根只公开 presenter factory 与恢复布尔值。SQL ledger、容量 Vault、
 * provider adapter、主管授权键及完整行材料全部留在闭包内。
 */
export function createProductionReturnRuntime(
  input: ProductionReturnRuntimeDependencies,
): PosReturnRuntimeService {
  const terminal = Object.freeze({
    storeCode: requiredText(input.terminal.storeCode),
    deviceCode: requiredText(input.terminal.deviceCode),
  });
  const localOrders = new OrderRepositoryLocalReturnLookup(
    input.repositories.orders,
  );
  const localCatalog = new CatalogLocalReturnAdapter(
    input.database.catalogSnapshots(),
  );
  const capacityVault = new DurableCapacityVaultAdapter({
    vault: input.database.returnCapacityVault(input.encryptor),
    createOpaqueId: () => runtimeId(input),
    nowIso: input.nowIso,
  });
  // 生产入口必须取得 PosDatabase 暴露的 SQLCipher ledger，而不是进程内替代品。
  const ledger: ReturnExecutionLedgerPort & ReturnRecoveryListPort =
    input.database.returnExecutionLedger(input.encryptor, {
      createTenderGuid: () => runtimeId(input),
      createAuditEventId: () => runtimeId(input),
    });
  const recoveryProbe = createScopedReturnRecoveryProbe(
    ledger,
    input.currentCashier,
    terminal,
  );

  let pendingCreation: PendingPresenterCreation | null = null;

  const service: PosReturnRuntimeService = Object.freeze({
    createPresenter(): Promise<ReturnPresenter> {
      let lease: TrustedCashierLease;
      let session: TrustedCashierSession;
      try {
        lease = input.currentCashier.createLease();
        session = requireScopedLease(lease, terminal);
      } catch {
        return Promise.reject(
          new PosReturnRuntimeError("RETURN_SESSION_UNAVAILABLE"),
        );
      }
      activateAuthorization(input.authorization, session);

      if (pendingCreation?.epoch === session.epoch) {
        return pendingCreation.promise;
      }

      const viewActionId = runtimeId(input);
      let entry: PendingPresenterCreation | null = null;
      const promise = (async () => {
        // 先让 entry 完成赋值，保证同步抛错也能精确清理本次 in-flight。
        await Promise.resolve();
        try {
          const result = await input.authorization.authorizeAndRun(
            permissionRequest(
              viewActionId,
              POS_RETURN_PERMISSIONS.view,
              "open-returns",
            ),
            () => {
              assertRuntimeLease(lease, terminal);
              return true;
            },
          );
          if (!result.authorized) {
            throw new PosReturnRuntimeError("RETURN_VIEW_FORBIDDEN");
          }
          assertRuntimeLease(lease, terminal);
          const recovery = await loadScopedRecovery(
            ledger,
            lease,
            terminal,
          );
          return createPresenterForLease({
            input,
            terminal,
            lease,
            ledger,
            localOrders,
            localCatalog,
            capacityVault,
            recovery,
          });
        } finally {
          if (entry && pendingCreation === entry) {
            pendingCreation = null;
          }
        }
      })();
      entry = Object.freeze({ epoch: session.epoch, promise });
      pendingCreation = entry;
      return promise;
    },

    async hasRecoveryRequired(): Promise<boolean> {
      return recoveryProbe.hasRecoveryRequired();
    },
  });

  return service;
}

function createScopedReturnRecoveryProbe(
  ledger: ReturnRecoveryListPort,
  currentCashier: CurrentCashierSession,
  terminal: Readonly<{ storeCode: string; deviceCode: string }>,
): ProductionReturnRecoveryProbe {
  return Object.freeze({
    async hasRecoveryRequired(): Promise<boolean> {
      const lease = currentCashier.createLease();
      const before = requireScopedLease(lease, terminal);
      const recoverable = await ledger.listRecoverable(
        recoveryScope(before),
      );
      requireScopedLease(lease, terminal);
      if (recoverable.length > 1) {
        throw new PosReturnRuntimeError(
          "RETURN_RECOVERY_STATE_INVALID",
        );
      }
      return recoverable.length === 1;
    },
  });
}

type PresenterFactoryContext = Readonly<{
  input: ProductionReturnRuntimeDependencies;
  terminal: Readonly<{ storeCode: string; deviceCode: string }>;
  lease: TrustedCashierLease;
  ledger: ReturnExecutionLedgerPort & ReturnRecoveryListPort;
  localOrders: ConstructorParameters<typeof ReturnLookupAdapter>[0]["localOrders"];
  localCatalog: ConstructorParameters<typeof ReturnLookupAdapter>[0]["localCatalog"];
  capacityVault: ConstructorParameters<typeof ReturnLookupAdapter>[0]["capacityVault"];
  recovery: DurableReturnRecoveryAction | null;
}>;

function createPresenterForLease(
  context: PresenterFactoryContext,
): ReturnPresenter {
  const { input, terminal, lease } = context;
  const materialCache = new ReturnLineMaterialCache();
  const collector = new PresenterReturnMaterialCollector(
    materialCache,
    () => runtimeId(input),
  );
  const lookup = new CollectingReturnLookup(
    new ReturnLookupAdapter({
      storeCode: terminal.storeCode,
      historyApi: input.historyApi,
      localOrders: context.localOrders,
      localCatalog: context.localCatalog,
      capacityVault: context.capacityVault,
      createOpaqueId: () => runtimeId(input),
    }),
    collector,
  );
  const assertActive = () =>
    assertWorkflowLease(lease, terminal);
  const identity = new LeaseBoundReturnIdentity(
    lease,
    terminal,
  );
  const orchestrator = new DurableReturnExecutionOrchestrator({
    ledger: context.ledger,
    trustedIdentity: identity,
    cashRefund: input.cashRefund,
    onlineRefund: input.onlineRefund,
    fingerprint: new CanonicalReturnFingerprint(input.sha256Hex),
    lineMaterial: materialCache,
    createOpaqueId: () => runtimeId(input),
    nowIso: input.nowIso,
    onAllocationFinalized: (finalized) =>
      acknowledgeFinalizedReturnAllocation(
        input.acknowledgements,
        finalized.allocation.durableAttemptId,
      ),
    supervisorResolution: input.supervisorResolution,
  });
  const execution = new MaterializingReturnExecution({
    delegate: orchestrator,
    identity,
    collector,
    fulfilment: input.fulfilment,
    supervisorAcknowledgements: input.supervisorAcknowledgements,
  });
  const leaseToken = runtimeId(input);
  const workflow = new AuthorizedReturnWorkflow(
    {
      lookup,
      connectivity: input.connectivity,
      supervisorAuthorization: {
        authorizeNoReceiptReturn: () =>
          authorizeNoReceiptReturn({
            authorization: input.authorization,
            assertActive,
            createId: () => runtimeId(input),
          }),
        ...(input.supervisorResolution
          ? {
              authorizeUnknownResolution: () =>
                authorizeUnknownResolution({
                  authorization: input.authorization,
                  assertActive,
                  createId: () => runtimeId(input),
                  requestingActor: () => requestingActorOf(identity),
                }),
            }
          : {}),
      },
      sessionGuard: {
        captureLease: () => leaseToken,
        assertActive: (captured) => {
          if (captured !== leaseToken) {
            throw new ReturnFeatureError("RETURN_SESSION_EXPIRED");
          }
          assertActive();
        },
      },
      execution,
      createActionId: () => runtimeId(input),
    },
    {
      authorization: input.authorization,
      assertActive,
      createId: () => runtimeId(input),
      collector,
    },
  );
  if (context.recovery) {
    workflow.hydrateRecovery(toRecoveryHydration(context.recovery));
  }
  return new ReturnPresenter(
    workflow,
    input.operationLease
      ? { operationLease: input.operationLease }
      : {},
  );
}

type AuthorizedWorkflowDependencies = Readonly<{
  authorization: PosReturnAuthorizationPort;
  assertActive(): void;
  createId(): string;
  collector: PresenterReturnMaterialCollector;
}>;

/**
 * 权限拒绝必须发生在 ReturnWorkflow 的支付边界之前；否则原 workflow 会按
 * Unknown 冻结。该窄子类先完成 AddReceiptLine/Confirm，再调用 super.confirm。
 */
class AuthorizedReturnWorkflow extends ReturnWorkflow {
  private addReceiptActionId: string | null = null;
  private confirmActionId: string | null = null;
  private recoveryConfirmActionId: string | null = null;
  private authorizationInFlight:
    | Promise<ReturnExecutionOutcome>
    | null = null;
  private recoveryAuthorizationInFlight:
    | Promise<ReturnExecutionOutcome>
    | null = null;

  public constructor(
    options: ReturnWorkflowOptions,
    private readonly dependencies: AuthorizedWorkflowDependencies,
  ) {
    super(options);
  }

  public override confirm(): Promise<ReturnExecutionOutcome> {
    if (this.getSnapshot().status === "unknown") {
      return super.confirm();
    }
    if (this.authorizationInFlight) {
      return this.authorizationInFlight;
    }
    const operation = this.authorizeAndConfirm().finally(() => {
      if (this.authorizationInFlight === operation) {
        this.authorizationInFlight = null;
      }
    });
    this.authorizationInFlight = operation;
    return operation;
  }

  public override recoverUnknown(): Promise<ReturnExecutionOutcome> {
    if (this.getSnapshot().status !== "unknown") {
      return super.recoverUnknown();
    }
    if (this.recoveryAuthorizationInFlight) {
      return this.recoveryAuthorizationInFlight;
    }
    const operation = this.authorizeAndRecover().finally(() => {
      if (this.recoveryAuthorizationInFlight === operation) {
        this.recoveryAuthorizationInFlight = null;
      }
    });
    this.recoveryAuthorizationInFlight = operation;
    return operation;
  }

  public override async loadReceipt(
    query: string,
  ): Promise<ReturnWorkflowSnapshot> {
    const snapshot = await super.loadReceipt(query);
    this.rotateAuthorizationCycle();
    return snapshot;
  }

  public override beginNoReceipt(): ReturnWorkflowSnapshot {
    const snapshot = super.beginNoReceipt();
    this.dependencies.collector.reset();
    this.rotateAuthorizationCycle();
    return snapshot;
  }

  public override reset(): ReturnWorkflowSnapshot {
    const snapshot = super.reset();
    this.dependencies.collector.reset();
    this.rotateAuthorizationCycle();
    return snapshot;
  }

  private async authorizeAndConfirm(): Promise<ReturnExecutionOutcome> {
    this.dependencies.assertActive();
    const snapshot = this.getSnapshot();
    const selectedReceiptLine =
      snapshot.caseKind === "receipt" &&
      snapshot.lines.some((line) => line.selectedQuantity > 0);
    if (selectedReceiptLine) {
      await this.requirePermission(
        this.addReceiptActionId ??
          (this.addReceiptActionId =
            requiredOpaque(this.dependencies.createId())),
        POS_RETURN_PERMISSIONS.addReceiptLine,
        "add-receipt-line",
        () => true,
      );
    }
    return this.requirePermission(
      this.confirmActionId ??
        (this.confirmActionId =
          requiredOpaque(this.dependencies.createId())),
      POS_RETURN_PERMISSIONS.confirm,
      "confirm-return",
      () => {
        this.dependencies.assertActive();
        return super.confirm();
      },
    );
  }

  private async authorizeAndRecover(): Promise<ReturnExecutionOutcome> {
    this.dependencies.assertActive();
    return this.requirePermission(
      this.recoveryConfirmActionId ??
        (this.recoveryConfirmActionId =
          requiredOpaque(this.dependencies.createId())),
      POS_RETURN_PERMISSIONS.confirm,
      "recover-return",
      () => {
        this.dependencies.assertActive();
        return super.recoverUnknown();
      },
    );
  }

  private async requirePermission<T>(
    actionId: string,
    permissionCode: string,
    action: string,
    operation: () => T | Promise<T>,
  ): Promise<T> {
    this.dependencies.assertActive();
    const result = await this.dependencies.authorization.authorizeAndRun(
      permissionRequest(actionId, permissionCode, action),
      () => {
        this.dependencies.assertActive();
        return operation();
      },
    );
    if (!result.authorized) {
      throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
    }
    this.dependencies.assertActive();
    return result.value;
  }

  private rotateAuthorizationCycle(): void {
    this.addReceiptActionId = null;
    this.confirmActionId = null;
    this.recoveryConfirmActionId = null;
  }
}

type NoReceiptAuthorizationContext = Readonly<{
  authorization: PosReturnAuthorizationPort;
  assertActive(): void;
  createId(): string;
}>;

async function authorizeNoReceiptReturn(
  context: NoReceiptAuthorizationContext,
): Promise<Readonly<{ authorizationKey: string }>> {
  context.assertActive();
  const result = await context.authorization.authorizeAndRun(
    permissionRequest(
      requiredOpaque(context.createId()),
      POS_RETURN_PERMISSIONS.addNoReceiptItem,
      "add-no-receipt-item",
    ),
    () => {
      context.assertActive();
      // 主管上下文和扫码票据绝不参与 grant；账本只消费独立随机不透明键。
      return Object.freeze({
        authorizationKey: requiredOpaque(context.createId()),
      });
    },
  );
  if (!result.authorized) {
    throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
  }
  context.assertActive();
  return result.value;
}

type UnknownResolutionAuthorizationContext = NoReceiptAuthorizationContext &
  Readonly<{ requestingActor(): Promise<AuditActorSnapshot> }>;

/**
 * 退款结果未知的主管结案必须强制另一名主管（当前收银员即使拥有 Returns.Confirm 也不能
 * 自己结案）；授权通过时在回调内冻结真实主管身份，主管凭据不进入 workflow 与页面。
 */
async function authorizeUnknownResolution(
  context: UnknownResolutionAuthorizationContext,
): Promise<ReturnSupervisorResolutionAuthorization> {
  context.assertActive();
  const authorizationId = requiredOpaque(context.createId());
  const requestingActor = await context.requestingActor();
  const result = await context.authorization.authorizeAndRun(
    {
      ...permissionRequest(
        authorizationId,
        POS_RETURN_PERMISSIONS.confirm,
        "resolve-unknown-refund",
      ),
      forceSupervisor: true,
    },
    (authorized) => {
      context.assertActive();
      if (!authorized.authorizingActor) {
        throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
      }
      return Object.freeze({
        authorizationId,
        supervisorActor: authorized.authorizingActor,
        requestingActor,
      });
    },
  );
  if (!result.authorized) {
    throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
  }
  context.assertActive();
  return result.value;
}

async function requestingActorOf(
  identity: ReturnTrustedIdentityPort,
): Promise<AuditActorSnapshot> {
  const trusted = await identity.getTrustedIdentity();
  return Object.freeze({
    cashierId: trusted.cashierId,
    cashierName: trusted.cashierName,
    userGuid: trusted.userGuid ?? null,
  });
}

class LeaseBoundReturnIdentity implements ReturnTrustedIdentityPort {
  public constructor(
    private readonly lease: TrustedCashierLease,
    private readonly terminal: Readonly<{
      storeCode: string;
      deviceCode: string;
    }>,
  ) {}

  public async getTrustedIdentity(): Promise<TrustedReturnIdentity> {
    const session = assertWorkflowLease(this.lease, this.terminal);
    return Object.freeze({
      storeCode: session.storeCode,
      deviceCode: session.deviceCode,
      cashierId: session.cashierId,
      cashierName: session.cashierName,
      userGuid: session.userGuid,
      sessionEpoch: String(session.epoch),
    });
  }
}

type CollectedReturnMaterial =
  | Readonly<ReceiptReturnLine & { sourceKind: "receipt" }>
  | NoReceiptReturnItem;

class PresenterReturnMaterialCollector {
  private readonly materials =
    new Map<string, CollectedReturnMaterial>();
  private readonly boundPlanByAction = new Map<string, string>();

  public constructor(
    private readonly cache: ReturnLineMaterialCache,
    private readonly createId: () => string,
  ) {}

  public replaceReceipt(lines: readonly ReceiptReturnLine[]): void {
    this.materials.clear();
    for (const line of lines) {
      this.record(
        Object.freeze({
          ...line,
          sourceKind: "receipt" as const,
        }),
      );
    }
  }

  public recordNoReceipt(item: NoReceiptReturnItem): void {
    this.record(Object.freeze({ ...item }));
  }

  public reset(): void {
    this.materials.clear();
  }

  /**
   * lookup 的完整非敏感展示材料只在此转成 durable line；workflow plan 本身
   * 不含名称、lookupCode 与单价，执行边界不得补猜。
   */
  public bindAction(
    command: ReturnExecutionCommand,
    identity: TrustedReturnIdentity,
  ): void {
    const actionId = requiredOpaque(command.actionId);
    const planSignature = JSON.stringify(command.plan);
    const existing = this.boundPlanByAction.get(actionId);
    if (existing !== undefined) {
      if (existing !== planSignature) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      return;
    }

    const lines = command.plan.lines.map((line) =>
      this.toDurableLine(line),
    );
    const workflowId = requiredOpaque(this.createId());
    this.cache.record({ workflowId, identity, lines });
    this.cache.bindAction({
      workflowId,
      actionId,
      identity,
      plan: command.plan,
    });
    this.boundPlanByAction.set(actionId, planSignature);
  }

  private record(material: CollectedReturnMaterial): void {
    const sourceKey = requiredOpaque(material.returnSourceKey);
    if (this.materials.has(sourceKey)) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    this.materials.set(sourceKey, material);
  }

  private toDurableLine(line: ReturnRefundLine): DurableReturnLine {
    const material = this.materials.get(line.returnSourceKey);
    if (!material || material.sourceKind !== line.sourceKind) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    assertMaterialIdentity(line, material);
    const amountCents = selectedMaterialAmount(line.quantity, material);
    if (line.signedAmountCents !== -amountCents) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    return Object.freeze({
      // 同一 presenter reset 后可能再次退同一 source；每个 durable action
      // 必须取得新的 lineId，不能复用上一次已落库的主键。
      lineId: requiredOpaque(this.createId()),
      selectionKey: material.selectionKey,
      sourceKind: material.sourceKind,
      returnSourceKey: material.returnSourceKey,
      originalOrderGuid:
        material.sourceKind === "receipt"
          ? material.originalOrderGuid
          : null,
      originalOrderDetailGuid:
        material.sourceKind === "receipt"
          ? material.originalOrderDetailGuid
          : null,
      productCode: material.productCode,
      itemNumber: material.itemNumber,
      lookupCode: material.lookupCode,
      displayName: material.displayName,
      quantity: line.quantity,
      unitRefundCents: material.unitRefundCents,
      signedAmountCents: line.signedAmountCents,
      availableQuantity:
        material.sourceKind === "receipt"
          ? material.availableQuantity
          : null,
      remainingAmountCents:
        material.sourceKind === "receipt"
          ? material.remainingAmountCents
          : null,
      syncProvenance: normalizeReturnLineSyncProvenance(
        material.syncProvenance,
      ),
    });
  }
}

class CollectingReturnLookup implements ReturnLookupPort {
  public constructor(
    private readonly delegate: ReturnLookupPort,
    private readonly collector: PresenterReturnMaterialCollector,
  ) {}

  public async lookupReceipt(
    query: string,
  ): Promise<ReceiptReturnContext | null> {
    const context = await this.delegate.lookupReceipt(query);
    if (context) this.collector.replaceReceipt(context.lines);
    return context;
  }

  public async lookupNoReceiptProduct(
    query: string,
  ): Promise<NoReceiptReturnItem | null> {
    const item = await this.delegate.lookupNoReceiptProduct(query);
    if (item) this.collector.recordNoReceipt(item);
    return item;
  }

  public async createNoReceiptOpenItem(input: Readonly<{
    displayName: string;
    unitRefundCents: number;
  }>): Promise<NoReceiptReturnItem | null> {
    const item = await this.delegate.createNoReceiptOpenItem(input);
    if (item) this.collector.recordNoReceipt(item);
    return item;
  }
}

type MaterializingExecutionOptions = Readonly<{
  delegate: ReturnExecutionPort & Readonly<{
    resolveUnknown?(
      command: ReturnSupervisorResolutionCommand,
    ): Promise<ReturnExecutionOutcome>;
  }>;
  supervisorAcknowledgements?: Readonly<{ drain(): Promise<unknown> }> | undefined;
  identity: ReturnTrustedIdentityPort;
  collector: PresenterReturnMaterialCollector;
  fulfilment: Pick<
    ReturnFulfilmentRuntime,
    "materializeAction" | "drainPending"
  >;
}>;

class MaterializingReturnExecution implements ReturnExecutionPort {
  public constructor(
    private readonly options: MaterializingExecutionOptions,
  ) {}

  public async execute(
    command: ReturnExecutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    const identity =
      await this.options.identity.getTrustedIdentity();
    this.options.collector.bindAction(command, identity);
    const outcome = await this.options.delegate.execute(command);
    if (outcome.status === "completed") {
      await bestEffortFulfilment(
        this.options.fulfilment,
        command.actionId,
      );
    }
    return outcome;
  }

  public async recover(input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>): Promise<ReturnExecutionOutcome> {
    const outcome = await this.options.delegate.recover(input);
    if (outcome.status === "completed") {
      await bestEffortFulfilment(
        this.options.fulfilment,
        input.actionId,
      );
    }
    return outcome;
  }

  public async resolveUnknown(
    command: ReturnSupervisorResolutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    const delegate = this.options.delegate;
    if (!delegate.resolveUnknown) {
      throw new ReturnFeatureError("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
    }
    const outcome = await delegate.resolveUnknown(command);
    if (outcome.status === "completed") {
      // provider 真实结果优先：结案前的恢复发现退款其实已批准并完成了退货。
      await bestEffortFulfilment(this.options.fulfilment, command.actionId);
    }
    try {
      // 结论已耐久；补发后端会话 ACK 失败只留队列，打开恢复中心/冷启动时继续重试。
      await this.options.supervisorAcknowledgements?.drain();
    } catch {
      // 队列保留，不翻转结案结果。
    }
    return outcome;
  }
}

async function acknowledgeFinalizedReturnAllocation(
  acknowledgements: PaymentAcknowledgementRuntimePort | undefined,
  durableAttemptId: string | null,
): Promise<boolean> {
  if (!acknowledgements || durableAttemptId === null) return true;
  try {
    const result = await acknowledgements.acknowledge(durableAttemptId);
    // 中文注释：Square 等非 Linkly attempt 会明确返回 pending=false，不能因它们
    // 没有 ACK marker 阻塞退款。只有 Linkly durable pending 才暂停下一笔退款。
    return result.pending === false;
  } catch {
    return false;
  }
}

async function bestEffortFulfilment(
  fulfilment: Pick<
    ReturnFulfilmentRuntime,
    "materializeAction" | "drainPending"
  >,
  actionId: string,
): Promise<void> {
  try {
    await fulfilment.materializeAction(actionId);
  } catch {
    // 履约失败只保留 pending plan，绝不能翻转 completed/Unknown 退款事实。
  }
  try {
    await fulfilment.drainPending();
  } catch {
    // drain 是独立 best-effort；单次失败同样不触发退款重放。
  }
}

async function loadScopedRecovery(
  ledger: ReturnRecoveryListPort,
  lease: TrustedCashierLease,
  terminal: Readonly<{ storeCode: string; deviceCode: string }>,
): Promise<DurableReturnRecoveryAction | null> {
  const before = requireScopedLease(lease, terminal);
  const recoverable = await ledger.listRecoverable(
    recoveryScope(before),
  );
  requireScopedLease(lease, terminal);
  if (recoverable.length > 1) {
    throw new PosReturnRuntimeError("RETURN_RECOVERY_STATE_INVALID");
  }
  return recoverable[0] ?? null;
}

function recoveryScope(
  session: TrustedCashierSession,
): ReturnRecoveryScope {
  return Object.freeze({
    storeCode: session.storeCode,
    deviceCode: session.deviceCode,
    cashierId: session.cashierId,
    sessionEpoch: String(session.epoch),
  });
}

function toRecoveryHydration(
  recovery: DurableReturnRecoveryAction,
): ReturnRecoveryHydration {
  return Object.freeze({
    actionId: recovery.actionId,
    sourceKind: recovery.sourceKind,
    totalRefundCents: recovery.totalRefundCents,
    lines: Object.freeze(
      recovery.lines.map((line) =>
        Object.freeze({
          sourceKind: line.sourceKind,
          itemNumber: line.itemNumber,
          displayName: line.displayName,
          quantity: line.quantity,
          unitRefundCents: line.unitRefundCents,
          signedAmountCents: line.signedAmountCents,
          syncProvenance: normalizeReturnLineSyncProvenance(
            line.syncProvenance,
          ),
        }),
      ),
    ),
  });
}

function activateAuthorization(
  authorization: PosReturnAuthorizationPort,
  session: TrustedCashierSession,
): void {
  authorization.activateRequestingCashier({
    cashierId: session.cashierId,
    cashierName: session.cashierName,
    userGuid: session.userGuid,
    storeCode: session.storeCode,
    deviceCode: session.deviceCode,
    permissions: session.permissionCodes,
  });
}

function permissionRequest(
  actionId: string,
  permissionCode: string,
  action: string,
): OperationAuthorizationRequest {
  return Object.freeze({
    actionId: requiredOpaque(actionId),
    permissionCode: requiredText(permissionCode),
    screen: "PosTerminal.Returns",
    action: requiredText(action),
  });
}

function requireScopedLease(
  lease: TrustedCashierLease,
  terminal: Readonly<{ storeCode: string; deviceCode: string }>,
): TrustedCashierSession {
  try {
    const session = lease.get();
    if (
      session.storeCode !== terminal.storeCode ||
      session.deviceCode !== terminal.deviceCode
    ) {
      throw new Error("scope mismatch");
    }
    return session;
  } catch {
    throw new PosReturnRuntimeError("RETURN_SESSION_UNAVAILABLE");
  }
}

function assertRuntimeLease(
  lease: TrustedCashierLease,
  terminal: Readonly<{ storeCode: string; deviceCode: string }>,
): TrustedCashierSession {
  return requireScopedLease(lease, terminal);
}

function assertWorkflowLease(
  lease: TrustedCashierLease,
  terminal: Readonly<{ storeCode: string; deviceCode: string }>,
): TrustedCashierSession {
  try {
    return requireScopedLease(lease, terminal);
  } catch {
    throw new ReturnFeatureError("RETURN_SESSION_EXPIRED");
  }
}

function assertMaterialIdentity(
  line: ReturnRefundLine,
  material: CollectedReturnMaterial,
): void {
  if (
    line.productCode !== material.productCode ||
    line.returnSourceKey !== material.returnSourceKey
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  if (material.sourceKind === "receipt") {
    if (
      line.originalOrderGuid !== material.originalOrderGuid ||
      line.originalOrderDetailGuid !==
        material.originalOrderDetailGuid
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    return;
  }
  if (
    line.originalOrderGuid !== null ||
    line.originalOrderDetailGuid !== null
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}

function selectedMaterialAmount(
  quantity: number,
  material: CollectedReturnMaterial,
): number {
  if (!Number.isSafeInteger(quantity) || quantity <= 0) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  if (
    material.sourceKind === "receipt" &&
    quantity === material.availableQuantity
  ) {
    return material.remainingAmountCents;
  }
  const amount = material.unitRefundCents * quantity;
  if (!Number.isSafeInteger(amount) || amount <= 0) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return amount;
}

function runtimeId(
  input: Pick<ProductionReturnRuntimeDependencies, "createId">,
): string {
  return requiredOpaque(input.createId());
}

function requiredOpaque(value: string): string {
  const normalized = requiredText(value);
  if (normalized.length > 128) {
    throw new Error("Return opaque id is invalid.");
  }
  return normalized;
}

function requiredText(value: string): string {
  const normalized = value.trim();
  if (!normalized) throw new Error("Return runtime text is required.");
  return normalized;
}

function normalizeReturnLineSyncProvenance(
  input: unknown,
): LineSyncProvenance {
  try {
    return normalizeLineSyncProvenance(input);
  } catch {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}
