import type { CurrentCashierSession } from "./current-cashier-session";
import type { RuntimePrinterAdapter } from "./lazy-printer-adapter";
import {
  ProductionSettingsControl,
} from "./production-settings-control";
import { createProductionSettingsRuntime } from "./production-settings-runtime";

import type {
  ReceiptPrinterSettings,
} from "@/core/db/pos-settings-repository";
import type { CatalogRefreshState } from "@/features/catalog/catalog-refresh-coordinator";
import {
  buildSaleReceiptDocument,
  documentToEscPosBytes,
} from "@hb/pos-receipt-core/features/receipts/receipt-document";
import type { ActivePricingCartSession } from "@/features/sales/runtime";
import {
  SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN,
  type SettingsAppUpdateSnapshot,
  type SettingsCatalogSnapshot,
  type SettingsCashDrawerTestResult,
  type SettingsClearSavedPrinterResult,
  type SettingsControlPort,
  type SettingsLinklyPairingPort,
  type SettingsLinklySetupControlPort,
  type SettingsPaymentSettingsInput,
  type SettingsPendingDataSnapshot,
  type SettingsReceiptProfileDraft,
  type SettingsScannerTestResult,
  type SettingsSquareSetupControlPort,
  type SettingsSnapshot,
} from "@/features/settings/settings-presenter";
import type { SettingsRuntimeFactory } from "@/features/settings/settings-runtime";
import type { PaymentMethodSettings } from "@/features/settings/payment-method-settings";
import type { SettingsSquareSetupPort } from "@hb/pos-domain/features/settings/settings-square-setup";

type TerminalScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

type SettingsDevicePresentation = Readonly<{
  deviceCode: string;
  storeCode: string;
  storeName: string;
  terminalName: string;
}>;

type PaymentAvailability = Readonly<{
  available: boolean;
  blockerCode: string | null;
}>;

type ControlledCashDrawerActionResult = Readonly<{
  state:
    | "Printed"
    | "Failed"
    | "Ambiguous"
    | "Completed"
    | "Unknown"
    | "recovery-required"
    | "not-retryable"
    | "not-found"
    | "denied";
  errorCode: string | null;
}>;

type LeaseAwareClearSavedPrinter = (
  signal: AbortSignal,
  assertActive?: () => void,
) => Promise<SettingsClearSavedPrinterResult>;

export type ProductionSettingsCompositionInput = Readonly<{
  currentCashier: CurrentCashierSession;
  terminal: TerminalScope;
  activeCart: Pick<
    ActivePricingCartSession,
    "getSnapshot" | "runExclusive"
  >;
  apiBaseUrl: string;
  appVersion: string;
  updateChannel: string;
  createId(): string;
  squareSetup?: SettingsSquareSetupPort | undefined;
  linklySetup?:
    | (SettingsLinklySetupControlPort & SettingsLinklyPairingPort)
    | undefined;
  readDevicePresentation(): Promise<SettingsDevicePresentation>;
  catalog: Readonly<{
    getActiveMetadata(): Promise<SettingsCatalogSnapshot | null>;
    getRefreshState(): CatalogRefreshState;
    subscribeRefresh(listener: () => void): () => void;
    runExclusive<T>(operation: () => Promise<T>): Promise<T>;
    download(signal: AbortSignal): Promise<SettingsCatalogSnapshot>;
    reset(signal: AbortSignal): Promise<SettingsCatalogSnapshot>;
  }>;
  receiptSettings: Readonly<{
    get(): Promise<ReceiptPrinterSettings>;
    save(input: ReceiptPrinterSettings): Promise<unknown>;
  }>;
  receiptProfile: Readonly<{
    load(signal: AbortSignal): Promise<SettingsReceiptProfileDraft | null>;
  }>;
  paymentConfiguration: Readonly<{
    current: SettingsPaymentSettingsInput | null;
    availability: Readonly<{
      square: PaymentAvailability;
      linkly: PaymentAvailability;
    }>;
    test(
      provider: "square" | "linkly",
      input: SettingsPaymentSettingsInput,
      signal: AbortSignal,
      terminals?: import("../../features/settings/settings-presenter").SettingsLinklyTerminalSelectionSnapshot | null,
    ): Promise<void>;
    save(input: SettingsPaymentSettingsInput): Promise<void>;
  }>;
  paymentMethods?: Readonly<{
    load(): Promise<PaymentMethodSettings>;
    save(settings: PaymentMethodSettings): Promise<void>;
  }>;
  paymentConfigurationTransition: Readonly<{
    run<T>(operation: () => Promise<T>): Promise<T>;
  }>;
  pendingData: Readonly<{
    read(): Promise<Omit<SettingsPendingDataSnapshot, "hasActiveCart">>;
  }>;
  apiConfiguration: Readonly<{
    /**
     * 仅允许开发构建在调试 API 时跨过本地待处理门禁。
     */
    allowSwitchWithPendingLocalData?: boolean;
    probe(healthUrl: string, signal: AbortSignal): Promise<boolean>;
    save(apiBaseUrl: string): Promise<void>;
    runSwitchGuarded?<T>(operation: () => Promise<T>): Promise<
      | Readonly<{ blocked: true }>
      | Readonly<{ blocked: false; value: T }>
    >;
  }>;
  runtimeReload: Readonly<{
    reload(signal: AbortSignal): Promise<void>;
  }>;
  device: Readonly<{
    previewActivationCode?:
      | ((
          activationCode: string,
          signal: AbortSignal,
        ) => Promise<import("../../features/settings/settings-presenter").SettingsDeviceActivationPreviewResponse>)
      | undefined;
    reregister(
      input: Readonly<{
        activationCode: string;
        terminalName?: string;
      }>,
      signal: AbortSignal,
      onCredentialsCommitted: () => void,
    ): Promise<void>;
    resetRegistration(
      employeeBarcode: string,
      signal: AbortSignal,
    ): Promise<"completed" | "pending-recovery">;
    hasRegistrationRecoveryRisk?(): Promise<boolean>;
  }>;
  printer: RuntimePrinterAdapter;
  /**
   * 必须注入正式 fulfilment 手动开箱动作；该动作负责 CashDrawer.Open 权限、
   * cashier lease、持久事件和审计，设置组合禁止直接调用 printer.open。
   */
  cashDrawerTest: Readonly<{
    execute(): Promise<ControlledCashDrawerActionResult>;
  }>;
  scanner: Readonly<{
    status: "ready" | "unavailable";
    test(signal: AbortSignal): Promise<SettingsScannerTestResult>;
  }>;
  appUpdate: Readonly<{
    snapshot(): SettingsAppUpdateSnapshot;
    check(signal: AbortSignal): Promise<SettingsAppUpdateSnapshot>;
    restart(signal: AbortSignal): Promise<boolean>;
  }>;
}>;

/**
 * 把设置页的公开参数、硬件测试和危险动作门禁接入真实 POS 组合根。页面只能拿到
 * 零参数 presenter factory；活动购物车、SQLCipher 风险计数和可信 cashier lease
 * 均保留在闭包中。
 */
export function createProductionSettingsComposition(
  input: ProductionSettingsCompositionInput,
): SettingsRuntimeFactory {
  const terminal = normalizeTerminal(input.terminal);
  const squareSetup = input.squareSetup;

  const productionControl = new ProductionSettingsControl({
    readSnapshot: async (signal) => {
      throwIfAborted(signal);
      const [device, catalog, printer, printerStatus, paymentMethods] =
        await Promise.all([
          input.readDevicePresentation(),
          input.catalog.getActiveMetadata(),
          input.receiptSettings.get(),
          input.printer.getStatus(),
          input.paymentMethods?.load() ?? Promise.resolve({ useManualCard: false }),
        ]);
      throwIfAborted(signal);
      assertDeviceScope(device, terminal);
      return Object.freeze({
        apiBaseUrl: input.apiBaseUrl,
        appUpdate: input.appUpdate.snapshot(),
        catalog: catalog ?? emptyCatalog(),
        device,
        hardware: {
          printerStatus: printerStatus === "ready"
            ? "connected"
            : printerStatus === "unavailable"
              ? "unavailable"
              : "disconnected",
          scannerStatus: input.scanner.status,
          lastScannerValue: null,
        },
        linkly: {
          ...input.paymentConfiguration.availability.linkly,
          environment:
            input.paymentConfiguration.current?.linkly?.environment ??
            "Production",
        },
        paymentProvider:
          input.paymentConfiguration.current?.provider ?? null,
        paymentMethods,
        printer,
        square: {
          ...input.paymentConfiguration.availability.square,
          environment:
            input.paymentConfiguration.current?.square?.environment ??
            "Production",
          deviceId:
            input.paymentConfiguration.current?.square?.deviceId ?? "",
          locationId:
            input.paymentConfiguration.current?.square?.locationId ?? "",
        },
      } satisfies SettingsSnapshot);
    },
    catalog: {
      getRefreshState: input.catalog.getRefreshState,
      subscribeRefresh: input.catalog.subscribeRefresh,
      runExclusive: input.catalog.runExclusive,
      download: async (signal) => {
        throwIfAborted(signal);
        const result = await input.catalog.download(signal);
        throwIfAborted(signal);
        return result;
      },
      reset: async (signal) => {
        throwIfAborted(signal);
        const result = await input.catalog.reset(signal);
        throwIfAborted(signal);
        return result;
      },
    },
    payments: {
      test: (provider, configuration, signal, terminals) =>
        input.paymentConfiguration.test(
          provider,
          configuration,
          signal,
          terminals,
        ),
    },
    paymentConfiguration: {
      save: (configuration) =>
        input.paymentConfiguration.save(configuration),
    },
    ...(input.paymentMethods ? { paymentMethods: input.paymentMethods } : {}),
    paymentConfigurationTransition: input.paymentConfigurationTransition,
    ...(input.linklySetup
      ? {
          linklySetup: {
            pair: input.linklySetup.pair.bind(input.linklySetup),
            ...(input.linklySetup.selectTerminal
              ? {
                  selectTerminal:
                    input.linklySetup.selectTerminal.bind(input.linklySetup),
                }
              : {}),
          },
        }
      : {}),
    runtimeReload: input.runtimeReload,
    printer: {
      saveSettings: async (settings, signal) => {
        throwIfAborted(signal);
        await input.receiptSettings.save(settings);
        throwIfAborted(signal);
      },
      scan: async (signal) => {
        throwIfAborted(signal);
        const devices = await input.printer.scan(8_000);
        throwIfAborted(signal);
        return Object.freeze(
          devices.map((device) =>
            Object.freeze({
              id: device.id,
              name: device.name,
              transport: "bluetooth-le",
              preferred:
                device.name.trim().toLowerCase() ===
                "printer001",
            }),
          ),
        );
      },
      connect: async (peripheralId, signal) => {
        throwIfAborted(signal);
        await input.printer.connect(peripheralId);
        throwIfAborted(signal);
      },
      test: async (signal) => {
        throwIfAborted(signal);
        const settings = await input.receiptSettings.get();
        if (!settings.peripheralId) {
          throw new Error("SETTINGS_PRINTER_NOT_CONFIGURED");
        }
        await input.printer.connect(settings.peripheralId);
        throwIfAborted(signal);
        const result = await input.printer.print(
          `settings-test:${requiredId(input.createId())}`,
          buildPrinterTestDocument(
            settings,
            terminal.deviceCode,
            terminal.storeCode,
          ),
        );
        throwIfAborted(signal);
        if (result.status === "ambiguous") {
          throw Object.assign(
            new Error(SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN),
            { code: SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN },
          );
        }
        if (result.status !== "printed") {
          throw Object.assign(
            new Error("SETTINGS_PRINTER_TEST_NOT_CONFIRMED"),
            {
              code:
                result.errorCode ??
                "SETTINGS_PRINTER_TEST_NOT_CONFIRMED",
            },
          );
        }
      },
    },
    scanner: input.scanner,
    appUpdate: input.appUpdate,
    pendingData: {
      read: async (signal) => {
        throwIfAborted(signal);
        const durable = await input.pendingData.read();
        throwIfAborted(signal);
        return Object.freeze({
          ...durable,
          hasActiveCart:
            input.activeCart.getSnapshot().lines.length > 0,
        });
      },
    },
    apiConfiguration: input.apiConfiguration,
    device: input.device,
    receiptProfile: input.receiptProfile,
  });
  const control: SettingsControlPort = Object.assign(productionControl, {
    ...(squareSetup
      ? {
          squareSetup: Object.freeze({
            getSquareTokenStatus:
              squareSetup.getSquareTokenStatus.bind(squareSetup),
            listSquareLocations:
              squareSetup.listSquareLocations.bind(squareSetup),
            listSquareDevices:
              squareSetup.listSquareDevices.bind(squareSetup),
            listSquareDeviceCodes:
              squareSetup.listSquareDeviceCodes.bind(squareSetup),
            createSquareDeviceCode: async (
              environment,
              locationId,
              name,
              signal,
            ) => {
              throwIfAborted(signal);
              const idempotencyKey = requiredId(input.createId());
              return squareSetup.createSquareDeviceCode(
                {
                  environment,
                  idempotencyKey,
                  locationId,
                  name,
                },
                signal,
              );
            },
            getSquareDeviceCode:
              squareSetup.getSquareDeviceCode.bind(squareSetup),
          } satisfies SettingsSquareSetupControlPort),
        }
      : {}),
    ...(input.linklySetup
      ? {
          linklySetup: Object.freeze({
            readState: input.linklySetup.readState.bind(input.linklySetup),
            ...(input.linklySetup.readTerminals
              ? {
                  readTerminals:
                    input.linklySetup.readTerminals.bind(input.linklySetup),
                }
              : {}),
            ...(input.linklySetup.selectTerminal
              ? {
                  selectTerminal:
                    productionControl.selectLinklyTerminalGuarded.bind(
                      productionControl,
                    ),
                }
              : {}),
          }),
        }
      : {}),
    testCashDrawer: async (
      signal: AbortSignal,
    ): Promise<SettingsCashDrawerTestResult> => {
      throwIfAborted(signal);
      // 正式动作自行连接持久设置中的 peripheralId，并负责权限、lease 与审计。
      const result = await input.cashDrawerTest.execute();
      // 硬件动作可能已完成；此处不因随后 abort 改写终态或诱导用户重试。
      return mapCashDrawerTestResult(result);
    },
    clearSavedPrinter: (async (
      signal: AbortSignal,
      assertActive?: () => void,
    ): Promise<SettingsClearSavedPrinterResult> => {
      throwIfAborted(signal);
      const settings = await input.receiptSettings.get();
      throwIfAborted(signal);
      assertActive?.();
      await input.receiptSettings.save({
        ...settings,
        peripheralId: null,
      });
      // 只清除后续连接目标；现有连接由 fulfilment hardware tail 串行管理。
      return { status: "completed", errorCode: null };
    }) satisfies LeaseAwareClearSavedPrinter,
  });

  return createProductionSettingsRuntime({
    createSessionLease: () => input.currentCashier.createLease(),
    control,
    runDangerousExclusive: (operation) =>
      input.activeCart.runExclusive(async () => operation()),
    // 复用支付配置已注入的全局 transition，确保重置先封门、等待在途业务，
    // 再由组合根 barrier 按目录→购物车锁序读取最终 pending 快照。
    runDeviceRegistrationResetTransition: (operation) =>
      input.paymentConfigurationTransition.run(operation),
  });
}

function normalizeTerminal(input: TerminalScope): TerminalScope {
  return Object.freeze({
    storeCode: requiredText(input.storeCode, "store code"),
    deviceCode: requiredText(input.deviceCode, "device code"),
  });
}

function assertDeviceScope(
  device: SettingsDevicePresentation,
  terminal: TerminalScope,
): void {
  if (
    requiredText(device.storeCode, "device store code") !==
      terminal.storeCode ||
    requiredText(device.deviceCode, "device code") !== terminal.deviceCode
  ) {
    throw new Error("SETTINGS_DEVICE_SCOPE_MISMATCH");
  }
}

function emptyCatalog(): SettingsCatalogSnapshot {
  return Object.freeze({
    snapshotId: null,
    itemCount: 0,
    activatedAt: null,
  });
}

function buildPrinterTestDocument(
  settings: ReceiptPrinterSettings,
  deviceCode: string,
  storeCode: string,
): Uint8Array {
  const soldAtIso = new Date().toISOString();
  const totalCents = 100;
  return documentToEscPosBytes(
    buildSaleReceiptDocument({
      locale: settings.locale,
      paper: settings.paper,
      store: {
        brandName: settings.brandName,
        storeName: settings.storeName,
        address: settings.address,
        phone: settings.phone,
        abn: settings.abn,
        returnPolicy: settings.returnPolicy,
      },
      orderNumber: "TEST",
      soldAtIso,
      cashierName: "SYSTEM TEST",
      deviceCode,
      storeCode: settings.profileStoreCode || storeCode,
      lines: [
        {
          name: "Printer test item",
          lookupCode: "TEST-001",
          quantity: "1",
          discountCents: 0,
          totalCents,
        },
      ],
      subtotalCents: totalCents,
      discountCents: 0,
      totalCents,
      tenders: [{ method: "cash", amountCents: totalCents, reference: null }],
      cashChangeCents: 0,
      title: "===== TEST =====",
      statusText: "*** NOT A SALE ***",
    }),
  );
}

function mapCashDrawerTestResult(
  result: ControlledCashDrawerActionResult,
): SettingsCashDrawerTestResult {
  switch (result.state) {
    case "Completed":
      return { status: "completed", errorCode: result.errorCode };
    case "Unknown":
    case "Ambiguous":
    case "recovery-required":
      // 脉冲可能已经发出或终态未能耐久化，禁止把它降级成可重试失败。
      return { status: "unknown", errorCode: result.errorCode };
    case "Printed":
    case "Failed":
    case "not-retryable":
    case "not-found":
    case "denied":
      return { status: "failed", errorCode: result.errorCode };
  }
}

function requiredText(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) throw new Error(`Settings ${label} is required.`);
  return normalized;
}

function requiredId(value: string): string {
  const normalized = value.trim();
  if (!normalized || normalized.length > 160) {
    throw new Error("SETTINGS_OPERATION_ID_INVALID");
  }
  return normalized;
}

function throwIfAborted(signal: AbortSignal): void {
  if (signal.aborted) {
    throw Object.assign(
      new Error("Settings operation aborted."),
      { name: "AbortError" },
    );
  }
}
