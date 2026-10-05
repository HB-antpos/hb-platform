import {
  HbposApiError,
  type CashierSessionDto,
  type DeviceRegistrationResetResponse,
  type DeviceVerifyResponse,
} from "../api/hbpos-api";

import type { CashierLoginResult } from "./cashier-authentication";
import type { DeviceRegistrationApiPartitionGuard } from "./device-registration-api-partition-guard";
import {
  CashierAuthorizationStore,
  DeviceCredentialStore,
  DeviceLockStore,
  DevicePresentationStore,
  DeviceRegistrationResetMarkerStore,
  InstallationIdentityStore,
  PendingDeviceRegistrationStore,
  type DeviceRegistrationResetMarker,
} from "./secure-storage";

const deviceRegistrationPermission =
  "Permissions.PosTerminal.Settings.DeviceRegistration";
const recoveryLockReason = "DEVICE_REGISTRATION_RESET_RECOVERY_REQUIRED";

export interface DeviceRegistrationResetApi {
  resetRegistration(
    input: Readonly<{ operationId: string }>,
    freshCashierAuthorization: string,
  ): Promise<DeviceRegistrationResetResponse>;
  verify(input: Readonly<{
    deviceCode: string;
    storeCode: string;
    hardwareId: string;
  }>): Promise<DeviceVerifyResponse>;
}

export type DeviceRegistrationResetRecoveryResult =
  | "none"
  | "completed"
  /** 服务端仍精确启用本机：重置未生效，已撤销本次重置标记与恢复锁，保留注册。 */
  | "restored"
  | "pending";

export type DeviceRegistrationResetDependencies = Readonly<{
  api: DeviceRegistrationResetApi;
  authenticateOnline(input: Readonly<{
    storeCode: string;
    deviceCode: string;
    userBarcode: string;
  }>): Promise<CashierLoginResult>;
  credentials: DeviceCredentialStore;
  presentation: DevicePresentationStore;
  pendingRegistration: PendingDeviceRegistrationStore;
  lock: DeviceLockStore;
  marker: DeviceRegistrationResetMarkerStore;
  cashierAuthorization: CashierAuthorizationStore;
  installation: InstallationIdentityStore;
  createOperationId(): string;
  nowIso(): string;
  invalidateCurrentCashier(): void;
  apiPartitionGuard?: DeviceRegistrationApiPartitionGuard;
}>;

/**
 * prepared marker 必须先于服务端写入；任何不确定结果都保持 fail-closed，
 * 直到匿名 verify 给出精确终态：精确停用则完成本机清理，精确仍启用则撤销本次重置。
 */
export class DeviceRegistrationResetCoordinator {
  public constructor(
    private readonly input: DeviceRegistrationResetDependencies,
  ) {}

  public reset(
    employeeBarcode: string,
  ): Promise<DeviceRegistrationResetResponse> {
    return this.runMutation(() => this.resetCore(employeeBarcode));
  }

  private async resetCore(
    employeeBarcode: string,
  ): Promise<DeviceRegistrationResetResponse> {
    const barcode = requiredText(employeeBarcode, "employee barcode");
    const credentials = await this.requireCurrentCredentials();
    const authentication = await this.input.authenticateOnline({
      storeCode: credentials.storeCode,
      deviceCode: credentials.deviceCode,
      userBarcode: barcode,
    });
    const token = this.requireFreshEmployeeSession(
      authentication,
      credentials.storeCode,
      credentials.deviceCode,
    );
    const marker = this.createMarker(credentials);
    try {
      await this.input.marker.save(marker);
    } catch (error) {
      await this.lockForRecovery();
      throw error;
    }

    let response: DeviceRegistrationResetResponse;
    try {
      response = await this.input.api.resetRegistration(
        { operationId: marker.operationId },
        token,
      );
      this.assertResetResponse(response, marker);
    } catch (error) {
      if (isExplicitServerRejection(error)) {
        try {
          await this.input.marker.clear();
        } catch {
          await this.lockForRecovery();
        }
      } else {
        await this.lockForRecovery();
      }
      throw error;
    }

    try {
      await this.input.marker.save({
        ...marker,
        phase: "server-disabled",
      });
      await this.clearLocalRegistration();
      return response;
    } catch (error) {
      await this.lockForRecovery();
      throw error;
    }
  }

  public recover(): Promise<DeviceRegistrationResetRecoveryResult> {
    return this.runMutation(() => this.recoverCore());
  }

  private async recoverCore(): Promise<DeviceRegistrationResetRecoveryResult> {
    let marker: DeviceRegistrationResetMarker | null;
    try {
      marker = await this.input.marker.load();
    } catch {
      await this.lockForRecovery();
      return "pending";
    }
    if (!marker) return "none";

    let response: DeviceVerifyResponse;
    try {
      response = await this.input.api.verify({
        deviceCode: marker.deviceCode,
        storeCode: marker.storeCode,
        hardwareId: marker.hardwareId,
      });
    } catch {
      await this.lockForRecovery();
      return "pending";
    }
    if (!sameMarkerIdentity(response, marker)) {
      await this.lockForRecovery();
      return "pending";
    }
    if (
      response.exactIdentityMatched === true &&
      response.deviceStatus === 0 &&
      response.isAllowed === false
    ) {
      try {
        await this.clearLocalRegistration();
        return "completed";
      } catch {
        await this.lockForRecovery();
        return "pending";
      }
    }

    if (
      response.exactIdentityMatched === true &&
      response.deviceStatus === 1 &&
      response.isAllowed === true
    ) {
      // 服务端对本机精确硬件身份仍是“启用且允许”：重置请求没有生效（未到达服务端），
      // 或生效后已被管理员重新启用。两种情况服务端的当前授权都覆盖本机，继续锁机不增加
      // 任何安全性（匿名 verify 本就会把授权码下发给同一身份），只会让设备永久不可用。
      return this.restoreUnappliedReset(marker);
    }

    await this.lockForRecovery();
    return "pending";
  }

  /**
   * 撤销一次未生效的重置：只在本机凭据仍完整且与 marker 身份一致时进行，
   * 仅移除本次重置写入的恢复锁，其他原因的设备锁（服务端停用、设备 403）原样保留。
   */
  private async restoreUnappliedReset(
    marker: DeviceRegistrationResetMarker,
  ): Promise<DeviceRegistrationResetRecoveryResult> {
    try {
      const [current, installationId] = await Promise.all([
        this.input.credentials.load(),
        this.input.installation.getOrCreate(),
      ]);
      if (
        !current ||
        current.deviceCode !== marker.deviceCode ||
        current.storeCode !== marker.storeCode ||
        current.hardwareId !== marker.hardwareId ||
        current.hardwareId !== installationId
      ) {
        // 本机清理已部分执行或凭据被替换：不能把残缺注册恢复成可营业状态。
        // 管理员在后台停用该设备后，下次启动会按“精确停用”完成清理。
        await this.lockForRecovery();
        return "pending";
      }
      // 先解除持久恢复锁再删 marker：删 marker 失败时下次启动仍会重新核对。
      await this.input.lock.unlockIfReason(recoveryLockReason);
      await this.input.marker.clear();
      this.input.lock.releaseRecoveryProcessLock();
      return "restored";
    } catch {
      await this.lockForRecovery();
      return "pending";
    }
  }

  public async isResetRecoveryPending(): Promise<boolean> {
    try {
      return (await this.input.marker.load()) !== null;
    } catch {
      await this.lockForRecovery();
      return true;
    }
  }

  private async runMutation<T>(operation: () => Promise<T>): Promise<T> {
    const lease = this.input.apiPartitionGuard?.beginMutation();
    try {
      return await operation();
    } finally {
      lease?.release();
    }
  }

  private async requireCurrentCredentials() {
    const [credentials, installationId] = await Promise.all([
      this.input.credentials.load(),
      this.input.installation.getOrCreate(),
    ]);
    if (!credentials || credentials.hardwareId !== installationId) {
      throw resetError("DEVICE_REGISTRATION_RESET_SCOPE_INVALID");
    }
    return credentials;
  }

  private requireFreshEmployeeSession(
    authentication: CashierLoginResult,
    storeCode: string,
    deviceCode: string,
  ): string {
    const session: CashierSessionDto = authentication.session;
    if (
      authentication.source !== "online" ||
      session.isEmergencyOverride === true ||
      session.storeCode?.trim() !== storeCode ||
      session.deviceCode?.trim() !== deviceCode ||
      !session.permissionCodes?.includes(deviceRegistrationPermission)
    ) {
      throw resetError("DEVICE_REGISTRATION_RESET_EMPLOYEE_DENIED");
    }
    return requiredText(
      session.authorizationToken ?? "",
      "fresh cashier authorization",
    );
  }

  private createMarker(
    credentials: Readonly<{
      deviceCode: string;
      storeCode: string;
      hardwareId: string;
    }>,
  ): DeviceRegistrationResetMarker {
    return {
      version: 1,
      operationId: requiredUuid(this.input.createOperationId()),
      phase: "prepared",
      deviceCode: requiredText(credentials.deviceCode, "device code"),
      storeCode: requiredText(credentials.storeCode, "store code"),
      hardwareId: requiredText(credentials.hardwareId, "hardware id"),
      createdAtUtc: requiredIso(this.input.nowIso()),
    };
  }

  private assertResetResponse(
    response: DeviceRegistrationResetResponse,
    marker: DeviceRegistrationResetMarker,
  ): void {
    if (
      response.operationId !== marker.operationId ||
      response.deviceCode?.trim() !== marker.deviceCode ||
      response.storeCode?.trim() !== marker.storeCode ||
      !Number.isFinite(Date.parse(response.disabledAtUtc))
    ) {
      throw new HbposApiError("Device registration reset response is invalid.", {
        kind: "transport",
        code: "DEVICE_REGISTRATION_RESET_RESPONSE_INVALID",
      });
    }
  }

  private async clearLocalRegistration(): Promise<void> {
    await this.input.cashierAuthorization.clear();
    await this.input.credentials.clear();
    await this.input.presentation.clear();
    await this.input.pendingRegistration.clear();
    await this.input.lock.unlock();
    await this.input.marker.clear();
    this.input.invalidateCurrentCashier();
    this.input.lock.releaseRecoveryProcessLock();
  }

  private async lockForRecovery(): Promise<void> {
    this.input.invalidateCurrentCashier();
    try {
      await this.input.lock.lockForRecovery(recoveryLockReason);
    } catch {
      // 进程闩锁已先置位，即使 Keychain 写入失败也不能继续营业。
    }
  }
}

function sameMarkerIdentity(
  response: DeviceVerifyResponse,
  marker: DeviceRegistrationResetMarker,
): boolean {
  return (
    response.deviceCode?.trim() === marker.deviceCode &&
    response.storeCode?.trim() === marker.storeCode
  );
}

function isExplicitServerRejection(error: unknown): boolean {
  if (!(error instanceof HbposApiError)) return false;
  if (error.kind === "envelope") return true;
  return (
    error.kind === "http" &&
    error.status !== undefined &&
    error.status >= 400 &&
    error.status < 500 &&
    error.status !== 408 &&
    error.status !== 429
  );
}

function resetError(code: string): HbposApiError {
  return new HbposApiError("Device registration reset was rejected.", {
    kind: "envelope",
    code,
  });
}

function requiredText(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) throw new TypeError(`${label} is required.`);
  return normalized;
}

function requiredUuid(value: string): string {
  const normalized = value.trim();
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
      normalized,
    )
  ) {
    throw new TypeError("operation id must be a UUID.");
  }
  return normalized;
}

function requiredIso(value: string): string {
  const normalized = value.trim();
  if (!Number.isFinite(Date.parse(normalized))) {
    throw new TypeError("reset marker time must be ISO-8601.");
  }
  return normalized;
}
