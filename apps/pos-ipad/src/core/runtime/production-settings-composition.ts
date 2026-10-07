import type { CurrentCashierSession } from "./current-cashier-session";
import {
  DEFAULT_PAYMENT_METHOD_SETTINGS,
  type PaymentMethodSettings,
} from "@/features/settings/payment-method-settings";
import type { RuntimePrinterAdapter } from "./lazy-printer-adapter";
import {
  ProductionSettingsControl,
} from "./production-settings-control";
import { createProductionSettingsRuntime } from "./production-settings-runtime";

import type { ExternalCustomerDisplayPort } from "@/core/contracts/external-display";
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
  type SettingsPaymentSettingsInput,
  type SettingsPendingDataSnapshot,
  type SettingsReceiptProfileDraft,
  type SettingsReceiptProfileSyncResult,
  type SettingsLinklyPairingPort,
  type SettingsLinklySetupControlPort,
  type SettingsScannerTestResult,
  type SettingsSquareSetupControlPort,
  type SettingsSnapshot,
} from "@/features/settings/settings-presenter";
import type { SettingsRuntimeFactory } from "@/features/settings/settings-runtime";
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
  paymentMethods?: Readonly<{
    load(): Promise<PaymentMethodSettings>;
    save(input: PaymentMethodSettings): Promise<unknown>;
  }>;
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
  /**
   * 设置快照读取失败时上报（中心日志）。页面仍按原逻辑显示 load-failed，
   * 这里只补充“哪一步失败、异常是什么”，便于线上设备排查。
   */
  reportSnapshotFailure?:
    | ((stage: SettingsSnapshotStage, error: unknown) => void)
    | undefined;
  /**
   * 打印机扫描异常上报（中心日志）：原生扫描失败，或个别设备名被清洗、坏 ID 被跳过。
   * 只上报计数，不带设备名与地址。
   */
  reportPrinterScanIssue?: ((issue: PrinterScanIssue) => void) | undefined;
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
    /** 「立即同步」；缺省表示不支持，设置页按同步失败处理。 */
    sync?:
      | ((signal: AbortSignal) => Promise<SettingsReceiptProfileSyncResult>)
      | undefined;
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
  externalDisplay?: ExternalCustomerDisplayPort | undefined;
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
  let externalDisplayEnabled = input.externalDisplay !== undefined;
  let displayRevision = 0;

  const productionControl = new ProductionSettingsControl({
    readSnapshot: async (signal) => {
      throwIfAborted(signal);
      const readStage = <T,>(
        stage: SettingsSnapshotStage,
        operation: () => Promise<T>,
      ): Promise<T> => readSnapshotStage(input, stage, operation);
      const [device, catalog, printer, printerStatus, displayStatus, paymentMethods] =
        await Promise.all([
          readStage("device", () => input.readDevicePresentation()),
          readStage("catalog", () => input.catalog.getActiveMetadata()),
          readStage("receipt-settings", () => input.receiptSettings.get()),
          readStage("printer-status", () => input.printer.getStatus()),
          readStage("external-display", () =>
            input.externalDisplay?.getStatus() ??
              Promise.resolve("disconnected" as const),
          ),
          readStage("payment-methods", () =>
            input.paymentMethods?.load() ??
              Promise.resolve(DEFAULT_PAYMENT_METHOD_SETTINGS),
          ),
        ]);
      throwIfAborted(signal);
      try {
        assertDeviceScope(device, terminal);
      } catch (error) {
        reportSnapshotFailure(input, "device-scope", error);
        throw error;
      }
      return Object.freeze({
        apiBaseUrl: input.apiBaseUrl,
        appUpdate: input.appUpdate.snapshot(),
        catalog: catalog ?? emptyCatalog(),
        device,
        externalDisplay: {
          available: input.externalDisplay !== undefined,
          enabled: externalDisplayEnabled,
          status: displayStatus === "ready"
            ? "connected"
            : input.externalDisplay
              ? "disconnected"
              : "unavailable",
        },
        hardware: {
          printerStatus: printerStatus === "ready"
            ? "connected"
            : printerStatus === "unavailable"
              ? "unavailable"
              : "disconnected",
          scannerStatus: input.scanner.status,
          externalDisplayStatus: displayStatus === "ready"
            ? "connected"
            : input.externalDisplay
              ? "disconnected"
              : "unavailable",
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
    paymentConfigurationTransition: input.paymentConfigurationTransition,
    ...(input.paymentMethods ? { paymentMethods: {
      save: async (settings: PaymentMethodSettings) => { await input.paymentMethods!.save(settings); },
    } } : {}),
    ...(input.linklySetup
      ? {
          linklySetup: {
            pair: input.linklySetup.pair.bind(input.linklySetup),
            ...(input.linklySetup.assignTerminal
              ? { assignTerminal: input.linklySetup.assignTerminal.bind(input.linklySetup) }
              : {}),
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
        let devices: Awaited<ReturnType<typeof input.printer.scan>>;
        try {
          devices = await input.printer.scan(8_000);
        } catch (error) {
          reportPrinterScanIssue(input, { kind: "failed", error });
          throw error;
        }
        throwIfAborted(signal);
        // 设置页列出附近全部 BLE 设备；单个设备的异常广播名/ID 不能让整次扫描失败。
        const scanned = sanitizeScannedPrinters(devices);
        if (scanned.sanitizedCount > 0 || scanned.skippedCount > 0) {
          reportPrinterScanIssue(input, {
            kind: "sanitized",
            sanitizedCount: scanned.sanitizedCount,
            skippedCount: scanned.skippedCount,
            totalCount: devices.length,
          });
        }
        return Object.freeze(
          scanned.devices.map((device) =>
            Object.freeze({
              id: device.id,
              name: device.name,
              transport: "bluetooth-le",
              preferred: device.name.toLowerCase() === "printer001",
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
    display: {
      setEnabled: async (enabled, signal) => {
        throwIfAborted(signal);
        if (!input.externalDisplay) {
          throw new Error("SETTINGS_EXTERNAL_DISPLAY_UNAVAILABLE");
        }
        await input.externalDisplay.setEnabled(enabled);
        throwIfAborted(signal);
        externalDisplayEnabled = enabled;
      },
      test: async (signal) => {
        throwIfAborted(signal);
        if (!input.externalDisplay) {
          throw new Error("SETTINGS_EXTERNAL_DISPLAY_UNAVAILABLE");
        }
        displayRevision += 1;
        await input.externalDisplay.publish({
          revision: displayRevision,
          mode: "idle",
          items: [],
          gst: { currency: "AUD", cents: 0 },
          discount: { currency: "AUD", cents: 0 },
          total: { currency: "AUD", cents: 0 },
          change: { currency: "AUD", cents: 0 },
          advert: null,
        });
        throwIfAborted(signal);
      },
    },
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
            supportsTerminalAssignment:
              input.linklySetup.supportsTerminalAssignment === true,
            readState: input.linklySetup.readState.bind(input.linklySetup),
            ...(input.linklySetup.readTerminals
              ? {
                  readTerminals:
                    input.linklySetup.readTerminals.bind(input.linklySetup),
                }
              : {}),
            ...(input.linklySetup.testTerminalConnection
              ? {
                  testTerminalConnection:
                    input.linklySetup.testTerminalConnection.bind(input.linklySetup),
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

export type SettingsSnapshotStage =
  | "device"
  | "catalog"
  | "receipt-settings"
  | "printer-status"
  | "external-display"
  | "payment-methods"
  | "device-scope";

async function readSnapshotStage<T>(
  input: Pick<ProductionSettingsCompositionInput, "reportSnapshotFailure">,
  stage: SettingsSnapshotStage,
  operation: () => Promise<T>,
): Promise<T> {
  try {
    return await operation();
  } catch (error) {
    reportSnapshotFailure(input, stage, error);
    throw error;
  }
}

function reportSnapshotFailure(
  input: Pick<ProductionSettingsCompositionInput, "reportSnapshotFailure">,
  stage: SettingsSnapshotStage,
  error: unknown,
): void {
  try {
    input.reportSnapshotFailure?.(stage, error);
  } catch {
    // 日志旁路失败不能改变设置页原有的失败语义。
  }
}

export type PrinterScanIssue =
  | Readonly<{ kind: "failed"; error: unknown }>
  | Readonly<{
      kind: "sanitized";
      sanitizedCount: number;
      skippedCount: number;
      totalCount: number;
    }>;

function reportPrinterScanIssue(
  input: Pick<ProductionSettingsCompositionInput, "reportPrinterScanIssue">,
  issue: PrinterScanIssue,
): void {
  try {
    input.reportPrinterScanIssue?.(issue);
  } catch {
    // 日志旁路失败不能改变扫描结果或失败语义。
  }
}

// 与设置 presenter 的公开文本/标识校验保持一致：名称 ≤120、标识 ≤128，均不含控制字符。
const SCANNED_PRINTER_NAME_MAX_LENGTH = 120;
const SCANNED_PRINTER_ID_MAX_LENGTH = 128;
const SCANNED_PRINTER_FALLBACK_NAME = "Bluetooth Printer";
// 判定用不带 g 的正则，避免 test() 受 lastIndex 状态影响；替换用全局版本。
const CONTROL_CHARACTER = /[\u0000-\u001F\u007F-\u009F]/u;
const CONTROL_CHARACTERS = /[\u0000-\u001F\u007F-\u009F]/gu;

/**
 * 逐条清洗原生扫描结果：名称去控制字符、按代码点截断到上限，清洗后为空用原生同款兜底名；
 * ID 不合规（非字符串、超长、含控制字符）的设备直接跳过，因为连接时无法安全回传。
 */
export function sanitizeScannedPrinters(
  devices: readonly Readonly<{ id: unknown; name: unknown }>[],
): Readonly<{
  devices: readonly Readonly<{ id: string; name: string }>[];
  sanitizedCount: number;
  skippedCount: number;
}> {
  let sanitizedCount = 0;
  let skippedCount = 0;
  const usable: Readonly<{ id: string; name: string }>[] = [];
  for (const device of devices) {
    const id = typeof device.id === "string" ? device.id.trim() : "";
    if (
      !id ||
      id.length > SCANNED_PRINTER_ID_MAX_LENGTH ||
      CONTROL_CHARACTER.test(id)
    ) {
      skippedCount += 1;
      continue;
    }
    const rawName = typeof device.name === "string" ? device.name : "";
    const cleaned = truncateByCodePoint(
      rawName.replace(CONTROL_CHARACTERS, "").trim(),
      SCANNED_PRINTER_NAME_MAX_LENGTH,
    ).trim();
    const name = cleaned || SCANNED_PRINTER_FALLBACK_NAME;
    if (name !== rawName.trim()) sanitizedCount += 1;
    usable.push(Object.freeze({ id, name }));
  }
  return Object.freeze({
    devices: Object.freeze(usable),
    sanitizedCount,
    skippedCount,
  });
}

/** 按 UTF-16 长度截断但不拆开代理对，避免留下半个 emoji。 */
function truncateByCodePoint(value: string, maxLength: number): string {
  if (value.length <= maxLength) return value;
  let result = "";
  for (const codePoint of value) {
    if (result.length + codePoint.length > maxLength) break;
    result += codePoint;
  }
  return result;
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
