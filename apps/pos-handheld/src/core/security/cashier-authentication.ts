import { HbposApiError, isHbposNonApiResponse, type CashierSessionDto } from "../api/hbpos-api";

import { CashierAuthorizationStore, CashierSessionCache, DeviceLockStore } from "./secure-storage";

export interface CashierAuthenticationApi {
  barcodeLogin(input: Readonly<{
    storeCode: string;
    deviceCode: string;
    userBarcode: string;
  }>): Promise<CashierSessionDto>;
}

export interface NetworkStatusPort {
  isOnline(): Promise<boolean>;
}

export type EmergencyCashierLoginResult =
  | Readonly<{
      ok: true;
      emergencyGrantId: string;
      expiresAtEpochMs: number;
      systemUptimeMs: number;
      trustedNowEpochMs: number;
    }>
  | Readonly<{ ok: false; errorCode: string }>;

export interface EmergencyCashierLoginPort {
  verifyAndActivate(
    token: string,
    device: Readonly<{ storeCode: string; deviceCode: string }>,
  ): Promise<EmergencyCashierLoginResult>;
}

export type EmergencyCashierAuthenticationConfiguration = Readonly<{
  permissionCodes: readonly string[];
  service: EmergencyCashierLoginPort;
}>;

export type CashierLoginResult = Readonly<{
  emergencyTiming?: Readonly<{
    systemUptimeMs: number;
    trustedNowEpochMs: number;
  }>;
  source: "online" | "offline-cache" | "emergency-override";
  session: CashierSessionDto;
}>;

export class CashierAuthenticationService {
  private readonly deviceLock: DeviceLockStore;
  private readonly emergencyPermissions: readonly string[];

  public constructor(
    private readonly api: CashierAuthenticationApi,
    private readonly cache: CashierSessionCache,
    private readonly network: NetworkStatusPort,
    private readonly authorizationStore?: CashierAuthorizationStore,
    deviceLock?: DeviceLockStore,
    private readonly emergency?: EmergencyCashierAuthenticationConfiguration,
  ) {
    this.deviceLock = deviceLock ?? new DeviceLockStore(cache.secureStore);
    this.emergencyPermissions = Object.freeze(
      [...new Set(emergency?.permissionCodes.map(requiredPermission) ?? [])]
        .sort(),
    );
  }

  public async login(input: Readonly<{
    storeCode: string;
    deviceCode: string;
    userBarcode: string;
  }>): Promise<CashierLoginResult> {
    const scope = verifiedLoginScope(input);
    if (await this.deviceLock.isLocked()) {
      throw new HbposApiError("Device is disabled and must be re-authorized online.", {
        kind: "http",
        status: 403,
        code: "DEVICE_LOCKED"
      });
    }
    if (hasEmergencyPrefix(input.userBarcode)) {
      return this.loginEmergency({ ...input, ...scope });
    }
    if (!(await this.network.isOnline())) {
      return this.loadCached({ ...input, ...scope });
    }

    try {
      const session = await this.api.barcodeLogin({ ...input, ...scope });
      assertCashierSessionScope(session, scope);
      await this.cache.save(scope.storeCode, scope.deviceCode, input.userBarcode, session);
      await this.activateAuthorization(session, "online", scope);
      return { source: "online", session };
    } catch (error: unknown) {
      // 关键逻辑：瞬时网络/服务端/网关失败可回退；401/403/400 等其他 4xx 与带信封的 envelope 拒绝是明确在线拒绝。
      if (isCacheFallbackEligible(error)) {
        return this.loadCached({ ...input, ...scope });
      }
      throw error;
    }
  }

  /**
   * 设备注册重置专用验票：只接受实时在线返回，不读取离线缓存、不接受紧急票据，
   * 也不替换当前营业收银员的授权。调用方必须立即消费返回票据，不得持久化条码。
   */
  public async loginOnlineOnly(input: Readonly<{
    storeCode: string;
    deviceCode: string;
    userBarcode: string;
  }>): Promise<CashierLoginResult> {
    const scope = verifiedLoginScope(input);
    if (await this.deviceLock.isLocked()) {
      throw new HbposApiError("Device is disabled and must be re-authorized online.", {
        kind: "http",
        status: 403,
        code: "DEVICE_LOCKED",
      });
    }
    if (hasEmergencyPrefix(input.userBarcode)) {
      throw new HbposApiError("A real employee barcode is required.", {
        kind: "envelope",
        code: "REAL_EMPLOYEE_BARCODE_REQUIRED",
      });
    }
    if (!(await this.network.isOnline())) {
      throw new HbposApiError("An online employee login is required.", {
        kind: "transport",
        code: "ONLINE_LOGIN_REQUIRED",
      });
    }

    const session = await this.api.barcodeLogin({ ...input, ...scope });
    assertCashierSessionScope(session, scope);
    return { source: "online", session };
  }

  private async loginEmergency(input: Readonly<{
    storeCode: string;
    deviceCode: string;
    userBarcode: string;
  }>): Promise<CashierLoginResult> {
    if (!this.emergency) {
      throw new HbposApiError("Emergency login is unavailable.", {
        kind: "envelope",
        code: "EMERGENCY_LOGIN_SERVICE_UNAVAILABLE",
      });
    }
    const verified = await this.emergency.service.verifyAndActivate(
      input.userBarcode,
      {
        storeCode: input.storeCode,
        deviceCode: input.deviceCode,
      },
    );
    if (!verified.ok) {
      throw new HbposApiError("Emergency login was rejected.", {
        kind: "envelope",
        code: requiredEmergencyErrorCode(verified.errorCode),
      });
    }
    const grantId = requiredUuid(verified.emergencyGrantId);
    if (
      !Number.isSafeInteger(verified.expiresAtEpochMs) ||
      !Number.isSafeInteger(verified.trustedNowEpochMs) ||
      !Number.isSafeInteger(verified.systemUptimeMs) ||
      verified.trustedNowEpochMs < 0 ||
      verified.systemUptimeMs < 0 ||
      verified.expiresAtEpochMs <= verified.trustedNowEpochMs
    ) {
      throw invalidEmergencyResult();
    }
    const compactGrantId = grantId.replaceAll("-", "");
    const emergencyIdentity = `EMERGENCY:${compactGrantId}`;
    const session: CashierSessionDto = {
      cashierId: emergencyIdentity,
      userGuid: emergencyIdentity,
      cashierName: "EMERGENCY",
      storeCode: requiredScopeText(input.storeCode),
      deviceCode: requiredScopeText(input.deviceCode),
      roles: ["EmergencyOverride"],
      permissionCodes: [...this.emergencyPermissions],
      allowedStoreCodes: [requiredScopeText(input.storeCode)],
      isSuperAdmin: false,
      isOfflineCached: false,
      isEmergencyOverride: true,
      authorizationToken: input.userBarcode,
      authorizationExpiresAtUtc: new Date(
        verified.expiresAtEpochMs,
      ).toISOString(),
      emergencyGrantId: grantId,
    };
    assertCashierSessionScope(session, verifiedLoginScope(input));
    return {
      emergencyTiming: {
        systemUptimeMs: verified.systemUptimeMs,
        trustedNowEpochMs: verified.trustedNowEpochMs,
      },
      source: "emergency-override",
      session,
    };
  }

  private async loadCached(input: Readonly<{
    storeCode: string;
    deviceCode: string;
    userBarcode: string;
  }>): Promise<CashierLoginResult> {
    const session = await this.cache.load(input.storeCode, input.deviceCode, input.userBarcode);
    if (!session) {
      throw new HbposApiError("Cashier is not available in the local offline cache.", {
        kind: "transport",
        code: "OFFLINE_CASHIER_NOT_CACHED"
      });
    }
    const scope = verifiedLoginScope(input);
    assertCashierSessionScope(session, scope);
    await this.activateAuthorization(session, "offline-cache", scope);
    return { source: "offline-cache", session };
  }

  private async activateAuthorization(
    session: CashierSessionDto,
    source: "online" | "offline-cache",
    scope: Readonly<{ storeCode: string; deviceCode: string }>,
  ): Promise<void> {
    if (!this.authorizationStore) {
      return;
    }
    const expiresAtEpochMs =
      typeof session.authorizationExpiresAtUtc === "string"
        ? Date.parse(session.authorizationExpiresAtUtc)
        : Number.NaN;
    if (
      !session.authorizationToken ||
      !Number.isSafeInteger(expiresAtEpochMs) ||
      expiresAtEpochMs < 0
    ) {
      await this.authorizationStore.clear();
      return;
    }
    await this.authorizationStore.set({
      authorizationToken: session.authorizationToken,
      expiresAtEpochMs,
      source,
      scope,
    });
  }
}

function verifiedLoginScope(input: Readonly<{
  storeCode: string;
  deviceCode: string;
}>): Readonly<{ storeCode: string; deviceCode: string }> {
  return Object.freeze({
    storeCode: requiredScopeText(input.storeCode),
    deviceCode: requiredScopeText(input.deviceCode),
  });
}

function assertCashierSessionScope(
  session: CashierSessionDto,
  expected: Readonly<{ storeCode: string; deviceCode: string }>,
): void {
  if (
    requiredScopeText(session.storeCode) !== expected.storeCode ||
    requiredScopeText(session.deviceCode) !== expected.deviceCode
  ) {
    throw new HbposApiError("Cashier session scope is invalid.", {
      kind: "envelope",
      code: "CASHIER_SESSION_SCOPE_INVALID",
    });
  }
}

/**
 * 与 WPF 收银员扫码登录同口径（#606、#607）：服务端 cashiers/barcode-login 只以 401（条码无效/停用、设备认证失败）、
 * 403（设备越权）、400（请求体校验）和 success=false 信封表达在线拒绝。
 * - 404/405 只会来自后端发布、网关切换或路由未就绪；408/429/5xx 是瞬时故障；
 * - 2xx 却不是 ApiResult 信封（网关 HTML、Wi-Fi 认证页、空 200）不是 POS API 的回答；
 * 以上都按服务不可用回退离线缓存，而不是误报「条码无效或已停用」。
 */
function isCacheFallbackEligible(error: unknown): boolean {
  if (!(error instanceof HbposApiError)) {
    return false;
  }
  if (error.kind === "transport") {
    return true;
  }
  if (error.kind === "envelope") {
    return isHbposNonApiResponse(error);
  }
  return error.kind === "http"
    && (error.status === 404
      || error.status === 405
      || error.status === 408
      || error.status === 429
      || (error.status !== undefined && error.status >= 500));
}

function hasEmergencyPrefix(value: string): boolean {
  return value.startsWith("HBPOSE1-") || value.startsWith("HBPOSE2-");
}

function requiredPermission(value: string): string {
  const normalized = value.trim();
  if (!normalized.startsWith("Permissions.PosTerminal.")) {
    throw new TypeError("Emergency permission must be POS-scoped.");
  }
  return normalized;
}

function requiredScopeText(value: unknown): string {
  const normalized = typeof value === "string" ? value.trim() : "";
  if (!normalized) throw invalidEmergencyResult();
  return normalized;
}

function requiredEmergencyErrorCode(value: string): string {
  const normalized = value.trim();
  if (!/^EMERGENCY_[A-Z0-9_]{1,96}$/u.test(normalized)) {
    throw invalidEmergencyResult();
  }
  return normalized;
}

function requiredUuid(value: string): string {
  const normalized = value.trim().toLowerCase();
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(
      normalized,
    )
  ) {
    throw invalidEmergencyResult();
  }
  return normalized;
}

function invalidEmergencyResult(): HbposApiError {
  return new HbposApiError("Emergency login response is invalid.", {
    kind: "envelope",
    code: "EMERGENCY_LOGIN_RESPONSE_INVALID",
  });
}
