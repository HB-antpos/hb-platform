import {
  canonicalLinklyAttemptGuid,
  deriveLinklyAttemptTxnRef,
} from "@hb/pos-payments-core/features/payments/linkly-attempt-txn-ref";
import { paymentProviderAmountCents } from "@hb/pos-payments-core/features/payments/payment-amount";

import {
  HbposApiError,
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@/core/api/hbpos-api";
import {
  normalizeCardSyncEvidence,
  type CardSyncEvidenceV1,
  type OnlinePaymentPort,
  type PaymentAttempt,
  type PaymentProviderResult,
} from "@hb/pos-domain/core/contracts/payment";
import type { components } from "@hb/pos-api-client/openapi";

type LinklySessionDto = components["schemas"]["LinklyCloudBackendSessionResponse"];
type LinklyCardTransactionDto = components["schemas"]["LinklyCloudBackendCardTransactionDto"];
type LinklyTransactionRequest =
  components["schemas"]["LinklyCloudBackendTransactionRequest"] &
  Readonly<{
    terminalId?: string;
    selectionRevision?: number;
  }>;

const LINKLY_HTTP_TIMEOUT_MS = 240_000;
const LINKLY_RECOVERY_DEADLINE_MS = 180_000;

export type LinklyPaymentRecoveryControl = Readonly<{
  signal: AbortSignal;
  deadlineAtMs: number;
}>;

export type LinklyLegacyReconciliation = Readonly<{
  environment: string;
  clientAcknowledgedAt: string | null;
}>;

export type LinklyUnacknowledgedSession = Readonly<{
  sessionId: string;
  environment: string;
  /** 从 transaction 通知中强匹配出的本地幂等键；无法唯一验证时不列出会话。 */
  idempotencyKey: string;
}>;

export type LinklyTerminalMode = "Active" | "Legacy" | "Draft";

export type LinklyTerminalPairingState =
  | "Unpaired"
  | "Ready"
  | "Unknown"
  | "NeedsRepair";

export type LinklyTerminalSummary = Readonly<{
  terminalId: string;
  laneNo: number;
  displayName: string;
  pairingState: LinklyTerminalPairingState;
  isBusy: boolean;
  isReady: boolean;
  lastHealthStatus: string | null;
  lastHealthAt: string | null;
}>;

export type LinklyTerminalSelectionSnapshot = Readonly<{
  environment: string;
  mode: LinklyTerminalMode;
  selectedTerminalId: string | null;
  selectionRevision: number;
  terminals: readonly LinklyTerminalSummary[];
}>;

export interface LinklyTerminalSelectionPort {
  readTerminals(
    environment: string,
    signal?: AbortSignal,
  ): Promise<LinklyTerminalSelectionSnapshot>;
  selectTerminal(
    environment: string,
    terminalId: string,
    expectedRevision: number,
    signal?: AbortSignal,
  ): Promise<LinklyTerminalSelectionSnapshot>;
}

export type LinklyPaymentTerminalSelectionExpectation =
  | Readonly<{
      environment: string;
      mode: "Active";
      terminalId: string;
      selectionRevision: number;
    }>
  | Readonly<{
      environment: string;
      mode: "Legacy" | "Draft";
    }>;

export interface LinklyPaymentTerminalSelectionBindingPort {
  runWithSelection<T>(
    orderGuid: string,
    selection: LinklyPaymentTerminalSelectionExpectation,
    operation: () => Promise<T>,
  ): Promise<T>;
}

interface LinklyPaymentAwareTerminalSelectionPort
  extends LinklyTerminalSelectionPort {
  readTerminalsForPayment(
    environment: string,
    orderGuid: string,
    requireBinding: boolean,
  ): Promise<LinklyTerminalSelectionSnapshot>;
}

export type LinklyCloudBackendSession = Readonly<{
  environment: string;
  storeCode: string;
  deviceCode: string;
  sessionId: string;
  terminalId?: string | null;
  terminalDisplayName?: string | null;
  status: string;
  txnRef: string | null;
  responseCode: string | null;
  responseText: string | null;
  recoveryAction: string | null;
  displayText: string | null;
  cancelKeyFlag: boolean;
  okKeyFlag: boolean;
  acceptYesKeyFlag: boolean;
  declineNoKeyFlag: boolean;
  authoriseKeyFlag: boolean;
  inputType: string | null;
  graphicCode: string | null;
  displayLines: readonly string[];
  receiptText: string | null;
  recoveryCount: number;
  receiptPrintedAt: string | null;
  clientAcknowledgedAt: string | null;
  lastHttpStatus: number | null;
  notifications: readonly Readonly<{ type: string; payloadJson: string; receivedAt: string }>[];
  transactionSuccess: boolean | null;
  cardTransaction?: LinklyCardTransactionDto | null;
}>;

export type LinklyCloudBackendProviderOptions = Readonly<{
  environment: string;
  terminalSelection: LinklyTerminalSelectionPort;
}>;

/** 手持 POS 仅调用 Hbpos.Api；Linkly terminal secret 和 POS ID 永不下发到客户端。 */
export class LinklyCloudBackendApi implements LinklyTerminalSelectionPort {
  public constructor(private readonly transport: HbposTransport) {}

  public create(input: LinklyTransactionRequest, signal?: AbortSignal): Promise<LinklyCloudBackendSession> {
    return this.requestSession({ method: "POST", url: "/api/v1/linkly/cloud-backend/transactions", data: input, ...(signal ? { signal } : {}) });
  }

  public async readTerminals(
    environment: string,
    signal?: AbortSignal,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "GET",
      url: "/api/v1/linkly/cloud-backend/terminals",
      params: { environment },
      timeoutMs: LINKLY_HTTP_TIMEOUT_MS,
      ...(signal ? { signal } : {}),
    });
    return normalizeTerminalSelection(unwrapHbposEnvelope(response.data));
  }

  public async selectTerminal(
    environment: string,
    terminalId: string,
    expectedRevision: number,
    signal?: AbortSignal,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    await this.transport.request<HbposEnvelope<unknown>>({
      method: "PUT",
      url: "/api/v1/linkly/cloud-backend/terminal-selection",
      data: { environment, terminalId, expectedRevision },
      timeoutMs: LINKLY_HTTP_TIMEOUT_MS,
      ...(signal ? { signal } : {}),
    });
    // PUT 响应可能只带选择头字段；始终重读安全列表作为唯一权威状态。
    return this.readTerminals(environment, signal);
  }

  public active(environment: string, signal?: AbortSignal, timeoutMs = LINKLY_HTTP_TIMEOUT_MS): Promise<LinklyCloudBackendSession | null> {
    return this.requestOptionalSession({ method: "GET", url: "/api/v1/linkly/cloud-backend/transactions/active", params: { environment }, timeoutMs, ...(signal ? { signal } : {}) });
  }

  public resumable(environment: string, signal?: AbortSignal, timeoutMs = LINKLY_HTTP_TIMEOUT_MS): Promise<LinklyCloudBackendSession | null> {
    return this.requestOptionalSession({ method: "GET", url: "/api/v1/linkly/cloud-backend/transactions/resumable", params: { environment }, timeoutMs, ...(signal ? { signal } : {}) });
  }

  public status(environment: string, sessionId: string, signal?: AbortSignal, timeoutMs = LINKLY_HTTP_TIMEOUT_MS): Promise<LinklyCloudBackendSession> {
    return this.requestSession({ method: "GET", url: sessionUrl(sessionId, "status"), params: { environment }, timeoutMs, ...(signal ? { signal } : {}) });
  }

  public recover(environment: string, sessionId: string, signal?: AbortSignal, timeoutMs = LINKLY_HTTP_TIMEOUT_MS): Promise<LinklyCloudBackendSession> {
    return this.requestSession({ method: "POST", url: sessionUrl(sessionId, "recover"), data: { environment }, timeoutMs, ...(signal ? { signal } : {}) });
  }

  public sendKey(environment: string, sessionId: string, key: string, data: string | null, signal?: AbortSignal): Promise<LinklyCloudBackendSession> {
    return this.requestSession({ method: "POST", url: sessionUrl(sessionId, "sendkey"), data: { environment, key, data }, ...(signal ? { signal } : {}) });
  }

  public markReceiptPrinted(environment: string, sessionId: string): Promise<LinklyCloudBackendSession> {
    return this.requestSession({ method: "POST", url: sessionUrl(sessionId, "receipt/printed"), data: { environment } });
  }

  public acknowledge(environment: string, sessionId: string): Promise<LinklyCloudBackendSession> {
    return this.requestSession({ method: "POST", url: sessionUrl(sessionId, "acknowledge"), params: { environment }, data: { environment } });
  }

  private async requestSession(request: Parameters<HbposTransport["request"]>[0]): Promise<LinklyCloudBackendSession> {
    const response = await this.transport.request<HbposEnvelope<LinklySessionDto>>({
      timeoutMs: LINKLY_HTTP_TIMEOUT_MS,
      ...request,
    });
    if (response.status === 404) throw sessionNotFound();
    return normalizeSession(unwrapHbposEnvelope(response.data));
  }

  private async requestOptionalSession(request: Parameters<HbposTransport["request"]>[0]): Promise<LinklyCloudBackendSession | null> {
    try {
      const response = await this.transport.request<HbposEnvelope<LinklySessionDto>>({
        timeoutMs: LINKLY_HTTP_TIMEOUT_MS,
        ...request,
      });
      if (response.status === 404) return null;
      return normalizeSession(unwrapHbposEnvelope(response.data));
    } catch (error) {
      if (isNotFound(error)) return null;
      throw error;
    }
  }
}

/**
 * 把支付页已展示的终端选择按 OrderGuid 临时绑定到 provider 调用。提交前仍重读
 * 权威目录；任何 mode、terminalId 或 revision 漂移都在交易 POST 前失败关闭。
 */
export class LinklyPaymentTerminalSelectionCoordinator
  implements
    LinklyPaymentAwareTerminalSelectionPort,
    LinklyPaymentTerminalSelectionBindingPort
{
  private readonly bindings = new Map<
    string,
    Readonly<{
      selection: LinklyPaymentTerminalSelectionExpectation;
      token: symbol;
    }>
  >();

  public constructor(private readonly api: LinklyCloudBackendApi) {}

  public readTerminals(
    environment: string,
    signal?: AbortSignal,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    return this.api.readTerminals(environment, signal);
  }

  public selectTerminal(
    environment: string,
    terminalId: string,
    expectedRevision: number,
    signal?: AbortSignal,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    return this.api.selectTerminal(
      environment,
      terminalId,
      expectedRevision,
      signal,
    );
  }

  public async runWithSelection<T>(
    orderGuid: string,
    selection: LinklyPaymentTerminalSelectionExpectation,
    operation: () => Promise<T>,
  ): Promise<T> {
    const token = Symbol("linkly-payment-terminal-selection");
    if (!orderGuid.trim() || this.bindings.has(orderGuid)) {
      throw new LinklyTerminalSelectionConflictError();
    }
    this.bindings.set(orderGuid, Object.freeze({ selection, token }));
    try {
      return await operation();
    } finally {
      if (this.bindings.get(orderGuid)?.token === token) {
        this.bindings.delete(orderGuid);
      }
    }
  }

  public async readTerminalsForPayment(
    environment: string,
    orderGuid: string,
    requireBinding: boolean,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    const binding = this.bindings.get(orderGuid);
    if (!binding) {
      if (requireBinding) throw new LinklyTerminalSelectionConflictError();
      return this.api.readTerminals(environment);
    }
    const snapshot = await this.api.readTerminals(environment);
    if (!matchesPaymentSelection(snapshot, binding.selection)) {
      throw new LinklyTerminalSelectionConflictError();
    }
    return snapshot;
  }
}

/**
 * Linkly Backend Async 的支付 Provider。create 一旦进入传输歧义，绝不重发 POST；
 * 没有已持久 SessionId 时只允许通过已持久 UID 强匹配 active/resumable，绝不凭同额认领。
 */
export class LinklyCloudBackendProvider implements OnlinePaymentPort {
  public readonly provider = "linkly-cloud" as const;
  public readonly environment: string;

  public constructor(
    private readonly api: LinklyCloudBackendApi,
    private readonly options: LinklyCloudBackendProviderOptions,
  ) {
    this.environment = options.environment;
  }

  public async submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    linklyProviderAmountCents(attempt);
    if (attempt.operation === "refund") return this.refund(attempt);
    if (attempt.state === "Unknown" || attempt.references.sessionId) return this.recover(attempt);
    return this.createSession(attempt);
  }

  /**
   * 新交易的唯一 create 入口（购买与退款共用）。按“是否已越过 POST 边界”分类失败：
   * - POST 之前的任何失败（读终端列表、active 预检）以及 create 被明确 4xx 拒绝，都证明没有创建会话，
   *   返回带具体“未提交”码的 Declined，释放订单与手持设备级触发器，而不是写成无会话的 Unknown；
   * - 只有 408、5xx、传输错误或响应无法解析（POST 可能已落地）才进入结果不确定恢复，且绝不重发 POST。
   */
  private async createSession(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    const environment = frozenEnvironment(attempt, this.environment);

    let selection: TransactionTerminalSelection;
    try {
      selection = await transactionTerminalSelection(
        this.options.terminalSelection,
        environment,
        attempt,
      );
    } catch {
      return notSubmittedDeclined(attempt, "LINKLY_NOT_SUBMITTED_TERMINAL_LIST");
    }
    if (!selection.ok) return terminalSelectionDeclined(attempt, selection.code);

    let active: LinklyCloudBackendSession | null;
    try {
      active = await this.api.active(environment);
    } catch {
      return notSubmittedDeclined(attempt, "LINKLY_NOT_SUBMITTED_ACTIVE_CHECK");
    }
    // 这是另一笔未完成交易，不能把它的 SessionId/TxnRef 绑定到当前新订单。
    if (active) return activeSessionConflict(attempt);

    const request = transactionRequest(attempt, environment, selection);
    try {
      return toPaymentResult(await this.api.create(request), attempt);
    } catch (error) {
      if (isActiveSessionConflict(error)) return activeSessionConflict(attempt);
      if (isTerminalSelectionConflict(error)) {
        return terminalSelectionDeclined(
          attempt,
          "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
        );
      }
      if (isTerminalNotReadyConflict(error)) {
        return terminalSelectionDeclined(attempt, "LINKLY_TERMINAL_NOT_READY");
      }
      if (isCreateDefinitelyNotSubmitted(error)) {
        return notSubmittedDeclined(attempt, `LINKLY_NOT_SUBMITTED_HTTP_${error.status}`);
      }
      return this.recoverAmbiguousCreate(attempt, {
        signal: new AbortController().signal,
        deadlineAtMs: Date.now() + LINKLY_RECOVERY_DEADLINE_MS,
      });
    }
  }

  public async recover(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    return this.recoverWithControl(attempt, {
      signal: new AbortController().signal,
      deadlineAtMs: Date.now() + LINKLY_RECOVERY_DEADLINE_MS,
    });
  }

  public async recoverWithControl(
    attempt: PaymentAttempt,
    control: LinklyPaymentRecoveryControl,
  ): Promise<PaymentProviderResult> {
    linklyProviderAmountCents(attempt);
    const environment = frozenEnvironmentOrNull(attempt);
    if (environment === null) {
      // 历史 attempt 的环境必须先由 reconcileLegacy 强匹配并通过本地 CAS 冻结，禁止猜当前环境恢复。
      return unknownResult(attempt, "LINKLY_RECOVERY_ENVIRONMENT_REQUIRED");
    }
    if (attempt.references.sessionId) {
      return this.recoverPersistedSession(attempt, environment, attempt.references.sessionId, control);
    }
    try {
      return await this.recoverAmbiguousCreate(attempt, control);
    } catch (error) {
      // 页面卸载/截止时的 abort 只代表本次查询停止，不能被 executeProvider 写成 Unknown。
      return controlledTransportFailure(attempt, error, control);
    }
  }

  public async cancel(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    linklyProviderAmountCents(attempt);
    // Unknown 只能恢复，绝不能在不知道终端是否已扣款时自动发送取消键。
    if (!attempt.references.sessionId) return unknownResult(attempt);
    const environment = frozenEnvironmentOrNull(attempt);
    if (environment === null) {
      // 没有持久化环境时连 status 也不能用当前配置猜测，必须先完成 legacy reconciliation。
      return unknownResult(attempt, "LINKLY_CANCEL_ENVIRONMENT_REQUIRED");
    }
    if (attempt.state === "Unknown") return unknownResult(attempt);
    let status: LinklyCloudBackendSession;
    try {
      status = await this.api.status(environment, attempt.references.sessionId);
    } catch (error) {
      if (isNotFound(error)) return unknownResult(attempt, "LINKLY_CANCEL_SESSION_NOT_FOUND");
      // 瞬时网络/5xx 不能把仍在等待顾客的 Pending 写成 Unknown：那会停掉自动轮询并禁用取消键。
      return controlledTransportFailure(attempt, error);
    }
    if (!sameSessionEnvironment(status, attempt.references.sessionId, environment) ||
      (attempt.references.txnRef !== null && !sameIdentity(status.txnRef, attempt.references.txnRef))) {
      return unknownResult(attempt, "LINKLY_CANCEL_CONTEXT_MISMATCH");
    }
    const statusResult = toPaymentResult(status, attempt);
    if (isFinalPaymentState(statusResult.state)) return statusResult;
    if (statusResult.state === "Unknown") return unknownResult(attempt, "LINKLY_CANCEL_CONTEXT_UNKNOWN");
    if (!supportsCancelPayment(status)) return unknownResult(attempt, "LINKLY_CANCEL_NOT_ALLOWED");
    let session: LinklyCloudBackendSession;
    try {
      session = await this.api.sendKey(environment, attempt.references.sessionId, "CANCEL", null);
    } catch (error) {
      // Linkly 拒绝取消键时控制器返回 400（提示继续等待交易结果），后端会话仍是 Pending。
      if (isSendKeyRejected(error)) {
        return stillPendingResult(attempt, "LINKLY_CANCEL_NOT_ACCEPTED");
      }
      return controlledTransportFailure(attempt, error);
    }
    if (!sameSessionEnvironment(session, attempt.references.sessionId, environment) ||
      (attempt.references.txnRef !== null && !sameIdentity(session.txnRef, attempt.references.txnRef))) {
      return unknownResult(attempt, "LINKLY_CANCEL_CONTEXT_MISMATCH");
    }
    return toPaymentResult(session, attempt);
  }

  public async refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> {
    linklyProviderAmountCents(attempt);
    if (attempt.references.rfn === null) return { state: "Declined", references: attempt.references, receiptText: null, responseCode: "LINKLY_RFN_REQUIRED" };
    if (attempt.state === "Unknown" || attempt.references.sessionId) return this.recover(attempt);
    return this.createSession(attempt);
  }

  private async recoverAmbiguousCreate(
    attempt: PaymentAttempt,
    control?: LinklyPaymentRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const proof = recoveryProof(attempt);
    if (proof === null) return unknownResult(attempt);

    const environment = frozenEnvironmentOrNull(attempt);
    if (environment === null) return unknownResult(attempt, "LINKLY_RECOVERY_ENVIRONMENT_REQUIRED");
    const activeTimeoutMs = recoveryTimeoutMs(control);
    if (activeTimeoutMs === null) return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
    const active = await this.api.active(environment, control?.signal, activeTimeoutMs);
    const activeScope = active === null
      ? null
      : matchingRecoveryScope(active, attempt, environment, proof);
    if (active !== null && activeScope !== null) {
      return this.recoverMatchedSession(attempt, active, activeScope, proof, control);
    }

    const resumableTimeoutMs = recoveryTimeoutMs(control);
    if (resumableTimeoutMs === null) return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
    const resumable = await this.api.resumable(environment, control?.signal, resumableTimeoutMs);
    const resumableScope = resumable === null
      ? null
      : matchingRecoveryScope(resumable, attempt, environment, proof);
    if (resumable === null || resumableScope === null) return unknownResult(attempt);
    return this.recoverMatchedSession(attempt, resumable, resumableScope, proof, control);
  }

  private async recoverMatchedSession(
    attempt: PaymentAttempt,
    candidate: LinklyCloudBackendSession,
    expectedScope: LinklyRecoveryScope,
    proof: LinklyRecoveryProof,
    control?: LinklyPaymentRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const statusTimeoutMs = recoveryTimeoutMs(control);
    if (statusTimeoutMs === null) return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
    const status = await this.api.status(expectedScope.environment, candidate.sessionId, control?.signal, statusTimeoutMs);
    if (!matchesRecoveryScope(status, expectedScope) ||
      matchingRecoveryScope(status, attempt, expectedScope.environment, proof) === null) {
      return unknownResult(attempt);
    }

    const statusResult = toPaymentResult(status, attempt);
    if (isFinalPaymentState(statusResult.state)) return statusResult;
    if (statusResult.state === "Pending" && !hasRecoveryAction(status)) return statusResult;

    const recoverTimeoutMs = recoveryTimeoutMs(control);
    if (recoverTimeoutMs === null) return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
    const recovered = await this.api.recover(expectedScope.environment, candidate.sessionId, control?.signal, recoverTimeoutMs);
    if (!matchesRecoveryScope(recovered, expectedScope) ||
      matchingRecoveryScope(recovered, attempt, expectedScope.environment, proof) === null) {
      return unknownResult(attempt);
    }
    return toPaymentResult(recovered, attempt);
  }

  private async recoverPersistedSession(
    attempt: PaymentAttempt,
    environment: string,
    sessionId: string,
    control: LinklyPaymentRecoveryControl,
  ): Promise<PaymentProviderResult> {
    const statusTimeoutMs = recoveryTimeoutMs(control);
    if (statusTimeoutMs === null) return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
    let status: LinklyCloudBackendSession;
    try {
      status = await this.api.status(environment, sessionId, control.signal, statusTimeoutMs);
    } catch (error) {
      if (isNotFound(error)) return unknownResult(attempt, "LINKLY_RECOVERY_SESSION_NOT_FOUND");
      return controlledTransportFailure(attempt, error, control);
    }
    if (!sameSessionEnvironment(status, sessionId, environment) ||
      (attempt.references.txnRef !== null && !sameIdentity(status.txnRef, attempt.references.txnRef))) {
      return unknownResult(attempt, "LINKLY_RECOVERY_CONTEXT_MISMATCH");
    }
    const statusResult = toPaymentResult(status, attempt);
    if (isFinalPaymentState(statusResult.state)) return statusResult;
    if (statusResult.state === "Pending" && !hasRecoveryAction(status)) return statusResult;
    const recoverTimeoutMs = recoveryTimeoutMs(control);
    if (recoverTimeoutMs === null) return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
    let recovered: LinklyCloudBackendSession;
    try {
      recovered = await this.api.recover(environment, sessionId, control.signal, recoverTimeoutMs);
    } catch (error) {
      return controlledTransportFailure(attempt, error, control);
    }
    if (!sameSessionEnvironment(recovered, sessionId, environment) ||
      (attempt.references.txnRef !== null && !sameIdentity(recovered.txnRef, attempt.references.txnRef))) {
      return unknownResult(attempt, "LINKLY_RECOVERY_CONTEXT_MISMATCH");
    }
    return toPaymentResult(recovered, attempt);
  }

  public async acknowledge(attempt: PaymentAttempt): Promise<void> {
    const environment = attempt.providerEnvironment?.trim() || null;
    const sessionId = attempt.references.sessionId?.trim() || null;
    if (environment === null || sessionId === null) {
      throw new Error("LINKLY_ACK_PROVIDER_ENVIRONMENT_REQUIRED");
    }
    const acknowledged = await this.api.acknowledge(environment, sessionId);
    if (!sameSessionEnvironment(acknowledged, sessionId, environment)) {
      throw new Error("LINKLY_ACK_CONTEXT_MISMATCH");
    }
    const acknowledgedState = sessionState(acknowledged);
    if (!isFinalPaymentState(acknowledgedState) || !isValidTimestamp(acknowledged.clientAcknowledgedAt)) {
      throw new Error("LINKLY_ACK_FINAL_STATE_REQUIRED");
    }
    if ((attempt.state === "Approved" || attempt.state === "Declined" || attempt.state === "Cancelled") && attempt.state !== acknowledgedState) {
      throw new Error("LINKLY_ACK_RESULT_MISMATCH");
    }
    if (acknowledgedState === "Approved") {
      const verified = toPaymentResult(acknowledged, attempt);
      if (verified.state !== "Approved" || verified.protectedSyncEvidence === undefined) {
        throw new Error("LINKLY_ACK_APPROVAL_EVIDENCE_REQUIRED");
      }
    }
  }

  /**
   * 为历史 NULL 环境记录提供只读、强匹配的环境冻结入口；不创建、不恢复、不 ACK。
   * 调用方必须先用返回值完成本地 CAS，再调用 acknowledge(attempt)。
   */
  public async reconcileLegacy(
    attempt: PaymentAttempt,
    control?: LinklyPaymentRecoveryControl,
  ): Promise<LinklyLegacyReconciliation | null> {
    if (attempt.providerEnvironment?.trim() || !attempt.references.sessionId) return null;
    const recoveryUid = normalizeRecoveryUid(attempt.idempotencyKey);
    if (recoveryUid === null) return null;
    const environment = this.environment;
    const candidates: LinklyCloudBackendSession[] = [];
    const statusTimeoutMs = recoveryTimeoutMs(control);
    if (statusTimeoutMs === null) return null;
    try {
      candidates.push(await this.api.status(environment, attempt.references.sessionId, control?.signal, statusTimeoutMs));
    } catch (error) {
      if (!isNotFound(error)) throw error;
    }
    const activeTimeoutMs = recoveryTimeoutMs(control);
    if (activeTimeoutMs === null) return null;
    const active = await this.api.active(environment, control?.signal, activeTimeoutMs);
    if (active) candidates.push(active);
    const resumableTimeoutMs = recoveryTimeoutMs(control);
    if (resumableTimeoutMs === null) return null;
    const resumable = await this.api.resumable(environment, control?.signal, resumableTimeoutMs);
    if (resumable) candidates.push(resumable);
    for (const candidate of candidates) {
      if (sameSessionEnvironment(candidate, attempt.references.sessionId, environment) &&
        matchingRecoveryScope(candidate, attempt, environment, { txnRef: null, uid: recoveryUid }) !== null) {
        return {
          environment,
          clientAcknowledgedAt: candidate.clientAcknowledgedAt,
        };
      }
    }
    return null;
  }

  /**
   * 冷启动 ACK probe 的窄入口：只读当前认证环境的 active/resumable，绝不扫描历史或发送 ACK。
   * 调用方负责按 sessionId+订单作用域找到本地 attempt 后再走 reconcileLegacy/CAS；不得用于页面轮询。
   */
  public async listUnacknowledgedSessions(): Promise<readonly LinklyUnacknowledgedSession[]> {
    const [active, resumable] = await Promise.all([
      this.api.active(this.environment),
      this.api.resumable(this.environment),
    ]);
    const seen = new Set<string>();
    const sessions: LinklyUnacknowledgedSession[] = [];
    for (const candidate of [active, resumable]) {
      if (candidate === null ||
        !sameCaseInsensitiveIdentity(candidate.environment, this.environment) ||
        !candidate.sessionId.trim() ||
        candidate.clientAcknowledgedAt !== null) {
        continue;
      }
      // active/resumable 可能仍在 Pending/Unknown；只有 transaction 通知中的
      // UID 全部一致且可解析，才允许把它交给本地窄查询，避免扫历史或猜订单。
      const idempotencyKey = sessionRecoveryUid(candidate);
      if (idempotencyKey === null) continue;
      const sessionId = candidate.sessionId.trim();
      if (seen.has(sessionId)) continue;
      seen.add(sessionId);
      sessions.push({
        sessionId,
        environment: candidate.environment.trim(),
        idempotencyKey,
      });
    }
    return Object.freeze(sessions);
  }
}

type LinklyRecoveryScope = Readonly<{
  environment: string;
  storeCode: string;
  deviceCode: string;
  sessionId: string;
  txnRef: string;
}>;

type LinklyRecoveryIdentity = Readonly<{
  uid: string;
  txnType: "P" | "R";
  amountCents: number;
  txnRef: string;
}>;

function sessionRecoveryUid(session: LinklyCloudBackendSession): string | null {
  const identities: LinklyRecoveryIdentity[] = [];
  for (const notification of session.notifications) {
    if (!sameCaseInsensitiveIdentity(notification.type, "transaction")) continue;
    const identity = parseRecoveryIdentity(notification.payloadJson);
    // 冷启动发现必须证明所有 transaction 通知属于同一笔交易；缺失或冲突
    // 的 UID 直接跳过，交由显式人工/业务恢复处理，不能猜本地订单。
    if (identity === null) return null;
    identities.push(identity);
  }
  if (identities.length === 0) return null;
  const first = identities[0]!;
  return identities.every((identity) => sameRecoveryIdentity(identity, first))
    ? first.uid
    : null;
}

/**
 * 认领 create 响应丢失的会话所依赖的本地证据：
 * - txnRef：由 attemptId 派生且已随 Submitted CAS 落库的 TxnRef，后端按同一算法派生，会话顶层 txnRef 未脱敏、
 *   不依赖通知，是强匹配键；
 * - uid：随请求发出的 PAD UID，仅用于没有派生 TxnRef 的旧 attempt（非 GUID attemptId、历史数据）的通知认领。
 */
type LinklyRecoveryProof = Readonly<{
  txnRef: string | null;
  uid: string | null;
}>;

/** 本地已落库的 TxnRef 确为该 attemptId 的派生值时返回它，否则 null（此时不得向后端发送 attemptGuid）。 */
function derivedTxnRefProof(attempt: PaymentAttempt): string | null {
  const expected = deriveLinklyAttemptTxnRef(attempt);
  return expected !== null && attempt.references.txnRef === expected ? expected : null;
}

function recoveryProof(attempt: PaymentAttempt): LinklyRecoveryProof | null {
  const txnRef = derivedTxnRefProof(attempt);
  const uid = normalizeRecoveryUid(attempt.idempotencyKey);
  return txnRef === null && uid === null ? null : { txnRef, uid };
}

function matchingRecoveryScope(
  session: LinklyCloudBackendSession,
  attempt: PaymentAttempt,
  environment: string,
  proof: LinklyRecoveryProof,
): LinklyRecoveryScope | null {
  // active/resumable 已由 Hbpos.Api 按当前门店/设备 claim 隔离；客户端仍要求后续响应保持同一作用域。
  if (!sameCaseInsensitiveIdentity(session.environment, environment) ||
    session.storeCode.trim().length === 0 ||
    session.deviceCode.trim().length === 0 ||
    session.sessionId.trim().length === 0 ||
    session.txnRef === null ||
    session.txnRef.trim().length === 0) {
    return null;
  }

  if (proof.txnRef !== null) {
    // 强匹配：顶层 txnRef（未脱敏）等于本地派生值即可认领；类型已编码在引用首字符里。
    // 服务端会在会话终态后剥离 Declined/Cancelled 的 transaction 通知，并对 Approved 通知里的 TxnRef 二次脱敏，
    // 所以通知只做“矛盾即拒绝”的交叉校验：缺失/无法解析的通知不阻止认领，通知里的 TxnRef 完全不参与比较。
    if (!sameIdentity(session.txnRef, proof.txnRef)) return null;
    if (!notificationsConsistentWithAttempt(session, attempt, proof.uid)) return null;
    return recoveryScopeOf(session);
  }

  // 旧 attempt（没有派生 TxnRef）：只能靠 transaction 通知里的 UID/类型/金额/TxnRef 强匹配。
  const recoveryUid = proof.uid;
  if (recoveryUid === null) return null;
  const identities: LinklyRecoveryIdentity[] = [];
  for (const notification of session.notifications) {
    if (!sameCaseInsensitiveIdentity(notification.type, "transaction")) continue;
    const identity = parseRecoveryIdentity(notification.payloadJson);
    // 任意 transaction 通知无法验证时都失败关闭，避免忽略冲突证据后误绑定。
    if (identity === null) return null;
    identities.push(identity);
  }
  if (identities.length === 0) return null;

  const first = identities[0]!;
  if (identities.some((identity) => !sameRecoveryIdentity(identity, first)) ||
    first.uid !== recoveryUid ||
    first.txnType !== (attempt.operation === "refund" ? "R" : "P") ||
    first.amountCents !== linklyProviderAmountCents(attempt) ||
    !sameIdentity(first.txnRef, session.txnRef)) {
    return null;
  }

  return recoveryScopeOf(session);
}

function recoveryScopeOf(session: LinklyCloudBackendSession): LinklyRecoveryScope {
  return {
    environment: session.environment.trim(),
    storeCode: session.storeCode.trim(),
    deviceCode: session.deviceCode.trim(),
    sessionId: session.sessionId.trim(),
    txnRef: (session.txnRef ?? "").trim(),
  };
}

/**
 * 强匹配路径下对 transaction 通知的交叉校验：只在通知里“出现了”类型/金额/UID 且与本地 attempt 矛盾时拒绝；
 * 通知缺失、JSON 无法解析或字段被脱敏/剥离都视为没有证据而不是矛盾。字段在大小写不同的重复键里出现多次，
 * 视为关联证据冲突，失败关闭。
 */
function notificationsConsistentWithAttempt(
  session: LinklyCloudBackendSession,
  attempt: PaymentAttempt,
  attemptUid: string | null,
): boolean {
  const expectedType = attempt.operation === "refund" ? "R" : "P";
  const expectedAmount = linklyProviderAmountCents(attempt);
  for (const notification of session.notifications) {
    if (!sameCaseInsensitiveIdentity(notification.type, "transaction")) continue;
    let parsed: unknown;
    try {
      parsed = JSON.parse(notification.payloadJson);
    } catch {
      continue;
    }
    if (!isRecord(parsed)) continue;
    const responses = recordValues(parsed, "Response");
    if (responses.length > 1) return false;
    let source: Readonly<Record<string, unknown>> = parsed;
    if (responses.length === 1) {
      const response = responses[0];
      if (!isRecord(response)) continue;
      source = response;
    }
    const types = recordValues(source, "TxnType");
    const amounts = recordValues(source, "AmtPurchase");
    const analyses = recordValues(source, "PurchaseAnalysisData");
    if (types.length > 1 || amounts.length > 1 || analyses.length > 1) return false;
    if (types.length === 1 && (types[0] === "P" || types[0] === "R") && types[0] !== expectedType) return false;
    const amount = amounts[0];
    if (typeof amount === "number" && Number.isSafeInteger(amount) && Math.abs(amount) !== expectedAmount) return false;
    const analysis = analyses[0];
    if (isRecord(analysis)) {
      const uids = recordValues(analysis, "UID");
      if (uids.length > 1) return false;
      const uid = normalizeRecoveryUid(uids[0]);
      if (uid !== null && attemptUid !== null && uid !== attemptUid) return false;
    }
  }
  return true;
}

function parseRecoveryIdentity(payloadJson: string): LinklyRecoveryIdentity | null {
  try {
    const parsed: unknown = JSON.parse(payloadJson);
    if (!isRecord(parsed)) return null;
    const responses = recordValues(parsed, "Response");
    if (responses.length > 1) return null;
    let source = parsed;
    if (responses.length === 1) {
      const response = responses[0];
      if (!isRecord(response)) return null;
      source = response;
    }
    const txnTypeValue = recordValue(source, "TxnType");
    const amountValue = recordValue(source, "AmtPurchase");
    const txnRefValue = recordValue(source, "TxnRef");
    const purchaseAnalysisData = recordValue(source, "PurchaseAnalysisData");
    if ((txnTypeValue !== "P" && txnTypeValue !== "R") ||
      typeof amountValue !== "number" ||
      !Number.isSafeInteger(amountValue) ||
      amountValue === 0 ||
      typeof txnRefValue !== "string" ||
      !txnRefValue.trim() ||
      !isRecord(purchaseAnalysisData)) {
      return null;
    }
    const uid = normalizeRecoveryUid(recordValue(purchaseAnalysisData, "UID"));
    if (uid === null) return null;
    return {
      uid,
      txnType: txnTypeValue,
      amountCents: Math.abs(amountValue),
      txnRef: txnRefValue.trim(),
    };
  } catch {
    return null;
  }
}

function recordValue(
  value: Readonly<Record<string, unknown>>,
  field: string,
): unknown {
  const values = recordValues(value, field);
  return values.length === 1 ? values[0] : undefined;
}

function recordValues(
  value: Readonly<Record<string, unknown>>,
  field: string,
): readonly unknown[] {
  return Object.entries(value)
    .filter(([key]) => key.toLowerCase() === field.toLowerCase())
    .map(([, fieldValue]) => fieldValue);
}

function normalizeRecoveryUid(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const normalized = value.trim().toLowerCase();
  return /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u
    .test(normalized)
    ? normalized
    : null;
}

function sameCaseInsensitiveIdentity(left: string, right: string): boolean {
  return left.trim().toLowerCase() === right.trim().toLowerCase();
}

function sameRecoveryIdentity(
  left: LinklyRecoveryIdentity,
  right: LinklyRecoveryIdentity,
): boolean {
  return left.uid === right.uid &&
    left.txnType === right.txnType &&
    left.amountCents === right.amountCents &&
    left.txnRef === right.txnRef;
}

function matchesRecoveryScope(
  session: LinklyCloudBackendSession,
  expected: LinklyRecoveryScope,
): boolean {
  return sameCaseInsensitiveIdentity(session.environment, expected.environment) &&
    session.storeCode.trim() === expected.storeCode &&
    session.deviceCode.trim() === expected.deviceCode &&
    session.sessionId.trim() === expected.sessionId &&
    session.txnRef?.trim() === expected.txnRef;
}

function isFinalPaymentState(
  state: PaymentProviderResult["state"],
): boolean {
  return state === "Approved" || state === "Declined" || state === "Cancelled";
}

function transactionRequest(
  attempt: PaymentAttempt,
  environment: string,
  selection: Extract<TransactionTerminalSelection, { ok: true }>,
): LinklyTransactionRequest {
  const request: LinklyTransactionRequest = {
    environment,
    ...(selection.mode === "Active"
      ? {
          terminalId: selection.terminalId,
          selectionRevision: selection.selectionRevision,
        }
      : {}),
    txnType: attempt.operation === "refund" ? "R" : "P",
    amtPurchase: linklyProviderAmountCents(attempt),
  };
  // 只有本地已落库的 TxnRef 确为该 attemptId 的派生值时才发送 attemptGuid：Hbpos.Api 用同一算法派生同一引用，
  // create 响应丢失后即可凭这个顶层 TxnRef 认领会话；否则（非 GUID 的旧数据）后端沿用随机引用，不能让两边不一致。
  if (derivedTxnRefProof(attempt) !== null) {
    request.attemptGuid = canonicalLinklyAttemptGuid(attempt.attemptId);
  }
  const purchaseAnalysisData: Record<string, string> = {};
  const recoveryUid = normalizeRecoveryUid(attempt.idempotencyKey);
  // Linkly PAD UID 是会在结果中回显的 UUID v4 关联值；它只用于认领恢复，绝不授权重发 create。
  if (recoveryUid !== null) purchaseAnalysisData.UID = recoveryUid;
  if (attempt.operation === "refund" && attempt.references.rfn) {
    purchaseAnalysisData.RFN = attempt.references.rfn;
  }
  if (Object.keys(purchaseAnalysisData).length > 0) {
    request.purchaseAnalysisData = purchaseAnalysisData;
  }
  return request;
}

function toPaymentResult(session: LinklyCloudBackendSession, attempt: PaymentAttempt): PaymentProviderResult {
  const state = sessionState(session);
  const sessionReferences = {
    checkoutId: null,
    paymentId: null,
    sessionId: session.sessionId,
    txnRef: session.txnRef,
    // 非终态仍保留旧兼容逻辑；Approved 会使用已验证的结构化 RFN 覆盖。
    rfn: attempt.operation === "purchase" ? session.txnRef : attempt.references.rfn,
    voucherReservationToken: null,
  };
  // 恢复中的异常响应不得把另一笔会话身份写回本地；新建会话仍保留服务端签发的 SessionId。
  const references = state === "Unknown" && attempt.references.sessionId !== null
    ? attempt.references
    : sessionReferences;
  const result: PaymentProviderResult = {
    state,
    references,
    receiptText: session.receiptText,
    responseCode: session.responseCode,
  };
  if (state !== "Approved") return result;

  const evidence = buildApprovedCardSyncEvidence(session, attempt);
  if (!evidence.ok) {
    return {
      state: "Unknown",
      references: attempt.references.sessionId === null
        ? references
        : attempt.references,
      receiptText: session.receiptText,
      responseCode: evidence.code,
    };
  }

  return {
    ...result,
    references: {
      ...references,
      rfn: evidence.value.refundReference,
    },
    protectedSyncEvidence: evidence.value,
  };
}

function unknownResult(attempt: PaymentAttempt, responseCode = "LINKLY_SESSION_UNRESOLVED"): PaymentProviderResult {
  return { state: "Unknown", references: attempt.references, receiptText: null, responseCode };
}

/** 仍在等待终端结果：保持 Pending（不要把瞬时失败升级成 Unknown，否则自动轮询停止、取消键被禁用）。 */
function stillPendingResult(attempt: PaymentAttempt, responseCode: string): PaymentProviderResult {
  return {
    state: "Pending",
    references: attempt.references,
    receiptText: attempt.receiptText ?? null,
    responseCode,
  };
}

/**
 * 受控恢复/取消途中的传输异常分类，避免 executeProvider 把任何抛出的异常都写成 Unknown：
 * - abort / 已过截止时间：返回中性码，PaymentAttemptService 保持原状态，重挂载后在剩余窗口内继续查询；
 * - 已有 SessionId 的瞬时错误（传输失败、408、429、5xx）：保持 Pending（Unknown 保持 Unknown），下一轮轮询自然重试；
 * - 其他错误（401/403/400 等需要人看的失败、解析错误）仍向上抛出，保持原有的失败关闭。
 */
function controlledTransportFailure(
  attempt: PaymentAttempt,
  error: unknown,
  control?: LinklyPaymentRecoveryControl,
): PaymentProviderResult {
  if (isRequestAborted(error) || control?.signal.aborted === true) {
    return unknownResult(attempt, "LINKLY_RECOVERY_ABORTED");
  }
  if (!(error instanceof HbposApiError) || !isTransientRecoveryError(error)) throw error;
  if (control !== undefined && control.deadlineAtMs <= Date.now()) {
    return unknownResult(attempt, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
  }
  if (attempt.references.sessionId === null || attempt.state === "Unknown") {
    return unknownResult(attempt, "LINKLY_RECOVERY_TRANSIENT_ERROR");
  }
  return stillPendingResult(attempt, "LINKLY_RECOVERY_TRANSIENT_ERROR");
}

function isRequestAborted(error: unknown): boolean {
  return error instanceof HbposApiError && error.kind === "transport" && error.code === "REQUEST_ABORTED";
}

function isTransientRecoveryError(error: HbposApiError): boolean {
  return error.kind === "transport" ||
    (error.kind === "http" && error.status !== undefined &&
      (error.status === 408 || error.status === 429 || error.status >= 500));
}

/** 控制器在 Linkly 拒绝按键时返回 400（提示继续等待结果），后端会话保持 Pending。 */
function isSendKeyRejected(error: unknown): boolean {
  return error instanceof HbposApiError && error.kind === "http" && error.status === 400;
}

function activeSessionConflict(attempt: PaymentAttempt): PaymentProviderResult {
  return {
    state: "Declined",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: null,
      rfn: attempt.operation === "refund" ? attempt.references.rfn : null,
      voucherReservationToken: null,
    },
    receiptText: null,
    responseCode: "LINKLY_ACTIVE_SESSION_CONFLICT",
  };
}

type TransactionTerminalSelection =
  | Readonly<{
      ok: true;
      mode: "Active";
      terminalId: string;
      selectionRevision: number;
    }>
  | Readonly<{
      ok: true;
      mode: "Legacy" | "Draft";
    }>
  | Readonly<{
      ok: false;
      code:
        | "LINKLY_TERMINAL_SELECTION_REQUIRED"
        | "LINKLY_TERMINAL_BUSY"
        | "LINKLY_TERMINAL_NOT_READY"
        | "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT";
    }>;

async function transactionTerminalSelection(
  port: LinklyTerminalSelectionPort,
  environment: string,
  attempt: PaymentAttempt,
): Promise<TransactionTerminalSelection> {
  let snapshot: LinklyTerminalSelectionSnapshot;
  try {
    snapshot = isPaymentAwareSelectionPort(port)
      ? await port.readTerminalsForPayment(
          environment,
          attempt.orderGuid,
          // 新支付必须带 UI 确认绑定；退款兼容旧入口，但一旦绑定也必须校验漂移。
          attempt.operation === "purchase",
        )
      : await port.readTerminals(environment);
  } catch (error) {
    if (isTerminalSelectionConflict(error)) {
      return {
        ok: false,
        code: "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
      };
    }
    throw error;
  }
  if (snapshot.mode !== "Active") {
    return { ok: true, mode: snapshot.mode };
  }
  const selected = snapshot.terminals.find(
    (terminal) => terminal.terminalId === snapshot.selectedTerminalId,
  );
  if (!selected || snapshot.environment !== environment) {
    return { ok: false, code: "LINKLY_TERMINAL_SELECTION_REQUIRED" };
  }
  if (selected.isBusy) return { ok: false, code: "LINKLY_TERMINAL_BUSY" };
  if (!selected.isReady || selected.pairingState !== "Ready") {
    return { ok: false, code: "LINKLY_TERMINAL_NOT_READY" };
  }
  return {
    ok: true,
    mode: "Active",
    terminalId: selected.terminalId,
    selectionRevision: snapshot.selectionRevision,
  };
}

/** 确定没有越过 POST 边界：保持 sessionId 为空并带具体“未提交”码，让订单与设备触发器立即释放。 */
function notSubmittedDeclined(attempt: PaymentAttempt, responseCode: string): PaymentProviderResult {
  return {
    state: "Declined",
    references: attempt.references,
    receiptText: null,
    responseCode,
  };
}

function terminalSelectionDeclined(
  attempt: PaymentAttempt,
  responseCode: Extract<TransactionTerminalSelection, { ok: false }>["code"],
): PaymentProviderResult {
  return {
    state: "Declined",
    references: attempt.references,
    receiptText: null,
    responseCode,
  };
}

const LINKLY_PENDING_STATUSES = new Set([
  "pending",
  "tokenrefreshrequired",
]);

const LINKLY_NOT_SUBMITTED_STATUSES = new Set([
  "failed",
  "notsubmitted",
]);

const LINKLY_DECLINED_STATUSES = new Set([
  "completed",
  "failed",
  "declined",
  "notsubmitted",
]);

function sessionState(session: LinklyCloudBackendSession): PaymentProviderResult["state"] {
  const status = session.status.trim().toLowerCase();
  if (!status) return "Unknown";

  if (status === "cancelled" || status === "canceled") {
    return session.transactionSuccess === false ? "Cancelled" : "Unknown";
  }

  if (LINKLY_PENDING_STATUSES.has(status)) {
    return session.transactionSuccess === null ? "Pending" : "Unknown";
  }

  if (status === "completed" && session.transactionSuccess === true && isLinklyApprovalCode(session.responseCode)) {
    return "Approved";
  }

  if (LINKLY_DECLINED_STATUSES.has(status) && session.transactionSuccess === false) {
    return "Declined";
  }

  // Failed/NotSubmitted 只来自 Hbpos.Api 对 Linkly 的 HTTP 层明确拒绝或 404（交易从未提交成功），后端不会再刷新，
  // 也不会写 TransactionSuccess；与 WPF 一致按“未批准的终态”处理，才能进入 ACK 队列释放设备/终端闸门。
  // 若同时声称 success=true 则字段互相矛盾，继续失败关闭为 Unknown。
  if (LINKLY_NOT_SUBMITTED_STATUSES.has(status) && session.transactionSuccess !== true) {
    return "Declined";
  }

  // 新状态、缺失最终结果或互相矛盾的字段都必须等待同一 SessionId 恢复。
  return "Unknown";
}

function normalizeSession(value: LinklySessionDto): LinklyCloudBackendSession {
  const safeValue = value as LinklySessionDto &
    Readonly<{
      terminalId?: unknown;
      terminalDisplayName?: unknown;
    }>;
  return {
    environment: requiredText(value.environment, "environment"), storeCode: requiredText(value.storeCode, "storeCode"), deviceCode: requiredText(value.deviceCode, "deviceCode"),
    sessionId: requiredText(value.sessionId, "sessionId"), terminalId: optionalText(safeValue.terminalId), terminalDisplayName: optionalText(safeValue.terminalDisplayName), status: typeof value.status === "string" ? value.status : "", txnRef: optionalText(value.txnRef), responseCode: optionalText(value.responseCode),
    responseText: optionalText(value.responseText), recoveryAction: optionalText(value.recoveryAction), displayText: optionalText(value.displayText), cancelKeyFlag: Boolean(value.cancelKeyFlag),
    okKeyFlag: Boolean(value.okKeyFlag), acceptYesKeyFlag: Boolean(value.acceptYesKeyFlag), declineNoKeyFlag: Boolean(value.declineNoKeyFlag), authoriseKeyFlag: Boolean(value.authoriseKeyFlag),
    inputType: optionalText(value.inputType), graphicCode: optionalText(value.graphicCode), displayLines: (value.displayLines ?? []).map((line) => requiredText(line, "displayLines")), receiptText: optionalText(value.receiptText),
    recoveryCount: integer(value.recoveryCount ?? 0, "recoveryCount"), receiptPrintedAt: optionalText(value.receiptPrintedAt), clientAcknowledgedAt: optionalText(value.clientAcknowledgedAt),
    lastHttpStatus: value.lastHttpStatus === null || value.lastHttpStatus === undefined ? null : integer(value.lastHttpStatus, "lastHttpStatus"),
    notifications: (value.notifications ?? []).map((notification) => ({ type: requiredText(notification.type, "notification.type"), payloadJson: requiredText(notification.payloadJson, "notification.payloadJson"), receivedAt: requiredText(notification.receivedAt, "notification.receivedAt") })),
    transactionSuccess: value.transactionSuccess ?? null,
    cardTransaction: value.cardTransaction ?? null,
  };
}

function normalizeTerminalSelection(value: unknown): LinklyTerminalSelectionSnapshot {
  if (!isRecord(value) || !Array.isArray(value.terminals)) {
    throw new Error("Invalid Linkly terminal selection response.");
  }
  const environment = requiredText(value.environment, "terminal.environment");
  const mode = normalizeTerminalMode(value.mode);
  const selectedTerminalId = optionalText(value.selectedTerminalId);
  const selectionRevision =
    value.selectionRevision === null || value.selectionRevision === undefined
      ? 0
      : integer(value.selectionRevision, "terminal.selectionRevision");
  if (
    selectionRevision < 0 ||
    (selectedTerminalId !== null && selectionRevision === 0)
  ) {
    throw new Error("Invalid Linkly terminal selectionRevision.");
  }
  const terminals = Object.freeze(
    value.terminals.map((candidate, index) => {
      if (!isRecord(candidate)) {
        throw new Error(`Invalid Linkly terminals[${index}].`);
      }
      const pairingState = requiredText(
        candidate.pairingState,
        `terminals[${index}].pairingState`,
      );
      if (!isLinklyPairingState(pairingState)) {
        throw new Error(`Invalid Linkly terminals[${index}].pairingState.`);
      }
      return Object.freeze({
        terminalId: requiredText(
          candidate.terminalId,
          `terminals[${index}].terminalId`,
        ),
        laneNo: integer(candidate.laneNo, `terminals[${index}].laneNo`),
        displayName: requiredText(
          candidate.displayName,
          `terminals[${index}].displayName`,
        ),
        pairingState,
        isBusy: candidate.isBusy === true,
        isReady: candidate.isReady === true,
        lastHealthStatus: optionalText(candidate.lastHealthStatus),
        lastHealthAt: optionalText(candidate.lastHealthAt),
      });
    }),
  );
  if (
    selectedTerminalId !== null &&
    !terminals.some((terminal) => terminal.terminalId === selectedTerminalId)
  ) {
    throw new Error("Invalid Linkly selectedTerminalId.");
  }
  return Object.freeze({
    environment,
    mode,
    selectedTerminalId,
    selectionRevision,
    terminals,
  });
}

function normalizeTerminalMode(value: unknown): LinklyTerminalMode {
  // 兼容尚未返回 mode 的旧服务；未知非空枚举保持失败关闭。
  if (value === undefined || value === null || value === "") return "Legacy";
  if (value === "Active" || value === "Legacy" || value === "Draft") {
    return value;
  }
  throw new Error("Invalid Linkly terminal mode.");
}

function isLinklyPairingState(
  value: string,
): value is LinklyTerminalPairingState {
  return (
    value === "Unpaired" ||
    value === "Ready" ||
    value === "Unknown" ||
    value === "NeedsRepair"
  );
}

const LINKLY_CARD_TRANSACTION_KEYS = new Set([
  "txnRef",
  "rfn",
  "authCode",
  "cardType",
  "maskedCardNumber",
  "merchantId",
  "responseCode",
  "responseText",
  "stan",
  "bankDateTime",
  "amountCents",
]);

type ApprovedEvidenceResult =
  | Readonly<{ ok: true; value: CardSyncEvidenceV1 }>
  | Readonly<{
      ok: false;
      code:
        | "LINKLY_CARD_EVIDENCE_REQUIRED"
        | "LINKLY_CARD_EVIDENCE_INVALID"
        | "LINKLY_CARD_EVIDENCE_MISMATCH";
    }>;

function buildApprovedCardSyncEvidence(
  session: LinklyCloudBackendSession,
  attempt: PaymentAttempt,
): ApprovedEvidenceResult {
  const raw = session.cardTransaction as unknown;
  if (raw === null || raw === undefined) {
    return { ok: false, code: "LINKLY_CARD_EVIDENCE_REQUIRED" };
  }
  if (
    !isRecord(raw) ||
    Object.keys(raw).some((key) => !LINKLY_CARD_TRANSACTION_KEYS.has(key))
  ) {
    return { ok: false, code: "LINKLY_CARD_EVIDENCE_INVALID" };
  }

  let evidence: CardSyncEvidenceV1;
  try {
    // 只逐字段映射后端脱敏 DTO；notifications、receipt 和任何额外 payload 永不进入证据。
    evidence = normalizeCardSyncEvidence({
      version: 1,
      provider: "linkly-cloud",
      operation: attempt.operation,
      processor: "ANZ",
      txnRef: nullableDtoField(raw, "txnRef"),
      authCode: nullableDtoField(raw, "authCode"),
      cardType: nullableDtoField(raw, "cardType"),
      cardBin: null,
      maskedCardNumber: nullableDtoField(raw, "maskedCardNumber"),
      merchantId: nullableDtoField(raw, "merchantId"),
      responseCode: nullableDtoField(raw, "responseCode"),
      responseText: nullableDtoField(raw, "responseText"),
      stan: nullableDtoField(raw, "stan"),
      bankDateTimeIso: nullableDtoField(raw, "bankDateTime"),
      amountCents: raw.amountCents,
      refundReference: nullableDtoField(raw, "rfn"),
    });
  } catch {
    return { ok: false, code: "LINKLY_CARD_EVIDENCE_INVALID" };
  }

  const expectedAmountCents = linklyProviderAmountCents(attempt);
  if (!isLinklyApprovalCode(session.responseCode) ||
    !isLinklyApprovalCode(evidence.responseCode) ||
    !sameIdentity(session.responseCode, evidence.responseCode) ||
    (session.responseText !== null && evidence.responseText !== null &&
      !sameIdentity(session.responseText, evidence.responseText))) {
    return { ok: false, code: "LINKLY_CARD_EVIDENCE_MISMATCH" };
  }
  if (
    evidence.amountCents !== expectedAmountCents ||
    evidence.txnRef === null ||
    evidence.refundReference === null ||
    !sameIdentity(evidence.txnRef, session.txnRef) ||
    (attempt.references.sessionId !== null &&
      !sameIdentity(attempt.references.sessionId, session.sessionId)) ||
    (attempt.references.sessionId !== null &&
      attempt.references.txnRef !== null &&
      !sameIdentity(attempt.references.txnRef, evidence.txnRef)) ||
    (attempt.operation === "refund" &&
      !sameIdentity(attempt.references.rfn, evidence.refundReference))
  ) {
    return { ok: false, code: "LINKLY_CARD_EVIDENCE_MISMATCH" };
  }

  return { ok: true, value: evidence };
}

function nullableDtoField(
  value: Readonly<Record<string, unknown>>,
  field: string,
): unknown {
  return value[field] === undefined ? null : value[field];
}

function sameIdentity(left: unknown, right: unknown): boolean {
  return typeof left === "string" &&
    typeof right === "string" &&
    left.trim().length > 0 &&
    left.trim() === right.trim();
}

function isLinklyApprovalCode(value: unknown): boolean {
  if (typeof value !== "string") return false;
  const normalized = value.trim().toUpperCase();
  return normalized === "00" || normalized === "08" || normalized === "11";
}

function frozenEnvironment(attempt: PaymentAttempt, fallback: string): string {
  const persisted = attempt.providerEnvironment?.trim();
  return persisted || fallback;
}

function frozenEnvironmentOrNull(attempt: PaymentAttempt): string | null {
  const persisted = attempt.providerEnvironment?.trim();
  return persisted || null;
}

function recoveryTimeoutMs(control?: LinklyPaymentRecoveryControl): number | null {
  if (!control) return LINKLY_HTTP_TIMEOUT_MS;
  if (control.signal.aborted) return null;
  if (!Number.isFinite(control.deadlineAtMs)) return null;
  const remaining = Math.floor(control.deadlineAtMs - Date.now());
  return remaining > 0 ? Math.min(remaining, LINKLY_HTTP_TIMEOUT_MS) : null;
}

function sameSessionEnvironment(
  session: LinklyCloudBackendSession,
  sessionId: string,
  environment: string,
): boolean {
  return sameCaseInsensitiveIdentity(session.environment, environment) &&
    sameIdentity(session.sessionId, sessionId);
}

function hasRecoveryAction(session: LinklyCloudBackendSession): boolean {
  return Boolean(session.recoveryAction?.trim());
}

function supportsCancelPayment(session: LinklyCloudBackendSession): boolean {
  const displays = session.notifications.filter((notification) =>
    notification.type.trim().toLowerCase() === "display");
  const latest = displays.at(-1);
  if (latest) {
    const flags = readDisplayFlags(latest.payloadJson);
    // 最新 display 快照优先于可能过期的顶层字段；解析失败时失败关闭。
    return flags?.cancelKeyFlag === true;
  }
  return session.cancelKeyFlag;
}

function readDisplayFlags(payloadJson: string): Readonly<{ cancelKeyFlag: boolean }> | null {
  try {
    const parsed: unknown = JSON.parse(payloadJson);
    if (!isRecord(parsed)) return null;
    const response = recordValue(parsed, "Response");
    const source = isRecord(response) ? response : parsed;
    const cancel = recordValue(source, "CancelKeyFlag");
    const decoded = decodeLinklyFlag(cancel);
    return decoded === null ? null : { cancelKeyFlag: decoded };
  } catch {
    return null;
  }
}

function decodeLinklyFlag(value: unknown): boolean | null {
  if (typeof value === "boolean") return value;
  if (typeof value === "number") return Number.isFinite(value) ? value !== 0 : null;
  if (typeof value !== "string") return null;
  switch (value.trim().toLowerCase()) {
    case "true":
    case "1":
    case "yes":
      return true;
    case "false":
    case "0":
    case "no":
      return false;
    default:
      return null;
  }
}

function isValidTimestamp(value: string | null): boolean {
  return typeof value === "string" && value.trim().length > 0 && Number.isFinite(Date.parse(value));
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function sessionUrl(sessionId: string, suffix: string): string { return `/api/v1/linkly/cloud-backend/transactions/${encodeURIComponent(sessionId)}/${suffix}`; }
function requiredText(value: unknown, field: string): string { if (typeof value !== "string" || !value.trim()) throw new Error(`Invalid Linkly ${field}.`); return value; }
function optionalText(value: unknown): string | null { return typeof value === "string" && value ? value : null; }
function integer(value: unknown, field: string): number { if (typeof value !== "number" || !Number.isSafeInteger(value)) throw new Error(`Invalid Linkly ${field}.`); return value; }
function isNotFound(error: unknown): boolean { return error instanceof HbposApiError && (error.status === 404 || error.code === "LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND"); }
function sessionNotFound(): HbposApiError { return new HbposApiError("Linkly session was not found.", { kind: "http", status: 404, code: "LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND" }); }
/**
 * create 被 Hbpos.Api 以 4xx 明确拒绝（400/401/403/404/非 ACTIVE 的 409/429…）：这些都发生在向 Linkly 发 POST 之前
 * （凭据/终端解析、参数校验、限流），证明没有创建会话。408、5xx、传输错误、HTTP 200 的异常信封以及响应解析失败
 * 都可能发生在会话创建之后，一律不算“明确未提交”。
 */
function isCreateDefinitelyNotSubmitted(error: unknown): error is HbposApiError & { status: number } { return error instanceof HbposApiError && error.kind === "http" && error.status !== undefined && error.status >= 400 && error.status < 500 && error.status !== 408; }
function isActiveSessionConflict(error: unknown): boolean { return error instanceof HbposApiError && error.status === 409 && error.code === "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION"; }
function isTerminalSelectionConflict(error: unknown): boolean { return error instanceof LinklyTerminalSelectionConflictError || (error instanceof HbposApiError && error.status === 409 && error.code === "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT"); }
function isTerminalNotReadyConflict(error: unknown): boolean { return error instanceof HbposApiError && error.status === 409 && error.code === "LINKLY_CLOUD_TERMINAL_NOT_READY"; }
function isPaymentAwareSelectionPort(port: LinklyTerminalSelectionPort): port is LinklyPaymentAwareTerminalSelectionPort { return "readTerminalsForPayment" in port && typeof port.readTerminalsForPayment === "function"; }
function matchesPaymentSelection(snapshot: LinklyTerminalSelectionSnapshot, expected: LinklyPaymentTerminalSelectionExpectation): boolean { return snapshot.environment === expected.environment && snapshot.mode === expected.mode && (expected.mode !== "Active" || (snapshot.selectedTerminalId === expected.terminalId && snapshot.selectionRevision === expected.selectionRevision)); }
class LinklyTerminalSelectionConflictError extends Error { public readonly code = "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT"; }
function linklyProviderAmountCents(attempt: PaymentAttempt): number {
  const amount = paymentProviderAmountCents(attempt.operation, attempt.amount);
  if (amount === null) throw new Error("LINKLY_AMOUNT_INVALID");
  return amount;
}
