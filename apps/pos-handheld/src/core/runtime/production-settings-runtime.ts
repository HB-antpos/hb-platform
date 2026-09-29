import {
  SettingsPresenter,
  type PaymentEnvironment,
  type SettingsClearSavedPrinterResult,
  type SettingsControlPort,
  type SettingsDangerousActionResult,
  type SettingsDangerousConfirmation,
  type SettingsLinklySetupReadPort,
  type SettingsLinklyTerminalSelectionSnapshot,
  type SettingsSquareSetupControlPort,
} from "../../features/settings/settings-presenter";
import type { SettingsRuntimeFactory } from "../../features/settings/settings-runtime";
import type { PaymentMethodSettings } from "../../features/settings/payment-method-settings";

export type SettingsTrustedSession = Readonly<{
  storeCode: string;
  deviceCode: string;
  permissionCodes: readonly string[];
}>;

export interface SettingsTrustedSessionLease {
  get(): SettingsTrustedSession;
}

export type ProductionSettingsRuntimeDependencies = Readonly<{
  createSessionLease(): SettingsTrustedSessionLease;
  control: SettingsControlPort;
  /**
   * 必须与销售、支付、退款和分期的写路径共享同一独占门闩。
   * 组合根负责在 operation 内复核所有耐久队列并执行动作。
   */
  runDangerousExclusive<T>(operation: () => Promise<T>): Promise<T>;
  /**
   * 设备重绑或重置会切换可信设备范围，必须走全局 transition 的目录→购物车锁序。
   * 缺失时拒绝执行，避免回退到普通购物车锁而与 barrier 自锁。
   */
  runDeviceRegistrationResetTransition?<T>(
    operation: () => Promise<T>,
  ): Promise<T>;
}>;

type LeaseAwareSettingsControlPort = SettingsControlPort &
  Readonly<{
    savePaymentMethodSettings?: (
      settings: PaymentMethodSettings,
      signal: AbortSignal,
      assertActive?: () => void,
    ) => Promise<SettingsDangerousActionResult>;
    executeDangerousAction(
      action: SettingsDangerousConfirmation,
      signal: AbortSignal,
      employeeBarcode?: string,
      assertActive?: () => void,
    ): Promise<SettingsDangerousActionResult>;
    clearSavedPrinter?:
      | ((
          signal: AbortSignal,
          assertActive?: () => void,
        ) => Promise<SettingsClearSavedPrinterResult>)
      | undefined;
    selectLinklyTerminalGuarded?:
      | ((
          environment: PaymentEnvironment,
          terminalId: string,
          expectedRevision: number,
          signal: AbortSignal,
          assertActive?: () => void,
        ) => Promise<SettingsLinklyTerminalSelectionSnapshot>)
      | undefined;
  }>;

/**
 * route 只能取得零参数 presenter 工厂。可信门店、设备和权限由 cashier lease
 * 冻结。普通动作在异步边界前后复核；不可撤销动作只在提交点前复核，避免把
 * 已完成的硬件脉冲或持久化写入伪装成失败。
 */
export function createProductionSettingsRuntime(
  input: ProductionSettingsRuntimeDependencies,
): SettingsRuntimeFactory {
  return Object.freeze({
    createPresenter: () => {
      const lease = input.createSessionLease();
      const identity = normalizedIdentity(lease.get());
      const port = securedSettingsPort(input, lease, identity);
      return new SettingsPresenter({
        permissions: identity.permissionCodes,
        port,
      });
    },
  });
}

function securedSettingsPort(
  input: ProductionSettingsRuntimeDependencies,
  lease: SettingsTrustedSessionLease,
  identity: SettingsTrustedSession,
): SettingsControlPort {
  const run = async <T>(
    operation: () => Promise<T>,
  ): Promise<T> => {
    assertSameSession(lease.get(), identity);
    const result = await operation();
    assertSameSession(lease.get(), identity);
    return result;
  };

  const runIrreversibleMutation = <T>(
    operation: () => Promise<T>,
  ): Promise<T> => {
    assertSameSession(lease.get(), identity);
    // 外部 POST 成功后不可因换班改写为失败，否则新幂等键重试会重复创建。
    return operation();
  };

  const leaseAwareControl = input.control as LeaseAwareSettingsControlPort;

  const runDangerous = async (
    action: SettingsDangerousConfirmation,
    signal: AbortSignal,
    employeeBarcode?: string,
  ): Promise<SettingsDangerousActionResult> => {
    assertSameSession(lease.get(), identity);
    const execute = async () => {
      const result =
        await leaseAwareControl.executeDangerousAction(
          action,
          signal,
          employeeBarcode,
          () => assertSameSession(lease.get(), identity),
        );
      // Pair 与设备范围变更提交后都不可逆；成功时旧 lease 本就会失效，不能后验改写终态。
      if (
        action.kind !== "pair-linkly" &&
        action.kind !== "reregister-device" &&
        action.kind !== "reset-device-registration"
      ) {
        assertSameSession(lease.get(), identity);
      }
      return result;
    };
    // restart/payment/pair 的最终互斥由 transition 按目录→购物车顺序取得；
    // 若这里先持有普通购物车 lease，transition 会等待当前动作自身而永久自锁。
    if (
      action.kind === "reregister-device" ||
      action.kind === "reset-device-registration"
    ) {
      const transition = input.runDeviceRegistrationResetTransition;
      if (!transition) {
        return Promise.reject(
          Object.assign(
            new Error("Device registration transition is unavailable."),
            { code: "DEVICE_REGISTRATION_RESET_TRANSITION_UNAVAILABLE" },
          ),
        );
      }
      return transition(execute);
    }
    return action.kind === "restart-app" ||
      action.kind === "change-payment-settings" ||
      action.kind === "pair-linkly"
      ? execute()
      : input.runDangerousExclusive(execute);
  };

  const squareSetup = input.control.squareSetup;
  const linklySetup = input.control.linklySetup;

  const secured: SettingsControlPort = {
    ...(squareSetup
      ? {
          squareSetup: Object.freeze({
            getSquareTokenStatus: (environment, signal) =>
              run(() =>
                squareSetup.getSquareTokenStatus(environment, signal),
              ),
            listSquareLocations: (environment, signal) =>
              run(() =>
                squareSetup.listSquareLocations(environment, signal),
              ),
            listSquareDevices: (environment, locationId, signal) =>
              run(() =>
                squareSetup.listSquareDevices(
                  environment,
                  locationId,
                  signal,
                ),
              ),
            listSquareDeviceCodes: (environment, locationId, signal) =>
              run(() =>
                squareSetup.listSquareDeviceCodes(
                  environment,
                  locationId,
                  signal,
                ),
              ),
            createSquareDeviceCode: (
              environment,
              locationId,
              name,
              signal,
            ) =>
              runIrreversibleMutation(() =>
                squareSetup.createSquareDeviceCode(
                  environment,
                  locationId,
                  name,
                  signal,
                ),
              ),
            getSquareDeviceCode: (environment, deviceCodeId, signal) =>
              run(() =>
                squareSetup.getSquareDeviceCode(
                  environment,
                  deviceCodeId,
                  signal,
                ),
              ),
          } satisfies SettingsSquareSetupControlPort),
        }
      : {}),
    ...(linklySetup
      ? {
          // Pair 只走危险动作；终端选择必须走 production control 的共享门禁。
          linklySetup: Object.freeze({
            readState: (
              environment: Parameters<SettingsLinklySetupReadPort["readState"]>[0],
              signal: Parameters<SettingsLinklySetupReadPort["readState"]>[1],
              terminals?: Parameters<SettingsLinklySetupReadPort["readState"]>[2],
            ) =>
              run(() =>
                linklySetup.readState(environment, signal, terminals),
              ),
            ...(linklySetup.readTerminals
              ? {
                  readTerminals: (
                    environment: PaymentEnvironment,
                    signal: AbortSignal,
                  ) =>
                    run(() =>
                      linklySetup.readTerminals!(environment, signal),
                    ),
                }
              : {}),
            ...(linklySetup.selectTerminal &&
            leaseAwareControl.selectLinklyTerminalGuarded
              ? {
                  selectTerminal: (
                    environment: PaymentEnvironment,
                    terminalId: string,
                    expectedRevision: number,
                    signal: AbortSignal,
                  ) => {
                    assertSameSession(lease.get(), identity);
                    return leaseAwareControl.selectLinklyTerminalGuarded!(
                      environment,
                      terminalId,
                      expectedRevision,
                      signal,
                      () => assertSameSession(lease.get(), identity),
                    );
                  },
                }
              : {}),
          }),
        }
      : {}),
    getCatalogRefreshState: () => {
      assertSameSession(lease.get(), identity);
      return input.control.getCatalogRefreshState();
    },
    subscribeCatalogRefresh: (listener) =>
      input.control.subscribeCatalogRefresh(() => {
        assertSameSession(lease.get(), identity);
        listener();
      }),
    // 此事件恰好表示旧 identity 已失效，不能再做旧 scope 的后验断言。
    subscribeDeviceReregistrationCommitted: (listener) =>
      input.control.subscribeDeviceReregistrationCommitted(listener),
    loadSnapshot: (signal) =>
      run(async () => {
        const snapshot = await input.control.loadSnapshot(signal);
        if (
          snapshot.device.storeCode !== identity.storeCode ||
          snapshot.device.deviceCode !== identity.deviceCode
        ) {
          throw new Error("SETTINGS_DEVICE_SCOPE_MISMATCH");
        }
        return snapshot;
      }),
    downloadCatalog: (signal) =>
      run(() => input.control.downloadCatalog(signal)),
    testApiAddress: (apiBaseUrl, signal) =>
      run(() => input.control.testApiAddress(apiBaseUrl, signal)),
    ...(input.control.previewDeviceActivationCode
      ? {
          previewDeviceActivationCode: (activationCode, signal) =>
            run(() =>
              input.control.previewDeviceActivationCode!(
                activationCode,
                signal,
              ),
            ),
        }
      : {}),
    preflightDeviceReregistration: (signal) =>
      run(() => input.control.preflightDeviceReregistration(signal)),
    testPaymentProvider: (provider, settings, signal, terminals) =>
      run(() =>
        input.control.testPaymentProvider(
          provider,
          settings,
          signal,
          terminals,
        ),
      ),
    savePrinterSettings: (settings, signal) =>
      run(() => input.control.savePrinterSettings(settings, signal)),
    ...(leaseAwareControl.savePaymentMethodSettings
      ? {
          savePaymentMethodSettings: async (
            settings: PaymentMethodSettings,
            signal: AbortSignal,
          ) => {
            assertSameSession(lease.get(), identity);
            return leaseAwareControl.savePaymentMethodSettings!(
              settings,
              signal,
              () => assertSameSession(lease.get(), identity),
            );
          },
        }
      : {}),
    loadReceiptProfile: (signal) =>
      run(() => input.control.loadReceiptProfile(signal)),
    scanPrinters: (signal) =>
      run(() => input.control.scanPrinters(signal)),
    connectPrinter: (peripheralId, signal) =>
      run(() => input.control.connectPrinter(peripheralId, signal)),
    testPrinter: (signal) =>
      run(() => input.control.testPrinter(signal)),
    ...(input.control.testCashDrawer
      ? {
          testCashDrawer: async (signal: AbortSignal) => {
            assertSameSession(lease.get(), identity);
            // 正式开箱动作在发出脉冲前自行复核 lease；脉冲后不得改写终态。
            return input.control.testCashDrawer!(signal);
          },
        }
      : {}),
    ...(leaseAwareControl.clearSavedPrinter
      ? {
          clearSavedPrinter: async (signal: AbortSignal) => {
            assertSameSession(lease.get(), identity);
            return leaseAwareControl.clearSavedPrinter!(
              signal,
              () => assertSameSession(lease.get(), identity),
            );
          },
        }
      : {}),
    testScanner: (signal) =>
      run(() => input.control.testScanner(signal)),
    checkForAppUpdate: (signal) =>
      run(() => input.control.checkForAppUpdate(signal)),
    executeDangerousAction: runDangerous,
  };
  return Object.freeze(secured);
}

function normalizedIdentity(
  session: SettingsTrustedSession,
): SettingsTrustedSession {
  const storeCode = requiredText(session.storeCode);
  const deviceCode = requiredText(session.deviceCode);
  const permissionCodes = Object.freeze(
    session.permissionCodes.map(requiredText),
  );
  return Object.freeze({ storeCode, deviceCode, permissionCodes });
}

function assertSameSession(
  observed: SettingsTrustedSession,
  expected: SettingsTrustedSession,
): void {
  if (
    requiredText(observed.storeCode) !== expected.storeCode ||
    requiredText(observed.deviceCode) !== expected.deviceCode
  ) {
    throw new Error("SETTINGS_CASHIER_SESSION_REPLACED");
  }
}

function requiredText(value: string): string {
  const normalized = value.trim();
  if (!normalized) throw new Error("SETTINGS_TRUSTED_IDENTITY_INVALID");
  return normalized;
}
