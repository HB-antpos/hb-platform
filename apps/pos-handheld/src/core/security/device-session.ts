import type {
  DeviceActivationRebindResponse,
  DeviceActivationPreviewResponse,
  DeviceActivationRedeemResponse,
  DeviceRegisterResponse,
  DeviceReregisterResponse,
  DeviceVerifyResponse
} from "../api/hbpos-api";
import { HbposApiError } from "../api/hbpos-api";

import {
  DeviceCredentialStore,
  DeviceLockStore,
  DevicePresentationStore,
  InstallationIdentityStore,
  PendingDeviceActivationCodeStore,
  PendingDeviceActivationConflictError,
  PendingDeviceRegistrationStore,
  normalizePendingDeviceActivationApiPartition,
} from "./secure-storage";
import { parseDeviceActivationCode } from "./device-activation-code";
import type { DeviceRegistrationApiPartitionGuard } from "./device-registration-api-partition-guard";

export type DeviceSessionStatus =
  | "unregistered"
  | "registering"
  | "pending-approval"
  | "verifying"
  | "reregistering"
  | "authorized"
  | "denied"
  | "disabled";

export type DeviceSessionState = Readonly<{
  status: DeviceSessionStatus;
  deviceCode?: string;
  storeCode?: string;
  message?: string;
}>;

export type DevicePresentation = Readonly<{
  deviceCode: string;
  storeCode: string;
  storeName: string | null;
}>;

export type DeviceScopeChange = Readonly<{
  previous: Readonly<{ deviceCode: string; storeCode: string }>;
  current: Readonly<{ deviceCode: string; storeCode: string }>;
}>;

const deviceScopeChangeListeners = new Set<
  (change: DeviceScopeChange) => void
>();

/**
 * 组合根只订阅脱敏 scope 变更；授权码与硬件标识始终留在设备会话内部。
 */
export function subscribeDeviceScopeChange(
  listener: (change: DeviceScopeChange) => void,
): () => void {
  deviceScopeChangeListeners.add(listener);
  return () => deviceScopeChangeListeners.delete(listener);
}

export interface DeviceSessionApi {
  register(input: Readonly<{ storeCode: string; hardwareId: string; terminalName?: string }>): Promise<DeviceRegisterResponse>;
  previewActivationCode?(input: Readonly<{ activationCode: string }>): Promise<DeviceActivationPreviewResponse>;
  redeemActivationCode?(
    input: Readonly<{ activationCode: string; hardwareId: string; terminalName?: string }>,
    options?: Readonly<{ recoveryOnly?: boolean }>,
  ): Promise<DeviceActivationRedeemResponse>;
  rebindActivationCode?(input: Readonly<{ activationCode: string; terminalName?: string }>): Promise<DeviceActivationRebindResponse>;
  verify(input: Readonly<{ deviceCode: string; storeCode: string; hardwareId: string; terminalName?: string }>): Promise<DeviceVerifyResponse>;
  reregister(input: Readonly<{ targetStoreCode: string; hardwareId: string; terminalName?: string }>): Promise<DeviceReregisterResponse>;
}

export class DeviceSessionCoordinator {
  private readonly lockStore: DeviceLockStore;
  private readonly pendingRegistration: PendingDeviceRegistrationStore;
  private readonly presentationStore: DevicePresentationStore;
  private readonly pendingActivation: PendingDeviceActivationCodeStore;
  private readonly activationApiPartition: string | null;
  // 每次发起会改变设备授权的操作均递增；迟到响应只能返回当前结果，不能回写持久化状态。
  private operationGeneration = 0;
  // 安全存储写入不可取消；同一队列确保已启动旧写入先结束，由新操作在队尾完成最终覆盖。
  private mutationQueue: Promise<void> = Promise.resolve();
  // 一次性开通码变更覆盖完整提交链，poll 不得在途中抢占 generation 或重放开通码。
  private activationMutationDepth = 0;
  private state: DeviceSessionState = { status: "unregistered" };

  public constructor(
    private readonly api: DeviceSessionApi,
    private readonly installation: InstallationIdentityStore,
    private readonly credentials: DeviceCredentialStore,
    lockStore?: DeviceLockStore,
    pendingRegistration?: PendingDeviceRegistrationStore,
    presentationStore?: DevicePresentationStore,
    pendingActivation?: PendingDeviceActivationCodeStore,
    activationApiPartition?: string,
    private readonly apiPartitionGuard?: DeviceRegistrationApiPartitionGuard,
  ) {
    this.lockStore = lockStore ?? new DeviceLockStore(credentials.secureStore);
    this.pendingRegistration = pendingRegistration ?? new PendingDeviceRegistrationStore(credentials.secureStore);
    this.presentationStore =
      presentationStore ??
      new DevicePresentationStore(credentials.secureStore);
    this.pendingActivation =
      pendingActivation ??
      new PendingDeviceActivationCodeStore(credentials.secureStore);
    // 兼容既有组合/测试构造：省略时只允许默认生产分区；运行时切换地址必须显式注入。
    this.activationApiPartition = normalizePendingDeviceActivationApiPartition(
      activationApiPartition ?? "https://hotbargain.vip/pos-api",
    );
  }

  public async register(input: Readonly<{ storeCode: string; terminalName?: string }>): Promise<DeviceSessionState> {
    const generation = this.beginOperation();
    this.state = { status: "registering", storeCode: input.storeCode };
    const hardwareId = await this.installation.getOrCreate();
    if (!this.isCurrentOperation(generation)) {
      return this.state;
    }
    return this.updateState(this.resolve(this.api.register({ ...input, hardwareId }), "register", generation), generation);
  }

  public async previewActivationCode(
    activationCode: string,
  ): Promise<DeviceActivationPreviewResponse> {
    await this.assertActivationPreviewAllowed();
    const previewActivationCode = this.api.previewActivationCode;
    if (!previewActivationCode) {
      throw new Error("Device activation preview is unavailable.");
    }
    const parsed = parseDeviceActivationCode(activationCode);
    if (!parsed) throw new TypeError("Device activation code is invalid.");
    return previewActivationCode.call(this.api, {
      activationCode: parsed,
    });
  }

  public redeemActivationCode(input: Readonly<{
    activationCode: string;
    terminalName?: string;
  }>): Promise<DeviceSessionState> {
    return this.runActivationMutation(() => this.redeemActivationCodeCore(input));
  }

  private async redeemActivationCodeCore(input: Readonly<{
    activationCode: string;
    terminalName?: string;
  }>): Promise<DeviceSessionState> {
    await this.assertActivationAllowed();
    const redeemActivationCode = this.api.redeemActivationCode;
    if (!redeemActivationCode) {
      throw new Error("Device activation redemption is unavailable.");
    }
    const hardwareId = await this.installation.getOrCreate();
    return this.withPendingActivationCode(
      input.activationCode,
      "redeem",
      hardwareId,
      async (normalized) => {
        const generation = this.beginOperation();
        this.state = { status: "registering" };
        if (!this.isCurrentOperation(generation)) return this.state;
        return this.updateState(
          this.resolve(
            redeemActivationCode.call(this.api, {
              activationCode: normalized,
              hardwareId,
              ...(input.terminalName ? { terminalName: input.terminalName } : {}),
            }),
            "activate",
            generation,
          ),
          generation,
        );
      },
    );
  }

  public rebindActivationCode(input: Readonly<{
    activationCode: string;
    terminalName?: string;
  }>, onCredentialsCommitted?: () => void): Promise<DeviceSessionState> {
    return this.runActivationMutation(() =>
      this.rebindActivationCodeCore(input, onCredentialsCommitted),
    );
  }

  private async rebindActivationCodeCore(input: Readonly<{
    activationCode: string;
    terminalName?: string;
  }>, onCredentialsCommitted?: () => void): Promise<DeviceSessionState> {
    await this.assertRebindAllowed();
    const rebindActivationCode = this.api.rebindActivationCode;
    if (!rebindActivationCode) {
      throw new Error("Device activation rebind is unavailable.");
    }
    const hardwareId = await this.installation.getOrCreate();
    return this.withPendingActivationCode(
      input.activationCode,
      "rebind",
      hardwareId,
      (normalized) => {
        const generation = this.beginOperation();
        this.state = { status: "reregistering" };
        return this.updateState(
          this.resolve(
            rebindActivationCode.call(this.api, {
              activationCode: normalized,
              ...(input.terminalName ? { terminalName: input.terminalName } : {}),
            }),
            "rebind",
            generation,
            onCredentialsCommitted,
          ),
          generation,
        );
      },
    );
  }

  public restorePendingActivationCode(): Promise<string | null> {
    return this.pendingActivation.load();
  }

  public async hasActivationRecoveryRisk(): Promise<boolean> {
    try {
      return (await this.pendingActivation.loadPending()) !== null;
    } catch {
      return true;
    }
  }

  public async verify(input: Readonly<{ deviceCode: string; storeCode: string; terminalName?: string }>): Promise<DeviceSessionState> {
    const generation = this.beginOperation();
    this.state = { status: "verifying", deviceCode: input.deviceCode, storeCode: input.storeCode };
    const hardwareId = await this.installation.getOrCreate();
    if (!this.isCurrentOperation(generation)) {
      return this.state;
    }
    return this.updateState(this.resolve(this.api.verify({ ...input, hardwareId }), "verify", generation), generation);
  }

  public async poll(): Promise<DeviceSessionState> {
    if (this.activationMutationDepth > 0) return this.state;
    const generation = this.beginOperation();
    const recovered = await this.tryRecoverPendingActivation(generation);
    if (recovered) return recovered;
    const current = await this.credentials.load();
    if (!this.isCurrentOperation(generation)) {
      return this.state;
    }
    if (current) {
      return this.verify({ deviceCode: current.deviceCode, storeCode: current.storeCode });
    }
    const pending = await this.pendingRegistration.load();
    if (!this.isCurrentOperation(generation)) {
      return this.state;
    }
    if (pending) {
      return this.verify(pending);
    }
    return this.updateState(Promise.resolve({ status: "unregistered" }), generation);
  }

  public async reregister(input: Readonly<{ targetStoreCode: string; terminalName?: string }>): Promise<DeviceSessionState> {
    const generation = this.beginOperation();
    this.state = { status: "reregistering", storeCode: input.targetStoreCode };
    const hardwareId = await this.installation.getOrCreate();
    if (!this.isCurrentOperation(generation)) {
      return this.state;
    }
    return this.updateState(this.resolve(this.api.reregister({ ...input, hardwareId }), "reregister", generation), generation);
  }

  public getState(): DeviceSessionState {
    return this.state;
  }

  public async getRequestHeaders(): Promise<Readonly<Record<string, string>> | null> {
    const credentials = await this.getTransportCredentials();
    if (!credentials) {
      return null;
    }
    return {
      Authorization: `Bearer ${credentials.authorizationCode}`,
      "X-HBPOS-Device-Code": credentials.deviceCode,
      "X-HBPOS-Store-Code": credentials.storeCode,
      "X-HBPOS-Hardware-Id": credentials.hardwareId
    };
  }

  /** UI/feature 可读取的安全设备身份；不会返回授权码或安装硬件标识。 */
  public async getDeviceIdentity(): Promise<Readonly<{
    deviceCode: string;
    storeCode: string;
  }> | null> {
    const credentials = await this.getTransportCredentials();
    return credentials
      ? {
          deviceCode: credentials.deviceCode,
          storeCode: credentials.storeCode,
        }
      : null;
  }

  public async getDevicePresentation(): Promise<DevicePresentation | null> {
    try {
      const credentials = await this.getTransportCredentials();
      if (!credentials) {
        return null;
      }
      const cached = await this.presentationStore.load();
      const matchesCredentials =
        cached?.deviceCode === credentials.deviceCode &&
        cached.storeCode === credentials.storeCode;
      return {
        deviceCode: credentials.deviceCode,
        storeCode: credentials.storeCode,
        storeName: matchesCredentials ? cached.storeName : null,
      };
    } catch {
      // 损坏凭据或安全存储读取失败时，公开展示身份同样失败关闭。
      return null;
    }
  }

  public async getTransportCredentials(): Promise<Readonly<{
    authorizationCode: string;
    deviceCode: string;
    storeCode: string;
    hardwareId: string;
  }> | null> {
    if (await this.lockStore.isLocked()) {
      return null;
    }
    const credentials = await this.credentials.load();
    const installationId = await this.installation.getOrCreate();
    return credentials && credentials.hardwareId === installationId ? credentials : null;
  }

  /** 认证中间件收到明确的设备 403 时调用；保留设备号以支持后续在线重新验证。 */
  public async lockFromAuthorizationFailure(reason: string): Promise<void> {
    const generation = this.beginOperation();
    await this.mutateCurrentOperation(generation, async () => {
      await this.lockStore.lock(reason);
      if (this.isCurrentOperation(generation)) {
        this.state = { status: "disabled", message: reason };
      }
    });
  }

  private async resolve(
    responsePromise: Promise<DeviceRegisterResponse | DeviceVerifyResponse | DeviceReregisterResponse>,
    operation: "register" | "verify" | "reregister" | "activate" | "rebind",
    generation: number,
    onCredentialsCommitted?: () => void,
  ): Promise<DeviceSessionState> {
    const response = await responsePromise;
    if (!this.isCurrentOperation(generation)) {
      return this.state;
    }
    return (await this.mutateCurrentOperation(generation, async () => {
      const activationOperation = operation === "activate" || operation === "rebind";
      if (activationOperation && (typeof response !== "object" || response === null)) {
        throw new DeviceActivationOutcomeUnknownError(
          "Device activation response is missing.",
        );
      }
      const deviceCode = response.deviceCode?.trim() ?? "";
      const storeCode = response.storeCode?.trim() ?? "";
      if (activationOperation) {
        if (response.isAllowed === false) {
          if (!activationRejectionReasonCode(response)) {
            throw new DeviceActivationOutcomeUnknownError(
              "Device activation rejection has no approved public reason code.",
            );
          }
          // 只有带稳定 reasonCode 的业务拒绝才证明本次码可安全清除。
          await this.pendingActivation.clear();
          if (!this.isCurrentOperation(generation)) return this.state;
        } else if (
          response.isAllowed !== true ||
          !activationSuccessReasonCode(response) ||
          !response.authorizationCode ||
          !deviceCode ||
          !storeCode
        ) {
          throw new DeviceActivationOutcomeUnknownError(
            "Device activation response is incomplete.",
          );
        }
      }
      if (response.isAllowed && response.authorizationCode && deviceCode && storeCode) {
        const hardwareId = await this.installation.getOrCreate();
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
        const previousCredentials =
          operation === "reregister" || operation === "rebind"
            ? await this.credentials.load()
            : null;
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
        try {
          await this.credentials.save({
            deviceCode,
            storeCode,
            hardwareId,
            authorizationCode: response.authorizationCode
          });
        } catch (error: unknown) {
          if (activationOperation) {
            // 服务端可能已原子消费开通码；本机凭据未确认落盘时必须保留临时码以便幂等恢复。
            throw new DeviceActivationOutcomeUnknownError(
              "Device activation credential save failed.",
            );
          }
          throw error;
        }
        if (operation === "rebind") {
          // 凭据落盘是不可逆提交点；后续清理失败或 generation 变化不得把它误报为未提交。
          onCredentialsCommitted?.();
        }
        if ((operation === "reregister" || operation === "rebind") && previousCredentials) {
          // 中文注释：save 已不可逆成功，即使本操作此刻变 stale 也必须广播实际 previous/current，generation 只阻止后续 UI 状态写入。
          publishDeviceScopeChange({
            previous: {
              deviceCode: previousCredentials.deviceCode,
              storeCode: previousCredentials.storeCode,
            },
            current: { deviceCode, storeCode },
          });
        }
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
        await this.updatePresentationBestEffort(
          deviceCode,
          storeCode,
          response.storeName,
          generation,
        );
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
        await this.pendingRegistration.clear();
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
        if (operation === "activate" || operation === "rebind") {
          // 设备凭据已耐久化后才删除临时开通码，响应丢失时仍可对同硬件幂等重试。
          await this.pendingActivation.clear();
          if (!this.isCurrentOperation(generation)) return this.state;
        }
        await this.lockStore.unlock();
        return { status: "authorized", deviceCode, storeCode };
      }

      const existingCredentials = await this.credentials.load();
      if (!this.isCurrentOperation(generation)) {
        return this.state;
      }
      const isPending = response.deviceStatus === -1;
      if (isPending && deviceCode && storeCode) {
        await this.pendingRegistration.save({ deviceCode, storeCode });
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
      }

      // 已授权设备的 verify 收到任何明确的非允许响应（硬件不匹配也可能仍返回启用状态）都必须停用出站凭据。
      const mustLockExistingDevice = operation === "verify" && existingCredentials !== null;
      const disabled = response.deviceStatus === 0 || response.deviceStatus === 2 || mustLockExistingDevice;
      if (disabled) {
        await this.lockStore.lock(response.message ?? "Device is disabled or no longer authorized.");
        if (!this.isCurrentOperation(generation)) {
          return this.state;
        }
        return stateWithDetails("disabled", deviceCode, storeCode, response.message);
      }

      return stateWithDetails(
        isPending ? "pending-approval" : "denied",
        deviceCode,
        storeCode,
        response.message
      );
    })) ?? this.state;
  }

  private async updatePresentationBestEffort(
    deviceCode: string,
    storeCode: string,
    storeName: string | null | undefined,
    generation: number,
  ): Promise<void> {
    try {
      if (!this.isCurrentOperation(generation)) {
        return;
      }
      const normalizedStoreName = storeName?.trim() ?? "";
      if (normalizedStoreName) {
        await this.presentationStore.save({
          deviceCode,
          storeCode,
          storeName: normalizedStoreName,
        });
        return;
      }

      const current = await this.presentationStore.load();
      if (!this.isCurrentOperation(generation)) {
        return;
      }
      if (
        current &&
        (current.deviceCode !== deviceCode ||
          current.storeCode !== storeCode)
      ) {
        await this.presentationStore.clear();
      }
    } catch {
      // 展示缓存永远不能改变凭据已保存后的授权结果。
    }
  }

  private async assertActivationAllowed(): Promise<void> {
    if (await this.lockStore.isLocked()) {
      throw new Error("Device registration is locked.");
    }
    if (await this.credentials.load()) {
      throw new Error("This installation is already registered.");
    }
  }

  private async assertActivationPreviewAllowed(): Promise<void> {
    if (await this.lockStore.isLocked()) {
      throw new Error("Device registration is locked.");
    }
  }

  private async assertRebindAllowed(): Promise<void> {
    if (!(await this.getTransportCredentials())) {
      throw new Error("Registered device credentials are required for rebind.");
    }
  }

  private async withPendingActivationCode<T>(
    activationCode: string,
    mode: "redeem" | "rebind",
    hardwareId: string,
    operation: (normalized: string) => Promise<T>,
  ): Promise<T> {
    const apiPartition = this.requireActivationApiPartition();
    // staging 尚未触达服务端；任何本地保存、读取或确认失败都必须原样保留
    // 既有 pending，不能误套用“确定性远端拒绝可清理”的规则。
    await this.pendingActivation.save(activationCode, mode, {
      apiPartition,
      hardwareId,
    });
    const pending = await this.pendingActivation.loadPending();
    if (
      !pending ||
      pending.apiPartition !== apiPartition ||
      pending.hardwareId !== hardwareId
    ) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation intent could not be confirmed.",
      );
    }
    try {
      return await operation(pending.activationCode);
    } catch (error: unknown) {
      if (!isActivationOutcomeUncertain(error)) {
        await this.clearPendingActivationBestEffort();
      }
      throw error;
    }
  }

  private async tryRecoverPendingActivation(
    generation: number,
  ): Promise<DeviceSessionState | null> {
    const pending = await this.pendingActivation.loadPending();
    if (!pending) return null;
    const hardwareId = await this.installation.getOrCreate();
    const apiPartition = this.requireActivationApiPartition();
    if (
      pending.apiPartition !== apiPartition ||
      pending.hardwareId !== hardwareId
    ) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation recovery intent does not match this API partition or hardware.",
      );
    }
    const { activationCode, mode } = pending;
    if (mode === "rebind") {
      const rebindActivationCode = this.api.rebindActivationCode;
      if (rebindActivationCode && !(await this.lockStore.isLocked())) {
        try {
          const response = await rebindActivationCode.call(this.api, {
            activationCode,
          });
          if (!this.isCurrentOperation(generation)) return this.state;
          if (!shouldRecoverRebindAnonymously(response)) {
            if (
              response.isAllowed === false &&
              activationRejectionReasonCode(response)
            ) {
              await this.clearPendingActivationBestEffort();
              return null;
            }
            if (
              !response.isAllowed ||
              !response.authorizationCode ||
              !response.deviceCode?.trim() ||
              !response.storeCode?.trim()
            ) {
              throw new DeviceActivationOutcomeUnknownError(
                "Device activation rebind recovery response is incomplete.",
              );
            }
            this.state = { status: "reregistering" };
            return this.updateState(
              this.resolve(Promise.resolve(response), "rebind", generation),
              generation,
            );
          }
        } catch (error: unknown) {
          if (!shouldRecoverRebindAnonymously(error)) {
            if (isActivationOutcomeUncertain(error)) throw error;
            await this.clearPendingActivationBestEffort();
            if (isDefinitiveRemoteActivationRejection(error)) return null;
            throw error;
          }
        }
      }
    }
    const redeemActivationCode = this.api.redeemActivationCode;
    if (!redeemActivationCode) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation recovery is unavailable.",
      );
    }

    let response: DeviceActivationRedeemResponse;
    try {
      if (!this.isCurrentOperation(generation)) return this.state;
      response = await redeemActivationCode.call(
        this.api,
        { activationCode, hardwareId },
        mode === "rebind" ? { recoveryOnly: true } : undefined,
      );
    } catch (error: unknown) {
      if (mode === "rebind") {
        if (isActivationOutcomeUncertain(error)) throw error;
        throw new DeviceActivationOutcomeUnknownError(
          "Device activation rebind recovery was not confirmed.",
        );
      }
      if (isActivationOutcomeUncertain(error)) throw error;
      await this.clearPendingActivationBestEffort();
      if (isDefinitiveRemoteActivationRejection(error)) return null;
      throw error;
    }

    if (typeof response !== "object" || response === null) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation recovery response is missing.",
      );
    }
    if (mode === "rebind") {
      if (
        response.isAllowed === false &&
        activationRejectionReasonCode(response)
      ) {
        // recoveryOnly 的明确拒绝码（主要是 NOT_AVAILABLE）既可能表示“码未被本硬件消费”，
        // 也可能表示“已被本硬件消费、但新注册已停用或门店不可用”，不能直接据此清除 pending。
        return this.resolveRebindRecoveryRejection(generation, hardwareId);
      }
      if (
        response.isAllowed !== true ||
        !hasExactActivationReasonCode(response, "ACTIVATION_RECOVERED")
      ) {
        throw new DeviceActivationOutcomeUnknownError(
          "Device activation recovery response must be ACTIVATION_RECOVERED.",
        );
      }
    }
    if (
      response.isAllowed === false &&
      activationRejectionReasonCode(response)
    ) {
      await this.clearPendingActivationBestEffort();
      return null;
    }
    if (
      !response.isAllowed ||
      !response.authorizationCode ||
      !response.deviceCode?.trim() ||
      !response.storeCode?.trim()
    ) {
      // 服务端声称允许但未返回完整新凭据，结果不可安全判定，保留码等待恢复。
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation recovery response is incomplete.",
      );
    }
    this.state = {
      status: mode === "rebind" ? "reregistering" : "registering",
    };
    return this.updateState(
      this.resolve(
        Promise.resolve(response),
        mode === "rebind" ? "rebind" : "activate",
        generation,
      ),
      generation,
    );
  }

  /**
   * 换绑 pending 的匿名恢复被明确拒绝后，用旧凭据 verify 判定换绑是否在本硬件生效。
   *
   * 服务端 rebind 在同一事务里消费开通码并停用旧注册（DisableSourceRegistrationAsync），
   * 且要求本硬件没有其他启用注册；因此旧设备号 + 旧门店 + 本硬件在 verify 中仍为启用，
   * 即证明这次换绑没有在本硬件生效，可以安全清除 pending，设备继续使用旧凭据。
   * verify 未启用（旧注册已停用/待审批）时无法排除“已消费但新注册不可恢复”，保留 pending，
   * 并按普通 verify 结果锁机或展示状态，避免启动反复进入 failed。
   * 网络、5xx 等异常原样抛出且保留 pending，由启动闸门按“待恢复、需联网”处理。
   */
  private async resolveRebindRecoveryRejection(
    generation: number,
    hardwareId: string,
  ): Promise<DeviceSessionState> {
    const previous = await this.credentials.load();
    if (!this.isCurrentOperation(generation)) return this.state;
    if (!previous) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation rebind recovery could not confirm the previous device binding.",
      );
    }
    this.state = {
      status: "verifying",
      deviceCode: previous.deviceCode,
      storeCode: previous.storeCode,
    };
    const verification = await this.api.verify({
      deviceCode: previous.deviceCode,
      storeCode: previous.storeCode,
      hardwareId,
    });
    if (!this.isCurrentOperation(generation)) return this.state;
    if (
      typeof verification !== "object" ||
      verification === null ||
      typeof verification.isAllowed !== "boolean"
    ) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation rebind recovery could not confirm the previous device binding.",
      );
    }
    if (verification.isAllowed) {
      const confirmsPreviousBinding =
        Boolean(verification.authorizationCode) &&
        verification.deviceCode?.trim() === previous.deviceCode &&
        verification.storeCode?.trim() === previous.storeCode;
      if (!confirmsPreviousBinding) {
        // 允许但身份或授权码不完整，不能作为“旧注册仍启用”的证据。
        throw new DeviceActivationOutcomeUnknownError(
          "Device activation rebind recovery could not confirm the previous device binding.",
        );
      }
      await this.clearPendingActivationBestEffort();
      if (!this.isCurrentOperation(generation)) return this.state;
    }
    return this.updateState(
      this.resolve(Promise.resolve(verification), "verify", generation),
      generation,
    );
  }

  private async clearPendingActivationBestEffort(): Promise<void> {
    try {
      await this.pendingActivation.clear();
    } catch {
      // 原始确定性失败优先返回；损坏或残留值下次 load 仍会严格校验并失败关闭。
    }
  }

  private requireActivationApiPartition(): string {
    if (!this.activationApiPartition) {
      throw new DeviceActivationOutcomeUnknownError(
        "Device activation API partition is unavailable.",
      );
    }
    return this.activationApiPartition;
  }

  private async runActivationMutation<T>(operation: () => Promise<T>): Promise<T> {
    this.activationMutationDepth += 1;
    try {
      const lease = this.apiPartitionGuard?.beginMutation();
      try {
        return await operation();
      } finally {
        lease?.release();
      }
    } finally {
      this.activationMutationDepth -= 1;
    }
  }

  private beginOperation(): number {
    this.operationGeneration += 1;
    return this.operationGeneration;
  }

  private isCurrentOperation(generation: number): boolean {
    return generation === this.operationGeneration;
  }

  private async mutateCurrentOperation<T>(
    generation: number,
    mutation: () => Promise<T>,
  ): Promise<T | undefined> {
    const previous = this.mutationQueue;
    let release!: () => void;
    this.mutationQueue = new Promise<void>((resolve) => {
      release = resolve;
    });

    await previous;
    try {
      return this.isCurrentOperation(generation)
        ? await mutation()
        : undefined;
    } finally {
      release();
    }
  }

  private async updateState(next: Promise<DeviceSessionState>, generation: number): Promise<DeviceSessionState> {
    const resolved = await next;
    if (this.isCurrentOperation(generation)) {
      this.state = resolved;
    }
    return this.state;
  }
}

function isActivationOutcomeUncertain(error: unknown): boolean {
  if (error instanceof PendingDeviceActivationConflictError) return true;
  if (error instanceof DeviceActivationOutcomeUnknownError) return true;
  if (!(error instanceof HbposApiError)) return false;
  if (error.kind === "http") {
    return error.status !== 400;
  }
  // Envelope 与任何 transport 分类都不能证明服务端未消费一次性码。
  return true;
}

function isDefinitiveRemoteActivationRejection(error: unknown): boolean {
  return (
    error instanceof HbposApiError &&
    error.kind === "http" &&
    error.status === 400
  );
}

class DeviceActivationOutcomeUnknownError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "DeviceActivationOutcomeUnknownError";
  }
}

const activationSuccessReasonCodes: ReadonlySet<string> = new Set([
  "ACTIVATED",
  "ACTIVATION_RECOVERED",
]);

const activationRejectionReasonCodes: ReadonlySet<string> = new Set([
  "ACTIVATION_CODE_REQUIRED",
  "ACTIVATION_CODE_NOT_AVAILABLE",
  "ACTIVATION_PLATFORM_MISMATCH",
  "STORE_UNAVAILABLE",
  "DEVICE_ALREADY_REGISTERED",
  "TARGET_STORE_UNCHANGED",
  "DEVICE_STATE_CONFLICT",
]);

function activationSuccessReasonCode(value: unknown): string | null {
  return approvedActivationReasonCode(value, activationSuccessReasonCodes);
}

function activationRejectionReasonCode(value: unknown): string | null {
  return approvedActivationReasonCode(value, activationRejectionReasonCodes);
}

function approvedActivationReasonCode(
  value: unknown,
  approved: ReadonlySet<string>,
): string | null {
  if (typeof value !== "object" || value === null) return null;
  const reasonCode = (value as Readonly<{ reasonCode?: unknown }>).reasonCode;
  if (typeof reasonCode !== "string") return null;
  return approved.has(reasonCode) ? reasonCode : null;
}

function hasExactActivationReasonCode(value: unknown, expected: string): boolean {
  return (
    typeof value === "object" &&
    value !== null &&
    (value as Readonly<{ reasonCode?: unknown }>).reasonCode === expected
  );
}

function shouldRecoverRebindAnonymously(
  value: DeviceActivationRebindResponse | unknown,
): boolean {
  if (value instanceof HbposApiError) {
    return (
      (value.kind === "http" &&
        (value.status === 401 || value.status === 403)) ||
      (value.kind === "envelope" && value.code === "DEVICE_DISABLED")
    );
  }
  if (typeof value !== "object" || value === null) return false;
  const response = value as Readonly<{
    isAllowed?: unknown;
    reasonCode?: unknown;
  }>;
  return (
    response.isAllowed === false && response.reasonCode === "DEVICE_DISABLED"
  );
}

function publishDeviceScopeChange(change: DeviceScopeChange): void {
  for (const listener of [...deviceScopeChangeListeners]) {
    try {
      listener(change);
    } catch {
      // 中文注释：凭据写入已完成，监听方故障不能回滚新设备授权或阻断其他安全订阅者。
    }
  }
}

function stateWithDetails(
  status: DeviceSessionStatus,
  deviceCode: string,
  storeCode: string,
  message: string | null | undefined
): DeviceSessionState {
  return {
    status,
    ...(deviceCode ? { deviceCode } : {}),
    ...(storeCode ? { storeCode } : {}),
    ...(message ? { message } : {})
  };
}
