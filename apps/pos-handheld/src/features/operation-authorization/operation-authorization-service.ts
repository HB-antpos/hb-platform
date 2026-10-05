import type { CashierSessionDto } from "@/core/api/hbpos-api";
import { auditActorPayload, type AuditActorSnapshot } from "@/core/contracts";
import type { AuditRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";
import type { CashierAuthenticationService } from "@/core/security/cashier-authentication";

export type OperationAuthorizationMode =
  | "current-cashier"
  | "offline-cache"
  | "online";

/** 组合根从已认证的收银员会话构造，业务调用方不得自行伪造。 */
export type RequestingCashierAuthorizationIdentity = Readonly<{
  cashierId: string;
  cashierName: string | null;
  userGuid: string | null;
  storeCode: string;
  deviceCode: string;
  permissions: readonly string[];
}>;

export type OperationAuthorizationRequest = Readonly<{
  /** 一次用户动作的稳定标识；相同标识不能通过重试切换权限或重复执行。 */
  actionId: string;
  permissionCode: string;
  screen: string;
  action: string;
  /** 高风险动作必须由另一名主管扫码授权，即使当前收银员自己已有该权限。 */
  forceSupervisor?: boolean;
}>;

/** 传给业务回调的上下文刻意不含主管票据或条码，仅含冻结的主管审计身份。 */
export type AuthorizedOperationContext = Readonly<{
  authorizationMode: OperationAuthorizationMode;
  requestingCashierId: string;
  authorizingCashierId: string | null;
  permissionCode: string;
  /** 仅主管授权成功时存在，供业务在动作发生时冻结真实审计身份。 */
  authorizingActor?: AuditActorSnapshot;
}>;

export type OperationAuthorizationResult<T> =
  | Readonly<{ authorized: true; value: T }>
  | Readonly<{ authorized: false; reason: OperationAuthorizationFailureReason }>;

export type OperationAuthorizationFailureReason =
  | "NO_ACTIVE_CASHIER"
  | "ACTION_ID_CONFLICT"
  | "ANOTHER_AUTHORIZATION_PENDING"
  | "CANCELLED"
  | "REVOKED"
  | "AUTHENTICATION_FAILED"
  | "AUTHORIZATION_VALIDATION_FAILED"
  | "EMERGENCY_OVERRIDE_DENIED"
  | "STORE_OR_DEVICE_MISMATCH"
  | "AUTHORIZATION_TICKET_INVALID"
  | "PERMISSION_DENIED"
  | "AUTHORIZER_IDENTITY_INVALID";

export type SupervisorBarcodeScanResult =
  | Readonly<{ consumed: false; outcome: "no-pending" }>
  | Readonly<{
      consumed: true;
      outcome:
        | "authorized"
        | "cancelled"
        | "denied"
        | "duplicate-ignored"
        | "ignored";
      reason?: OperationAuthorizationFailureReason;
    }>;

export type OperationAuthorizationPublicState =
  | Readonly<{ kind: "idle" }>
  | Readonly<{
      kind: "awaiting-supervisor";
      actionId: string;
      permissionCode: string;
      screen: string;
      action: string;
      verifying: boolean;
    }>;

type SupervisorAuthenticationPort = Pick<CashierAuthenticationService, "login">;

export type OperationAuthorizationServiceOptions = Readonly<{
  /** 必须是不绑定全局 CashierAuthorizationStore 的认证实例，主管登录不能替换当前收银员。 */
  cashierAuthentication: SupervisorAuthenticationPort;
  audit: Pick<AuditRepositoryPort, "append">;
  createId(): string;
  nowIso(): string;
}>;

type FrozenRequestingCashier = Readonly<{
  cashierId: string;
  cashierName: string | null;
  userGuid: string | null;
  storeCode: string;
  deviceCode: string;
  permissions: readonly string[];
  signature: string;
}>;

type NormalizedRequest = Readonly<{
  actionId: string;
  permissionCode: string;
  screen: string;
  action: string;
  forceSupervisor: boolean;
  signature: string;
}>;

type ActionStatus = "pending" | "executing" | "terminal";

type ActionRecord = {
  readonly request: NormalizedRequest;
  readonly cashier: FrozenRequestingCashier;
  readonly operation: (context: AuthorizedOperationContext) => unknown | Promise<unknown>;
  readonly promise: Promise<OperationAuthorizationResult<unknown>>;
  readonly resolve: (result: OperationAuthorizationResult<unknown>) => void;
  readonly reject: (error: unknown) => void;
  status: ActionStatus;
  settled: boolean;
};

type PendingAuthorization = {
  readonly record: ActionRecord;
  readonly generation: number;
  readonly cancelled: Deferred<void>;
  verifying: boolean;
};

type AuthorizerAuditIdentity = Readonly<{
  cashierId: string | null;
  userGuid: string | null;
}>;

type ValidationResult =
  | Readonly<{
      valid: true;
      cashierId: string;
      cashierName: string | null;
      userGuid: string | null;
      authorizationMode: "offline-cache" | "online";
    }>
  | Readonly<{ valid: false; reason: OperationAuthorizationFailureReason }>;

const MAX_TERMINAL_REPLAY_TOMBSTONES = 2048;
const IDLE_STATE: OperationAuthorizationPublicState = Object.freeze({ kind: "idle" });

/**
 * WPF OperationAuthorizationService 的无界面等价切片。
 *
 * 主管授权票据只在验证栈帧内存在；任何公开 API、回调和状态订阅均无法读取票据。
 */
export class OperationAuthorizationService {
  private readonly records = new Map<string, ActionRecord>();
  private readonly terminalTombstones = new Map<string, ActionRecord>();
  private readonly listeners = new Set<(state: OperationAuthorizationPublicState) => void>();
  private requestingCashier: FrozenRequestingCashier | null = null;
  private pending: PendingAuthorization | null = null;
  private revocationGeneration = 0;

  public constructor(private readonly options: OperationAuthorizationServiceOptions) {}

  public get hasPendingAuthorization(): boolean {
    return this.pending !== null;
  }

  /** 组合根切换当前收银员时调用；同一会话重复激活不会重置在途授权。 */
  public activateRequestingCashier(identity: RequestingCashierAuthorizationIdentity): void {
    const next = freezeCashier(identity);
    if (this.requestingCashier?.signature === next.signature) return;

    // 会话边界必须同步撤销临时授权，旧会话的运行记录不能被新收银员复用。
    this.revokeAll();
    this.records.clear();
    this.terminalTombstones.clear();
    this.requestingCashier = next;
  }

  /** lock、登出和会话失效都会调用；待验证扫码立即返回 cancelled。 */
  public clearRequestingCashier(): void {
    this.revokeAll();
    this.records.clear();
    this.terminalTombstones.clear();
    this.requestingCashier = null;
  }

  public getState(): OperationAuthorizationPublicState {
    const pending = this.pending;
    if (!pending) return IDLE_STATE;
    const { request } = pending.record;
    return Object.freeze({
      kind: "awaiting-supervisor" as const,
      actionId: request.actionId,
      permissionCode: request.permissionCode,
      screen: request.screen,
      action: request.action,
      verifying: pending.verifying,
    });
  }

  public subscribe(listener: (state: OperationAuthorizationPublicState) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * 业务动作最多执行一次。相同 actionId 和签名重用原 Promise；不同签名 fail-closed。
   * 回调只得到脱敏上下文，主管票据不能经过 JSON、闭包参数或公开对象泄漏。
   */
  public authorizeAndRun<T>(
    input: OperationAuthorizationRequest,
    operation: (context: AuthorizedOperationContext) => T | Promise<T>,
  ): Promise<OperationAuthorizationResult<T>> {
    const cashier = this.requestingCashier;
    const request = normalizeRequest(input);
    if (!cashier) return Promise.resolve(denied("NO_ACTIVE_CASHIER"));

    const existing = this.records.get(request.actionId) ?? this.terminalTombstones.get(request.actionId);
    if (existing) {
      if (existing.request.signature !== request.signature || existing.cashier.signature !== cashier.signature) {
        void this.recordOverride(request, cashier, null, "Failed", "ACTION_ID_CONFLICT", "unavailable");
        return Promise.resolve(denied("ACTION_ID_CONFLICT"));
      }
      return existing.promise as Promise<OperationAuthorizationResult<T>>;
    }

    if (!request.forceSupervisor && cashier.permissions.includes(request.permissionCode)) {
      const record = this.createRecord(request, cashier, operation);
      this.records.set(request.actionId, record);
      this.execute(record, {
        authorizationMode: "current-cashier",
        requestingCashierId: cashier.cashierId,
        authorizingCashierId: null,
        permissionCode: request.permissionCode,
      }, null);
      return record.promise as Promise<OperationAuthorizationResult<T>>;
    }

    if (this.pending) {
      const record = this.createRecord(request, cashier, operation);
      this.records.set(request.actionId, record);
      this.settle(record, denied("ANOTHER_AUTHORIZATION_PENDING"));
      void this.recordOverride(request, cashier, null, "Failed", "ANOTHER_AUTHORIZATION_PENDING", "unavailable");
      return record.promise as Promise<OperationAuthorizationResult<T>>;
    }

    const record = this.createRecord(request, cashier, operation);
    this.records.set(request.actionId, record);
    this.pending = {
      record,
      generation: this.revocationGeneration,
      cancelled: createDeferred<void>(),
      verifying: false,
    };
    this.emitState();
    return record.promise as Promise<OperationAuthorizationResult<T>>;
  }

  public async submitSupervisorBarcode(barcodeInput: string): Promise<SupervisorBarcodeScanResult> {
    const pending = this.pending;
    if (!pending) return { consumed: false, outcome: "no-pending" };

    const barcode = barcodeInput.trim();
    if (!barcode) return { consumed: true, outcome: "ignored" };
    if (pending.verifying) return { consumed: true, outcome: "duplicate-ignored" };
    pending.verifying = true;
    this.emitState();

    // 将登录 rejection 映射为普通结果，取消后迟到的 resolve/reject 不会悬挂或形成 unhandled rejection。
    const login = this.options.cashierAuthentication.login({
      storeCode: pending.record.cashier.storeCode,
      deviceCode: pending.record.cashier.deviceCode,
      userBarcode: barcode,
    }).then(
      (result) => ({ kind: "login" as const, result }),
      () => ({ kind: "failed" as const }),
    );
    const outcome = await Promise.race([
      login,
      pending.cancelled.promise.then(() => ({ kind: "cancelled" as const })),
    ]);
    if (outcome.kind === "cancelled" || !this.isPendingActive(pending)) {
      return { consumed: true, outcome: "cancelled" };
    }
    if (outcome.kind === "failed") {
      pending.verifying = false;
      this.emitState();
      await this.recordOverride(pending.record.request, pending.record.cashier, null, "Failed", "AUTHENTICATION_FAILED", "unavailable");
      return { consumed: true, outcome: "denied", reason: "AUTHENTICATION_FAILED" };
    }

    let validation: ValidationResult;
    try {
      validation = this.validateAuthorizer(pending.record, outcome.result.session, outcome.result.source);
    } catch {
      if (!this.isPendingActive(pending)) return { consumed: true, outcome: "cancelled" };
      pending.verifying = false;
      this.emitState();
      await this.recordOverride(pending.record.request, pending.record.cashier, authorizerAuditIdentity(outcome.result.session), "Failed", "AUTHORIZATION_VALIDATION_FAILED", outcome.result.source);
      return { consumed: true, outcome: "denied", reason: "AUTHORIZATION_VALIDATION_FAILED" };
    }
    if (!validation.valid) {
      pending.verifying = false;
      this.emitState();
      await this.recordOverride(pending.record.request, pending.record.cashier, authorizerAuditIdentity(outcome.result.session), "Denied", validation.reason, outcome.result.source);
      return { consumed: true, outcome: "denied", reason: validation.reason };
    }

    this.pending = null;
    this.emitState();
    const context: AuthorizedOperationContext = Object.freeze({
      authorizationMode: validation.authorizationMode,
      requestingCashierId: pending.record.cashier.cashierId,
      authorizingCashierId: validation.cashierId,
      permissionCode: pending.record.request.permissionCode,
      authorizingActor: Object.freeze({
        cashierId: validation.cashierId,
        cashierName: validation.cashierName,
        userGuid: validation.userGuid,
      }),
    });
    // 授权结论先固定；审计失败不扩大权限，也不翻转已开始的动作。
    void this.recordOverride(pending.record.request, pending.record.cashier, {
      cashierId: validation.cashierId,
      userGuid: validation.userGuid,
    }, "Succeeded", null, validation.authorizationMode);
    this.execute(pending.record, context, pending);
    return { consumed: true, outcome: "authorized" };
  }

  public cancel(actionId?: string): boolean {
    const pending = this.pending;
    if (!pending || (actionId !== undefined && actionId !== pending.record.request.actionId)) return false;
    this.finishPending(pending, "CANCELLED");
    return true;
  }

  /** lock 会同步调用；待验证和执行中的结果在返回前 fail-closed。 */
  public revokeAll(): void {
    const pending = this.pending;
    if (pending) this.finishPending(pending, "REVOKED");
    this.revocationGeneration += 1;
    for (const record of this.records.values()) {
      // 已进入业务回调的耐久动作无法安全撤销；必须等待其真实结果，不能伪装为已取消。
      if (!record.settled && record.status !== "executing") {
        this.settle(record, denied("REVOKED"));
      }
    }
  }

  private createRecord(
    request: NormalizedRequest,
    cashier: FrozenRequestingCashier,
    operation: (context: AuthorizedOperationContext) => unknown | Promise<unknown>,
  ): ActionRecord {
    const deferred = createDeferred<OperationAuthorizationResult<unknown>>();
    return {
      request,
      cashier,
      operation,
      promise: deferred.promise,
      resolve: deferred.resolve,
      reject: deferred.reject,
      status: "pending",
      settled: false,
    };
  }

  private execute(
    record: ActionRecord,
    context: AuthorizedOperationContext,
    _pending: PendingAuthorization | null,
  ): void {
    if (record.settled || record.status === "executing") return;
    const generation = this.revocationGeneration;
    // Promise.resolve().then 确保同步异常同样走 finally 失效路径，且每条记录只调用一次回调。
    void Promise.resolve()
      .then(() => {
        // revoke/clear 发生在微任务真正执行前时，不得触发任何业务副作用。
        if (record.settled || generation !== this.revocationGeneration) return undefined;
        record.status = "executing";
        return record.operation(context);
      })
      .then(
        (value) => {
          if (record.status === "executing") this.settle(record, { authorized: true, value });
        },
        (error: unknown) => {
          // 已获授权的业务失败不是权限拒绝；清理动作记录后向调用方原样传播。
          if (record.status === "executing") this.reject(record, error);
        },
      );
  }

  private finishPending(pending: PendingAuthorization, reason: "CANCELLED" | "REVOKED"): void {
    if (!this.isPendingActive(pending)) return;
    this.pending = null;
    pending.cancelled.resolve();
    this.settle(pending.record, denied(reason));
    this.emitState();
    void this.recordOverride(pending.record.request, pending.record.cashier, null, "Denied", reason, "unavailable");
  }

  private settle(record: ActionRecord, result: OperationAuthorizationResult<unknown>): void {
    if (record.settled) return;
    record.settled = true;
    record.status = "terminal";
    record.resolve(result);
    this.addTombstone(record);
  }

  private reject(record: ActionRecord, error: unknown): void {
    if (record.settled) return;
    record.settled = true;
    record.status = "terminal";
    record.reject(error);
    this.addTombstone(record);
  }

  private addTombstone(record: ActionRecord): void {
    this.terminalTombstones.delete(record.request.actionId);
    this.terminalTombstones.set(record.request.actionId, record);
    while (this.terminalTombstones.size > MAX_TERMINAL_REPLAY_TOMBSTONES) {
      const oldest = this.terminalTombstones.keys().next().value as string | undefined;
      if (!oldest) break;
      const removed = this.terminalTombstones.get(oldest);
      this.terminalTombstones.delete(oldest);
      if (removed && this.records.get(oldest) === removed) this.records.delete(oldest);
    }
  }

  private isPendingActive(pending: PendingAuthorization): boolean {
    return this.pending === pending && pending.record.status === "pending" && pending.generation === this.revocationGeneration;
  }

  private emitState(): void {
    const state = this.getState();
    for (const listener of [...this.listeners]) {
      try {
        listener(state);
      } catch {
        // UI 订阅者故障不能中断授权状态机；下次状态变化仍可继续收到通知。
      }
    }
  }

  private validateAuthorizer(
    record: ActionRecord,
    session: CashierSessionDto,
    authorizationMode:
      | "emergency-override"
      | "offline-cache"
      | "online",
  ): ValidationResult {
    if (
      authorizationMode === "emergency-override" ||
      session.isEmergencyOverride === true
    ) {
      return {
        valid: false,
        reason: "EMERGENCY_OVERRIDE_DENIED",
      };
    }
    if (!sameIdentityText(session.storeCode, record.cashier.storeCode) || !sameIdentityText(session.deviceCode, record.cashier.deviceCode)) {
      return { valid: false, reason: "STORE_OR_DEVICE_MISMATCH" };
    }
    const authorizationToken = typeof session.authorizationToken === "string" ? session.authorizationToken : "";
    const expiresAt = typeof session.authorizationExpiresAtUtc === "string" ? Date.parse(session.authorizationExpiresAtUtc) : Number.NaN;
    if (!authorizationToken.trim() || !Number.isFinite(expiresAt) || expiresAt <= this.nowEpochMs()) {
      return { valid: false, reason: "AUTHORIZATION_TICKET_INVALID" };
    }
    if (!Array.isArray(session.permissionCodes) || !session.permissionCodes.some((permission) => permission === record.request.permissionCode)) {
      return { valid: false, reason: "PERMISSION_DENIED" };
    }
    const cashierId = optionalText(session.cashierId);
    if (!cashierId) return { valid: false, reason: "AUTHORIZER_IDENTITY_INVALID" };
    const userGuid = optionalText(session.userGuid);
    if (
      record.request.forceSupervisor &&
      (sameIdentityText(cashierId, record.cashier.cashierId) ||
        (userGuid !== null && record.cashier.userGuid !== null && sameIdentityText(userGuid, record.cashier.userGuid)))
    ) {
      // 强制主管模式要求第二人授权；收银员不能用自己的另一张票据绕过双人复核。
      return { valid: false, reason: "AUTHORIZER_IDENTITY_INVALID" };
    }
    return {
      valid: true,
      cashierId,
      cashierName: optionalText(session.cashierName),
      userGuid,
      authorizationMode,
    };
  }

  private nowEpochMs(): number {
    const now = Date.parse(this.options.nowIso());
    if (!Number.isFinite(now)) throw new TypeError("Operation authorization clock must return ISO time.");
    return now;
  }

  private async recordOverride(
    request: NormalizedRequest,
    cashier: FrozenRequestingCashier,
    authorizer: AuthorizerAuditIdentity | null,
    outcome: "Denied" | "Failed" | "Succeeded",
    reason: string | null,
    authorizationMode:
      | "emergency-override"
      | "offline-cache"
      | "online"
      | "unavailable",
  ): Promise<void> {
    try {
      const occurredAtIso = this.options.nowIso();
      if (!Number.isFinite(Date.parse(occurredAtIso))) throw new TypeError("Operation authorization clock must return ISO time.");
      await this.options.audit.append([{
        eventId: requiredText(this.options.createId(), "Audit event id"),
        eventType: "PERMISSION_OVERRIDE",
        occurredAtIso,
        orderGuid: null,
        correlationId: requiredText(this.options.createId(), "Audit correlation id"),
        payload: {
          source: "pos-handheld",
          ...auditActorPayload(cashier),
          authorizingCashierId: authorizer?.cashierId ?? null,
          authorizingUserGuid: authorizer?.userGuid ?? null,
          permissionCode: request.permissionCode,
          authorizationMode,
          screen: request.screen,
          action: request.action,
          outcome,
          reason,
        },
      }]);
    } catch {
      // 审计故障不能翻转权限结论；令牌从未进入审计对象。
    }
  }
}

function normalizeRequest(input: OperationAuthorizationRequest): NormalizedRequest {
  const normalized = {
    actionId: requiredText(input.actionId, "Authorization action id"),
    permissionCode: requiredText(input.permissionCode, "Authorization permission code"),
    screen: requiredText(input.screen, "Authorization screen"),
    action: requiredText(input.action, "Authorization action"),
    forceSupervisor: input.forceSupervisor === true,
  };
  return { ...normalized, signature: JSON.stringify(normalized) };
}

function freezeCashier(input: RequestingCashierAuthorizationIdentity): FrozenRequestingCashier {
  // 权限集合复制、去重并排序后冻结，调用方随后修改原数组不能改变授权判断。
  const permissions = Object.freeze([...new Set(
    input.permissions.map((permission) => requiredText(permission, "Requesting cashier permission")),
  )].sort());
  const normalized = {
    cashierId: requiredText(input.cashierId, "Requesting cashier id"),
    cashierName: optionalText(input.cashierName),
    userGuid: optionalText(input.userGuid),
    storeCode: requiredText(input.storeCode, "Requesting store code"),
    deviceCode: requiredText(input.deviceCode, "Requesting device code"),
    permissions,
  };
  return Object.freeze({ ...normalized, signature: JSON.stringify(normalized) });
}

function denied(reason: OperationAuthorizationFailureReason): OperationAuthorizationResult<never> {
  return Object.freeze({ authorized: false as const, reason });
}

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

type Deferred<T> = Readonly<{
  promise: Promise<T>;
  resolve(value: T): void;
  reject(reason?: unknown): void;
}>;

function authorizerAuditIdentity(session: CashierSessionDto): AuthorizerAuditIdentity {
  return { cashierId: optionalText(session.cashierId), userGuid: optionalText(session.userGuid) };
}

function sameIdentityText(left: unknown, right: string): boolean {
  return typeof left === "string" && left.trim().localeCompare(right, undefined, { sensitivity: "accent" }) === 0;
}

function optionalText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function requiredText(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) throw new TypeError(`${label} is required.`);
  return normalized;
}
