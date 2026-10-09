import * as Application from "expo-application";
import Constants from "expo-constants";
import * as Crypto from "expo-crypto";
import * as ExpoNetwork from "expo-network";
import * as Updates from "expo-updates";
import { Linking } from "react-native";

import { AppUpdateCoordinator } from "../../features/app-updates/app-update-coordinator";
import { AppUpdateOrchestrator } from "../../features/app-updates/app-update-orchestrator";
import {
  type AppUpdateRecoveryRuntimePort,
  createAppUpdateRecoveryRuntimeSnapshot,
} from "../../features/app-updates/app-update-recovery-contract";
import { ExpoOtaUpdatePort } from "../../features/app-updates/expo-ota-update-port";
import { HbposPosIpadOtaUpdateApi } from "../../features/app-updates/hbpos-pos-ipad-ota-update-api";
import { HbposPosIpadUpdateApi } from "../../features/app-updates/hbpos-pos-ipad-update-api";
import {
  OtaUpdateCoordinator,
  shouldCheckOtaPolicy,
} from "../../features/app-updates/ota-update-coordinator";
import { UpdateTransitionLeaseCoordinator } from "../../features/app-updates/update-transition-lease-coordinator";
import {
  HbposAttendanceSecurityApi,
  HbposOperationAuditReadApi,
} from "../../features/attendance-audit";
import {
  CustomerDisplayAdvertisementCache,
} from "../../features/customer-display";
import { HbposCatalogFileSync } from "../../features/catalog/catalog-file-sync";
import { ExpoCatalogFileStore } from "../../features/catalog/expo-catalog-file-store";
import type { SettingsPaymentSettingsInput } from "../../features/settings";
import {
  createAxiosHbposTransport,
  createFreshCashierAxiosHbposTransport,
  type HbposAuthenticationFailureHandler,
  type HbposRequestCredentialProvider,
  type HbposRequestCredentials,
} from "../api/axios-transport";
import {
  HbposCashierApi,
  HbposDeviceApi,
} from "../api/hbpos-api";
import type { ExternalCustomerDisplayPort } from "../contracts/external-display";
import { createExpoKeychainDatabaseKeyProvider } from "../db/expo-keychain-database-key-provider";
import { ExpoSqliteDriver } from "../db/expo-sqlite-driver";
import { PosDatabase } from "../db/pos-database";
import {
  ApplicationLogActorBinding,
  ApplicationLogger,
  ApplicationLogRuntime,
  ApplicationLogUploader,
  resolveApplicationLogCenterConfig,
} from "../logging/application-log";
import { businessStartupClock } from "../performance/business-startup-clock";
import {
  ClientMetricRuntime,
  clientMetrics,
} from "../performance/client-metrics";
import { createExpoClientMetricRuntime } from "../performance/expo-client-metric-runtime";
import {
  createExpoAttendanceSecurityAdapter,
} from "../peripherals/attendance-security/native";
import {
  customerDisplayAdvertisementCacheRootUri,
  customerDisplayProductImageCacheRootUri,
  ExpoAdvertisementFileSystem,
  ExpoProductImageFileSystem,
  externalDisplay,
} from "../peripherals/customer-display/native";
import { HidScannerRouter } from "../peripherals/scanner";
import { SecurityApiCredentialProvider } from "../security/api-credential-provider";
import { CashierAuthenticationService } from "../security/cashier-authentication";
import { CashierSessionInvalidationBus } from "@hb/pos-domain/core/security/cashier-session-invalidation";
import { DeviceRegistrationResetCoordinator } from "../security/device-registration-reset";
import { DeviceRegistrationApiPartitionGuard } from "../security/device-registration-api-partition-guard";
import { DeviceSessionCoordinator } from "../security/device-session";
import { ExpoSecureStoreAdapter } from "../security/expo-secure-store";
import {
  mergePosPaymentPublicConfiguration,
  normalizeTrustedApiOrigins,
  PosPublicRuntimeConfigurationStore,
} from "../security/pos-public-runtime-configuration";
import {
  CashierAuthorizationStore,
  CashierSessionCache,
  DeviceCredentialStore,
  DeviceLockStore,
  DevicePresentationStore,
  DeviceRegistrationResetMarkerStore,
  InstallationIdentityStore,
  PendingDeviceActivationCodeStore,
  PendingDeviceRegistrationStore,
} from "../security/secure-storage";
import { SensitivePayloadEncryptor } from "../security/sensitive-payload-encryptor";

import { createEmergencyCashierRuntime } from "./emergency-cashier-runtime";
import { createExpoAttendanceRuntimeConfiguration } from "./expo-attendance-runtime-configuration";
import { createLazyExpoPrinterAdapter } from "./expo-printer-adapter";
import {
  createSettingsApiHealthProbe,
  reloadSettingsRuntimeTerminally,
  reregisterSettingsDevice,
  settingsAppUpdateSnapshot,
  settingsPaymentConfiguration,
} from "./expo-settings-configuration";
import { readCurrentUpdateGroupId } from "./expo-update-identity";
import { resolveExpoUpdateRuntimeVersion } from "./expo-update-runtime-version";
import { resolveLocalDeviceState } from "./local-device-state";
import { createPaymentProviderRuntimeBootstrap } from "./payment-provider-runtime-bootstrap";
import {
  configuredLinklyEnvironment,
  type PosPaymentPublicExtra,
} from "./payment-runtime-config";
import {
  PosRuntimeController,
  type PosRuntimeServices,
} from "./pos-runtime";
import { PreloginServerConnectionControl } from "./prelogin-server-connection-control";
import {
  createProductionPosRuntimeServices,
  type PosCashierSessionRuntimeService,
  type ProductionPosRuntimeServices,
} from "./production-pos-service-composition";
import {
  createPublicCashierInvalidation,
  type PosCashierInvalidationRuntimeService,
} from "./public-cashier-invalidation";
import {
  createPublicDeviceSession,
  type PosDeviceSessionRuntimeService,
} from "./public-device-session";
import { resolveHbposApiUrl } from "./runtime-config";
import { HbposSettingsLinklySetupApi } from "./settings-linkly-setup-api";
import { HbposSettingsPaymentTestApi } from "./settings-payment-test-api";
import { SettingsScannerTestCoordinator } from "./settings-scanner-test";
import { HbposSettingsSquareSetupApi } from "./settings-square-setup-api";
import { resolveStartupDeviceGate } from "./startup-device-gate";

const POS_DATABASE_NAME = "hb-pos-ipad.db";
const POS_APP_ID = "com.hbweb.posipad";

type HbposExtraConfig = Readonly<{
  hbpos?: Readonly<{
    apiBaseUrl?: string;
    automaticOtaChecks?: boolean;
    buildProfile?: string;
    businessTimeZone?: string;
    logCenter?: Readonly<{
      enabled?: boolean;
      ingestUrl?: string;
      writeKey?: string;
      environment?: string;
    }>;
    trustedApiOrigins?: readonly string[];
  }>;
  payments?: PosPaymentPublicExtra;
}>;

export type ExpoPosUpdateIdentity = Readonly<{
  runtimeVersion: string;
  updateId: string | null;
  isEmbeddedLaunch: boolean;
}>;

export type ExpoPosRuntimeServices = PosRuntimeServices &
  ProductionPosRuntimeServices &
  Readonly<{
    apiBaseUrl: string;
    deviceSession: PosDeviceSessionRuntimeService;
    cashierSessionInvalidation: PosCashierInvalidationRuntimeService;
    externalDisplay: ExternalCustomerDisplayPort;
    appUpdates: AppUpdateOrchestrator;
    appUpdateRecovery: AppUpdateRecoveryRuntimePort;
    serverConnection: PreloginServerConnectionControl;
    scanner: Readonly<{ router: HidScannerRouter }>;
    applicationLog: ApplicationLogRuntime;
    updateIdentity: ExpoPosUpdateIdentity;
  }>;

type ExpoSettingsDevicePresentation = Readonly<{
  deviceCode: string;
  storeCode: string;
  storeName: string;
  terminalName: string;
}>;

export async function readSettingsDevicePresentation(
  deviceSession: Pick<
    PosDeviceSessionRuntimeService,
    "getDevicePresentation"
  >,
): Promise<ExpoSettingsDevicePresentation> {
  const presentation = await deviceSession.getDevicePresentation();
  if (!presentation) {
    throw new Error("SETTINGS_DEVICE_IDENTITY_REQUIRED");
  }
  return Object.freeze({
    deviceCode: presentation.deviceCode,
    storeCode: presentation.storeCode,
    storeName: presentation.storeName ?? "",
    terminalName: "",
  });
}

/**
 * 收银员条码和会话票据只留在收银域。日志仅在可信登录成功后接收身份投影，
 * 登录失败、锁屏、401/403 都会先清空旧投影，避免把 Alice 的日志归给 Bob。
 */
export function bindCashierSessionToApplicationLog(
  cashierSession: PosCashierSessionRuntimeService,
  actor: ApplicationLogActorBinding,
): PosCashierSessionRuntimeService {
  return Object.freeze({
    signIn: async (userBarcode) => {
      actor.clear();
      const summary = await cashierSession.signIn(userBarcode);
      actor.bind({
        userId: summary.userGuid ?? summary.cashierId,
        userName: summary.cashierName,
      });
      return summary;
    },
  });
}

/** 初始化异常必须保持原始抛出语义；这里只在关闭 SQLite 前尽力留下诊断。 */
export async function recordRuntimeInitializationFailure(
  applicationLog: Pick<ApplicationLogRuntime, "logger" | "shutdown"> | null,
  error: unknown,
  closeDatabase: () => Promise<void>,
): Promise<void> {
  try {
    await applicationLog?.logger.record({
      level: "Critical",
      message: "POS runtime initialization failed.",
      category: "runtime.initialization",
      error,
    });
  } catch {
    // 日志旁路自身故障不能覆盖初始化异常或阻止 SQLite 收尾。
  }
  try {
    await applicationLog?.shutdown();
  } catch {
    // 保留既有 database.close 错误与上层恢复语义。
  }
  try {
    await closeDatabase();
  } catch {
    // close 同样属于失败收尾；caller 必须最终重抛原始初始化异常。
  }
}

/** 初始化失败的收尾必须优先释放组合根订阅，并无条件关闭 SQLite。 */
export async function shutdownCompositionBeforeDatabaseClose(
  shutdownComposition: (() => Promise<void>) | null,
  closeDatabase: () => Promise<void>,
): Promise<void> {
  try {
    await shutdownComposition?.();
  } finally {
    // 中文注释：后台目录清理失败也不能把打开的 SQLite 句柄遗留给下一次启动。
    await closeDatabase();
  }
}

type ExpoPosRuntimeShutdownDependencies = Readonly<{
  beforeShutdown?: readonly (() => void | Promise<void>)[];
  sync: Pick<ProductionPosRuntimeServices["sync"], "shutdown">;
  applicationLog: Pick<ApplicationLogRuntime, "logger" | "shutdown">;
  shutdownBackgroundWork(): Promise<void>;
  closeDatabase(): Promise<void>;
}>;

/** 正常关闭按固定顺序尽力收尾，后续清理错误不能覆盖第一个真实失败。 */
export async function shutdownExpoPosRuntimeServices(
  dependencies: ExpoPosRuntimeShutdownDependencies,
): Promise<void> {
  let firstError: unknown;
  let hasFirstError = false;
  const attempt = async (operation: () => void | Promise<void>) => {
    try {
      await operation();
    } catch (error) {
      if (!hasFirstError) {
        hasFirstError = true;
        firstError = error;
      }
    }
  };

  // 中文注释：SQLite 关闭前必须取消旧服务后台任务；任一步失败也继续执行后续收尾。
  for (const operation of dependencies.beforeShutdown ?? []) {
    await attempt(operation);
  }
  await attempt(() => dependencies.sync.shutdown());
  await attempt(() =>
    dependencies.applicationLog.logger.record({
      level: "Information",
      message: "POS runtime shutting down.",
      category: "runtime.shutdown",
    }),
  );
  await attempt(() => dependencies.applicationLog.shutdown());
  await attempt(dependencies.shutdownBackgroundWork);
  await attempt(dependencies.closeDatabase);

  if (hasFirstError) {
    throw firstError;
  }
}

class ExpoNetworkStatus {
  private online = false;

  public async isOnline(): Promise<boolean> {
    const state = await ExpoNetwork.getNetworkStateAsync();
    this.online =
      state.isConnected === true &&
      state.isInternetReachable !== false;
    return this.online;
  }

  public currentOnline(): boolean {
    return this.online;
  }
}

/**
 * 解决 DeviceSessionCoordinator 与 Axios credential provider 的组合环：
 * 注册 API 先构造，但所有请求发生前真实安全提供者必定已绑定。
 */
class DeferredSecurityBridge
  implements HbposRequestCredentialProvider, HbposAuthenticationFailureHandler
{
  private delegate: SecurityApiCredentialProvider | undefined;

  public bind(delegate: SecurityApiCredentialProvider): void {
    if (this.delegate) {
      throw new Error("Security credential bridge is already bound.");
    }
    this.delegate = delegate;
  }

  public getCredentials(): Promise<HbposRequestCredentials> {
    return this.requireDelegate().getCredentials();
  }

  public onUnauthorized(): Promise<void> {
    return this.requireDelegate().onUnauthorized();
  }

  public onForbidden(): Promise<void> {
    return this.requireDelegate().onForbidden();
  }

  private requireDelegate(): SecurityApiCredentialProvider {
    if (!this.delegate) {
      throw new Error("Security credential bridge is not initialized.");
    }
    return this.delegate;
  }
}

export async function createExpoPosRuntimeServices(): Promise<ExpoPosRuntimeServices> {
  try {
    return await createExpoPosRuntimeServicesCore();
  } catch (error) {
    // 覆盖数据库打开等内部清理 try 之前的启动失败；计时器自身严格一次。
    businessStartupClock.fail();
    throw error;
  }
}

async function createExpoPosRuntimeServicesCore(): Promise<ExpoPosRuntimeServices> {
  const publicExtra =
    Constants.expoConfig?.extra as HbposExtraConfig | undefined;
  const secureStore = new ExpoSecureStoreAdapter();
  const attendanceSecurity =
    createExpoAttendanceSecurityAdapter();
  const trustedApiOrigins = normalizeTrustedApiOrigins([
    ...(publicExtra?.hbpos?.trustedApiOrigins ?? []),
    ...(publicExtra?.hbpos?.apiBaseUrl
      ? [publicExtra.hbpos.apiBaseUrl]
      : [resolveHbposApiUrl(undefined)]),
  ]);
  const publicConfigurationStore =
    new PosPublicRuntimeConfigurationStore(
      secureStore,
      trustedApiOrigins,
    );
  const persistedPublicConfiguration =
    await publicConfigurationStore.load();
  const configuredApiUrl =
    persistedPublicConfiguration.apiBaseUrl ??
    publicExtra?.hbpos?.apiBaseUrl;
  const apiBaseUrl = resolveHbposApiUrl(configuredApiUrl);
  const paymentPublicConfiguration =
    mergePosPaymentPublicConfiguration(
      publicExtra?.payments,
      persistedPublicConfiguration.payments,
    );
  const installation = new InstallationIdentityStore(
    secureStore,
    () => Crypto.randomUUID(),
  );
  const deviceCredentials = new DeviceCredentialStore(secureStore);
  const devicePresentation = new DevicePresentationStore(secureStore);
  const pendingRegistration = new PendingDeviceRegistrationStore(secureStore);
  const pendingActivation = new PendingDeviceActivationCodeStore(secureStore);
  const deviceLock = new DeviceLockStore(secureStore);
  const deviceRegistrationResetMarker =
    new DeviceRegistrationResetMarkerStore(secureStore);
  const cashierAuthorization = new CashierAuthorizationStore(
    secureStore,
    {
      getSystemUptimeMilliseconds: () =>
        attendanceSecurity.getSystemUptimeMilliseconds(),
      nowEpochMs: Date.now,
    },
  );
  const cashierSessionInvalidation = new CashierSessionInvalidationBus();
  const publicCashierSessionInvalidation = createPublicCashierInvalidation(
    cashierSessionInvalidation,
  );
  const applicationLogActor = new ApplicationLogActorBinding();
  cashierSessionInvalidation.subscribe(() => applicationLogActor.clear());
  const securityBridge = new DeferredSecurityBridge();
  const transport = createAxiosHbposTransport(
    apiBaseUrl,
    securityBridge,
    undefined,
    securityBridge,
  );
  const anonymousDeviceTransport = createAxiosHbposTransport(apiBaseUrl, {
    getCredentials: async () => Object.freeze({}),
  });
  const deviceApi = new HbposDeviceApi(transport, anonymousDeviceTransport);
  const deviceResetApi = new HbposDeviceApi(
    createFreshCashierAxiosHbposTransport(
      apiBaseUrl,
      securityBridge,
      undefined,
      securityBridge,
    ),
    anonymousDeviceTransport,
  );
  const apiPartitionGuard = new DeviceRegistrationApiPartitionGuard();
  const deviceSession = new DeviceSessionCoordinator(
    deviceApi,
    installation,
    deviceCredentials,
    deviceLock,
    pendingRegistration,
    devicePresentation,
    pendingActivation,
    apiBaseUrl,
    apiPartitionGuard,
  );
  const publicDeviceSession = createPublicDeviceSession(
    deviceSession,
    () => deviceApi.listRegistrationStores(),
  );
  const lockRuntimeDevice = async (reason: string): Promise<void> => {
    await deviceSession.lockFromAuthorizationFailure(reason);
    // 锁定已经耐久化后才通知 React 重建 runtime；锁机失败必须保留原始异常。
    cashierSessionInvalidation.notify("forbidden");
  };
  securityBridge.bind(
    new SecurityApiCredentialProvider(
      deviceSession,
      cashierAuthorization,
      cashierSessionInvalidation,
    ),
  );

  const network = new ExpoNetworkStatus();
  const cashierApi = new HbposCashierApi(transport);
  const resetRecovery = new DeviceRegistrationResetCoordinator({
    api: deviceResetApi,
    authenticateOnline: async () => {
      throw new Error("Device reset authentication is unavailable during startup recovery.");
    },
    credentials: deviceCredentials,
    presentation: devicePresentation,
    pendingRegistration,
    lock: deviceLock,
    marker: deviceRegistrationResetMarker,
    cashierAuthorization,
    installation,
    createOperationId: () => Crypto.randomUUID(),
    nowIso: () => new Date().toISOString(),
    invalidateCurrentCashier: () => {
      cashierSessionInvalidation.notify("device-scope-change");
    },
    apiPartitionGuard,
  });
  await resetRecovery.recover();
  const cashierCache = new CashierSessionCache(secureStore, {
    sha256Hex: async (material) =>
      Crypto.digestStringAsync(
        Crypto.CryptoDigestAlgorithm.SHA256,
        `${await installation.getOrCreate()}\n${material}`,
        { encoding: Crypto.CryptoEncoding.HEX },
      ),
  }, {
    getAuthorizationFingerprint: async () => {
      const current = await deviceCredentials.load();
      if (!current?.authorizationCode) {
        throw new Error("Cashier cache device authorization is unavailable.");
      }
      return Crypto.digestStringAsync(
        Crypto.CryptoDigestAlgorithm.SHA256,
        current.authorizationCode,
        { encoding: Crypto.CryptoEncoding.HEX },
      );
    },
  });
  const [
    locked,
    credentials,
    pending,
    resetPending,
    installationId,
    online,
  ] = await Promise.all([
    deviceLock.isLocked(),
    deviceCredentials.load(),
    pendingRegistration.load(),
    resetRecovery.isResetRecoveryPending(),
    installation.getOrCreate(),
    network.isOnline(),
  ]);
  const localDevice = resolveLocalDeviceState({
    locked,
    registrationResetPending: resetPending,
    credentials,
    pending,
    installationId,
  });
  const startupGate = await resolveStartupDeviceGate({
    internetReachable: online,
    registrationResetPending: resetPending,
    readPendingDeviceActivation: () =>
      deviceSession.restorePendingActivationCode(),
    verifyCurrentDevice: () => deviceSession.poll(),
    readLocalDevice: async () => {
      const [
        currentLocked,
        currentCredentials,
        currentPending,
        currentResetPending,
        currentInstallationId,
      ] =
        await Promise.all([
          deviceLock.isLocked(),
          deviceCredentials.load(),
          pendingRegistration.load(),
          resetRecovery.isResetRecoveryPending(),
          installation.getOrCreate(),
        ]);
      return resolveLocalDeviceState({
        locked: currentLocked,
        registrationResetPending: currentResetPending,
        credentials: currentCredentials,
        pending: currentPending,
        installationId: currentInstallationId,
      });
    },
    lockDevice: lockRuntimeDevice,
  });
  // startup gate 的 verify 可能刚把 pending 设备批准并写入 Keychain；
  // 组合根必须重新读取，不能继续捕获启动前的 unregistered 身份。
  const runtimeCredentials = await deviceCredentials.load();
  const database = await PosDatabase.open({
    databaseName: POS_DATABASE_NAME,
    driver: new ExpoSqliteDriver(),
    keyProvider: createExpoKeychainDatabaseKeyProvider(secureStore),
    nowIso: () => new Date().toISOString(),
  });
  const now = () => new Date();
  const createId = () => Crypto.randomUUID();
  const appVersion =
    Constants.nativeAppVersion ??
    Constants.expoConfig?.version ??
    "0.0.0";
  const applicationLogCenterConfig = resolveApplicationLogCenterConfig({
    enabled: publicExtra?.hbpos?.logCenter?.enabled,
    ingestUrl: publicExtra?.hbpos?.logCenter?.ingestUrl,
    writeKey: publicExtra?.hbpos?.logCenter?.writeKey,
    environment: publicExtra?.hbpos?.logCenter?.environment,
  });
  let applicationLog: ApplicationLogRuntime | null = null;
  let clientMetricRuntime: ClientMetricRuntime | null = null;
  let releaseClientMetricBinding: (() => void) | null = null;
  let shutdownComposition: (() => Promise<void>) | null = null;

  try {
    // 数据库一旦可用立即创建日志器；后续任一组合步骤失败都可在关闭前持久记录。
    applicationLog = new ApplicationLogRuntime(
      new ApplicationLogger(
        database.applicationLogOutbox(),
        () => {
          const actor = applicationLogActor.read();
          return {
            storeCode: runtimeCredentials?.storeCode ?? null,
            deviceCode: runtimeCredentials?.deviceCode ?? null,
            userId: actor?.userId ?? null,
            userName: actor?.userName ?? null,
            appVersion,
            instanceId: installationId,
          };
        },
        createId,
        () => now().toISOString(),
      ),
      new ApplicationLogUploader(
        database.applicationLogOutbox(),
        applicationLogCenterConfig,
        fetch,
        now,
      ),
    );
    const sha256Hex = (material: string) =>
      Crypto.digestStringAsync(
        Crypto.CryptoDigestAlgorithm.SHA256,
        material,
        { encoding: Crypto.CryptoEncoding.HEX },
      );
    const encryptor = new SensitivePayloadEncryptor(secureStore, {
      getRandomBytes: (length) => Crypto.getRandomBytesAsync(length),
    });
    const attendanceCredentials =
      runtimeCredentials?.hardwareId === installationId
        ? runtimeCredentials
        : null;
    const attendanceAuthorizationMarker = attendanceCredentials
      ? (
          await sha256Hex(attendanceCredentials.authorizationCode)
        ).toUpperCase()
      : null;
    const attendanceRemote = attendanceCredentials
      ? new HbposAttendanceSecurityApi(transport)
      : null;
    const attendanceCrypto = attendanceCredentials
      ? attendanceSecurity
      : null;
    const attendancePersistence =
      attendanceCredentials && attendanceAuthorizationMarker
        ? database.attendanceSecurity(encryptor, {
            apiPartition: apiBaseUrl,
            storeCode: attendanceCredentials.storeCode,
            deviceCode: attendanceCredentials.deviceCode,
            hardwareId: attendanceCredentials.hardwareId,
            authorizationMarker: attendanceAuthorizationMarker,
          })
        : null;
    const emergencyCashier =
      attendanceRemote &&
      attendanceCrypto &&
      attendancePersistence
        ? createEmergencyCashierRuntime({
            authorization: cashierAuthorization,
            cache:
              attendancePersistence.emergencyPublicKeyCache,
            crypto: attendanceCrypto,
            remote: attendanceRemote,
            systemUptime: attendanceCrypto,
            trustedTime:
              attendancePersistence.emergencyTrustedTime,
          })
        : null;
    const cashierAuthentication = new CashierAuthenticationService(
      cashierApi,
      cashierCache,
      network,
      cashierAuthorization,
      deviceLock,
      emergencyCashier?.authentication,
    );
    const deviceRegistrationReset =
      new DeviceRegistrationResetCoordinator({
        api: deviceResetApi,
        authenticateOnline: (input) =>
          cashierAuthentication.loginOnlineOnly(input),
        credentials: deviceCredentials,
        presentation: devicePresentation,
        pendingRegistration,
        lock: deviceLock,
        marker: deviceRegistrationResetMarker,
        cashierAuthorization,
        installation,
        createOperationId: () => Crypto.randomUUID(),
        nowIso: () => new Date().toISOString(),
        invalidateCurrentCashier: () => {
          cashierSessionInvalidation.notify("device-scope-change");
        },
        apiPartitionGuard,
      });
    // 主管代授权复用同一加密离线缓存，但紧急二维码不得替换当前收银员或主管票据。
    const supervisorAuthentication =
      new CashierAuthenticationService(
        cashierApi,
        cashierCache,
        network,
        undefined,
        deviceLock,
      );
    const paymentMethodSettingsRepository = database.paymentMethodSettings();
    let paymentMethods = await paymentMethodSettingsRepository.load();
    const paymentBootstrap =
      await createPaymentProviderRuntimeBootstrap({
        transport,
        extra: paymentPublicConfiguration,
        readPaymentMethods: () => paymentMethods,
        voucherProtectedTokens: database.voucherProtectedTokens(
          encryptor,
          createId,
        ),
      });
    const installmentPaymentPersistence =
      database.installmentPaymentPersistence(encryptor, createId);
    const installmentPaymentBootstrap =
      await createPaymentProviderRuntimeBootstrap({
        transport,
        extra: paymentPublicConfiguration,
        readPaymentMethods: () => paymentMethods,
        allowManualCard: false,
        voucherProtectedTokens:
          installmentPaymentPersistence.voucherProtectedTokens,
      });
    const runtimeVersion = resolveExpoUpdateRuntimeVersion(
      Updates.runtimeVersion,
      appVersion,
    );
    const updateIdentity = Object.freeze({
      runtimeVersion,
      updateId: Updates.updateId,
      isEmbeddedLaunch: Updates.isEmbeddedLaunch,
    });
    let appUpdateSafety:
      | ProductionPosRuntimeServices["appUpdateSafety"]
      | null = null;
    const updateCacheScope = Object.freeze({
      apiOrigin: new URL(apiBaseUrl).origin,
      storeCode: runtimeCredentials?.storeCode ?? "unregistered",
      runtimeVersion,
      installedVersion: appVersion,
    });
    const nativeAppUpdates = new AppUpdateCoordinator({
      metadata: {
        version: appVersion,
        build: Constants.nativeBuildVersion ?? "0",
        runtimeVersion,
      },
      policyStore: database.appUpdatePolicy(updateCacheScope),
      remote: new HbposPosIpadUpdateApi(transport),
    });
    const otaInstaller = new ExpoOtaUpdatePort({
      enabled: Updates.isEnabled,
      runtimeVersion: Updates.runtimeVersion,
      updates: {
        setUpdateRequestHeadersOverride: (headers) =>
          Updates.setUpdateRequestHeadersOverride(headers),
        checkForUpdateAsync: () => Updates.checkForUpdateAsync(),
        fetchUpdateAsync: () => Updates.fetchUpdateAsync(),
        reloadAsync: () => Updates.reloadAsync(),
      },
    });
    const otaAppUpdates = new OtaUpdateCoordinator({
      automaticChecksEnabled: shouldCheckOtaPolicy({
        automaticChecksConfigured:
          publicExtra?.hbpos?.automaticOtaChecks === true,
        updatesEnabled: Updates.isEnabled,
      }),
      metadata: {
        runtimeVersion,
        currentUpdateId: Updates.updateId,
        currentUpdateGroupId: readCurrentUpdateGroupId(
          Updates.manifest,
        ),
      },
      policyStore: database.otaUpdatePolicy(updateCacheScope),
      remote: new HbposPosIpadOtaUpdateApi(transport),
      installer: otaInstaller,
    });
    const appUpdateTransition =
      new UpdateTransitionLeaseCoordinator();
    const appUpdates = new AppUpdateOrchestrator({
      installedVersion: appVersion,
      native: nativeAppUpdates,
      ota: otaAppUpdates,
      transition: appUpdateTransition,
      appStore: {
        open: (url) => Linking.openURL(url),
      },
      safety: {
        getSafetySnapshot: () => {
          if (!appUpdateSafety) {
            throw new Error(
              "App update safety is not initialized.",
            );
          }
          return appUpdateSafety.getSnapshot();
        },
      },
    });
    const printer = createLazyExpoPrinterAdapter();
    const scannerRouter = new HidScannerRouter();
    const scannerTest =
      new SettingsScannerTestCoordinator(scannerRouter);
    const paymentTestApi =
      new HbposSettingsPaymentTestApi(transport);
    const squareSetupApi =
      new HbposSettingsSquareSetupApi(transport);
    const linklySetupApi =
      new HbposSettingsLinklySetupApi(transport);
    const currentPaymentSettings =
      settingsPaymentConfiguration(paymentPublicConfiguration);
    const updateChannel = Updates.channel?.trim() || "embedded";
    try {
      clientMetricRuntime = await createExpoClientMetricRuntime({
        appVersion,
        channel: updateChannel,
        store: runtimeCredentials?.storeCode ?? null,
        sessionId: createId(),
        logCenter: applicationLogCenterConfig,
        createId,
        getRequestHeaders: () => deviceSession.getRequestHeaders(),
      });
      releaseClientMetricBinding = clientMetrics.bind(clientMetricRuntime);
      clientMetricRuntime.start();
    } catch (error) {
      applicationLog.record({
        level: "Warning",
        message: "POS client metric runtime initialization failed.",
        category: "runtime.performance",
        error,
      });
    }
    const readUpdateSnapshot = () =>
      settingsAppUpdateSnapshot({
        channel: updateChannel,
        currentVersion: appVersion,
        policy: appUpdates.getPolicy(),
        restartAvailable: Updates.isEnabled,
      });
    const probeApiHealth = createSettingsApiHealthProbe(
      (url, init) => fetch(url, init),
    );
    const attendanceAudit =
      attendanceCredentials &&
      attendanceAuthorizationMarker &&
      attendanceRemote &&
      attendanceCrypto &&
      attendancePersistence
        ? createExpoAttendanceRuntimeConfiguration({
            attendanceSecurity: attendanceRemote,
            authorizationMarker: attendanceAuthorizationMarker,
            connectivity: network,
            credentials: attendanceCredentials,
            localAudit: database.operationAudits({
              storeCode: attendanceCredentials.storeCode,
              deviceCode: attendanceCredentials.deviceCode,
            }),
            qrCache: attendancePersistence.attendanceQrCache,
            qrCrypto: attendanceCrypto,
            readCurrentCredentials: () =>
              deviceSession.getTransportCredentials(),
            readStoreName: async () => {
              const receipt =
                await database.settings().getReceiptPrinterSettings();
              return receipt.storeName;
            },
            remoteAudit: new HbposOperationAuditReadApi(
              transport,
              attendanceCredentials.storeCode,
              attendanceCredentials.deviceCode,
            ),
            scheduler: {
              every(intervalMs, task) {
                const timer = setInterval(task, intervalMs);
                return () => clearInterval(timer);
              },
            },
            sha256Hex,
          })
        : null;
    const composition = createProductionPosRuntimeServices({
      database,
      transport,
      encryptor,
      syncSecurity: {
        lockDevice: lockRuntimeDevice,
      },
      auditMetadata: {
        storeCode: runtimeCredentials?.storeCode ?? "unregistered",
        deviceCode: runtimeCredentials?.deviceCode ?? "unregistered",
        appVersion,
        instanceId: installationId,
      },
      supportAppId: POS_APP_ID,
      // 总部下发小票资料同步只上报版本号、触发源与失败原因，绝不带地址/电话/ABN 等资料内容。
      reportReceiptProfileSync(event) {
        applicationLog?.record({
          level: event.level,
          message: event.message,
          category: "settings.receipt-profile",
          properties: event.properties,
        });
      },
      reportPaymentRecoveryFallback(fallback) {
        // 设备仍可启动，但说明耐久草稿已无法被当前代码重算；须人工在支付页完成恢复或安全取消。
        applicationLog?.record({
          level: "Warning",
          message: "Payment recovery fell back to the durable cart.",
          category: "runtime.payment-recovery",
          properties: {
            checkoutIntentId: fallback.checkoutIntentId,
            differences: fallback.differences.join(","),
          },
        });
      },
      clock: {
        now,
        nowIso: () => now().toISOString(),
      },
      systemUptimeMilliseconds: () =>
        attendanceSecurity.getSystemUptimeMilliseconds(),
      installmentPerformanceRecorder: {
        record(event) {
          applicationLog?.record({
            level: "Information",
            message: "Cash installment repayment performance stage recorded.",
            category: "payment.installment.cash-repayment.performance",
            traceId: event.operationHash,
            properties: {
              stage: event.name,
              elapsedMs: event.elapsedMs,
              path: event.path,
              outcome: event.outcome,
            },
          });
        },
      },
      createId,
      random: Math.random,
      sha256Hex,
      // 整文件目录下载：残片放在缓存目录，跨重启续传；服务端开关未开时自动回退分页。
      catalogFileSync: new HbposCatalogFileSync({
        transport,
        store: new ExpoCatalogFileStore("catalog-files"),
        digest: async (bytes) =>
          bytesToLowerHex(new Uint8Array(await Crypto.digest(Crypto.CryptoDigestAlgorithm.SHA256, bytes))),
      }),
      // 仅返回惰性 adapter；requireNativeModule("HbPrinter") 要到实际硬件动作才会调用。
      createPrinter: () => printer,
      externalDisplay,
      customerDisplayAdvertisementCacheRootUri,
      advertisementCache: new CustomerDisplayAdvertisementCache({
        rootUri: customerDisplayAdvertisementCacheRootUri,
        files: new ExpoAdvertisementFileSystem(),
        sha256Hex,
      }),
      // 客显商品缩略图：专用缓存目录 + 与销售页同口径的受信任地址解析。
      customerDisplayProductImages: {
        rootUri: customerDisplayProductImageCacheRootUri,
        files: new ExpoProductImageFileSystem(),
        apiBaseUrl,
      },
      ...(publicExtra?.hbpos?.businessTimeZone
        ? {
            businessTimeZone:
              publicExtra.hbpos.businessTimeZone,
          }
        : {}),
      connectivity: network,
      cashierAuthentication,
      cashierSessionSecurity: {
        getDeviceIdentity: () => deviceSession.getDeviceIdentity(),
        clearAuthorization: () => cashierAuthorization.clear(),
        invalidateAuthorizationForDeviceScope: () => {
          cashierAuthorization.invalidateForDeviceScope();
          cashierSessionInvalidation.notify("device-scope-change");
        },
        subscribeSessionInvalidation: (listener) =>
          cashierSessionInvalidation.subscribe(() => listener()),
      },
      newTransactionGate: appUpdates,
      appUpdateTransition,
      operationAuthorization: {
        cashierAuthentication: supervisorAuthentication,
      },
      ...(attendanceAudit ? { attendanceAudit } : {}),
      cashierLock: {
        onLocked: () => {
          cashierSessionInvalidation.notify("manual-lock");
        },
      },
      payments: {
        bootstrap: paymentBootstrap,
        linklyEnvironment: configuredLinklyEnvironment(
          paymentPublicConfiguration,
        ),
      },
      installments: {
        bootstrap: installmentPaymentBootstrap,
      },
      settings: {
        paymentMethods: {
          load: () => paymentMethodSettingsRepository.load(),
          save: async (settings) => {
            // 全局写租约内先耐久保存，再同步发布新快照；无需重启重建 provider。
            paymentMethods = await paymentMethodSettingsRepository.save(settings);
          },
        },
        apiBaseUrl,
        appVersion,
        updateChannel,
        squareSetup: squareSetupApi,
        linklySetup: linklySetupApi,
        printer,
        readDevicePresentation: () =>
          readSettingsDevicePresentation(publicDeviceSession),
        // 设置页读取失败原本只显示 load-failed；把失败阶段与异常写入中心日志。
        reportSnapshotFailure: (stage, error) => {
          applicationLog?.record({
            level: "Error",
            message: "Settings snapshot load failed.",
            category: "settings.load",
            error,
            properties: { stage },
          });
        },
        // 打印机扫描失败、或个别设备名被清洗/坏 ID 被跳过时留痕；只记计数，不记设备名与地址。
        reportPrinterScanIssue: (issue) => {
          if (issue.kind === "failed") {
            applicationLog?.record({
              level: "Warning",
              message: "Printer scan failed.",
              category: "settings.printer-scan",
              error: issue.error,
              properties: { kind: issue.kind },
            });
            return;
          }
          applicationLog?.record({
            level: "Information",
            message: "Printer scan results sanitized.",
            category: "settings.printer-scan",
            properties: {
              kind: issue.kind,
              sanitizedCount: issue.sanitizedCount,
              skippedCount: issue.skippedCount,
              totalCount: issue.totalCount,
            },
          });
        },
        paymentConfiguration: {
          current: currentPaymentSettings,
          availability: {
            square: paymentAvailability(
              paymentBootstrap.configurationAvailability.getAvailability(
                "square",
              ),
            ),
            linkly: paymentAvailability(
              paymentBootstrap.configurationAvailability.getAvailability(
                "linkly-cloud",
              ),
            ),
          },
          test: async (
            provider: "square" | "linkly",
            configuration: SettingsPaymentSettingsInput,
            signal: AbortSignal,
            terminals?: import("../../features/settings/settings-presenter").SettingsLinklyTerminalSelectionSnapshot | null,
          ) => {
            throwIfRuntimeAborted(signal);
            await paymentTestApi.test(
              provider,
              configuration,
              signal,
              terminals,
            );
            throwIfRuntimeAborted(signal);
          },
          save: (configuration) =>
            publicConfigurationStore.savePayments(configuration),
        },
        apiConfiguration: {
          allowSwitchWithPendingLocalData: __DEV__,
          probe: probeApiHealth,
          save: (nextApiBaseUrl) =>
            publicConfigurationStore.saveApiBaseUrl(nextApiBaseUrl),
          runSwitchGuarded: (operation) =>
            apiPartitionGuard.runSwitch(operation),
        },
        runtimeReload: {
          reload: async (signal) => {
            throwIfRuntimeAborted(signal);
            return reloadSettingsRuntimeTerminally(() =>
              Updates.reloadAsync(),
            );
          },
        },
        device: {
          previewActivationCode: async (activationCode, signal) => {
            throwIfRuntimeAborted(signal);
            const response = await deviceSession.previewActivationCode(
              activationCode,
            );
            throwIfRuntimeAborted(signal);
            if (
              response.isAllowed !== true ||
              response.deviceSystem !== "iPadOS"
            ) {
              throw new Error("SETTINGS_DEVICE_ACTIVATION_PREVIEW_REJECTED");
            }
            return response;
          },
          reregister: async (request, signal, onCredentialsCommitted) => {
            await reregisterSettingsDevice(
              request,
              signal,
              (nextRequest, markCommitted) =>
                deviceSession.rebindActivationCode(nextRequest, markCommitted),
              onCredentialsCommitted,
            );
          },
          resetRegistration: async (employeeBarcode, signal) => {
            throwIfRuntimeAborted(signal);
            try {
              await deviceRegistrationReset.reset(employeeBarcode);
              return "completed" as const;
            } catch (error) {
              // 由 coordinator 做类型明确的失败后恢复判定：marker 存在或读取失败
              // 都按 pending 处理（读取失败时已失效收银员并锁定），
              // 显式拒绝且 marker 确认不存在才抛原错误。
              if (await deviceRegistrationReset.isResetRecoveryPending()) {
                return "pending-recovery" as const;
              }
              throw error;
            }
          },
          hasRegistrationRecoveryRisk: async () => {
            const [activationPending, resetPendingNow] = await Promise.all([
              deviceSession.hasActivationRecoveryRisk(),
              deviceRegistrationReset.isResetRecoveryPending(),
            ]);
            return activationPending || resetPendingNow;
          },
        },
        scanner: {
          status: "ready",
          test: (signal) => scannerTest.test(signal),
        },
        appUpdate: {
          snapshot: readUpdateSnapshot,
          check: async (signal) => {
            throwIfRuntimeAborted(signal);
            await appUpdates.refreshOnForeground();
            throwIfRuntimeAborted(signal);
            return readUpdateSnapshot();
          },
          restart: async (signal) => {
            throwIfRuntimeAborted(signal);
            const decision = await appUpdates.restartIfSafe();
            throwIfRuntimeAborted(signal);
            return decision.canRestart;
          },
        },
      },
    });
    shutdownComposition = composition.shutdownBackgroundWork;
    const serverConnection = new PreloginServerConnectionControl({
      currentApiBaseUrl: apiBaseUrl,
      trustedApiOrigins,
      allowSwitchWithPendingLocalData: __DEV__,
      runExclusive: (operation) =>
        composition.catalogRefresh.runExclusive(operation),
      readPendingData: async (signal) => {
        throwIfRuntimeAborted(signal);
        const [durable, runtimeSafety] = await Promise.all([
          database.settingsSafety().read(),
          composition.appUpdateSafety.getSnapshot(),
        ]);
        throwIfRuntimeAborted(signal);
        return Object.freeze({
          ...durable,
          hasActiveCart: runtimeSafety.hasActiveCart,
          hasFulfilmentInFlight:
            runtimeSafety.hasFulfilmentInFlight,
          hasSyncOrAuditInFlight:
            runtimeSafety.hasSyncOrAuditInFlight,
          pendingDurableWriteCount: Math.max(
            durable.pendingDurableWriteCount,
            runtimeSafety.hasPendingDurableWrite ? 1 : 0,
          ),
          pendingReturnCount: Math.max(
            durable.pendingReturnCount,
            runtimeSafety.hasRecoveryRequired ? 1 : 0,
          ),
          unresolvedPaymentCount: Math.max(
            durable.unresolvedPaymentCount,
            runtimeSafety.hasUnresolvedPayment ? 1 : 0,
          ),
        });
      },
      probe: probeApiHealth,
      save: (nextApiBaseUrl) =>
        publicConfigurationStore.saveApiBaseUrl(nextApiBaseUrl),
      runSwitchGuarded: (operation) =>
        apiPartitionGuard.runSwitch(operation),
      hasRegistrationRecoveryRisk: async () => {
        const [activationPending, resetPendingNow] = await Promise.all([
          deviceSession.hasActivationRecoveryRisk(),
          deviceRegistrationReset.isResetRecoveryPending(),
        ]);
        return activationPending || resetPendingNow;
      },
    });
    appUpdateSafety = composition.appUpdateSafety;
    // 销售路由拿到 runtime 前必须先处理崩溃遗留的 HoldClear/RecallActive fence。
    // 初始化失败保持数据库可恢复并让启动 fail-closed，绝不开放普通收银。
    await composition.initialize();
    businessStartupClock.markRuntimeReady();
    applicationLog.record({
      level: "Information",
      message: "POS runtime initialized.",
      category: "runtime.startup",
      properties: { backend: startupGate.backend, device: startupGate.device },
    });
    if (online) {
      // 公钥同步失败只关闭紧急登录；普通在线/离线收银登录保持原有路径。
      void emergencyCashier?.syncPublicKeys();
    }
    const {
      initialize: _initialize,
      shutdownBackgroundWork,
      ...services
    } = composition;
    void _initialize;
    const cashierSession = bindCashierSessionToApplicationLog(
      services.cashierSession,
      applicationLogActor,
    );
    // `applicationLog` 仅为 catch 路径保留可空状态；返回的 runtime 闭包固定使用已构造实例。
    const activeApplicationLog = applicationLog;

    return {
      ...services,
      cashierSession,
      apiBaseUrl,
      deviceSession: publicDeviceSession,
      cashierSessionInvalidation: publicCashierSessionInvalidation,
      externalDisplay,
      appUpdates,
      serverConnection,
      appUpdateRecovery: Object.freeze({
        readSnapshot: () =>
          Promise.resolve(
            createAppUpdateRecoveryRuntimeSnapshot({
              appVersion,
              buildNumber:
                Application.nativeBuildVersion ?? "unknown",
              runtimeVersion,
              channel: updateChannel,
              apiOrigin: updateCacheScope.apiOrigin,
            }),
          ),
      }),
      scanner: Object.freeze({ router: scannerRouter }),
      applicationLog: activeApplicationLog,
      updateIdentity,
      shutdown: async () => {
        releaseClientMetricBinding?.();
        await clientMetricRuntime?.shutdown();
        await shutdownExpoPosRuntimeServices({
          beforeShutdown: [
            () => {
              if (services.customerDisplay.status === "available") {
                services.customerDisplay.stopAdvertisements();
              }
            },
            () =>
              services.customerDisplay.status === "available"
                ? services.customerDisplay.clearSensitiveContent()
                : undefined,
            // 与 401/403/手动锁屏使用同一可信桥撤销可信会话。
            () => cashierSessionInvalidation.notify("manual-lock"),
            () => appUpdates.dispose(),
          ],
          sync: services.sync,
          applicationLog: activeApplicationLog,
          // 页面离开不会取消目录刷新；只有 runtime 关闭会先中止并等待 staging 清理。
          shutdownBackgroundWork,
          closeDatabase: () => database.close(),
        });
      },
      backend: startupGate.backend,
      device:
        startupGate.backend === "unverified"
          ? localDevice
          : startupGate.device,
    };
  } catch (error) {
    businessStartupClock.fail();
    releaseClientMetricBinding?.();
    await clientMetricRuntime?.shutdown();
    await recordRuntimeInitializationFailure(
      applicationLog,
      error,
      () =>
        shutdownCompositionBeforeDatabaseClose(
          shutdownComposition,
          () => database.close(),
        ),
    );
    throw error;
  }
}

export function createExpoPosRuntimeController(): PosRuntimeController<ExpoPosRuntimeServices> {
  return new PosRuntimeController(createExpoPosRuntimeServices);
}

function paymentAvailability(input: Readonly<{
  available: boolean;
  blocker: string | null;
}>): Readonly<{ available: boolean; blockerCode: string | null }> {
  return Object.freeze({
    available: input.available,
    blockerCode: input.blocker,
  });
}

function throwIfRuntimeAborted(signal: AbortSignal): void {
  if (signal.aborted) {
    throw Object.assign(
      new Error("Runtime settings operation aborted."),
      { name: "AbortError" },
    );
  }
}

function bytesToLowerHex(bytes: Uint8Array): string {
  let hex = "";
  for (const byte of bytes) hex += byte.toString(16).padStart(2, "0");
  return hex;
}
