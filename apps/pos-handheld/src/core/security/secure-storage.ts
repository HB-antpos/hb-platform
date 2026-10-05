import type { CashierSessionDto } from "../api/hbpos-api";
import { parseDeviceActivationCode } from "./device-activation-code";

export type SecureStoreWriteOptions = Readonly<{
  requireThisDeviceOnly: boolean;
}>;

export interface SecureStorePort {
  get(key: string): Promise<string | null>;
  set(key: string, value: string, options: SecureStoreWriteOptions): Promise<void>;
  remove(key: string): Promise<void>;
}

/** 由运行时注入 SHA-256，核心层不依赖 Node 或 Expo Crypto。 */
export interface CashierSessionKeyHasher {
  sha256Hex(input: string): Promise<string>;
}

const installationIdKey = "hbpos.handheld.installation-id.v1";
const deviceCredentialsKey = "hbpos.handheld.device-credentials.v1";
const devicePresentationKey = "hbpos.handheld.device-presentation.v1";
const pendingDeviceRegistrationKey = "hbpos.handheld.pending-device-registration.v1";
const pendingDeviceActivationCodeKey = "hbpos.handheld.pending-device-activation-code.v1";
const deviceRegistrationResetMarkerKey = "hbpos.handheld.device-registration-reset.v1";
const deviceLockKey = "hbpos.handheld.device-lock.v1";
const activeCashierAuthorizationKey = "hbpos.handheld.active-cashier-authorization.v1";
const secureThisDeviceOnly: SecureStoreWriteOptions = { requireThisDeviceOnly: true };
const pendingActivationSaveQueues = new WeakMap<SecureStorePort, Promise<void>>();

export type StoredDeviceCredentials = Readonly<{
  deviceCode: string;
  storeCode: string;
  hardwareId: string;
  authorizationCode: string;
}>;

export type DevicePresentationCache = Readonly<{
  deviceCode: string;
  storeCode: string;
  storeName: string;
}>;

type StoredDevicePresentation = DevicePresentationCache &
  Readonly<{
    version: 1;
  }>;

export type PendingDeviceRegistration = Readonly<{
  deviceCode: string;
  storeCode: string;
}>;

export type DeviceRegistrationResetMarker = Readonly<{
  version: 1;
  operationId: string;
  phase: "prepared" | "server-disabled";
  deviceCode: string;
  storeCode: string;
  hardwareId: string;
  createdAtUtc: string;
}>;

export class InstallationIdentityStore {
  public constructor(
    private readonly secureStore: SecureStorePort,
    private readonly createUuid: () => string
  ) {}

  public async getOrCreate(): Promise<string> {
    const current = await this.secureStore.get(installationIdKey);
    if (current !== null) {
      return requireInstallationId(current);
    }

    const installationId = requireInstallationId(this.createUuid());
    await this.secureStore.set(installationIdKey, installationId, secureThisDeviceOnly);
    return installationId;
  }
}

function requireInstallationId(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 256 ||
    value !== value.trim() ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new Error("Unable to create a secure installation identifier.");
  }
  return value;
}

export class DeviceCredentialStore {
  public constructor(public readonly secureStore: SecureStorePort) {}

  public async load(): Promise<StoredDeviceCredentials | null> {
    const raw = await this.secureStore.get(deviceCredentialsKey);
    if (!raw) {
      return null;
    }

    return parseDeviceCredentials(raw);
  }

  public async save(credentials: StoredDeviceCredentials): Promise<void> {
    await this.secureStore.set(
      deviceCredentialsKey,
      JSON.stringify(validateDeviceCredentials(credentials)),
      secureThisDeviceOnly,
    );
  }

  public async clear(): Promise<void> {
    await this.secureStore.remove(deviceCredentialsKey);
  }
}

/** 展示名称不属于设备凭据；损坏或不可读时只降级为无名称。 */
export class DevicePresentationStore {
  public constructor(private readonly secureStore: SecureStorePort) {}

  public async load(): Promise<DevicePresentationCache | null> {
    try {
      const raw = await this.secureStore.get(devicePresentationKey);
      return raw ? parseDevicePresentation(raw) : null;
    } catch {
      try {
        await this.clear();
      } catch {
        // 清理失败也不能让非认证展示缓存阻断 POS。
      }
      return null;
    }
  }

  public async save(
    presentation: DevicePresentationCache,
  ): Promise<void> {
    const normalized = validateDevicePresentation(presentation);
    const stored: StoredDevicePresentation = {
      version: 1,
      ...normalized,
    };
    await this.secureStore.set(
      devicePresentationKey,
      JSON.stringify(stored),
      secureThisDeviceOnly,
    );
  }

  public clear(): Promise<void> {
    return this.secureStore.remove(devicePresentationKey);
  }
}

/** 首次注册尚未获得授权码时，仍需保存可恢复的 verify 目标。 */
export class PendingDeviceRegistrationStore {
  public constructor(private readonly secureStore: SecureStorePort) {}

  public async load(): Promise<PendingDeviceRegistration | null> {
    const raw = await this.secureStore.get(pendingDeviceRegistrationKey);
    return raw ? parsePendingDeviceRegistration(raw) : null;
  }

  public save(registration: PendingDeviceRegistration): Promise<void> {
    return this.secureStore.set(
      pendingDeviceRegistrationKey,
      JSON.stringify(validatePendingDeviceRegistration(registration)),
      secureThisDeviceOnly,
    );
  }

  public clear(): Promise<void> {
    return this.secureStore.remove(pendingDeviceRegistrationKey);
  }
}

export type PendingDeviceActivation = Readonly<{
  activationCode: string;
  mode: "redeem" | "rebind";
  apiPartition: string;
  hardwareId: string;
}>;

export type PendingDeviceActivationIntent = Pick<
  PendingDeviceActivation,
  "apiPartition" | "hardwareId"
>;

export class PendingDeviceActivationConflictError extends Error {
  public constructor() {
    super("Pending device activation intent conflict.");
    this.name = "PendingDeviceActivationConflictError";
  }
}

/** 只在最终 redeem/rebind 请求窗口保存，成功落盘设备凭据后立即清除。 */
export class PendingDeviceActivationCodeStore {
  public constructor(private readonly secureStore: SecureStorePort) {}

  public async load(): Promise<string | null> {
    return (await this.loadPending())?.activationCode ?? null;
  }

  public async loadPending(): Promise<PendingDeviceActivation | null> {
    const raw = await this.secureStore.get(pendingDeviceActivationCodeKey);
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw) as Readonly<{
        activationCode?: unknown;
        apiPartition?: unknown;
        hardwareId?: unknown;
        mode?: unknown;
        version?: unknown;
      }>;
      const activationCode =
        typeof parsed.activationCode === "string"
          ? parseDeviceActivationCode(parsed.activationCode)
          : null;
      if (
        parsed.version === 3 &&
        activationCode &&
        typeof parsed.apiPartition === "string" &&
        typeof parsed.hardwareId === "string" &&
        (parsed.mode === "redeem" || parsed.mode === "rebind")
      ) {
        return Object.freeze({
          activationCode,
          mode: parsed.mode,
          apiPartition: normalizePendingDeviceActivationApiPartition(parsed.apiPartition),
          hardwareId: normalizeActivationHardwareId(parsed.hardwareId),
        });
      }
    } catch {
      // 损坏 JSON 与字段非法走同一失败关闭清理路径。
    }
    // 状态可能对应一次服务端已消费请求；损坏记录不得当作“无 pending”并删除。
    throw new Error(`Stored ${pendingDeviceActivationCodeKey} is invalid.`);
  }

  public async save(
    value: string,
    mode: PendingDeviceActivation["mode"],
    intent: PendingDeviceActivationIntent,
  ): Promise<void> {
    const activationCode = parseDeviceActivationCode(value);
    if (!activationCode) {
      throw new TypeError("Device activation code is invalid.");
    }
    const apiPartition = normalizePendingDeviceActivationApiPartition(intent.apiPartition);
    const hardwareId = normalizeActivationHardwareId(intent.hardwareId);
    await serializePendingActivationSave(this.secureStore, async () => {
      const existing = await this.loadPending();
      if (existing) {
        if (
          existing.activationCode === activationCode &&
          existing.mode === mode &&
          existing.apiPartition === apiPartition &&
          existing.hardwareId === hardwareId
        ) {
          return;
        }
        // 已发起的最终消费可能已在服务端成功，第二个意图不得覆盖恢复凭据。
        throw new PendingDeviceActivationConflictError();
      }
      await this.secureStore.set(
        pendingDeviceActivationCodeKey,
        JSON.stringify({
          version: 3,
          activationCode,
          mode,
          apiPartition,
          hardwareId,
        }),
        secureThisDeviceOnly,
      );
    });
  }

  public clear(): Promise<void> {
    return this.secureStore.remove(pendingDeviceActivationCodeKey);
  }
}

export function normalizePendingDeviceActivationApiPartition(value: string): string {
  const input = value.trim();
  let parsed: URL;
  try {
    parsed = new URL(input);
  } catch {
    throw new TypeError("Device activation API partition is invalid.");
  }
  if (
    (parsed.protocol !== "https:" && parsed.protocol !== "http:") ||
    parsed.username ||
    parsed.password ||
    parsed.search ||
    parsed.hash
  ) {
    throw new TypeError("Device activation API partition is invalid.");
  }
  const path = parsed.pathname.replace(/\/+$/u, "");
  return `${parsed.origin}${path}`;
}

function normalizeActivationHardwareId(value: string): string {
  const hardwareId = value.trim();
  if (
    !hardwareId ||
    hardwareId.length > 256 ||
    /[\u0000-\u001f\u007f]/u.test(hardwareId)
  ) {
    throw new TypeError("Device activation hardware identifier is invalid.");
  }
  return hardwareId;
}

async function serializePendingActivationSave<T>(
  secureStore: SecureStorePort,
  operation: () => Promise<T>,
): Promise<T> {
  const previous = pendingActivationSaveQueues.get(secureStore) ?? Promise.resolve();
  let release!: () => void;
  const current = new Promise<void>((resolve) => {
    release = resolve;
  });
  pendingActivationSaveQueues.set(secureStore, current);
  await previous;
  try {
    return await operation();
  } finally {
    release();
    if (pendingActivationSaveQueues.get(secureStore) === current) {
      pendingActivationSaveQueues.delete(secureStore);
    }
  }
}

/** 服务端重置与本机 Keychain 清理之间的崩溃恢复标记；损坏时必须失败关闭。 */
export class DeviceRegistrationResetMarkerStore {
  public constructor(private readonly secureStore: SecureStorePort) {}

  public async load(): Promise<DeviceRegistrationResetMarker | null> {
    const raw = await this.secureStore.get(deviceRegistrationResetMarkerKey);
    return raw ? parseDeviceRegistrationResetMarker(raw) : null;
  }

  public save(marker: DeviceRegistrationResetMarker): Promise<void> {
    return this.secureStore.set(
      deviceRegistrationResetMarkerKey,
      JSON.stringify(validateDeviceRegistrationResetMarker(marker)),
      secureThisDeviceOnly,
    );
  }

  public clear(): Promise<void> {
    return this.secureStore.remove(deviceRegistrationResetMarkerKey);
  }
}

export class DeviceLockStore {
  private recoveryProcessLocked = false;

  public constructor(private readonly secureStore: SecureStorePort) {}

  public async isLocked(): Promise<boolean> {
    if (this.recoveryProcessLocked) return true;
    return (await this.secureStore.get(deviceLockKey)) !== null;
  }

  public async lock(reason: string): Promise<void> {
    await this.secureStore.set(deviceLockKey, reason, secureThisDeviceOnly);
  }

  public async unlock(): Promise<void> {
    await this.secureStore.remove(deviceLockKey);
  }

  /**
   * 仅当持久锁原因正是 expectedReason 时才移除。用于撤销重置恢复自己写的锁，
   * 绝不能顺带解除服务端停用或设备 403 写入的其他锁（那些锁由设备会话按服务端结果处理）。
   */
  public async unlockIfReason(expectedReason: string): Promise<void> {
    if ((await this.secureStore.get(deviceLockKey)) !== expectedReason) return;
    await this.secureStore.remove(deviceLockKey);
  }

  /** 持久锁写入失败时，进程闩锁仍必须先同步阻止登录及设备会话。 */
  public async lockForRecovery(reason: string): Promise<void> {
    this.recoveryProcessLocked = true;
    await this.lock(reason);
  }

  /** 仅在精确恢复及本机清理全部完成后释放。 */
  public releaseRecoveryProcessLock(): void {
    this.recoveryProcessLocked = false;
  }
}

export class CashierSessionCache {
  public constructor(
    public readonly secureStore: SecureStorePort,
    private readonly keyHasher: CashierSessionKeyHasher,
  ) {}

  public async load(
    storeCode: string,
    deviceCode: string,
    userBarcode: string
  ): Promise<CashierSessionDto | null> {
    const raw = await this.secureStore.get(await this.key(storeCode, deviceCode, userBarcode));
    return raw
      ? validateCashierSession(parseStored<unknown>(raw, "cashier session"), {
          storeCode,
          deviceCode,
        })
      : null;
  }

  public async save(
    storeCode: string,
    deviceCode: string,
    userBarcode: string,
    session: CashierSessionDto
  ): Promise<void> {
    const validated = validateCashierSession(session, {
      storeCode,
      deviceCode,
    });
    await this.secureStore.set(
      await this.key(storeCode, deviceCode, userBarcode),
      JSON.stringify(validated),
      secureThisDeviceOnly
    );
  }

  private async key(storeCode: string, deviceCode: string, userBarcode: string): Promise<string> {
    // Keychain 项名不能泄露可扫描的收银员条码；仅保存规范化三元组的 SHA-256 摘要。
    const material = `${storeCode.trim()}\n${deviceCode.trim()}\n${userBarcode.trim()}`;
    const digest = await this.keyHasher.sha256Hex(material);
    if (!/^[a-f0-9]{64}$/i.test(digest)) {
      throw new Error("Cashier cache key hasher returned an invalid SHA-256 digest.");
    }
    return `hbpos.handheld.cashier.v2.${digest.toLowerCase()}`;
  }
}

export class CashierAuthorizationStore {
  // 每次撤销或写入都推进版本；异步 Keychain 读写完成后必须再次确认仍是当前版本。
  private authorizationVersion = 0;
  private authorizationRevoked = false;
  private mutationQueue: Promise<void> = Promise.resolve();

  public constructor(
    private readonly secureStore: SecureStorePort,
    private readonly time: CashierAuthorizationTimePort = {
      getSystemUptimeMilliseconds: defaultSystemUptimeMilliseconds,
      nowEpochMs: Date.now,
    },
  ) {}

  public async get(scope: CashierAuthorizationScope): Promise<string | null> {
    const requestedScope = this.requiredScopeOrNull(scope);
    if (!requestedScope || this.authorizationRevoked) return null;
    const version = this.authorizationVersion;
    const raw = await this.secureStore.get(
      activeCashierAuthorizationKey,
    );
    if (this.authorizationRevoked || version !== this.authorizationVersion) {
      return null;
    }
    if (!raw) return null;

    let stored: StoredCashierAuthorization;
    try {
      stored = parseCashierAuthorization(raw);
      if (!sameCashierAuthorizationScope(stored.scope, requestedScope)) {
        this.clearInBackground();
        return null;
      }
      const systemUptimeMs = validTimeValue(
        this.time.getSystemUptimeMilliseconds(),
        "system uptime",
      );
      if (
        systemUptimeMs < stored.activatedAtSystemUptimeMs ||
        systemUptimeMs >= stored.expiresAtSystemUptimeMs ||
        (stored.source !== "emergency-override" &&
          validTimeValue(this.time.nowEpochMs(), "wall time") >=
            stored.expiresAtEpochMs)
      ) {
        this.clearInBackground();
        return null;
      }
    } catch {
      // 旧版纯字符串或损坏 envelope 不能继续成为网络 bearer。
      this.clearInBackground();
      return null;
    }
    return stored.authorizationToken;
  }

  public async set(
    authorization: CashierAuthorizationWrite,
  ): Promise<void> {
    let normalized: StoredCashierAuthorization | null;
    try {
      normalized = normalizeCashierAuthorizationWrite(
        authorization,
        this.time,
      );
    } catch (error) {
      await this.clear();
      throw error;
    }
    if (normalized === null) {
      await this.clear();
      return;
    }
    const version = this.revokeInMemory();
    await this.enqueueMutation(async () => {
      await this.secureStore.set(
        activeCashierAuthorizationKey,
        JSON.stringify(normalized),
        secureThisDeviceOnly,
      );
      // 只有本次成功登录仍是最新操作，才能解除进程内撤销状态。
      if (version === this.authorizationVersion) {
        this.authorizationRevoked = false;
      }
    });
  }

  public clear(): Promise<void> {
    this.revokeInMemory();
    return this.enqueueMutation(() =>
      this.secureStore.remove(activeCashierAuthorizationKey),
    );
  }

  /** scope 发布的同步边界：先撤销进程内可读性，再异步处理 Keychain。 */
  public invalidateForDeviceScope(): void {
    this.clearInBackground();
  }

  private revokeInMemory(): number {
    this.authorizationVersion += 1;
    this.authorizationRevoked = true;
    return this.authorizationVersion;
  }

  private async enqueueMutation(
    mutation: () => Promise<void>,
  ): Promise<void> {
    const previous = this.mutationQueue;
    let release!: () => void;
    this.mutationQueue = new Promise<void>((resolve) => {
      release = resolve;
    });
    // 旧 clear 的 Keychain 删除失败不能永久阻塞随后成功登录的写入。
    await previous.catch(() => undefined);
    try {
      await mutation();
    } finally {
      release();
    }
  }

  private requiredScopeOrNull(
    scope: CashierAuthorizationScope,
  ): CashierAuthorizationScope | null {
    try {
      return normalizeCashierAuthorizationScope(scope);
    } catch {
      this.clearInBackground();
      return null;
    }
  }

  private clearInBackground(): void {
    // clear 会先同步撤销内存可读性；后台删除挂起或失败都不得阻塞读取或产生未处理拒绝。
    void this.clear().catch(() => undefined);
  }
}

export type CashierAuthorizationSource =
  | "online"
  | "offline-cache"
  | "emergency-override";

export type CashierAuthorizationWrite = Readonly<{
  authorizationToken: string;
  expiresAtEpochMs: number;
  source: CashierAuthorizationSource;
  scope: CashierAuthorizationScope;
  systemUptimeMs?: number;
  trustedNowEpochMs?: number;
}>;

export type CashierAuthorizationScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

export type CashierAuthorizationTimePort = Readonly<{
  getSystemUptimeMilliseconds(): number;
  nowEpochMs(): number;
}>;

type StoredCashierAuthorization = Readonly<{
  activatedAtSystemUptimeMs: number;
  authorizationToken: string;
  expiresAtEpochMs: number;
  expiresAtSystemUptimeMs: number;
  scope: CashierAuthorizationScope;
  source: CashierAuthorizationSource;
  version: 3;
}>;

function normalizeCashierAuthorizationWrite(
  input: CashierAuthorizationWrite,
  time: CashierAuthorizationTimePort,
): StoredCashierAuthorization | null {
  const authorizationToken = validAuthorizationToken(
    input.authorizationToken,
  );
  const expiresAtEpochMs = validTimeValue(
    input.expiresAtEpochMs,
    "authorization expiry",
  );
  const source = validCashierAuthorizationSource(input.source);
  const scope = normalizeCashierAuthorizationScope(input.scope);
  const currentSystemUptimeMs = validTimeValue(
    time.getSystemUptimeMilliseconds(),
    "system uptime",
  );
  const trustedNowEpochMs =
    source === "emergency-override"
      ? validTimeValue(
          input.trustedNowEpochMs,
          "trusted emergency time",
        )
      : validTimeValue(time.nowEpochMs(), "wall time");
  const activatedAtSystemUptimeMs =
    source === "emergency-override"
      ? validTimeValue(
          input.systemUptimeMs,
          "trusted emergency uptime",
        )
      : currentSystemUptimeMs;
  const durationMs = expiresAtEpochMs - trustedNowEpochMs;
  const expiresAtSystemUptimeMs =
    activatedAtSystemUptimeMs + durationMs;
  if (
    durationMs <= 0 ||
    currentSystemUptimeMs < activatedAtSystemUptimeMs ||
    !Number.isSafeInteger(expiresAtSystemUptimeMs) ||
    currentSystemUptimeMs >= expiresAtSystemUptimeMs
  ) {
    return null;
  }
  return Object.freeze({
    activatedAtSystemUptimeMs,
    authorizationToken,
    expiresAtEpochMs,
    expiresAtSystemUptimeMs,
    scope,
    source,
    version: 3,
  });
}

function parseCashierAuthorization(
  raw: string,
): StoredCashierAuthorization {
  const value = parseStored<unknown>(
    raw,
    activeCashierAuthorizationKey,
  );
  const record = storedRecord(
    value,
    activeCashierAuthorizationKey,
  );
  const expectedKeys = [
    "activatedAtSystemUptimeMs",
    "authorizationToken",
    "expiresAtEpochMs",
    "expiresAtSystemUptimeMs",
    "scope",
    "source",
    "version",
  ];
  if (
    Object.keys(record).length !== expectedKeys.length ||
    expectedKeys.some((key) => !(key in record)) ||
    record.version !== 3
  ) {
    throw new Error(
      `Stored ${activeCashierAuthorizationKey} is invalid.`,
    );
  }
  const activatedAtSystemUptimeMs = validTimeValue(
    record.activatedAtSystemUptimeMs,
    "stored authorization uptime",
  );
  const expiresAtSystemUptimeMs = validTimeValue(
    record.expiresAtSystemUptimeMs,
    "stored authorization monotonic expiry",
  );
  const expiresAtEpochMs = validTimeValue(
    record.expiresAtEpochMs,
    "stored authorization expiry",
  );
  if (expiresAtSystemUptimeMs <= activatedAtSystemUptimeMs) {
    throw new Error(
      `Stored ${activeCashierAuthorizationKey} is invalid.`,
    );
  }
  return Object.freeze({
    activatedAtSystemUptimeMs,
    authorizationToken: validAuthorizationToken(
      record.authorizationToken,
    ),
    expiresAtEpochMs,
    expiresAtSystemUptimeMs,
    scope: normalizeCashierAuthorizationScope(record.scope),
    source: validCashierAuthorizationSource(record.source),
    version: 3,
  });
}

function normalizeCashierAuthorizationScope(
  value: unknown,
): CashierAuthorizationScope {
  const record = storedRecord(value, "cashier authorization scope");
  const expectedKeys = ["storeCode", "deviceCode"];
  if (
    Object.keys(record).length !== expectedKeys.length ||
    expectedKeys.some((key) => !(key in record))
  ) {
    throw new Error("Cashier authorization scope is invalid.");
  }
  return Object.freeze({
    storeCode: storedText(record.storeCode, "cashier authorization scope").trim(),
    deviceCode: storedText(record.deviceCode, "cashier authorization scope").trim(),
  });
}

function sameCashierAuthorizationScope(
  left: CashierAuthorizationScope,
  right: CashierAuthorizationScope,
): boolean {
  return left.storeCode === right.storeCode && left.deviceCode === right.deviceCode;
}

function validCashierAuthorizationSource(
  value: unknown,
): CashierAuthorizationSource {
  if (
    value !== "online" &&
    value !== "offline-cache" &&
    value !== "emergency-override"
  ) {
    throw new Error("Cashier authorization source is invalid.");
  }
  return value;
}

function validAuthorizationToken(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 16_384 ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new Error("Cashier authorization token is invalid.");
  }
  return value;
}

function validTimeValue(
  value: unknown,
  label: string,
): number {
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    value < 0
  ) {
    throw new Error(`${label} is invalid.`);
  }
  return value;
}

function defaultSystemUptimeMilliseconds(): number {
  const value = globalThis.performance?.now();
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error("System uptime is unavailable.");
  }
  return Math.floor(value);
}

function parseStored<T>(raw: string, label: string): T {
  try {
    return JSON.parse(raw) as T;
  } catch {
    throw new Error(`Stored ${label} is invalid.`);
  }
}

function parseDeviceCredentials(raw: string): StoredDeviceCredentials {
  return validateDeviceCredentials(
    parseStored<unknown>(raw, deviceCredentialsKey),
  );
}

function validateDeviceCredentials(value: unknown): StoredDeviceCredentials {
  const record = storedRecord(value, deviceCredentialsKey);
  return {
    deviceCode: storedText(record.deviceCode, deviceCredentialsKey),
    storeCode: storedText(record.storeCode, deviceCredentialsKey),
    hardwareId: storedText(record.hardwareId, deviceCredentialsKey),
    authorizationCode: storedText(record.authorizationCode, deviceCredentialsKey),
  };
}

function parseDevicePresentation(
  raw: string,
): DevicePresentationCache {
  return validateDevicePresentation(
    parseStored<unknown>(raw, devicePresentationKey),
    true,
  );
}

function validateDevicePresentation(
  value: unknown,
  requireVersion = false,
): DevicePresentationCache {
  const record = storedRecord(value, devicePresentationKey);
  const expectedKeys = requireVersion
    ? ["version", "deviceCode", "storeCode", "storeName"]
    : ["deviceCode", "storeCode", "storeName"];
  if (
    Object.keys(record).length !== expectedKeys.length ||
    expectedKeys.some((key) => !(key in record)) ||
    (requireVersion && record.version !== 1)
  ) {
    throw new Error(`Stored ${devicePresentationKey} is invalid.`);
  }
  return {
    deviceCode: storedText(
      record.deviceCode,
      devicePresentationKey,
    ).trim(),
    storeCode: storedText(
      record.storeCode,
      devicePresentationKey,
    ).trim(),
    storeName: storedText(
      record.storeName,
      devicePresentationKey,
    ).trim(),
  };
}

function parsePendingDeviceRegistration(raw: string): PendingDeviceRegistration {
  return validatePendingDeviceRegistration(
    parseStored<unknown>(raw, pendingDeviceRegistrationKey),
  );
}

function validatePendingDeviceRegistration(
  value: unknown,
): PendingDeviceRegistration {
  const record = storedRecord(value, pendingDeviceRegistrationKey);
  return {
    deviceCode: storedText(record.deviceCode, pendingDeviceRegistrationKey),
    storeCode: storedText(record.storeCode, pendingDeviceRegistrationKey),
  };
}

function parseDeviceRegistrationResetMarker(
  raw: string,
): DeviceRegistrationResetMarker {
  return validateDeviceRegistrationResetMarker(
    parseStored<unknown>(raw, deviceRegistrationResetMarkerKey),
  );
}

function validateDeviceRegistrationResetMarker(
  value: unknown,
): DeviceRegistrationResetMarker {
  const record = storedRecord(value, deviceRegistrationResetMarkerKey);
  const expectedKeys = [
    "version",
    "operationId",
    "phase",
    "deviceCode",
    "storeCode",
    "hardwareId",
    "createdAtUtc",
  ];
  const operationId = storedText(
    record.operationId,
    deviceRegistrationResetMarkerKey,
  ).trim();
  const createdAtUtc = storedText(
    record.createdAtUtc,
    deviceRegistrationResetMarkerKey,
  ).trim();
  if (
    Object.keys(record).length !== expectedKeys.length ||
    expectedKeys.some((key) => !(key in record)) ||
    record.version !== 1 ||
    (record.phase !== "prepared" && record.phase !== "server-disabled") ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
      operationId,
    ) ||
    !Number.isFinite(Date.parse(createdAtUtc))
  ) {
    throw new Error(`Stored ${deviceRegistrationResetMarkerKey} is invalid.`);
  }
  return {
    version: 1,
    operationId,
    phase: record.phase,
    deviceCode: storedText(
      record.deviceCode,
      deviceRegistrationResetMarkerKey,
    ).trim(),
    storeCode: storedText(
      record.storeCode,
      deviceRegistrationResetMarkerKey,
    ).trim(),
    hardwareId: storedText(
      record.hardwareId,
      deviceRegistrationResetMarkerKey,
    ).trim(),
    createdAtUtc,
  };
}

function validateCashierSession(
  value: unknown,
  binding: Readonly<{ storeCode: string; deviceCode: string }>,
): CashierSessionDto {
  const label = "cashier session";
  const record = storedRecord(value, label);
  const storeCode = storedText(record.storeCode, label);
  const deviceCode = storedText(record.deviceCode, label);
  if (storeCode !== binding.storeCode || deviceCode !== binding.deviceCode) {
    throw new Error(`Stored ${label} is invalid.`);
  }

  return {
    cashierId: storedText(record.cashierId, label),
    userGuid: storedText(record.userGuid, label),
    cashierName: storedText(record.cashierName, label),
    storeCode,
    deviceCode,
    ...optionalStoredTextArrayProperty(record, "roles", label),
    ...optionalStoredTextArrayProperty(record, "permissionCodes", label),
    ...optionalStoredTextArrayProperty(record, "allowedStoreCodes", label),
    ...optionalStoredBooleanProperty(record, "isSuperAdmin", label),
    ...optionalStoredBooleanProperty(record, "isOfflineCached", label),
    ...optionalStoredBooleanProperty(record, "isEmergencyOverride", label),
    ...optionalStoredTextProperty(record, "authorizationToken", label),
    ...optionalStoredDateProperty(record, "authorizationExpiresAtUtc", label),
    ...optionalStoredTextProperty(record, "emergencyGrantId", label),
  };
}

function optionalStoredTextArrayProperty(
  record: Readonly<Record<string, unknown>>,
  key: "roles" | "permissionCodes" | "allowedStoreCodes",
  label: string,
): Partial<Record<typeof key, string[] | null>> {
  const value = record[key];
  if (value === undefined) return {};
  if (value === null) return { [key]: null };
  if (!Array.isArray(value)) throw new Error(`Stored ${label} is invalid.`);
  return { [key]: value.map((item) => storedText(item, label)) };
}

function optionalStoredBooleanProperty(
  record: Readonly<Record<string, unknown>>,
  key: "isSuperAdmin" | "isOfflineCached" | "isEmergencyOverride",
  label: string,
): Partial<Record<typeof key, boolean>> {
  const value = record[key];
  if (value === undefined) return {};
  if (typeof value !== "boolean") throw new Error(`Stored ${label} is invalid.`);
  return { [key]: value };
}

function optionalStoredTextProperty(
  record: Readonly<Record<string, unknown>>,
  key: "authorizationToken" | "emergencyGrantId",
  label: string,
): Partial<Record<typeof key, string | null>> {
  const value = record[key];
  if (value === undefined) return {};
  return { [key]: value === null ? null : storedText(value, label) };
}

function optionalStoredDateProperty(
  record: Readonly<Record<string, unknown>>,
  key: "authorizationExpiresAtUtc",
  label: string,
): Partial<Record<typeof key, string | null>> {
  const value = record[key];
  if (value === undefined || value === null) {
    return value === undefined ? {} : { [key]: null };
  }
  const date = storedText(value, label);
  if (!Number.isFinite(Date.parse(date))) {
    throw new Error(`Stored ${label} is invalid.`);
  }
  return { [key]: date };
}

function storedRecord(
  value: unknown,
  label: string,
): Readonly<Record<string, unknown>> {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`Stored ${label} is invalid.`);
  }
  return value as Readonly<Record<string, unknown>>;
}

function storedText(value: unknown, label: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`Stored ${label} is invalid.`);
  }
  return value;
}

/** 仅用于 Node 测试；生产应用必须注入 Expo Keychain 适配器。 */
export class InMemorySecureStore implements SecureStorePort {
  private readonly values = new Map<string, string>();

  public lastWriteOptions: SecureStoreWriteOptions | undefined;
  public lastWriteKey: string | undefined;

  public async get(key: string): Promise<string | null> {
    return this.values.get(key) ?? null;
  }

  public async set(key: string, value: string, options: SecureStoreWriteOptions): Promise<void> {
    this.lastWriteKey = key;
    this.lastWriteOptions = options;
    this.values.set(key, value);
  }

  public async remove(key: string): Promise<void> {
    this.values.delete(key);
  }
}
