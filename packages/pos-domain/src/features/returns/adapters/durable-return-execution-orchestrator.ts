import {
  ReturnFeatureError,
  type OfflineCashCapacityProof,
  type ReturnRefundAllocation,
  type ReturnRefundLine,
  type ReturnRefundPlan,
  type ReturnTenderMethod,
} from "../return-domain";
import type {
  ReturnExecutionCommand,
  ReturnExecutionOutcome,
  ReturnExecutionPort,
  ReturnSupervisorFinding,
  ReturnSupervisorResolutionCommand,
} from "../return-workflow";

import type { AuditActorSnapshot } from "../../../core/contracts/audit-actor";
import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "../../../core/contracts/line-sync-provenance";

export type TrustedReturnIdentity = Readonly<{
  storeCode: string;
  deviceCode: string;
  cashierId: string;
  cashierName: string;
  /** 审计身份快照；订单表不持久化此字段，恢复时必须来自动作账本。 */
  userGuid?: string | null;
  /** Keychain/session coordinator 提供的不可伪造 epoch。 */
  sessionEpoch: string;
}>;

export interface ReturnTrustedIdentityPort {
  getTrustedIdentity(): Promise<TrustedReturnIdentity>;
}

export type DurableReturnAllocationStatus =
  | "created"
  | "submitted"
  | "completed"
  | "declined"
  | "unknown";

export type DurableReturnActionStatus =
  | "processing"
  | "unknown"
  | "declined"
  | "completed";

export type ReturnAllocationExecutionKind =
  | "offline-cash"
  | "online-refund";

export type DurableExternalAttemptKind =
  | "payment-provider"
  | "hbpos-api";

export type DurableReturnAllocation = Readonly<{
  allocationId: string;
  index: number;
  executionKind: ReturnAllocationExecutionKind;
  method: ReturnTenderMethod;
  signedAmountCents: number;
  capacityId: string | null;
  originalOrderGuid: string | null;
  offlineCashProof: OfflineCashCapacityProof | null;
  /** 在线退款端口据此幂等创建其耐久 provider/API attempt。 */
  externalAttemptId: string | null;
  externalAttemptKind: DurableExternalAttemptKind | null;
  externalActionId: string | null;
  durableAttemptId: string | null;
  status: DurableReturnAllocationStatus;
  /** provider recovery id 只允许留在受保护账本和支付端口中。 */
  protectedRecoveryKey: string | null;
}>;

export type DurableReturnLine = Readonly<{
  lineId: string;
  selectionKey: string;
  sourceKind: ReturnRefundLine["sourceKind"];
  returnSourceKey: string;
  originalOrderGuid: string | null;
  originalOrderDetailGuid: string | null;
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  quantity: number;
  unitRefundCents: number;
  signedAmountCents: number;
  /** receipt 冻结 lookup 时的容量；无小票必须为 null。 */
  availableQuantity: number | null;
  remainingAmountCents: number | null;
  syncProvenance: LineSyncProvenance;
}>;

export type PrepareDurableReturnAction = Readonly<{
  actionId: string;
  requestFingerprint: string;
  returnOrderGuid: string;
  actionRecoveryToken: string;
  identity: TrustedReturnIdentity;
  plan: ReturnRefundPlan;
  /**
   * 无小票退货必须提供一次性主管授权键；账本首次 prepare 时须在同一事务消费。
   */
  supervisorGrantKey: string | null;
  createdAtIso: string;
  lines: readonly DurableReturnLine[];
  allocations: readonly DurableReturnAllocation[];
}>;

export type DurableReturnAction = PrepareDurableReturnAction &
  Readonly<{
    status: DurableReturnActionStatus;
    completedAtIso: string | null;
  }>;

export type ReturnRecordDraft = Readonly<{
  returnDetailGuid: string;
  returnOrderGuid: string;
  originalOrderGuid: string | null;
  originalOrderDetailGuid: string | null;
  returnSourceKey: string;
  productCode: string;
  returnQuantity: number;
  returnAmountCents: number;
}>;

export type ReturnOutboxDraft = Readonly<{
  messageId: string;
  aggregateId: string;
  idempotencyKey: string;
  kind: "return-order-sync";
}>;

export type ReturnFulfilmentDraft = Readonly<{
  printJobId: string | null;
  drawerEventId: string | null;
  receiptKind: "none" | "refund-voucher" | "refund-receipt";
  drawerRequired: boolean;
}>;

export type CompleteDurableReturnAction = Readonly<{
  actionId: string;
  returnOrderGuid: string;
  completedAtIso: string;
  identity: TrustedReturnIdentity;
  plan: ReturnRefundPlan;
  lines: readonly DurableReturnLine[];
  returnRecords: readonly ReturnRecordDraft[];
  outbox: ReturnOutboxDraft;
  fulfilment: ReturnFulfilmentDraft;
}>;

/**
 * 生产实现是唯一允许写退货账本、订单、return records、outbox 和 fulfilment 的边界。
 * 所有 CAS 返回 false 都表示调用方已失去所有权，绝不能继续调用外部退款。
 */
export interface ReturnExecutionLedgerPort {
  /**
   * 首次调用原子保存完整 plan、可信身份、allocation 绑定并消费主管一次性授权。
   * actionId 已存在时只返回原记录，禁止覆盖任何字段。
   */
  prepareOrLoad(
    draft: PrepareDurableReturnAction,
  ): Promise<DurableReturnAction>;
  load(actionId: string): Promise<DurableReturnAction | null>;
  markAllocationSubmitted(input: Readonly<{
    actionId: string;
    allocationId: string;
  }>): Promise<boolean>;
  /** 校验 returnOrderGuid、refund signature、金额和方法；同 allocation 只能绑定一次。 */
  bindAllocationAttempt(input: Readonly<{
    actionId: string;
    allocationId: string;
    attemptKind: DurableExternalAttemptKind;
    externalActionId: string;
    durableAttemptId: string;
  }>): Promise<boolean>;
  recordAllocationOutcome(input: Readonly<{
    actionId: string;
    allocationId: string;
    expectedStatuses: readonly Extract<
      DurableReturnAllocationStatus,
      "submitted" | "unknown"
    >[];
    status: Extract<
      DurableReturnAllocationStatus,
      "completed" | "declined" | "unknown"
    >;
    protectedRecoveryKey: string | null;
  }>): Promise<boolean>;
  markActionUnknown(input: Readonly<{
    actionId: string;
  }>): Promise<void>;
  resumeUnknownAction(input: Readonly<{
    actionId: string;
  }>): Promise<boolean>;
  markActionDeclined(input: Readonly<{
    actionId: string;
  }>): Promise<void>;
  /**
   * 单事务落相同 returnOrderGuid 的本地退货订单、return records、outbox、
   * 打印任务和（若需要）钱箱事件，然后把 action 标为 completed。
   */
  completeAtomically(
    input: CompleteDurableReturnAction,
  ): Promise<DurableReturnAction>;
}

export type ReturnRecoveryScope = Readonly<{
  storeCode: string;
  deviceCode: string;
  cashierId: string;
  /** 当前 session 只用于输入完整性校验；查询刻意忽略旧 action 的 epoch。 */
  sessionEpoch: string;
}>;

export type DurableReturnRecoveryLine = Readonly<{
  sourceKind: DurableReturnLine["sourceKind"];
  itemNumber: string | null;
  displayName: string;
  quantity: number;
  unitRefundCents: number;
  signedAmountCents: number;
  syncProvenance: LineSyncProvenance;
}>;

/**
 * 恢复列表只能返回渲染与绑定原 action 所需的最小投影。
 * allocation、capacity、provider recovery key、主管授权与密文均不在此合同中。
 */
export type DurableReturnRecoveryAction = Readonly<{
  actionId: string;
  returnOrderGuid: string;
  sourceKind: ReturnRefundPlan["sourceKind"];
  totalRefundCents: number;
  status: Extract<DurableReturnActionStatus, "processing" | "unknown">;
  lines: readonly DurableReturnRecoveryLine[];
}>;

export interface ReturnRecoveryListPort {
  /**
   * 实现按 store + device + cashier 查询 processing/unknown。
   * 同一终端同时存在多条时必须失败关闭，不得猜测要恢复哪一条。
   */
  listRecoverable(
    scope: ReturnRecoveryScope,
  ): Promise<readonly DurableReturnRecoveryAction[]>;
}

export type ReturnAllocationExternalOutcome =
  | Readonly<{ status: "completed" }>
  | Readonly<{ status: "declined" }>
  | Readonly<{
      status: "unknown";
      /** 只写受保护账本，不返回 UI。 */
      protectedRecoveryKey: string | null;
    }>;

export type OfflineCashRefundInput = Readonly<{
  actionId: string;
  allocationId: string;
  returnOrderGuid: string;
  signedAmountCents: number;
  originalOrderGuid: string;
  capacityId: string;
  offlineCashProof: OfflineCashCapacityProof;
}>;

/** 实现必须按 allocationId 幂等保存现金退款，不得直接开钱箱。 */
export interface DurableOfflineCashRefundPort {
  submit(
    input: OfflineCashRefundInput,
  ): Promise<ReturnAllocationExternalOutcome>;
  recover(
    input: OfflineCashRefundInput &
      Readonly<{ protectedRecoveryKey: string | null }>,
  ): Promise<ReturnAllocationExternalOutcome>;
}

export type OnlineReturnRefundInput = Readonly<{
  actionId: string;
  allocationId: string;
  externalAttemptId: string;
  returnOrderGuid: string;
  actor: AuditActorSnapshot;
  method: ReturnTenderMethod;
  signedAmountCents: number;
  capacityId: string | null;
  originalOrderGuid: string | null;
  attemptKind: DurableExternalAttemptKind;
  externalActionId: string;
  durableAttemptId: string;
}>;

export type PreparedOnlineReturnAttempt = Readonly<{
  attemptKind: DurableExternalAttemptKind;
  externalActionId: string;
  durableAttemptId: string;
}>;

/**
 * 卡/券实现可绑定 provider payment attempt；在线现金/分期绑定 Hbpos API attempt。
 * 两类实现都必须先以 externalAttemptId 耐久化，再从受保护 Vault 解析 capacityId；
 * 本 Port 永远不接收原 provider reference。
 */
export interface DurableOnlineReturnRefundPort {
  /** 只创建或读取耐久 attempt，不得在本方法调用支付 provider。 */
  prepareAttempt(
    input: Omit<
      OnlineReturnRefundInput,
      "attemptKind" | "externalActionId" | "durableAttemptId"
    >,
  ): Promise<PreparedOnlineReturnAttempt>;
  submit(
    input: OnlineReturnRefundInput,
  ): Promise<ReturnAllocationExternalOutcome>;
  recover(
    input: OnlineReturnRefundInput &
      Readonly<{ protectedRecoveryKey: string | null }>,
  ): Promise<ReturnAllocationExternalOutcome>;
}

export interface ReturnRequestFingerprintPort {
  digest(input: Readonly<{
    command: ReturnExecutionCommand;
    identity: TrustedReturnIdentity;
    lines: readonly DurableReturnLine[];
  }>): Promise<string>;
}

/**
 * 根 workflow 的退款 plan 刻意不携带展示字段；生产实现必须按 actionId
 * 从受保护 draft store 解析完整行，不能在 provider 调用后再猜商品名称或单价。
 */
export interface ReturnExecutionLineMaterialPort {
  resolveForAction(input: Readonly<{
    actionId: string;
    identity: TrustedReturnIdentity;
    plan: ReturnRefundPlan;
  }>): Promise<readonly DurableReturnLine[]>;
}

export type ReturnSupervisorResolutionRecord = Readonly<{
  actionId: string;
  finding: ReturnSupervisorFinding;
  evidenceReference: string;
  note: string;
  authorizationId: string;
  supervisorActor: AuditActorSnapshot;
  requestingActor: AuditActorSnapshot;
  /** 作用域来自可信身份，存储层据此复核 action 属于本终端。 */
  scope: Readonly<{ storeCode: string; deviceCode: string }>;
}>;

/**
 * 主管结案的耐久落点。实现必须在同一事务内完成：复核 action/allocation 仍处于
 * 未决、写不可变结论与审计、（not-refunded）作废 action 并释放额度、登记后端会话
 * 的 supervisorResolved ACK 待办。任何不满足条件的情形抛错且不留下半截状态。
 * - "declined"：整张 action 已作废（not-refunded）；
 * - "waiting"：只留审计（keep-waiting），action 仍锁定。
 */
export interface ReturnSupervisorResolutionPort {
  resolve(record: ReturnSupervisorResolutionRecord): Promise<"declined" | "waiting">;
}

export type DurableReturnExecutionOptions = Readonly<{
  ledger: ReturnExecutionLedgerPort;
  /** 缺省表示该端不开放主管结案，resolveUnknown 会失败关闭。 */
  supervisorResolution?: ReturnSupervisorResolutionPort | undefined;
  trustedIdentity: ReturnTrustedIdentityPort;
  cashRefund: DurableOfflineCashRefundPort;
  onlineRefund: DurableOnlineReturnRefundPort;
  fingerprint: ReturnRequestFingerprintPort;
  lineMaterial: ReturnExecutionLineMaterialPort;
  createOpaqueId(
    kind:
      | "return-order"
      | "allocation"
      | "external-attempt"
      | "recovery-token"
      | "return-record"
      | "outbox"
      | "print-job"
      | "drawer-event",
  ): string;
  nowIso(): string;
  /**
   * 外部退款终态已耐久化后的可选确认闸门。false 表示确认仍 pending：
   * 不能继续下一笔外部退款，必须把 action 留给恢复流程先重试原确认。
   */
  onAllocationFinalized?: ((input: Readonly<{
    action: DurableReturnAction;
    allocation: DurableReturnAllocation;
  }>) => Promise<boolean>) | undefined;
}>;

/**
 * 每个外部退款前先 CAS 为 submitted；submitted/unknown 只能走 recover。
 * 因此“批准后崩溃”不会通过重新 submit 造成二次退款。
 */
export class DurableReturnExecutionOrchestrator
  implements ReturnExecutionPort
{
  private readonly active = new Map<
    string,
    Promise<ReturnExecutionOutcome>
  >();
  /** 同一进程已确认过的 allocation 不重复调用确认端口；崩溃后的新实例仍会从 durable ledger 重试。 */
  private readonly acknowledgedAllocations = new Set<string>();

  public constructor(private readonly options: DurableReturnExecutionOptions) {}

  public execute(
    command: ReturnExecutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    return this.serialized(command.actionId, () => this.executeOnce(command));
  }

  public recover(input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>): Promise<ReturnExecutionOutcome> {
    return this.serialized(input.actionId, () => this.recoverOnce(input));
  }

  public resolveUnknown(
    command: ReturnSupervisorResolutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    return this.serialized(command.actionId, () => this.resolveUnknownOnce(command));
  }

  private async resolveUnknownOnce(
    command: ReturnSupervisorResolutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    const port = this.options.supervisorResolution;
    if (!port) {
      throw new ReturnFeatureError("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
    }
    const action = await this.requireAction(command.actionId);
    const identity = await this.options.trustedIdentity.getTrustedIdentity();
    validateIdentity(identity);
    assertSameRecoveryScope(action.identity, identity);
    if (action.status === "completed") return completedOutcome(action);
    if (action.status === "declined") return { status: "declined" };

    if (command.finding === "not-refunded") {
      // provider 的真实结果优先：先按原 attempt 做一次标准恢复，只有仍然未知才允许主管
      // 凭线下核对作出“未退款”结论，避免迟到的已批准退款被人工结论盖掉后重复退款。
      const recovered = await this.recoverOnce({
        actionId: action.actionId,
        recoveryKey: null,
      });
      if (recovered.status !== "unknown") return recovered;
    }

    const result = await port.resolve({
      actionId: action.actionId,
      finding: command.finding,
      evidenceReference: command.evidenceReference,
      note: command.note,
      authorizationId: command.authorization.authorizationId,
      supervisorActor: command.authorization.supervisorActor,
      requestingActor: command.authorization.requestingActor,
      scope: { storeCode: identity.storeCode, deviceCode: identity.deviceCode },
    });
    if (result === "declined") return { status: "declined" };
    return unknownOutcome(await this.requireAction(action.actionId));
  }

  private async executeOnce(
    command: ReturnExecutionCommand,
  ): Promise<ReturnExecutionOutcome> {
    validateExecutionCommand(command);
    const identity = await this.options.trustedIdentity.getTrustedIdentity();
    validateIdentity(identity);
    const lines = await this.options.lineMaterial.resolveForAction({
      actionId: command.actionId,
      identity,
      plan: command.plan,
    });
    validateLineMaterials(command.plan.lines, lines);
    const requestFingerprint = await this.options.fingerprint.digest({
      command,
      identity,
      lines,
    });
    const draft = this.buildActionDraft(
      command,
      identity,
      requiredOpaque(requestFingerprint),
      lines,
    );
    const action = await this.options.ledger.prepareOrLoad(draft);
    this.assertBoundAction(action, draft);

    if (action.status === "completed") return completedOutcome(action);
    if (action.status === "declined") return { status: "declined" };
    if (
      action.status === "unknown" ||
      action.allocations.some(
        (allocation) =>
          allocation.status === "submitted" ||
          allocation.status === "unknown",
      )
    ) {
      return unknownOutcome(action);
    }
    return this.drive(action.actionId, "submit");
  }

  private async recoverOnce(input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>): Promise<ReturnExecutionOutcome> {
    const action = await this.requireAction(input.actionId);
    const identity = await this.options.trustedIdentity.getTrustedIdentity();
    validateIdentity(identity);
    assertSameRecoveryScope(action.identity, identity);
    if (
      input.recoveryKey !== null &&
      input.recoveryKey !== action.actionRecoveryToken
    ) {
      throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
    }
    if (action.status === "completed") return completedOutcome(action);
    if (action.status === "declined") return { status: "declined" };
    if (
      action.status === "unknown" &&
      !(await this.options.ledger.resumeUnknownAction({
        actionId: action.actionId,
      }))
    ) {
      const latest = await this.requireAction(action.actionId);
      return terminalOrUnknown(latest);
    }
    return this.drive(action.actionId, "recover");
  }

  private async drive(
    actionId: string,
    mode: "submit" | "recover",
  ): Promise<ReturnExecutionOutcome> {
    while (true) {
      const action = await this.requireAction(actionId);
      if (action.status === "completed") return completedOutcome(action);
      if (action.status === "declined") return { status: "declined" };
      if (!(await this.confirmPersistedFinalAllocations(action))) {
        await this.safelyMarkActionUnknown(actionId);
        const latest = await this.options.ledger.load(actionId);
        return unknownOutcome(latest ?? action);
      }
      const allocation = [...action.allocations]
        .sort((left, right) => left.index - right.index)
        .find((candidate) => candidate.status !== "completed");

      if (!allocation) {
        return this.complete(action);
      }
      if (allocation.status === "declined") {
        await this.options.ledger.markActionDeclined({ actionId });
        return { status: "declined" };
      }
      if (
        mode === "submit" &&
        (allocation.status === "submitted" ||
          allocation.status === "unknown")
      ) {
        await this.safelyMarkActionUnknown(actionId);
        return unknownOutcome(action);
      }

      const outcome =
        allocation.status === "created"
          ? await this.submitAllocation(action, allocation)
          : await this.recoverAllocation(action, allocation);
      if (
        outcome.status !== "unknown" &&
        !(await this.confirmPersistedFinalAllocation(
          actionId,
          allocation.allocationId,
        ))
      ) {
        await this.safelyMarkActionUnknown(actionId);
        const latest = await this.options.ledger.load(actionId);
        return unknownOutcome(latest ?? action);
      }
      if (outcome.status === "declined") {
        await this.options.ledger.markActionDeclined({ actionId });
        return { status: "declined" };
      }
      if (outcome.status === "unknown") {
        await this.safelyMarkActionUnknown(actionId);
        const latest = await this.options.ledger.load(actionId);
        return unknownOutcome(latest ?? action);
      }
    }
  }

  private async submitAllocation(
    action: DurableReturnAction,
    allocation: DurableReturnAllocation,
  ): Promise<ReturnAllocationExternalOutcome> {
    let claimed = false;
    try {
      claimed = await this.options.ledger.markAllocationSubmitted({
        actionId: action.actionId,
        allocationId: allocation.allocationId,
      });
    } catch {
      return { status: "unknown", protectedRecoveryKey: null };
    }
    if (!claimed) {
      return { status: "unknown", protectedRecoveryKey: null };
    }

    let outcome: ReturnAllocationExternalOutcome;
    try {
      outcome =
        allocation.executionKind === "offline-cash"
          ? await this.options.cashRefund.submit(
              toOfflineCashInput(action, allocation),
            )
          : await this.submitOnlineAllocation(action, allocation);
    } catch {
      outcome = { status: "unknown", protectedRecoveryKey: null };
    }
    try {
      await this.persistExternalOutcome(action.actionId, allocation, outcome, [
        "submitted",
      ]);
    } catch {
      return {
        status: "unknown",
        protectedRecoveryKey:
          outcome.status === "unknown"
            ? outcome.protectedRecoveryKey
            : null,
      };
    }
    return outcome;
  }

  private async recoverAllocation(
    action: DurableReturnAction,
    allocation: DurableReturnAllocation,
  ): Promise<ReturnAllocationExternalOutcome> {
    let outcome: ReturnAllocationExternalOutcome;
    try {
      outcome =
        allocation.executionKind === "offline-cash"
          ? await this.options.cashRefund.recover({
              ...toOfflineCashInput(action, allocation),
              protectedRecoveryKey: allocation.protectedRecoveryKey,
            })
          : await this.recoverOnlineAllocation(action, allocation);
    } catch {
      outcome = {
        status: "unknown",
        protectedRecoveryKey: allocation.protectedRecoveryKey,
      };
    }
    try {
      await this.persistExternalOutcome(action.actionId, allocation, outcome, [
        "submitted",
        "unknown",
      ]);
    } catch {
      return {
        status: "unknown",
        protectedRecoveryKey:
          outcome.status === "unknown"
            ? outcome.protectedRecoveryKey
            : allocation.protectedRecoveryKey,
      };
    }
    return outcome;
  }

  private async persistExternalOutcome(
    actionId: string,
    allocation: DurableReturnAllocation,
    outcome: ReturnAllocationExternalOutcome,
    expectedStatuses: readonly Extract<
      DurableReturnAllocationStatus,
      "submitted" | "unknown"
    >[],
  ): Promise<void> {
    const saved = await this.options.ledger.recordAllocationOutcome({
      actionId,
      allocationId: allocation.allocationId,
      expectedStatuses,
      status: outcome.status,
      protectedRecoveryKey:
        outcome.status === "unknown"
          ? outcome.protectedRecoveryKey
          : allocation.protectedRecoveryKey,
    });
    if (!saved) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
  }

  private async confirmPersistedFinalAllocations(
    action: DurableReturnAction,
  ): Promise<boolean> {
    for (const allocation of action.allocations) {
      if (
        (allocation.status === "completed" || allocation.status === "declined") &&
        !(await this.confirmPersistedFinalAllocation(
          action.actionId,
          allocation.allocationId,
        ))
      ) {
        return false;
      }
    }
    return true;
  }

  private async confirmPersistedFinalAllocation(
    actionId: string,
    allocationId: string,
  ): Promise<boolean> {
    const callback = this.options.onAllocationFinalized;
    if (!callback) return true;
    const key = `${actionId}:${allocationId}`;
    if (this.acknowledgedAllocations.has(key)) return true;
    try {
      const action = await this.requireAction(actionId);
      const allocation = action.allocations.find(
        (candidate) => candidate.allocationId === allocationId,
      );
      if (
        !allocation ||
        (allocation.status !== "completed" && allocation.status !== "declined")
      ) {
        return true;
      }
      // 中文注释：先读取刚完成持久化的 allocation，再确认 Linkly；任何确认失败都
      // 只能暂停整张 return action，绝不能让第二笔退款撞上同终端的 server guard。
      if (!(await callback({ action, allocation }))) return false;
      this.acknowledgedAllocations.add(key);
      return true;
    } catch {
      return false;
    }
  }

  private async submitOnlineAllocation(
    action: DurableReturnAction,
    allocation: DurableReturnAllocation,
  ): Promise<ReturnAllocationExternalOutcome> {
    const bound = await this.ensureOnlineAttemptBinding(action, allocation);
    return this.options.onlineRefund.submit(
      toOnlineRefundInput(action, bound),
    );
  }

  private async recoverOnlineAllocation(
    action: DurableReturnAction,
    allocation: DurableReturnAllocation,
  ): Promise<ReturnAllocationExternalOutcome> {
    const bound = await this.ensureOnlineAttemptBinding(action, allocation);
    return this.options.onlineRefund.recover({
      ...toOnlineRefundInput(action, bound),
      protectedRecoveryKey: allocation.protectedRecoveryKey,
    });
  }

  private async ensureOnlineAttemptBinding(
    action: DurableReturnAction,
    allocation: DurableReturnAllocation,
  ): Promise<DurableReturnAllocation> {
    if (
      (allocation.externalAttemptKind === null) !==
        (allocation.externalActionId === null) ||
      (allocation.externalActionId === null) !==
        (allocation.durableAttemptId === null)
    ) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
    if (
      allocation.externalAttemptKind &&
      allocation.externalActionId &&
      allocation.durableAttemptId
    ) {
      return allocation;
    }

    const prepared = await this.options.onlineRefund.prepareAttempt(
      toOnlineRefundPreparationInput(action, allocation),
    );
    const attemptKind = prepared.attemptKind;
    assertAttemptKindCompatible(allocation.method, attemptKind);
    const externalActionId = requiredOpaque(prepared.externalActionId);
    const durableAttemptId = requiredOpaque(prepared.durableAttemptId);
    await this.options.ledger.bindAllocationAttempt({
      actionId: action.actionId,
      allocationId: allocation.allocationId,
      attemptKind,
      externalActionId,
      durableAttemptId,
    });
    const latest = await this.requireAction(action.actionId);
    const latestAllocation = latest.allocations.find(
      (candidate) => candidate.allocationId === allocation.allocationId,
    );
    if (
      !latestAllocation ||
      latestAllocation.externalAttemptKind !== attemptKind ||
      latestAllocation.externalActionId !== externalActionId ||
      latestAllocation.durableAttemptId !== durableAttemptId
    ) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
    return latestAllocation;
  }

  private async complete(
    action: DurableReturnAction,
  ): Promise<ReturnExecutionOutcome> {
    const fulfilmentPolicy = deriveReturnFulfilmentPolicy(
      action.allocations,
    );
    let completed: DurableReturnAction;
    try {
      completed = await this.options.ledger.completeAtomically({
        actionId: action.actionId,
        returnOrderGuid: action.returnOrderGuid,
        completedAtIso: requiredIso(this.options.nowIso()),
        identity: action.identity,
        plan: action.plan,
        lines: action.lines,
        returnRecords: action.plan.lines.map((line) =>
          this.toReturnRecord(action.returnOrderGuid, line),
        ),
        outbox: {
          messageId: this.id("outbox"),
          aggregateId: action.returnOrderGuid,
          idempotencyKey: action.returnOrderGuid,
          kind: "return-order-sync",
        },
        fulfilment: {
          printJobId:
            fulfilmentPolicy.receiptKind === "none"
              ? null
              : this.id("print-job"),
          drawerEventId: fulfilmentPolicy.drawerRequired
            ? this.id("drawer-event")
            : null,
          receiptKind: fulfilmentPolicy.receiptKind,
          drawerRequired: fulfilmentPolicy.drawerRequired,
        },
      });
    } catch {
      let latest: DurableReturnAction | null = null;
      try {
        latest = await this.options.ledger.load(action.actionId);
      } catch {
        // 本地账本暂不可读时仍保持 Unknown；禁止据此重新生成退货单。
      }
      if (latest?.status === "completed") return completedOutcome(latest);
      await this.safelyMarkActionUnknown(action.actionId);
      return unknownOutcome(action);
    }
    if (
      completed.status !== "completed" ||
      completed.returnOrderGuid !== action.returnOrderGuid
    ) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
    return completedOutcome(completed);
  }

  private buildActionDraft(
    command: ReturnExecutionCommand,
    identity: TrustedReturnIdentity,
    requestFingerprint: string,
    lines: readonly DurableReturnLine[],
  ): PrepareDurableReturnAction {
    const returnOrderGuid = this.id("return-order");
    return {
      actionId: requiredOpaque(command.actionId),
      requestFingerprint,
      returnOrderGuid,
      actionRecoveryToken: this.id("recovery-token"),
      identity,
      plan: command.plan,
      supervisorGrantKey: command.noReceiptAuthorizationKey,
      createdAtIso: requiredIso(this.options.nowIso()),
      lines,
      allocations: command.plan.allocations.map((allocation, index) =>
        this.toDurableAllocation(allocation, index),
      ),
    };
  }

  private toDurableAllocation(
    allocation: ReturnRefundAllocation,
    index: number,
  ): DurableReturnAllocation {
    const executionKind = isOfflineCashAllocation(allocation)
      ? "offline-cash"
      : "online-refund";
    return {
      allocationId: this.id("allocation"),
      index,
      executionKind,
      method: allocation.method,
      signedAmountCents: allocation.signedAmountCents,
      capacityId: allocation.originalCapacityId,
      originalOrderGuid: allocation.originalOrderGuid,
      offlineCashProof: allocation.offlineCashProof,
      externalAttemptId:
        executionKind === "online-refund"
          ? this.id("external-attempt")
          : null,
      externalAttemptKind: null,
      externalActionId: null,
      durableAttemptId: null,
      status: "created",
      protectedRecoveryKey: null,
    };
  }

  private toReturnRecord(
    returnOrderGuid: string,
    line: ReturnRefundLine,
  ): ReturnRecordDraft {
    return {
      returnDetailGuid: this.id("return-record"),
      returnOrderGuid,
      originalOrderGuid: line.originalOrderGuid,
      originalOrderDetailGuid: line.originalOrderDetailGuid,
      returnSourceKey: requiredOpaque(line.returnSourceKey),
      productCode: requiredOpaque(line.productCode),
      returnQuantity: line.quantity,
      returnAmountCents: -line.signedAmountCents,
    };
  }

  private assertBoundAction(
    action: DurableReturnAction,
    draft: PrepareDurableReturnAction,
  ): void {
    if (
      action.actionId !== draft.actionId ||
      action.requestFingerprint !== draft.requestFingerprint
    ) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
    assertSameIdentity(action.identity, draft.identity);
    validatePersistedAction(action);
  }

  private async requireAction(
    actionId: string,
  ): Promise<DurableReturnAction> {
    const action = await this.options.ledger.load(requiredOpaque(actionId));
    if (!action) throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
    validatePersistedAction(action);
    return action;
  }

  private async safelyMarkActionUnknown(actionId: string): Promise<void> {
    try {
      await this.options.ledger.markActionUnknown({ actionId });
    } catch {
      // submitted allocation 本身已是耐久恢复锚点；账本暂时不可写时仍必须向 UI 返回 Unknown。
    }
  }

  private id(
    kind: Parameters<DurableReturnExecutionOptions["createOpaqueId"]>[0],
  ): string {
    return requiredOpaque(this.options.createOpaqueId(kind));
  }

  private serialized(
    actionId: string,
    operation: () => Promise<ReturnExecutionOutcome>,
  ): Promise<ReturnExecutionOutcome> {
    const key = requiredOpaque(actionId);
    const existing = this.active.get(key);
    if (existing) return existing;
    const promise = operation().finally(() => {
      if (this.active.get(key) === promise) this.active.delete(key);
    });
    this.active.set(key, promise);
    return promise;
  }
}

function deriveReturnFulfilmentPolicy(
  allocations: readonly DurableReturnAllocation[],
): Readonly<{
  receiptKind: ReturnFulfilmentDraft["receiptKind"];
  drawerRequired: boolean;
}> {
  const hasCash = allocations.some(
    (allocation) => allocation.method === "cash",
  );
  const hasCard = allocations.some(
    (allocation) => allocation.method === "card",
  );
  // 与 WPF 对齐：代金券买的部分须退代金券后，现金/刷卡 + 退款券的混合退款常见，每张退款券都要出票。
  // 含刷卡打印退货小票（其后追加各张退款券）；不含刷卡但有退款券时逐张打印退款券；其余只开钱箱。
  const hasVoucher = allocations.some(
    (allocation) => allocation.method === "voucher",
  );
  return Object.freeze({
    receiptKind: hasCard
      ? "refund-receipt"
      : hasVoucher
        ? "refund-voucher"
        : "none",
    drawerRequired: hasCash,
  });
}

function validateExecutionCommand(command: ReturnExecutionCommand): void {
  requiredOpaque(command.actionId);
  validatePlan(command.plan);
  if (
    command.plan.sourceKind === "no-receipt" &&
    !command.noReceiptAuthorizationKey?.trim()
  ) {
    throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
  }
  if (
    command.plan.sourceKind === "receipt" &&
    command.noReceiptAuthorizationKey !== null
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}

function validatePlan(plan: ReturnRefundPlan): void {
  if (
    !Number.isSafeInteger(plan.totalRefundCents) ||
    plan.totalRefundCents <= 0 ||
    plan.lines.length === 0 ||
    plan.allocations.length === 0
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  let lineTotal = 0;
  const receiptOrderGuids = new Set<string>();
  for (const line of plan.lines) {
    if (
      !Number.isSafeInteger(line.quantity) ||
      line.quantity <= 0 ||
      !Number.isSafeInteger(line.signedAmountCents) ||
      line.signedAmountCents >= 0
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    requiredOpaque(line.returnSourceKey);
    requiredOpaque(line.productCode);
    requireSyncProvenance(
      line.syncProvenance,
      "RETURN_SOURCE_MISMATCH",
    );
    if (plan.sourceKind === "receipt") {
      if (
        line.sourceKind !== "receipt" ||
        !line.originalOrderGuid ||
        !line.originalOrderDetailGuid
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      receiptOrderGuids.add(requiredOpaque(line.originalOrderGuid));
      requiredOpaque(line.originalOrderDetailGuid);
    } else if (
      line.sourceKind === "receipt" ||
      line.originalOrderGuid !== null ||
      line.originalOrderDetailGuid !== null
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    lineTotal = safePositiveAdd(lineTotal, -line.signedAmountCents);
  }
  if (plan.sourceKind === "receipt" && receiptOrderGuids.size !== 1) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  let allocationTotal = 0;
  for (const allocation of plan.allocations) {
    if (
      !Number.isSafeInteger(allocation.signedAmountCents) ||
      allocation.signedAmountCents >= 0
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    allocationTotal = safePositiveAdd(
      allocationTotal,
      -allocation.signedAmountCents,
    );
    if (plan.sourceKind === "no-receipt") {
      if (
        allocation.originalCapacityId !== null ||
        allocation.originalOrderGuid !== null ||
        allocation.offlineCashProof !== null
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
    } else if (
      !allocation.originalCapacityId ||
      !allocation.originalOrderGuid ||
      !receiptOrderGuids.has(allocation.originalOrderGuid)
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    if (allocation.offlineCashProof) {
      const proof = allocation.offlineCashProof;
      if (
        allocation.method !== "cash" ||
        proof.capacityId !== allocation.originalCapacityId ||
        proof.originalOrderGuid !== allocation.originalOrderGuid ||
        !proof.evidenceId.trim() ||
        -allocation.signedAmountCents > proof.remainingCents
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
    }
    if (
      !plan.online &&
      !isOfflineCashAllocation(allocation)
    ) {
      throw new ReturnFeatureError("RETURN_ONLINE_REQUIRED");
    }
  }
  if (
    lineTotal !== plan.totalRefundCents ||
    allocationTotal !== plan.totalRefundCents
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}

function validatePersistedAction(action: DurableReturnAction): void {
  requiredOpaque(action.actionId);
  requiredOpaque(action.returnOrderGuid);
  requiredOpaque(action.actionRecoveryToken);
  requiredOpaque(action.requestFingerprint);
  validateIdentity(action.identity);
  validatePlan(action.plan);
  if (action.allocations.length !== action.plan.allocations.length) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  validateLineMaterials(action.plan.lines, action.lines);
  const ids = new Set<string>();
  const indexes = new Set<number>();
  for (const allocation of action.allocations) {
    if (
      ids.has(allocation.allocationId) ||
      indexes.has(allocation.index) ||
      allocation.index < 0 ||
      !Number.isSafeInteger(allocation.index) ||
      (allocation.externalAttemptKind === null) !==
        (allocation.externalActionId === null) ||
      (allocation.externalActionId === null) !==
        (allocation.durableAttemptId === null) ||
      (allocation.executionKind === "offline-cash" &&
        (allocation.externalAttemptId !== null ||
          allocation.externalAttemptKind !== null ||
          allocation.externalActionId !== null ||
          allocation.durableAttemptId !== null)) ||
      (allocation.executionKind === "online-refund" &&
        allocation.externalAttemptId === null)
    ) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
    ids.add(requiredOpaque(allocation.allocationId));
    indexes.add(allocation.index);
    if (allocation.externalAttemptId) {
      requiredOpaque(allocation.externalAttemptId);
    }
    if (allocation.externalActionId) {
      requiredOpaque(allocation.externalActionId);
    }
    if (allocation.durableAttemptId) {
      requiredOpaque(allocation.durableAttemptId);
    }
    if (allocation.externalAttemptKind) {
      assertAttemptKindCompatible(
        allocation.method,
        allocation.externalAttemptKind,
      );
    }
  }
  if (
    [...indexes].some(
      (index) => index >= action.allocations.length,
    )
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
}

function validateLineMaterials(
  planLines: readonly ReturnRefundLine[],
  materials: readonly DurableReturnLine[],
): void {
  if (materials.length !== planLines.length) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  const bySource = new Map(materials.map((line) => [line.returnSourceKey, line]));
  if (bySource.size !== materials.length) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  for (const planned of planLines) {
    const material = bySource.get(planned.returnSourceKey);
    if (
      !material ||
      material.sourceKind !== planned.sourceKind ||
      material.originalOrderGuid !== planned.originalOrderGuid ||
      material.originalOrderDetailGuid !== planned.originalOrderDetailGuid ||
      material.productCode !== planned.productCode ||
      material.quantity !== planned.quantity ||
      material.signedAmountCents !== planned.signedAmountCents ||
      !sameSyncProvenance(
        material.syncProvenance,
        planned.syncProvenance,
      ) ||
      !Number.isSafeInteger(material.unitRefundCents) ||
      material.unitRefundCents <= 0 ||
      (planned.sourceKind === "receipt"
        ? !Number.isSafeInteger(material.availableQuantity) ||
          material.availableQuantity === null ||
          material.availableQuantity < planned.quantity ||
          !Number.isSafeInteger(material.remainingAmountCents) ||
          material.remainingAmountCents === null ||
          material.remainingAmountCents < -planned.signedAmountCents
        : material.availableQuantity !== null ||
          material.remainingAmountCents !== null)
    ) {
      throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
    }
    requiredOpaque(material.lineId);
    requiredOpaque(material.selectionKey);
    requiredOpaque(material.lookupCode);
    requiredOpaque(material.displayName);
    requireSyncProvenance(
      material.syncProvenance,
      "RETURN_EXECUTION_FAILED",
    );
  }
}

function validateIdentity(identity: TrustedReturnIdentity): void {
  requiredOpaque(identity.storeCode);
  requiredOpaque(identity.deviceCode);
  requiredOpaque(identity.cashierId);
  requiredOpaque(identity.cashierName);
  if (identity.userGuid !== null && identity.userGuid !== undefined) {
    requiredOpaque(identity.userGuid);
  }
  requiredOpaque(identity.sessionEpoch);
}

function assertSameIdentity(
  expected: TrustedReturnIdentity,
  actual: TrustedReturnIdentity,
): void {
  if (
    expected.storeCode !== actual.storeCode ||
    expected.deviceCode !== actual.deviceCode ||
    expected.cashierId !== actual.cashierId ||
    expected.cashierName !== actual.cashierName ||
    expected.sessionEpoch !== actual.sessionEpoch
  ) {
    throw new ReturnFeatureError("RETURN_SESSION_EXPIRED");
  }
}

function assertSameRecoveryScope(
  expected: TrustedReturnIdentity,
  actual: TrustedReturnIdentity,
): void {
  if (
    expected.storeCode !== actual.storeCode ||
    expected.deviceCode !== actual.deviceCode ||
    expected.cashierId !== actual.cashierId
  ) {
    throw new ReturnFeatureError("RETURN_SESSION_EXPIRED");
  }
}

function isOfflineCashAllocation(
  allocation: ReturnRefundAllocation,
): boolean {
  const proof = allocation.offlineCashProof;
  return (
    allocation.method === "cash" &&
    proof !== null &&
    allocation.originalCapacityId === proof.capacityId &&
    allocation.originalOrderGuid === proof.originalOrderGuid &&
    -allocation.signedAmountCents <= proof.remainingCents
  );
}

function assertAttemptKindCompatible(
  method: ReturnTenderMethod,
  kind: DurableExternalAttemptKind,
): void {
  if (
    ((method === "cash" || method === "installment") &&
      kind !== "hbpos-api") ||
    (method === "card" && kind !== "payment-provider")
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  // 券退款可能走本地 provider SDK，也可能走 Hbpos voucher API，均需耐久 attempt。
}

function toOfflineCashInput(
  action: DurableReturnAction,
  allocation: DurableReturnAllocation,
): OfflineCashRefundInput {
  if (
    allocation.executionKind !== "offline-cash" ||
    allocation.method !== "cash" ||
    !allocation.offlineCashProof ||
    !allocation.capacityId ||
    !allocation.originalOrderGuid
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  return {
    actionId: action.actionId,
    allocationId: allocation.allocationId,
    returnOrderGuid: action.returnOrderGuid,
    signedAmountCents: allocation.signedAmountCents,
    originalOrderGuid: allocation.originalOrderGuid,
    capacityId: allocation.capacityId,
    offlineCashProof: allocation.offlineCashProof,
  };
}

function toOnlineRefundInput(
  action: DurableReturnAction,
  allocation: DurableReturnAllocation,
): OnlineReturnRefundInput {
  if (
    allocation.executionKind !== "online-refund" ||
    !allocation.externalAttemptId ||
    !allocation.externalAttemptKind ||
    !allocation.externalActionId ||
    !allocation.durableAttemptId
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  return {
    actionId: action.actionId,
    allocationId: allocation.allocationId,
    externalAttemptId: allocation.externalAttemptId,
    returnOrderGuid: action.returnOrderGuid,
    actor: returnAuditActor(action.identity),
    method: allocation.method,
    signedAmountCents: allocation.signedAmountCents,
    capacityId: allocation.capacityId,
    originalOrderGuid: allocation.originalOrderGuid,
    attemptKind: allocation.externalAttemptKind,
    externalActionId: allocation.externalActionId,
    durableAttemptId: allocation.durableAttemptId,
  };
}

function toOnlineRefundPreparationInput(
  action: DurableReturnAction,
  allocation: DurableReturnAllocation,
): Omit<
  OnlineReturnRefundInput,
  "attemptKind" | "externalActionId" | "durableAttemptId"
> {
  if (
    allocation.executionKind !== "online-refund" ||
    !allocation.externalAttemptId
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  return {
    actionId: action.actionId,
    allocationId: allocation.allocationId,
    externalAttemptId: allocation.externalAttemptId,
    returnOrderGuid: action.returnOrderGuid,
    actor: returnAuditActor(action.identity),
    method: allocation.method,
    signedAmountCents: allocation.signedAmountCents,
    capacityId: allocation.capacityId,
    originalOrderGuid: allocation.originalOrderGuid,
  };
}

function returnAuditActor(
  identity: TrustedReturnIdentity,
): AuditActorSnapshot {
  return Object.freeze({
    cashierId: identity.cashierId,
    cashierName: identity.cashierName,
    userGuid: identity.userGuid ?? null,
  });
}

function completedOutcome(
  action: DurableReturnAction,
): ReturnExecutionOutcome {
  return {
    status: "completed",
    returnOrderGuid: action.returnOrderGuid,
  };
}

function unknownOutcome(
  action: DurableReturnAction,
): ReturnExecutionOutcome {
  return {
    status: "unknown",
    recoveryKey: action.actionRecoveryToken,
  };
}

function terminalOrUnknown(
  action: DurableReturnAction,
): ReturnExecutionOutcome {
  if (action.status === "completed") return completedOutcome(action);
  if (action.status === "declined") return { status: "declined" };
  return unknownOutcome(action);
}

function requiredOpaque(value: string): string {
  const normalized = value.trim();
  if (!normalized) throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  return normalized;
}

function requiredIso(value: string): string {
  const normalized = requiredOpaque(value);
  if (!Number.isFinite(Date.parse(normalized))) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return normalized;
}

function safePositiveAdd(left: number, right: number): number {
  const value = left + right;
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return value;
}

function requireSyncProvenance(
  input: unknown,
  code: "RETURN_SOURCE_MISMATCH" | "RETURN_EXECUTION_FAILED",
): LineSyncProvenance {
  try {
    return normalizeLineSyncProvenance(input);
  } catch {
    throw new ReturnFeatureError(code);
  }
}

function sameSyncProvenance(
  left: unknown,
  right: unknown,
): boolean {
  const normalizedLeft = requireSyncProvenance(
    left,
    "RETURN_EXECUTION_FAILED",
  );
  const normalizedRight = requireSyncProvenance(
    right,
    "RETURN_EXECUTION_FAILED",
  );
  return (
    normalizedLeft.referenceCode ===
      normalizedRight.referenceCode &&
    normalizedLeft.priceSource === normalizedRight.priceSource
  );
}
