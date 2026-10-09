import {
  CurrentCashierSession,
  type TrustedCashierLease,
  type TrustedCashierSession,
} from "./current-cashier-session";
import {
  ActivePricingCartPaymentLeaseCoordinator,
  type PaymentCartDurableRecoveryFallback,
  type PaymentCartRecoveryMaterialPort,
} from "./payment-cart-lease-coordinator";
import {
  OrderRepositoryPaymentCompletionProjection,
  OrderRepositoryPaymentReceiptRenderer,
  PersistedPaymentCompletionSettings,
  type PaymentReceiptSettingsPort,
} from "./payment-completion-runtime";
import type { PaymentProviderRuntimeBootstrap } from "./payment-provider-runtime-bootstrap";
import { ProductionReturnRefundAdapter } from "./production-return-refund-adapter";
import { withPersistedLinklyAcknowledgementRecovery } from "@/features/payments/runtime/linkly-acknowledgement-recovery-runtime";

import type {
  AuditActorSnapshot,
  CartSnapshot,
  Money,
  OrderTender,
  PaymentAttempt,
} from "@/core/contracts";
import type { PosDatabase } from "@/core/db/pos-database";
import type {
  PaymentDraftRecovery,
  PaymentRecoveryScope,
  SqlitePaymentDraftRecoveryStore,
} from "@/core/db/sqlite-payment-draft-recovery-store";
import type {
  PosRepositoryBundle,
  SensitivePayloadEncryptor,
} from "@/core/db/sqlite-repositories";
import type {
  VoucherTenderReversalDispositionStorePort,
  VoucherTenderReversalRecord,
  VoucherTenderReversalRecoveryStorePort,
  VoucherTenderReversalStorePort,
} from "@/core/db/sqlite-voucher-tender-reversal-store";
import { instrumentPaymentPresenter } from "@/core/performance/payment-performance";
import type { HbposAuditMetadata } from "@hb/pos-sync/core/sync/hbpos-sync-adapters";
import {
  ApprovedPaymentOrderCompletionService,
} from "@/features/payments/approved-payment-order-completion";
import {
  MixedPaymentCoordinator,
  type MixedPaymentOrderTruthPort,
  type MixedTenderReversalPort,
} from "@/features/payments/mixed";
import {
  VoucherTenderReversalService,
} from "@/features/payments/mixed/voucher-tender-reversal-service";
import {
  PaymentAttemptService,
  type PaymentConnectivityPort,
  type PaymentRecoveryControl,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import {
  PaymentAcknowledgementService,
  type LinklyLegacyAcknowledgementReconciler,
  type LinklyPaymentAcknowledgementPort,
  type PaymentAcknowledgementRuntimePort,
} from "@hb/pos-payments-core/features/payments/payment-acknowledgement-service";
import {
  SupervisorResolutionAcknowledgementService,
  type LinklySupervisorResolvedAcknowledger,
} from "@hb/pos-payments-core/features/payments/supervisor-resolution-acknowledgement-service";
import {
  PaymentCheckoutRuntime,
  PaymentCheckoutRuntimeError,
  type PaymentCheckoutDraft,
  type PaymentCheckoutDraftPort,
  type PaymentCheckoutPublicSnapshot,
  type PaymentCheckoutRecoveryRecord,
  type PaymentCheckoutRuntimePort,
  type PaymentPermissionCode,
} from "@/features/payments/runtime/payment-checkout-runtime";
import {
  PaymentFinalCompletionPlanner,
  SafeApprovedPaymentCompletionPlanner,
} from "@/features/payments/runtime/payment-completion-planner";
import type {
  VoucherApprovedPurchaseReleasePort,
} from "@/features/payments/runtime/payment-provider-registry";
import { DurableVoucherPreparationService } from "@/features/payments/runtime/voucher-preparation";
import {
  PaymentPresenter,
  type PaymentCheckoutEntryContext,
} from "@/features/payments/ui/payment-presenter";
import type {
  DurableOnlineReturnRefundPort,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import type { ActivePricingCartSession } from "@/features/sales/runtime";

const CASH_DRAWER_PERMISSION =
  "Permissions.PosTerminal.CashDrawer.Open";
/** 作废 Blocked 礼券撤券需要主管具备移除 tender 权限，且必须是另一名员工。 */
export const VOUCHER_REVERSAL_VOID_PERMISSION =
  "Permissions.PosTerminal.Payment.RemoveTender";

export type TenderReversalVoidAuthorization = Readonly<{
  /** 主管扫码核验后冻结的真实身份；不得由 UI 或当前会话拼接。 */
  authorizingActor: AuditActorSnapshot;
  permissionCode: string;
}>;

/**
 * 组合根把现有 operationAuthorization 主管授权流程注入这里；run 只能在授权
 * 成功的回调内执行，授权失败或取消必须抛错且不执行 run。
 */
export type TenderReversalVoidAuthorizer = <T>(
  request: Readonly<{
    action: "payment-tender-void-unreleased";
    screen: "payment";
    orderGuid: string;
    tenderGuid: string;
    permissionCode: string;
  }>,
  run: (authorization: TenderReversalVoidAuthorization) => Promise<T>,
) => Promise<T>;

export type PosPaymentRuntimeService =
  | Readonly<{
      status: "available";
      /**
       * entry 只用于首次页面展示；真实订单、金额和 revision 始终由 SQLCipher
       * draft 与独占购物车 lease 复核。
       */
      createPresenter(
        entry: PaymentCheckoutEntryContext | null,
      ): PaymentPresenter;
      /** 仅返回脱敏布尔值，供登录后的路由把崩溃恢复导向支付页。 */
      hasRecoveryRequired(): Promise<boolean>;
    }>
  | Readonly<{
      status: "unavailable";
      blockers: readonly string[];
    }>;

export type ProductionPaymentRuntime = Readonly<{
  service: PosPaymentRuntimeService;
  /** 不依赖 provider bootstrap 的窄恢复探针，只暴露当前终端是否存在阻断状态。 */
  recoveryProbe: Readonly<{
    hasRecoveryRequired(): Promise<boolean>;
  }>;
  /** 仅供生产组合根接入退货编排；不会进入 route 可见的 payments service。 */
  returnRefund: DurableOnlineReturnRefundPort | null;
  /** 仅供组合根在退货账本完成后确认，禁止暴露到页面。 */
  acknowledgements: PaymentAcknowledgementRuntimePort | null;
  /** 主管结案 ACK 的补发入口，仅供组合根在退货主管结案后触发；失败只保留队列。 */
  supervisorAcknowledgements: Readonly<{ drain(): Promise<unknown> }> | null;
  initializeRecovery(): Promise<void>;
}>;

export type ProductionPaymentRuntimeDependencies = Readonly<{
  database: PosDatabase;
  repositories: PosRepositoryBundle;
  encryptor: SensitivePayloadEncryptor;
  activeCart: ActivePricingCartSession;
  currentCashier: CurrentCashierSession;
  terminal: Pick<HbposAuditMetadata, "storeCode" | "deviceCode">;
  clock: Readonly<{
    now(): Date;
    nowIso(): string;
  }>;
  createId(): string;
  connectivity: PaymentConnectivityPort;
  bootstrap?: PaymentProviderRuntimeBootstrap | undefined;
  receiptSettings?: PaymentReceiptSettingsPort | undefined;
  /** 退货存在活动恢复时封锁新的付款/退款金融操作。 */
  hasReturnRecoveryRequired?: (() => Promise<boolean>) | undefined;
  /** 缺失时 Blocked 撤券仍保持阻断（失败关闭），不提供人工作废入口。 */
  authorizeTenderReversalVoid?: TenderReversalVoidAuthorizer | undefined;
  /** 冷启动支付草稿无法精确重算、已改按耐久购物车恢复时的诊断旁路（只含字段路径）。 */
  reportPaymentRecoveryFallback?:
    | ((fallback: PaymentCartDurableRecoveryFallback) => void)
    | undefined;
  drainFulfilment(): Promise<unknown>;
}>;

/**
 * 支付生产组合根。公开面只有 presenter 工厂与恢复布尔值；attempt service、
 * provider registry、数据库、可信会话及 Linkly session 均留在闭包内。
 */
export function createProductionPaymentRuntime(
  input: ProductionPaymentRuntimeDependencies,
): ProductionPaymentRuntime {
  const terminalScope = normalizeScope(input.terminal);
  const drafts = input.database.paymentDraftRecovery({
    createOrderGuid: input.createId,
    createOrderLineGuid: input.createId,
    createAuditEventId: input.createId,
  });
  const voucherReversalStore = voucherTenderReversalStore(input);
  // legacy 发现只在冷启动/显式恢复时执行一次；普通 read 不应触发 provider 查询。
  let discoverLegacyForRecoveryProbe:
    ((retry?: boolean) => Promise<void>) | null = null;
  const recoveryProbe = Object.freeze({
    async hasRecoveryRequired(): Promise<boolean> {
      await discoverLegacyForRecoveryProbe?.();
      const [draftRecovery, voucherReversal, pendingAcknowledgement] = await Promise.all([
        drafts.findBlockingRecovery(terminalScope),
        voucherReversalStore.findBlocking(terminalScope),
        drafts.findPendingLinklyAcknowledgement(terminalScope),
      ]);
      return draftRecovery !== null || voucherReversal !== null || pendingAcknowledgement !== null;
    },
  });

  if (!input.bootstrap) {
    return {
      initializeRecovery: async () => undefined,
      recoveryProbe,
      returnRefund: null,
      acknowledgements: null,
      supervisorAcknowledgements: null,
      service: {
        status: "unavailable",
        blockers: [
          "SQUARE_TERMINAL_CONFIGURATION_MISSING",
          "LINKLY_ENVIRONMENT_MISSING",
          "VOUCHER_PROTECTED_TOKEN_STORE_MISSING",
          "APPROVED_PAYMENT_COMPLETION_PLANNER_MISSING",
        ],
      },
    };
  }

  const receiptSettings: PaymentReceiptSettingsPort = input.receiptSettings ?? {
    getReceiptPrinterSettings: () =>
      input.database.settings().getReceiptPrinterSettings(),
  };
  const voucherRelease = availableVoucherRelease(input.bootstrap);
  const cartLease = new ActivePricingCartPaymentLeaseCoordinator(
    input.activeCart,
    paymentCartRecovery(drafts, terminalScope),
    input.createId,
    input.reportPaymentRecoveryFallback,
  );
  let recoveryInitialized = false;
  let returnRefund: ProductionReturnRefundAdapter | null = null;
  const linklyAcknowledger = input.bootstrap.linklyTerminals ? requireLinklyAcknowledger(input.bootstrap.providers) : null;
  const legacyReconciler = linklyAcknowledger ? legacyReconcilerOrNull(linklyAcknowledger) : null;
  const attempts = new PaymentAttemptService({
    ledger: input.repositories.payments,
    actionBindings: input.database.paymentActionBindings(),
    drafts,
    providers: input.bootstrap.providers,
    connectivity: input.connectivity,
    createAttemptId: input.createId,
    createIdempotencyKey: input.createId,
    nowIso: input.clock.nowIso,
    ...(legacyReconciler ? { legacyPaymentRecoveryEnvironment: legacyReconciler.reconcileLegacy } : {}),
    trustedRefundReferenceSeed: (request) => {
      if (!returnRefund) {
        throw new Error("RETURN_REFUND_RUNTIME_NOT_INITIALIZED");
      }
      return returnRefund.trustedRefundReferenceSeed(request);
    },
  });
  // 主管结案 ACK 队列：行与人工结论同事务写入，ACK 失败可在冷启动/下一次结案时重试。
  const supervisorAcks = linklyAcknowledger && isSupervisorResolvedAcknowledger(linklyAcknowledger)
    ? new SupervisorResolutionAcknowledgementService({
        queue: input.database.paymentSupervisorAckQueue(),
        ledger: input.repositories.payments,
        acknowledger: linklyAcknowledger,
        nowIso: input.clock.nowIso,
      })
    : null;
  const acknowledgements = linklyAcknowledger
    ? new PaymentAcknowledgementService({ ledger: input.repositories.payments, acknowledger: linklyAcknowledger, ...(legacyReconciler ? { legacyReconciler } : {}), nowIso: input.clock.nowIso })
    : null;
  let legacyDiscovery: Promise<void> | null = null;
  let legacyDiscoveryAttempted = false;
  const discoverLegacy = (retry = false): Promise<void> => {
    if (retry) legacyDiscoveryAttempted = false;
    if (legacyDiscovery) return legacyDiscovery;
    if (legacyDiscoveryAttempted) return Promise.resolve();
    const list = linklyAcknowledger?.listUnacknowledgedSessions;
    if (typeof list !== "function" || !legacyReconciler) {
      legacyDiscoveryAttempted = true;
      return Promise.resolve();
    }
    legacyDiscoveryAttempted = true;
    // 只从后端当前未确认会话反查本机旧记录；不扫描全部历史，也不发送金融请求。
    legacyDiscovery = (async () => {
      requireScopedCurrentCashier(input);
      // 新库没有旧环境缺失的 Linkly attempt 时，不触发 active/resumable 的
      // 240 秒网络探针；先用本地阻断记录确认存在候选，再查询后端当前会话。
      if (!await drafts.hasLegacyLinklyRecovery(terminalScope)) return;
      const sessions = await list.call(linklyAcknowledger) as readonly { sessionId: string; environment: string; idempotencyKey?: string }[];
      requireScopedCurrentCashier(input);
      for (const session of sessions) {
        if (typeof session.idempotencyKey !== "string") continue;
        const id = await drafts.findLegacyLinklyAttemptForSession(terminalScope, session.sessionId, session.idempotencyKey);
        if (!id) continue;
        const attempt = await input.repositories.payments.get(id);
        if (!attempt) continue;
        const environment = await legacyReconciler.reconcileLegacy(attempt);
        requireScopedCurrentCashier(input);
        if (environment !== session.environment) continue;
        await input.repositories.payments.verifyProviderEnvironment?.(attempt, environment);
      }
    })().catch(() => {
      // 权限/网络失败不是“已完成发现”；登录后 probe 必须允许重试。
      legacyDiscoveryAttempted = false;
    }).finally(() => {
      legacyDiscovery = null;
    });
    return legacyDiscovery;
  };
  discoverLegacyForRecoveryProbe = discoverLegacy;
  const voucherPreparation = new DurableVoucherPreparationService(
    input.database.voucherPreparationStore(
      input.encryptor,
      input.createId,
    ),
    {
      resolve: async () => {
        const session = requireScopedCurrentCashier(input);
        return {
          storeCode: session.storeCode,
          cashierId: session.cashierId,
        };
      },
    },
    {
      assertActive: async () => {
        requireScopedCurrentCashier(input);
      },
    },
  );
  returnRefund = new ProductionReturnRefundAdapter({
    paymentAttempts: attempts,
    capacityVault: input.database.returnCapacityVault(input.encryptor),
    providers: input.bootstrap.providers,
    voucherPreparation,
  });

  // Registry 构造早于可信收银员；这里只绑定一次动态、带 epoch 复核的受保护上下文。
  input.bootstrap.bindVoucherContextProvider(async (attempt) => {
    const lease = input.currentCashier.createLease();
    const before = requireScopedLease(lease, input.terminal);
    const context = await voucherPreparation.contextForAttempt(attempt);
    const after = requireScopedLease(lease, input.terminal);
    if (
      before !== after ||
      context.storeCode !== after.storeCode ||
      context.cashierId !== after.cashierId
    ) {
      throw new PaymentCheckoutRuntimeError(
        "VOUCHER_CONTEXT_NOT_PREPARED",
      );
    }
    return context;
  });

  const createContext = () => {
    if (!recoveryInitialized) {
      throw new Error("PAYMENT_RUNTIME_NOT_INITIALIZED");
    }
    const cashierLease = input.currentCashier.createLease();
    const actor = paymentAuditActor(
      requireScopedLease(cashierLease, input.terminal),
    );
    const guard = paymentSessionGuard(cashierLease, input.terminal);
    const draftPort = paymentDraftPort(
      drafts,
      terminalScope,
      cashierLease,
      actor,
      input,
      voucherRelease !== null,
    );
    const finalPlanner = new PaymentFinalCompletionPlanner({
      settings: new PersistedPaymentCompletionSettings(
        receiptSettings,
        {
          canOpenCashDrawer: () =>
            requireScopedLease(cashierLease, input.terminal)
              .permissionCodes.includes(CASH_DRAWER_PERMISSION),
        },
      ),
      renderer: new OrderRepositoryPaymentReceiptRenderer(
        input.repositories.orders,
        receiptSettings,
      ),
      createId: input.createId,
      nowIso: input.clock.nowIso,
    });
    const approvedCompletion = new ApprovedPaymentOrderCompletionService({
      planner: new SafeApprovedPaymentCompletionPlanner({
        projection: new OrderRepositoryPaymentCompletionProjection(
          input.repositories.orders,
        ),
        finalPlanner,
        createId: input.createId,
        nowIso: input.clock.nowIso,
      }),
      committer: input.database.paymentOrderCommitter(input.encryptor),
      recallCompletion: {
        // 支付 lease 持有期间 active cart 是唯一可信的恢复挂单绑定。
        resolveBinding: () => input.activeCart.read().recallBinding,
        createId: input.createId,
        nowIso: input.clock.nowIso,
      },
    });
    const orderTruth = input.database.mixedPaymentOrderTruth();
    const tenders = input.database.mixedPaymentTenders(
      {
        createTenderGuid: input.createId,
        createAuditEventId: input.createId,
      },
      {
        planner: finalPlanner,
        encryptor: input.encryptor,
        recallCompletion: {
          async resolve(orderGuid, actor) {
            const binding = input.activeCart.read().recallBinding;
            if (!binding) return null;
            const recalledAtIso = input.clock.nowIso();
            return {
              binding,
              recalledAtIso,
              recallAudit: {
                eventId: input.createId(),
                eventType: "ORDER_RECALL",
                occurredAtIso: recalledAtIso,
                orderGuid,
                correlationId: binding.holdId,
                payload: {
                  source: "pos-handheld",
                  action: "recall",
                  result: "completed",
                  storeCode: binding.scope.storeCode,
                  deviceCode: binding.scope.deviceCode,
                  cashierId: actor.cashierId,
                  cashierName: actor.cashierName,
                  userGuid: actor.userGuid,
                },
              },
            };
          },
        },
      },
    );
    const voucherReversal = voucherRelease
      ? new VoucherTenderReversalService({
          store: voucherReversalStore,
          paymentAttempts: attempts,
          release: voucherRelease,
        })
      : null;
    const mixed = new MixedPaymentCoordinator({
      actor,
      orderTruth,
      paymentAttempts: attempts,
      approvedCompletion,
      cashTender: tenders,
      tenderReversal: createProductionTenderReversalRouter({
        orderTruth,
        cash: tenders,
        voucher: voucherReversal,
      }),
    });
    const runtime = new PaymentCheckoutRuntime({
      mixed,
      attempts,
      drafts: draftPort,
      cartLease,
      providers: input.bootstrap!.providers,
      trustedSession: guard,
      permissions: guard,
      ...(input.bootstrap!.linklyPaymentSelection
        ? {
            linklyPaymentSelection:
              input.bootstrap!.linklyPaymentSelection,
          }
        : {}),
      voucherPreparation: {
        async preparePurchase(request) {
          await guard.assertActive();
          const prepared =
            await voucherPreparation.preparePurchase(request);
          await guard.assertActive();
          return prepared;
        },
      },
    });
    // 履约基于本地完成事实触发；ACK 网络故障不能推迟已入队的小票，也不能在 ACK-only 重试时重放履约。
    const committedRuntime = withPostCommitFulfilment(runtime, input.drainFulfilment);
    // 退货恢复封门必须位于 ACK wrapper 之外：没有 Linkly/ACK 能力时也要阻止
    // 新的付款金融操作；ACK-only 已持久终态快路径仍由外层 wrapper 直接处理。
    const financialGuardedRuntime = withReturnRecoveryFinancialGate(
      committedRuntime,
      async () => {
        await guard.assertActive();
        if (await input.hasReturnRecoveryRequired?.()) {
          await guard.assertActive();
          throw new PaymentCheckoutRuntimeError("RETURN_RECOVERY_REQUIRED");
        }
        await guard.assertActive();
      },
    );
    const acknowledgedRuntime = acknowledgements
      ? withPersistedLinklyAcknowledgementRecovery({
          runtime: financialGuardedRuntime,
          acknowledgements,
          assertView: async () => {
            await guard.assertActive();
            await guard.assert("Permissions.PosTerminal.Payment.View");
          },
          assertAcknowledge: async () => {
            await guard.assertActive();
            await guard.assert("Permissions.PosTerminal.Payment.View");
            await guard.assert("Permissions.PosTerminal.Payment.TakeCard");
            await guard.assert("Permissions.PosTerminal.Payment.Confirm");
          },
          async findPendingAttempt() {
            const id = await drafts.findPendingLinklyAcknowledgement(terminalScope);
            return id ? input.repositories.payments.get(id) : null;
          },
          getAttempt: (id) => input.repositories.payments.get(id),
          canAcknowledge: async (attempt) =>
            input.repositories.payments.canProviderAcknowledged?.(attempt) ?? false,
          async readFinalSnapshot(attempt) {
            await guard.assertActive();
            const order = await input.repositories.orders.getByGuid(attempt.orderGuid);
            await guard.assertActive();
            if (!order || order.storeCode !== terminalScope.storeCode || order.deviceCode !== terminalScope.deviceCode) {
              throw new PaymentCheckoutRuntimeError("PAYMENT_ATTEMPT_ORDER_MISMATCH");
            }
            const draft = await draftPort.read(attempt.orderGuid);
            const base = draft ? await runtime.read(attempt.orderGuid) : null;
            const status = attempt.state === "Declined" ? "declined"
              : attempt.state === "Cancelled" ? "cancelled"
                : order.state !== "Draft" && order.state !== "Completing" ? "completed" : "partial";
            return {
              ...(base ?? {
                orderGuid: order.orderGuid,
                total: { currency: "AUD" as const, cents: Math.abs(order.actualAmount.cents) },
                remaining: { currency: "AUD" as const, cents: 0 },
                tenders: [],
                allowedActions: { start: false, changeProvider: false, recover: false, cancel: false, addCash: false, removeTender: false },
              }),
              status,
              errorCode: null,
              attemptId: attempt.attemptId,
              attemptCreatedAtIso: attempt.createdAtIso,
              provider: attempt.provider,
              ...(status === "completed" ? { allowedActions: { start: false, changeProvider: false, recover: false, cancel: false, addCash: false, removeTender: false } } : {}),
            };
          },
        })
      : financialGuardedRuntime;
    return {
      runtime: withPersistedVoucherTenderReversalRecovery({
        runtime: acknowledgedRuntime,
        store: voucherReversalStore,
        scope: terminalScope,
        retryAvailable: voucherReversal !== null,
        voidBlocked: input.authorizeTenderReversalVoid
          ? createBlockedTenderReversalVoider({
              authorize: input.authorizeTenderReversalVoid,
              store: voucherReversalStore,
              scope: terminalScope,
              requestingActor: actor,
              assertActive: () => guard.assertActive(),
            })
          : null,
      }),
      linkly: acknowledgements ? input.bootstrap!.createLinklyOperator({ attempts, acknowledgements, trustedSession: guard, permissions: guard }) : null,
    };
  };

  return {
    returnRefund,
    acknowledgements,
    supervisorAcknowledgements: supervisorAcks
      ? Object.freeze({ drain: () => supervisorAcks.drain() })
      : null,
    recoveryProbe,
    initializeRecovery: async () => {
      await cartLease.initializeRecovery();
      recoveryInitialized = true;
      void supervisorAcks?.drain().catch(() => undefined);
      await discoverLegacy(true);
    },
    service: {
      status: "available",
      createPresenter(entry) {
        const context = createContext();
        return instrumentPaymentPresenter(new PaymentPresenter({
          runtime: context.runtime,
          ...(context.linkly
            ? { linklyOperator: context.linkly }
            : {}),
          ...(input.bootstrap!.linklyTerminals
            ? { linklyTerminals: input.bootstrap!.linklyTerminals }
            : {}),
          entry: normalizeEntry(entry, input.activeCart.getSnapshot()),
          createActionId: input.createId,
        }));
      },
      async hasRecoveryRequired() {
        const context = createContext();
        return (await context.runtime.findRecoveryRequired()) !== null;
      },
    },
  };
}

function requireLinklyAcknowledger(
  providers: PaymentProviderRuntimeBootstrap["providers"],
): LinklyPaymentAcknowledgementPort & Readonly<{ reconcileLegacy?: unknown; listUnacknowledgedSessions?: unknown }> {
  const provider = providers.get("linkly-cloud") as unknown;
  if (!provider || typeof provider !== "object" || !("acknowledge" in provider) || typeof provider.acknowledge !== "function") throw new Error("LINKLY_ACKNOWLEDGEMENT_PORT_MISSING");
  return provider as LinklyPaymentAcknowledgementPort & Readonly<{ reconcileLegacy?: unknown; listUnacknowledgedSessions?: unknown }>;
}
function isSupervisorResolvedAcknowledger(
  provider: object,
): provider is LinklySupervisorResolvedAcknowledger {
  return "acknowledgeSupervisorResolved" in provider &&
    typeof provider.acknowledgeSupervisorResolved === "function";
}

function legacyReconcilerOrNull(
  provider: Readonly<{ reconcileLegacy?: unknown; listUnacknowledgedSessions?: unknown }>,
): LinklyLegacyAcknowledgementReconciler | null {
  if (typeof provider.reconcileLegacy !== "function") return null;
  const reconcile = provider.reconcileLegacy as (
    attempt: PaymentAttempt,
    control?: PaymentRecoveryControl,
  ) => Promise<unknown>;
  return {
    async reconcileLegacy(attempt, control) {
      const verified = await reconcile.call(provider, attempt, control);
      if (
        !verified ||
        typeof verified !== "object" ||
        !("environment" in verified) ||
        typeof verified.environment !== "string"
      ) return null;
      return verified.environment;
    },
  };
}

function paymentAuditActor(
  session: TrustedCashierSession,
): AuditActorSnapshot {
  return Object.freeze({
    cashierId: session.cashierId,
    cashierName: session.cashierName,
    userGuid: session.userGuid,
  });
}

function paymentCartRecovery(
  drafts: SqlitePaymentDraftRecoveryStore,
  scope: PaymentRecoveryScope,
): PaymentCartRecoveryMaterialPort {
  return {
    async findBlockingCart() {
      const recovery = await drafts.findBlockingRecovery(scope);
      if (!recovery) return null;
      if (!recovery.draftId) {
        throw new Error("PAYMENT_RECOVERY_DRAFT_BINDING_MISSING");
      }
      return {
        checkoutIntentId: recovery.draftId,
        cart: recovery.cart,
        pricingState: recovery.pricingState,
        recallBinding: recovery.recallBinding,
      };
    },
  };
}

function paymentDraftPort(
  store: SqlitePaymentDraftRecoveryStore,
  scope: PaymentRecoveryScope,
  cashierLease: TrustedCashierLease,
  actor: AuditActorSnapshot,
  input: ProductionPaymentRuntimeDependencies,
  voucherReversalAvailable: boolean,
): PaymentCheckoutDraftPort {
  const assertActive = () =>
    requireScopedLease(cashierLease, input.terminal);
  const read = async (orderGuid: string) => {
    assertActive();
    const draft = await store.readDraft(orderGuid, scope);
    assertActive();
    return draft
      ? publicDraft(draft, voucherReversalAvailable)
      : null;
  };
  const readAfterDurableCompletion = async (orderGuid: string) => {
    // 不可逆支付已提交后只能使用组合根冻结的原 scope 读取耐久真相，绝不重新读取收银员或终端身份。
    const draft = await store.readDraft(orderGuid, scope);
    return draft
      ? publicDraft(draft, voucherReversalAvailable)
      : null;
  };

  return {
    async createOrReuse(request) {
      const identity = assertActive();
      const mutation = await store.createOrReuseDraft({
        draftId: request.checkoutIntentId,
        cart: request.lease.cart,
        pricingState: request.lease.pricingState,
        // 支付 lease 独占期间 binding 不可改变；与草稿同事务写入后可安全跨崩溃恢复。
        recallBinding: input.activeCart.read().recallBinding,
        identity: {
          storeCode: identity.storeCode,
          deviceCode: identity.deviceCode,
          cashierId: identity.cashierId,
          cashierName: identity.cashierName,
        },
        soldAtIso: input.clock.nowIso(),
      });
      assertActive();
      if (mutation.draftId !== request.checkoutIntentId) {
        throw new PaymentCheckoutRuntimeError(
          "PAYMENT_DRAFT_CONFLICT",
        );
      }
      const draft = await read(mutation.orderGuid);
      if (!draft) {
        throw new PaymentCheckoutRuntimeError(
          "PAYMENT_DRAFT_NOT_FOUND",
        );
      }
      return draft;
    },
    read,
    readAfterDurableCompletion,
    async findBlockingRecovery() {
      assertActive();
      const recovery = await store.findBlockingRecovery(scope);
      assertActive();
      if (!recovery) return null;
      return toCheckoutRecovery(
        store,
        scope,
        recovery,
        assertActive,
        voucherReversalAvailable,
      );
    },
    async abandonPrepared(request) {
      assertActive();
      const draft = await read(request.orderGuid);
      if (!draft) {
        throw new PaymentCheckoutRuntimeError(
          "PAYMENT_DRAFT_NOT_FOUND",
        );
      }
      const command = {
        ...scope,
        orderGuid: draft.orderGuid,
        draftId: draft.checkoutIntentId,
        actionId: request.actionId,
        actor,
      };
      const result = draft.cancellableAfterReversal
        ? await store.closeFullyReversedDraft(command)
        : await store.abandonPreparedDraft(command);
      // store resolve 即 durable commit；之后不再读取或复核可能已过期的收银员会话。
      return { replayed: result.replayed };
    },
    async closeCancelled(request) {
      assertActive();
      const result = await store.closeCancelledDraft({
        ...scope,
        orderGuid: request.orderGuid,
        actionId: request.actionId,
        actor,
      });
      assertActive();
      return {
        draft: publicDraft(
          result.draft,
          voucherReversalAvailable,
        ),
        replayed: result.replayed,
      };
    },
  };
}

async function toCheckoutRecovery(
  store: SqlitePaymentDraftRecoveryStore,
  scope: PaymentRecoveryScope,
  recovery: PaymentDraftRecovery,
  assertActive: () => TrustedCashierSession,
  voucherReversalAvailable: boolean,
): Promise<PaymentCheckoutRecoveryRecord> {
  const draft = await store.readDraft(recovery.orderGuid, scope);
  assertActive();
  if (!draft) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_NOT_FOUND");
  }
  const action = recovery.boundAction;
  if (action && action.operation !== "purchase") {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
    );
  }
  return {
    draft: publicDraft(draft, voucherReversalAvailable),
    attemptId:
      recovery.kind === "AttemptBlocking"
        ? recovery.attemptId
        : null,
    preparedAction: action
      ? {
          actionId: action.actionId,
          provider: action.provider,
          operation: "purchase",
          amount: copyMoney(action.amount),
          ...(action.manualConfirmed === undefined
            ? {}
            : { manualConfirmed: action.manualConfirmed }),
        }
      : null,
  };
}

function publicDraft(
  draft: PaymentCheckoutDraft,
  voucherReversalAvailable: boolean,
): PaymentCheckoutDraft {
  const canReverse = draft.state === "Draft" || draft.state === "Completing";
  return Object.freeze({
    ...draft,
    total: copyMoney(draft.total),
    remaining: copyMoney(draft.remaining),
    // 卡 reversal 未提供生产恢复语义；礼券只在窄 release capability 可用时开放。
    tenders: Object.freeze(
      draft.tenders.map((tender) =>
        Object.freeze({
          ...tender,
          amount: copyMoney(tender.amount),
          reversible:
            canReverse &&
            (tender.method === "cash" ||
              (tender.method === "voucher" &&
                voucherReversalAvailable)) &&
            tender.reversible,
        }),
      ),
    ),
  });
}

export function createProductionTenderReversalRouter(
  options: Readonly<{
    orderTruth: MixedPaymentOrderTruthPort;
    cash: MixedTenderReversalPort;
    voucher: MixedTenderReversalPort | null;
  }>,
): MixedTenderReversalPort {
  return {
    async reverseTender(command) {
      const order = await options.orderTruth.getPaymentTruth(
        command.orderGuid,
      );
      const source = order?.tenders.find(
        (tender: OrderTender) =>
          tender.tenderGuid === command.tenderGuid,
      );
      if (
        !source ||
        source.amount.currency !== "AUD" ||
        source.amount.cents <= 0
      ) {
        throw new Error("TENDER_REVERSAL_UNAVAILABLE");
      }
      if (source.method === "cash") {
        return options.cash.reverseTender(command);
      }
      if (source.method === "voucher" && options.voucher) {
        return options.voucher.reverseTender(command);
      }
      // 银行卡以及缺少 release capability 的礼券都不会落到任何 provider。
      throw new Error("TENDER_REVERSAL_UNAVAILABLE");
    },
  };
}

type AvailableVoucherRelease = Extract<
  VoucherApprovedPurchaseReleasePort,
  { status: "available" }
>;

function availableVoucherRelease(
  bootstrap: PaymentProviderRuntimeBootstrap,
): AvailableVoucherRelease | null {
  const candidate = (
    bootstrap as PaymentProviderRuntimeBootstrap &
      Partial<{
        voucherApprovedPurchaseRelease:
          VoucherApprovedPurchaseReleasePort;
      }>
  ).voucherApprovedPurchaseRelease;
  return candidate?.status === "available" ? candidate : null;
}

function voucherTenderReversalStore(
  input: Pick<
    ProductionPaymentRuntimeDependencies,
    "database" | "encryptor" | "createId"
  >,
): VoucherTenderReversalStorePort &
  VoucherTenderReversalRecoveryStorePort &
  VoucherTenderReversalDispositionStorePort {
  return input.database.voucherTenderReversals(
    input.encryptor,
    {
      createReversalTenderGuid: input.createId,
      createAuditEventId: input.createId,
    },
  );
}

type BlockedTenderReversalVoider = (
  blocking: VoucherTenderReversalRecord,
) => Promise<void>;

/**
 * 主管作废 Blocked 撤券：授权回调内再次复核可信会话，再由 store 在独占事务里
 * 校验 Blocked/来源绑定并追加负 tender + 审计。全程不调用 provider。
 */
function createBlockedTenderReversalVoider(
  options: Readonly<{
    authorize: TenderReversalVoidAuthorizer;
    store: VoucherTenderReversalDispositionStorePort;
    scope: PaymentRecoveryScope;
    requestingActor: AuditActorSnapshot;
    assertActive(): void;
  }>,
): BlockedTenderReversalVoider {
  return async (blocking) => {
    options.assertActive();
    let authorized = false;
    try {
      await options.authorize(
        {
          action: "payment-tender-void-unreleased",
          screen: "payment",
          orderGuid: blocking.orderGuid,
          tenderGuid: blocking.sourceTenderGuid,
          permissionCode: VOUCHER_REVERSAL_VOID_PERMISSION,
        },
        async (authorization) => {
          authorized = true;
          // 授权期间可能换班或锁屏；必须仍是同一可信收银员租约才落账。
          options.assertActive();
          await options.store.voidBlockedUnreleased({
            scope: options.scope,
            actionId: blocking.actionId,
            orderGuid: blocking.orderGuid,
            sourceTenderGuid: blocking.sourceTenderGuid,
            requestingActor: options.requestingActor,
            authorizingActor: authorization.authorizingActor,
            permissionCode: authorization.permissionCode,
          });
        },
      );
    } catch (error) {
      // 授权取消/拒绝时 run 从未执行，账本保持 Blocked；只暴露稳定错误码。
      if (!authorized) {
        throw new PaymentCheckoutRuntimeError(
          "TENDER_REVERSAL_VOID_NOT_AUTHORIZED",
        );
      }
      throw error;
    }
  };
}

function withPersistedVoucherTenderReversalRecovery(
  options: Readonly<{
    runtime: PaymentCheckoutRuntimePort;
    store: VoucherTenderReversalRecoveryStorePort;
    scope: PaymentRecoveryScope;
    retryAvailable: boolean;
    voidBlocked: BlockedTenderReversalVoider | null;
  }>,
): PaymentCheckoutRuntimePort {
  const readBlocking = () => options.store.findBlocking(options.scope);
  const voidAvailable = options.voidBlocked !== null;
  const projectCurrent = async (
    snapshot: PaymentCheckoutPublicSnapshot,
  ): Promise<PaymentCheckoutPublicSnapshot> => {
    const blocking = await readBlocking();
    if (!blocking) return snapshot;
    if (blocking.orderGuid !== snapshot.orderGuid) {
      throw new PaymentCheckoutRuntimeError(
        "TENDER_REVERSAL_RECOVERY_REQUIRED",
      );
    }
    return persistedVoucherReversalSnapshot(
      snapshot,
      blocking,
      options.retryAvailable,
      voidAvailable,
    );
  };
  const assertNoBlocking = async (
    orderGuid: string,
  ): Promise<void> => {
    // 先走公开 read，重新复核当前收银员、门店和 View 权限。
    await options.runtime.read(orderGuid);
    const blocking = await readBlocking();
    if (blocking) {
      throw new PaymentCheckoutRuntimeError(
        blocking.state === "Blocked"
          ? "TENDER_REVERSAL_BLOCKED"
          : "TENDER_REVERSAL_RECOVERY_REQUIRED",
      );
    }
  };
  const assertNoScopedBlocking = async (): Promise<void> => {
    const blocking = await readBlocking();
    if (blocking) {
      // start 尚无 orderGuid；先用持久 action 找回订单，再经公开 read
      // 复核当前收银员、门店和 View 权限，随后才返回稳定阻断。
      await options.runtime.read(blocking.orderGuid);
      throw new PaymentCheckoutRuntimeError(
        blocking.state === "Blocked"
          ? "TENDER_REVERSAL_BLOCKED"
          : "TENDER_REVERSAL_RECOVERY_REQUIRED",
      );
    }
  };

  return {
    listProviderAvailability: () =>
      options.runtime.listProviderAvailability(),
    canTakeCash: () => options.runtime.canTakeCash?.() === true,
    async read(orderGuid) {
      return projectCurrent(await options.runtime.read(orderGuid));
    },
    async findRecoveryRequired() {
      // 即使普通 payment recovery 误把 Approved source 视为已结清，
      // M16 action 仍是最终恢复真相。
      const blocking = await readBlocking();
      if (blocking) {
        return persistedVoucherReversalSnapshot(
          await options.runtime.read(blocking.orderGuid),
          blocking,
          options.retryAvailable,
          voidAvailable,
        );
      }
      return options.runtime.findRecoveryRequired();
    },
    async resumeCurrent(prepared) {
      const blocking = await readBlocking();
      if (blocking) {
        return persistedVoucherReversalSnapshot(
          await options.runtime.read(blocking.orderGuid),
          blocking,
          options.retryAvailable,
          voidAvailable,
        );
      }
      return options.runtime.resumeCurrent(prepared);
    },
    async start(request) {
      await assertNoScopedBlocking();
      return options.runtime.start(request);
    },
    ...(options.runtime.startCash
      ? {
          async startCash(request) {
            await assertNoScopedBlocking();
            return options.runtime.startCash!(request);
          },
        }
      : {}),
    async recover(request) {
      await assertNoBlocking(request.orderGuid);
      return options.runtime.recover(request);
    },
    async cancel(request) {
      await assertNoBlocking(request.orderGuid);
      return options.runtime.cancel(request);
    },
    async abandonPrepared(request) {
      await assertNoBlocking(request.orderGuid);
      return options.runtime.abandonPrepared(request);
    },
    async addCash(request) {
      await assertNoBlocking(request.orderGuid);
      return options.runtime.addCash(request);
    },
    async removeTender(request) {
      await assertNoBlocking(request.orderGuid);
      return projectCurrent(
        await options.runtime.removeTender(request),
      );
    },
    ...(options.voidBlocked
      ? {
          async voidBlockedTenderReversal(request: {
            orderGuid: string;
            tenderGuid: string;
          }) {
            // read 先复核可信会话、门店与 View 权限，再以私有 durable record 为准。
            const base = await options.runtime.read(request.orderGuid);
            const blocking = await readBlocking();
            if (!blocking) {
              // 幂等：已处置后不再有阻断，直接返回当前真实快照。
              return projectCurrent(base);
            }
            if (
              blocking.state !== "Blocked" ||
              blocking.orderGuid !== request.orderGuid ||
              blocking.sourceTenderGuid !== request.tenderGuid
            ) {
              throw new PaymentCheckoutRuntimeError(
                "TENDER_REVERSAL_UNAVAILABLE",
              );
            }
            await options.voidBlocked!(blocking);
            return projectCurrent(
              await options.runtime.read(request.orderGuid),
            );
          },
        }
      : {}),
    async retryTenderReversal(request) {
      // read 先完成可信会话与 View 权限复核；真正 removeTender 仍会复核
      // RemoveTender 权限并使用下面从私有 durable record 取得的原 actionId。
      const base = await options.runtime.read(request.orderGuid);
      const blocking = await readBlocking();
      if (
        !blocking ||
        blocking.orderGuid !== request.orderGuid ||
        blocking.sourceTenderGuid !== request.tenderGuid
      ) {
        throw new PaymentCheckoutRuntimeError(
          "TENDER_REVERSAL_UNAVAILABLE",
        );
      }
      if (blocking.state === "Blocked" || !options.retryAvailable) {
        return persistedVoucherReversalSnapshot(
          base,
          blocking,
          options.retryAvailable,
          voidAvailable,
        );
      }
      const result = await options.runtime.removeTender({
        orderGuid: blocking.orderGuid,
        actionId: blocking.actionId,
        tenderGuid: blocking.sourceTenderGuid,
      });
      return projectCurrent(result);
    },
  };
}

function persistedVoucherReversalSnapshot(
  snapshot: PaymentCheckoutPublicSnapshot,
  record: VoucherTenderReversalRecord,
  retryAvailable: boolean,
  voidAvailable: boolean,
): PaymentCheckoutPublicSnapshot {
  const source = snapshot.tenders.find(
    (tender) => tender.tenderGuid === record.sourceTenderGuid,
  );
  if (
    snapshot.orderGuid !== record.orderGuid ||
    record.truth.orderGuid !== record.orderGuid ||
    record.truth.state !== "Completing" ||
    !source ||
    source.method !== "voucher" ||
    source.amount.currency !== "AUD" ||
    source.amount.cents !== record.amount.cents ||
    record.amount.currency !== "AUD" ||
    !Number.isSafeInteger(record.amount.cents) ||
    record.amount.cents <= 0 ||
    record.state === "Reversed"
  ) {
    throw new PaymentCheckoutRuntimeError(
      "TENDER_REVERSAL_TRUTH_MISMATCH",
    );
  }
  const recoveryStatus =
    record.state === "Blocked"
      ? "blocked"
      : record.state === "Unknown"
        ? "unknown"
        : "pending";
  const canRetry =
    retryAvailable && record.state !== "Blocked";
  return Object.freeze({
    ...snapshot,
    tenders: Object.freeze(
      snapshot.tenders.map((tender) =>
        Object.freeze({
          ...tender,
          amount: copyMoney(tender.amount),
          reversible: false,
        }),
      ),
    ),
    status:
      record.state === "Unknown"
        ? "unknown"
        : "recovery-required",
    errorCode:
      record.state === "Blocked"
        ? "TENDER_REVERSAL_BLOCKED"
        : retryAvailable
          ? "TENDER_REVERSAL_RECOVERY_REQUIRED"
          : "TENDER_REVERSAL_UNAVAILABLE",
    tenderReversalRecovery: Object.freeze({
      tenderGuid: record.sourceTenderGuid,
      status: recoveryStatus,
      // 只有 Blocked（不会自动重试）才开放主管作废；Unknown 等仍按原 action 恢复。
      ...(record.state === "Blocked" && voidAvailable
        ? { voidAvailable: true }
        : {}),
    }),
    allowedActions: Object.freeze({
      start: false,
      changeProvider: false,
      recover: canRetry,
      cancel: false,
      addCash: false,
      removeTender: false,
    }),
  });
}

function paymentSessionGuard(
  lease: TrustedCashierLease,
  terminal: Pick<HbposAuditMetadata, "storeCode" | "deviceCode">,
) {
  return {
    assertActive(): void {
      requireScopedLease(lease, terminal);
    },
    assert(code: PaymentPermissionCode): void {
      const session = requireScopedLease(lease, terminal);
      if (!session.permissionCodes.includes(code)) {
        throw new PaymentCheckoutRuntimeError(
          "PAYMENT_CHECKOUT_FAILED",
        );
      }
    },
    can(code: PaymentPermissionCode): boolean {
      return requireScopedLease(lease, terminal).permissionCodes.includes(code);
    },
  };
}

function withReturnRecoveryFinancialGate(
  runtime: PaymentCheckoutRuntimePort,
  assertFinancialAvailable: () => Promise<void>,
): PaymentCheckoutRuntimePort {
  return {
    listProviderAvailability: () => runtime.listProviderAvailability(),
    canTakeCash: () => runtime.canTakeCash?.() === true,
    read: (orderGuid) => runtime.read(orderGuid),
    findRecoveryRequired: () => runtime.findRecoveryRequired(),
    async resumeCurrent(input) {
      await assertFinancialAvailable();
      return runtime.resumeCurrent(input);
    },
    async start(input) {
      await assertFinancialAvailable();
      return runtime.start(input);
    },
    ...(runtime.startCash
      ? {
          async startCash(input) {
            await assertFinancialAvailable();
            return runtime.startCash!(input);
          },
        }
      : {}),
    async recover(input) {
      await assertFinancialAvailable();
      return runtime.recover(input);
    },
    ...(runtime.retryTenderReversal
      ? {
          async retryTenderReversal(input: Parameters<NonNullable<PaymentCheckoutRuntimePort["retryTenderReversal"]>>[0]) {
            await assertFinancialAvailable();
            return runtime.retryTenderReversal!(input);
          },
        }
      : {}),
    cancel: (input) => runtime.cancel(input),
    abandonPrepared: (input) => runtime.abandonPrepared(input),
    async addCash(input) {
      await assertFinancialAvailable();
      return runtime.addCash(input);
    },
    async removeTender(input) {
      await assertFinancialAvailable();
      return runtime.removeTender(input);
    },
  };
}

function withPostCommitFulfilment(
  runtime: PaymentCheckoutRuntimePort,
  drain: () => Promise<unknown>,
): PaymentCheckoutRuntimePort {
  const after = async (
    operation: Promise<PaymentCheckoutPublicSnapshot | null>,
  ) => {
    const snapshot = await operation;
    if (snapshot?.status === "completed") {
      void drain().catch(() => undefined);
    }
    return snapshot;
  };
  return {
    listProviderAvailability: () =>
      runtime.listProviderAvailability(),
    canTakeCash: () => runtime.canTakeCash?.() === true,
    read: (orderGuid) => runtime.read(orderGuid),
    findRecoveryRequired: () =>
      runtime.findRecoveryRequired(),
    resumeCurrent: (prepared) =>
      after(runtime.resumeCurrent(prepared)),
    start: (request) => after(runtime.start(request)).then(requireSnapshot),
    ...(runtime.startCash
      ? {
          startCash: (request) =>
            after(runtime.startCash!(request)).then(requireSnapshot),
        }
      : {}),
    recover: (request) =>
      after(runtime.recover(request)).then(requireSnapshot),
    cancel: (request) =>
      after(runtime.cancel(request)).then(requireSnapshot),
    abandonPrepared: (request) =>
      runtime.abandonPrepared(request),
    addCash: (request) =>
      after(runtime.addCash(request)).then(requireSnapshot),
    removeTender: (request) =>
      after(runtime.removeTender(request))
        .then(requireSnapshot)
        .then(hardenUnresolvedReversalSnapshot),
    ...(runtime.retryTenderReversal
      ? {
          retryTenderReversal: (request) =>
            after(runtime.retryTenderReversal!(request))
              .then(requireSnapshot)
              .then(hardenUnresolvedReversalSnapshot),
        }
      : {}),
  };
}

function hardenUnresolvedReversalSnapshot(
  snapshot: PaymentCheckoutPublicSnapshot,
): PaymentCheckoutPublicSnapshot {
  if (
    snapshot.status !== "pending" &&
    snapshot.status !== "unknown" &&
    snapshot.status !== "declined" &&
    snapshot.status !== "recovery-required"
  ) {
    return snapshot;
  }
  // 撤券未形成 Reversed 事实时，页面只能读当前真相；不能换 provider、补现金或再撤。
  return Object.freeze({
    ...snapshot,
    allowedActions: Object.freeze({
      start: false,
      changeProvider: false,
      recover:
        snapshot.tenderReversalRecovery !== undefined &&
        snapshot.tenderReversalRecovery.status !== "blocked" &&
        snapshot.allowedActions.recover,
      cancel: false,
      addCash: false,
      removeTender: false,
    }),
  });
}

function requireSnapshot(
  snapshot: PaymentCheckoutPublicSnapshot | null,
): PaymentCheckoutPublicSnapshot {
  if (!snapshot) {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_CHECKOUT_FAILED",
    );
  }
  return snapshot;
}

function requireScopedCurrentCashier(
  input: Pick<
    ProductionPaymentRuntimeDependencies,
    "currentCashier" | "terminal"
  >,
): TrustedCashierSession {
  const session = input.currentCashier.require();
  assertScope(session, input.terminal);
  return session;
}

function requireScopedLease(
  lease: TrustedCashierLease,
  terminal: Pick<HbposAuditMetadata, "storeCode" | "deviceCode">,
): TrustedCashierSession {
  const session = lease.get();
  assertScope(session, terminal);
  return session;
}

function assertScope(
  session: Pick<TrustedCashierSession, "storeCode" | "deviceCode">,
  terminal: Pick<HbposAuditMetadata, "storeCode" | "deviceCode">,
): void {
  if (
    session.storeCode !== terminal.storeCode ||
    session.deviceCode !== terminal.deviceCode
  ) {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_CHECKOUT_FAILED",
    );
  }
}

function normalizeScope(
  scope: Pick<HbposAuditMetadata, "storeCode" | "deviceCode">,
): PaymentRecoveryScope {
  return {
    storeCode: requiredText(scope.storeCode),
    deviceCode: requiredText(scope.deviceCode),
  };
}

function normalizeEntry(
  entry: PaymentCheckoutEntryContext | null,
  activeCart: CartSnapshot,
): PaymentCheckoutEntryContext | null {
  if (!entry) return null;
  const checkoutIntentId = requiredText(entry.checkoutIntentId);
  if (
    !Number.isSafeInteger(entry.expectedCartRevision) ||
    entry.expectedCartRevision < 0 ||
    entry.total.currency !== "AUD" ||
    !Number.isSafeInteger(entry.total.cents) ||
    entry.total.cents <= 0
  ) {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_DRAFT_CONFLICT",
    );
  }
  // 路由只携带定位字段；展示明细必须来自组合根当前持有的可信购物车。
  // revision 或金额已变化时不展示可能过期的行，后续独占 lease 仍会失败关闭。
  const trustedLines =
    activeCart.revision === entry.expectedCartRevision &&
    activeCart.actualAmount.currency === entry.total.currency &&
    activeCart.actualAmount.cents === entry.total.cents
      ? activeCart.lines
      : [];
  return Object.freeze({
    checkoutIntentId,
    expectedCartRevision: entry.expectedCartRevision,
    total: copyMoney(entry.total),
    lines: Object.freeze(
      trustedLines.map((line) =>
        Object.freeze({
          lineKey: requiredText(line.lineId),
          displayName: requiredText(line.displayName),
          quantity: requiredText(line.quantity),
          actualAmountCents: line.actualAmount.cents,
        }),
      ),
    ),
  });
}

function copyMoney(value: Money): Money {
  return { currency: value.currency, cents: value.cents };
}

function requiredText(value: string): string {
  const normalized = value.trim();
  if (!normalized) {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_DRAFT_CONFLICT",
    );
  }
  return normalized;
}
