import {
  getAttendanceQrRefreshIntervalMs,
  isBackgroundPollDue,
} from "@hb/pos-domain/core/contracts/background-polling";
import {
  AttendanceSecurityApiError,
  type AttendanceSecurityRemotePort,
  type RegisteredAttendanceSigningKey,
} from "@hb/pos-api-client/features/attendance-audit/hbpos-attendance-security-api";

export const ATTENDANCE_QR_TOKEN_LIFETIME_MS = 15_000;
export const ATTENDANCE_QR_REFRESH_LEAD_MS = 5_000;
export const ATTENDANCE_QR_TICK_INTERVAL_MS = 1_000;
/** 刷新基础节拍：每 15 秒唤醒一次，是否真正刷新由 isRefreshDue 按状态自适应决定（健康 5 分钟、异常 15 秒）。 */
export const ATTENDANCE_QR_REFRESH_INTERVAL_MS = 15_000;

export type AttendanceDeviceContext = Readonly<{
  authorizationMarker: string;
  deviceCode: string;
  hardwareId: string;
  isAllowed: boolean;
  storeCode: string;
  storeName: string;
}>;

export type AttendanceQrIdentity = Readonly<{
  authorizationMarker: string;
  deviceCode: string;
  hardwareId: string;
  keyHandle: string;
  kid: string;
  registeredAtEpochMs: number;
  storeCode: string;
}>;

export type AttendanceTrustedTime = Readonly<{
  localEpochMs: number;
  serverEpochMs: number;
}>;

export type AttendanceQrProvisioning = Readonly<{
  identity: AttendanceQrIdentity;
  trustedTime: AttendanceTrustedTime;
}>;

export interface AttendanceDeviceContextPort {
  getDeviceContext(): Promise<AttendanceDeviceContext | null>;
}

export interface AttendanceQrCachePort {
  load(): Promise<AttendanceQrProvisioning | null>;
  replace(value: AttendanceQrProvisioning): Promise<void>;
  clear(): Promise<void>;
}

export interface AttendanceConnectivityPort {
  isOnline(): Promise<boolean>;
}

export interface AttendanceQrCryptoPort {
  createA256Identity(): Promise<
    Readonly<{ keyHandle: string; kid: string }>
  >;
  hasA256Key(keyHandle: string): Promise<boolean>;
  /**
   * keyMaterial 只在回调栈中短暂存在，不得放入 presenter state、日志或普通存储。
   * 原生实现应在回调结束后立即清零临时字节。
   */
  withRegistrationKey<T>(
    keyHandle: string,
    use: (keyMaterialBase64Url: string) => Promise<T>,
  ): Promise<T>;
  issueAttendanceQr(input: Readonly<{
    deviceCode: string;
    issuedAtEpochMs: number;
    keyHandle: string;
    kid: string;
    storeCode: string;
  }>): Promise<Readonly<{ imageUri: string }>>;
  destroyKey(keyHandle: string): Promise<void>;
}

export interface AttendanceSchedulerPort {
  every(intervalMs: number, task: () => void): () => void;
}

export type AttendanceQrStateKind =
  | "idle"
  | "initializing"
  | "ready"
  | "unavailable"
  | "setup-failed"
  | "clock-invalid";

export type AttendanceQrState = Readonly<{
  deviceText: string;
  kind: AttendanceQrStateKind;
  online: boolean;
  qrImageUri: string | null;
  requiresOnlineResync: boolean;
  secondsRemaining: number;
  statusCode:
    | "clock-rollback"
    | "enable-online"
    | "offline-signed"
    | "online-verified"
    | "setup-failed";
  storeText: string;
}>;

export type AttendanceQrControllerOptions = Readonly<{
  cache: AttendanceQrCachePort;
  clock: Readonly<{ now(): number }>;
  connectivity: AttendanceConnectivityPort;
  crypto: AttendanceQrCryptoPort;
  deviceContext: AttendanceDeviceContextPort;
  remote: AttendanceSecurityRemotePort;
  scheduler: AttendanceSchedulerPort;
}>;

/**
 * WPF 等价状态机：tick 与在线登记独立调度；二维码有效期固定 15 秒；本机 UTC
 * 一旦回拨即锁存，只有设备认证的在线登记与可信时间持久化都成功后才能解除。
 */
export class AttendanceQrController {
  private readonly listeners = new Set<() => void>();
  private state: AttendanceQrState = Object.freeze({
    deviceText: "",
    kind: "idle",
    online: false,
    qrImageUri: null,
    requiresOnlineResync: false,
    secondsRemaining: 0,
    statusCode: "enable-online",
    storeText: "",
  });
  private provisioning: AttendanceQrProvisioning | null = null;
  private device: AttendanceDeviceContext | null = null;
  private lastObservedLocalEpochMs: number | null = null;
  private tokenExpiresAtTrustedEpochMs: number | null = null;
  private tokenGeneration = 0;
  private refreshInFlight: Promise<void> | null = null;
  private lastRefreshStartedAtMs: number | null = null;
  private tickInFlight: Promise<void> | null = null;
  private cancelTick: (() => void) | null = null;
  private cancelRefresh: (() => void) | null = null;
  private started = false;
  private destroyed = false;

  public constructor(
    private readonly options: AttendanceQrControllerOptions,
  ) {}

  public readonly getState = (): AttendanceQrState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public start(): void {
    if (this.destroyed || this.started) return;
    this.started = true;
    this.cancelTick = this.options.scheduler.every(
      ATTENDANCE_QR_TICK_INTERVAL_MS,
      () => {
        void this.tick();
      },
    );
    this.cancelRefresh = this.options.scheduler.every(
      ATTENDANCE_QR_REFRESH_INTERVAL_MS,
      () => {
        if (this.isRefreshDue()) void this.refresh();
      },
    );
    void this.refresh();
  }

  public destroy(): void {
    if (this.destroyed) return;
    this.cancelTick?.();
    this.cancelRefresh?.();
    this.cancelTick = null;
    this.cancelRefresh = null;
    this.tokenGeneration += 1;
    this.state = Object.freeze({
      ...this.state,
      qrImageUri: null,
      secondsRemaining: 0,
    });
    this.destroyed = true;
    this.listeners.clear();
  }

  public refresh(): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.refreshInFlight) return this.refreshInFlight;
    let running!: Promise<void>;
    this.lastRefreshStartedAtMs = this.safeClockNow();
    running = this.runRefresh().finally(() => {
      if (this.refreshInFlight === running) {
        this.refreshInFlight = null;
      }
    });
    this.refreshInFlight = running;
    return running;
  }

  /**
   * 后台刷新（在线登记 / 校时 / 连通性检查）是否到期（对齐 WPF #927）：
   * 二维码在本机每秒签发，可信时间由「服务器时间锚点 + 本机流逝时间」推算，登记只是后台保活。
   * 状态健康（在线、已启用、校时有效）时 5 分钟一次；离线、启用失败、需要重新校时等异常仍 15 秒重试，
   * 保证提示在恢复后尽快消失。
   */
  private isRefreshDue(): boolean {
    const nowMs = this.safeClockNow();
    if (nowMs === null) return true;
    const healthy =
      this.state.kind === "ready" &&
      this.state.online &&
      !this.state.requiresOnlineResync;
    return isBackgroundPollDue(
      nowMs,
      this.lastRefreshStartedAtMs,
      getAttendanceQrRefreshIntervalMs(healthy),
    );
  }

  private safeClockNow(): number | null {
    try {
      return validClockNow(this.options.clock.now());
    } catch {
      return null;
    }
  }

  public tick(): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.tickInFlight) return this.tickInFlight;
    let running!: Promise<void>;
    running = this.runTick().finally(() => {
      if (this.tickInFlight === running) this.tickInFlight = null;
    });
    this.tickInFlight = running;
    return running;
  }

  private async runRefresh(): Promise<void> {
    const firstLoad = this.provisioning === null;
    if (firstLoad) {
      this.patch({
        kind: "initializing",
        qrImageUri: null,
        secondsRemaining: 0,
      });
    }
    let transientIdentity: AttendanceQrIdentity | null = null;
    try {
      const [rawContext, rawCached] = await Promise.all([
        this.options.deviceContext.getDeviceContext(),
        this.options.cache.load(),
      ]);
      if (this.destroyed) return;
      const context = validDeviceContext(rawContext);
      let cached = validProvisioning(rawCached);
      if (rawCached && !cached) {
        await this.options.cache.clear();
      }
      this.device = context;
      this.patch({
        deviceText: context?.deviceCode ?? "",
        storeText: context
          ? formatStore(context.storeName, context.storeCode)
          : "",
      });

      if (!context) {
        if (cached) {
          await this.clearPersistedIdentity(cached.identity);
          cached = null;
        } else {
          await this.clearProvisioning(true);
        }
        this.markUnavailable();
        return;
      }

      if (cached && !matchesContext(cached.identity, context)) {
        await this.clearPersistedIdentity(cached.identity);
        cached = null;
      }
      if (
        cached &&
        !(await this.options.crypto.hasA256Key(
          cached.identity.keyHandle,
        ))
      ) {
        await this.clearPersistedIdentity(cached.identity);
        cached = null;
      }
      if (this.destroyed) return;

      this.applyCached(cached);
      await this.tick();

      const online = await this.safeConnectivityCheck();
      if (this.destroyed) return;
      if (!online) {
        this.patch({ online: false });
        if (this.state.requiresOnlineResync) {
          this.markClockInvalid();
        } else if (this.provisioning) {
          await this.tick();
        } else {
          this.markUnavailable();
        }
        return;
      }

      let identity =
        this.provisioning?.identity ??
        (await this.createIdentity(context));
      if (!this.provisioning) transientIdentity = identity;
      let registration: RegisteredAttendanceSigningKey;
      try {
        registration = await this.register(identity);
      } catch (error) {
        if (!isKidConflict(error)) throw error;
        await this.clearPersistedIdentity(identity);
        transientIdentity = null;
        identity = await this.createIdentity(context);
        transientIdentity = identity;
        registration = await this.register(identity);
      }
      if (this.destroyed) return;

      const localEpochMs = validClockNow(this.options.clock.now());
      const next: AttendanceQrProvisioning = Object.freeze({
        identity: Object.freeze({
          ...identity,
          registeredAtEpochMs: registration.registeredAtEpochMs,
        }),
        trustedTime: Object.freeze({
          localEpochMs,
          serverEpochMs: registration.serverTimeEpochMs,
        }),
      });
      await this.options.cache.replace(next);
      if (this.destroyed) return;
      this.provisioning = next;
      transientIdentity = null;
      this.lastObservedLocalEpochMs = localEpochMs;
      this.tokenGeneration += 1;
      this.tokenExpiresAtTrustedEpochMs = null;
      this.patch({
        kind: "ready",
        online: true,
        qrImageUri: null,
        requiresOnlineResync: false,
        secondsRemaining: 0,
        statusCode: "online-verified",
      });
      await this.tick();
    } catch {
      if (transientIdentity) {
        await this.safeDestroyKey(transientIdentity.keyHandle);
      }
      if (!this.destroyed) this.markSetupFailed();
    }
  }

  private async runTick(): Promise<void> {
    const provisioning = this.provisioning;
    if (!provisioning) {
      if (this.state.kind !== "initializing") this.markUnavailable();
      return;
    }
    if (this.state.requiresOnlineResync) {
      this.markClockInvalid();
      return;
    }

    let localNow: number;
    try {
      localNow = validClockNow(this.options.clock.now());
    } catch {
      this.markSetupFailed();
      return;
    }
    if (
      localNow < provisioning.trustedTime.localEpochMs ||
      (this.lastObservedLocalEpochMs !== null &&
        localNow < this.lastObservedLocalEpochMs)
    ) {
      this.tokenGeneration += 1;
      this.tokenExpiresAtTrustedEpochMs = null;
      this.lastObservedLocalEpochMs = Math.max(
        this.lastObservedLocalEpochMs ?? localNow,
        provisioning.trustedTime.localEpochMs,
      );
      this.patch({
        kind: "clock-invalid",
        qrImageUri: null,
        requiresOnlineResync: true,
        secondsRemaining: 0,
        statusCode: "clock-rollback",
      });
      return;
    }
    this.lastObservedLocalEpochMs = localNow;
    const trustedNow =
      provisioning.trustedTime.serverEpochMs +
      (localNow - provisioning.trustedTime.localEpochMs);
    if (
      this.tokenExpiresAtTrustedEpochMs === null ||
      trustedNow >=
        this.tokenExpiresAtTrustedEpochMs - ATTENDANCE_QR_REFRESH_LEAD_MS
    ) {
      await this.rotateQr(provisioning, trustedNow);
      return;
    }
    this.patch({
      kind: "ready",
      secondsRemaining: remainingSeconds(
        this.tokenExpiresAtTrustedEpochMs,
        trustedNow,
      ),
      statusCode: this.state.online
        ? "online-verified"
        : "offline-signed",
    });
  }

  private async rotateQr(
    provisioning: AttendanceQrProvisioning,
    trustedNow: number,
  ): Promise<void> {
    const generation = ++this.tokenGeneration;
    this.tokenExpiresAtTrustedEpochMs = null;
    this.patch({
      qrImageUri: null,
      secondsRemaining: 0,
    });
    try {
      const issued =
        await this.options.crypto.issueAttendanceQr({
          deviceCode: provisioning.identity.deviceCode,
          issuedAtEpochMs: trustedNow,
          keyHandle: provisioning.identity.keyHandle,
          kid: provisioning.identity.kid,
          storeCode: provisioning.identity.storeCode,
        });
      if (
        this.destroyed ||
        generation !== this.tokenGeneration ||
        this.provisioning !== provisioning ||
        this.state.requiresOnlineResync
      ) {
        return;
      }
      const imageUri = validQrImageUri(issued.imageUri);
      this.tokenExpiresAtTrustedEpochMs =
        trustedNow + ATTENDANCE_QR_TOKEN_LIFETIME_MS;
      this.patch({
        kind: "ready",
        qrImageUri: imageUri,
        secondsRemaining: 15,
        statusCode: this.state.online
          ? "online-verified"
          : "offline-signed",
      });
    } catch {
      if (generation === this.tokenGeneration && !this.destroyed) {
        this.markSetupFailed();
      }
    }
  }

  private async createIdentity(
    context: AttendanceDeviceContext,
  ): Promise<AttendanceQrIdentity> {
    const created = await this.options.crypto.createA256Identity();
    const kid = validAttendanceKid(created.kid);
    const keyHandle = validOpaqueHandle(created.keyHandle);
    return Object.freeze({
      authorizationMarker: context.authorizationMarker,
      deviceCode: context.deviceCode,
      hardwareId: context.hardwareId,
      keyHandle,
      kid,
      registeredAtEpochMs: 0,
      storeCode: context.storeCode,
    });
  }

  private register(
    identity: AttendanceQrIdentity,
  ): Promise<RegisteredAttendanceSigningKey> {
    return this.options.crypto.withRegistrationKey(
      identity.keyHandle,
      (keyMaterialBase64Url) =>
        this.options.remote.registerAttendanceKey({
          algorithm: "A256GCM",
          keyMaterialBase64Url,
          kid: identity.kid,
        }),
    );
  }

  private applyCached(
    provisioning: AttendanceQrProvisioning | null,
  ): void {
    this.provisioning = provisioning;
    if (!provisioning) {
      this.lastObservedLocalEpochMs = null;
      this.tokenExpiresAtTrustedEpochMs = null;
      this.tokenGeneration += 1;
      this.patch({
        qrImageUri: null,
        secondsRemaining: 0,
      });
      return;
    }
    this.lastObservedLocalEpochMs = Math.max(
      this.lastObservedLocalEpochMs ??
        provisioning.trustedTime.localEpochMs,
      provisioning.trustedTime.localEpochMs,
    );
  }

  private async clearProvisioning(destroyKey: boolean): Promise<void> {
    const current = this.provisioning;
    this.provisioning = null;
    this.tokenGeneration += 1;
    this.tokenExpiresAtTrustedEpochMs = null;
    this.lastObservedLocalEpochMs = null;
    if (current && destroyKey) {
      await this.clearPersistedIdentity(current.identity);
    } else {
      await this.options.cache.clear();
    }
  }

  private async clearPersistedIdentity(
    identity: AttendanceQrIdentity,
  ): Promise<void> {
    if (this.provisioning?.identity.keyHandle === identity.keyHandle) {
      this.provisioning = null;
    }
    this.tokenGeneration += 1;
    this.tokenExpiresAtTrustedEpochMs = null;
    await this.options.cache.clear();
    await this.safeDestroyKey(identity.keyHandle);
  }

  private async safeDestroyKey(keyHandle: string): Promise<void> {
    try {
      await this.options.crypto.destroyKey(keyHandle);
    } catch {
      // 清除缓存后 key 销毁失败由原生安全层重试；不恢复已失效身份。
    }
  }

  private async safeConnectivityCheck(): Promise<boolean> {
    try {
      return await this.options.connectivity.isOnline();
    } catch {
      return false;
    }
  }

  private markUnavailable(): void {
    this.tokenGeneration += 1;
    this.tokenExpiresAtTrustedEpochMs = null;
    this.patch({
      kind: "unavailable",
      online: false,
      qrImageUri: null,
      secondsRemaining: 0,
      statusCode: "enable-online",
    });
  }

  private markClockInvalid(): void {
    this.tokenGeneration += 1;
    this.tokenExpiresAtTrustedEpochMs = null;
    this.patch({
      kind: "clock-invalid",
      qrImageUri: null,
      requiresOnlineResync: true,
      secondsRemaining: 0,
      statusCode: "clock-rollback",
    });
  }

  private markSetupFailed(): void {
    this.tokenGeneration += 1;
    this.tokenExpiresAtTrustedEpochMs = null;
    this.patch({
      kind: "setup-failed",
      online: true,
      qrImageUri: null,
      secondsRemaining: 0,
      statusCode: "setup-failed",
    });
  }

  private patch(patch: Partial<AttendanceQrState>): void {
    if (this.destroyed) return;
    this.state = Object.freeze({ ...this.state, ...patch });
    for (const listener of this.listeners) listener();
  }
}

function validDeviceContext(
  value: AttendanceDeviceContext | null,
): AttendanceDeviceContext | null {
  if (
    !value ||
    value.isAllowed !== true ||
    !validText(value.storeCode, 50) ||
    !validText(value.storeName, 256) ||
    !validText(value.deviceCode, 128) ||
    !validText(value.hardwareId, 256) ||
    !validText(value.authorizationMarker, 256)
  ) {
    return null;
  }
  return Object.freeze({ ...value });
}

function validProvisioning(
  value: AttendanceQrProvisioning | null,
): AttendanceQrProvisioning | null {
  if (
    !value ||
    !validText(value.identity.storeCode, 50) ||
    !validText(value.identity.deviceCode, 128) ||
    !validText(value.identity.hardwareId, 256) ||
    !validText(value.identity.authorizationMarker, 256) ||
    !Number.isSafeInteger(value.identity.registeredAtEpochMs) ||
    value.identity.registeredAtEpochMs < 0 ||
    !Number.isSafeInteger(value.trustedTime.localEpochMs) ||
    !Number.isSafeInteger(value.trustedTime.serverEpochMs)
  ) {
    return null;
  }
  try {
    validAttendanceKid(value.identity.kid);
    validOpaqueHandle(value.identity.keyHandle);
  } catch {
    return null;
  }
  return Object.freeze({
    identity: Object.freeze({ ...value.identity }),
    trustedTime: Object.freeze({ ...value.trustedTime }),
  });
}

function matchesContext(
  identity: AttendanceQrIdentity,
  context: AttendanceDeviceContext,
): boolean {
  return (
    identity.storeCode === context.storeCode &&
    identity.deviceCode === context.deviceCode &&
    identity.hardwareId === context.hardwareId &&
    identity.authorizationMarker === context.authorizationMarker
  );
}

function validAttendanceKid(value: unknown): string {
  if (
    typeof value !== "string" ||
    !/^[A-Za-z0-9_-]{1,64}$/u.test(value)
  ) {
    throw new Error("Invalid attendance kid.");
  }
  return value;
}

function validOpaqueHandle(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 256 ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new Error("Invalid secure key handle.");
  }
  return value;
}

function validClockNow(value: unknown): number {
  if (!Number.isSafeInteger(value) || Number(value) < 0) {
    throw new Error("Invalid clock.");
  }
  return Number(value);
}

function validQrImageUri(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 2_500_000 ||
    /[\u0000-\u001f\u007f]/u.test(value) ||
    (!/^data:image\/png;base64,[A-Za-z0-9+/=]+$/u.test(value) &&
      !/^file:\/\/\/[^?#]+$/u.test(value))
  ) {
    throw new Error("Invalid QR image URI.");
  }
  return value;
}

function validText(value: unknown, maxLength: number): value is string {
  return (
    typeof value === "string" &&
    value.trim() === value &&
    value.length > 0 &&
    value.length <= maxLength &&
    !/[\u0000-\u001f\u007f]/u.test(value)
  );
}

function formatStore(name: string, code: string): string {
  return `${name} (${code})`;
}

function remainingSeconds(expiresAt: number, now: number): number {
  return Math.max(
    0,
    Math.min(
      15,
      Math.ceil((expiresAt - now) / ATTENDANCE_QR_TICK_INTERVAL_MS),
    ),
  );
}

function isKidConflict(error: unknown): boolean {
  return (
    error instanceof AttendanceSecurityApiError &&
    (error.status === 409 ||
      error.code === "ATTENDANCE_QR_KEY_KID_CONFLICT")
  );
}
