import {
  resolveSettingsAccess,
  type SettingsAccess,
} from "./settings-authorization";
import {
  mergeSettingsSquareDevices,
  normalizeSettingsSquareDeviceId,
  SETTINGS_SQUARE_SANDBOX_CHECKOUT_DEVICES,
  type SettingsSquareDevice,
  type SettingsSquareDeviceCode,
  type SettingsSquareEnvironment,
  type SettingsSquareLocation,
  type SettingsSquareSetupPort,
  type SettingsSquareTokenStatus,
} from "@hb/pos-domain/features/settings/settings-square-setup";

import {
  DEFAULT_RECEIPT_PRINTER_SETTINGS,
  type ReceiptPrinterSettings,
} from "@/core/db/pos-settings-repository";
import { isTrustedLocalHbposApiOrigin } from "@hb/pos-domain/core/security/pos-api-addresses";
import { RECEIPT_TERMS_TEXT_MAX_LENGTH } from "@hb/pos-receipt-core/features/receipts/receipt-terms-text";
import type { CatalogRefreshState } from "@/features/catalog/catalog-refresh-coordinator";
import { parseDeviceActivationCode } from "@/core/security/device-activation-code";
import type {
  PendingWorkBlocker,
  PendingWorkSnapshot,
} from "@hb/pos-domain";

import {
  DEFAULT_PAYMENT_METHOD_SETTINGS,
  normalizePaymentMethodSettings,
  type PaymentMethodSettings,
} from "./payment-method-settings";

export type SettingsPane =
  "general" | "payments" | "peripherals" | "device" | "hardware";

export type PaymentEnvironment = "Sandbox" | "Production";
export type SettingsPaymentProvider = "square" | "linkly";

export type SettingsCatalogSnapshot = Readonly<{
  snapshotId: string | null;
  itemCount: number;
  activatedAt: string | null;
}>;

export type SettingsPaymentProviderSnapshot = Readonly<{
  available: boolean;
  blockerCode: string | null;
  environment: PaymentEnvironment;
}>;

export type SettingsSquareSnapshot = SettingsPaymentProviderSnapshot &
  Readonly<{
    deviceId: string;
    locationId: string;
  }>;

export type SettingsLinklyHealthCheck = Readonly<{
  code: string;
  isReady: boolean;
  message: string | null;
}>;

export type SettingsLinklyHealthSnapshot = Readonly<{
  environment: PaymentEnvironment;
  storeCode: string;
  deviceCode: string;
  isReady: boolean;
  checks: readonly SettingsLinklyHealthCheck[];
}>;

export type SettingsLinklyPairResult = Readonly<{
  status: "completed" | "unknown";
}>;

export type SettingsLinklyPairingState =
  | "Unpaired"
  | "Ready"
  | "Unknown"
  | "NeedsRepair";

export type SettingsLinklyTerminal = Readonly<{
  terminalId: string;
  laneNo: number;
  displayName: string;
  pairingState: SettingsLinklyPairingState;
  isBusy: boolean;
  isReady: boolean;
  lastHealthStatus: string | null;
  lastHealthAt: string | null;
  assignedDeviceCode?: string | null;
  assignmentRevision?: number;
  terminalVersion?: string | null;
}>;

export type SettingsLinklyAssignableDevice = Readonly<{
  deviceCode: string;
  deviceSystem: string;
  isAvailable: boolean;
  selectedTerminalId: string | null;
  selectionRevision: number;
}>;

export type SettingsLinklyTerminalSelectionSnapshot = Readonly<{
  environment: PaymentEnvironment;
  mode: "Active" | "Legacy" | "Draft";
  selectedTerminalId: string | null;
  selectionRevision: number;
  terminals: readonly SettingsLinklyTerminal[];
  devices?: readonly SettingsLinklyAssignableDevice[];
  /** 旧服务缺少版本字段时必须隐藏写操作，不能合成可写版本。 */
  lineManagementSupported?: boolean;
}>;

export type SettingsLinklyConnectionTestResult = Readonly<{
  terminalId: string;
  environment: PaymentEnvironment;
  terminalVersion: string;
  assignedDeviceCode: string | null;
  assignmentRevision: number;
  succeeded: boolean;
  status: "connected" | "unreachable" | "unknown" | "needs-repair";
  checkedAt: string;
  message: string;
  responseCode: string | null;
}>;

export type SettingsLinklyTerminalAssignmentInput = Readonly<{
  terminalId: string;
  terminalVersion: string;
  assignedDeviceCode: string | null;
  assignmentRevision: number;
  targetDeviceCode: string | null;
  expectedTargetTerminalId: string | null;
  expectedTargetSelectionRevision: number;
}>;

export interface SettingsLinklySetupReadPort {
  readonly supportsTerminalAssignment?: boolean;
  readState(
    environment: PaymentEnvironment,
    signal: AbortSignal,
    terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
  ): Promise<SettingsLinklyHealthSnapshot>;
  readTerminals?(
    environment: PaymentEnvironment,
    signal: AbortSignal,
  ): Promise<SettingsLinklyTerminalSelectionSnapshot>;
  selectTerminal?(
    environment: PaymentEnvironment,
    terminalId: string,
    expectedRevision: number,
    signal: AbortSignal,
  ): Promise<SettingsLinklyTerminalSelectionSnapshot>;
  testTerminalConnection?(
    environment: PaymentEnvironment,
    terminal: Readonly<{ terminalId: string; terminalVersion: string; assignedDeviceCode: string | null; assignmentRevision: number }>,
    signal: AbortSignal,
  ): Promise<SettingsLinklyConnectionTestResult>;
  assignTerminal?(
    environment: PaymentEnvironment,
    input: SettingsLinklyTerminalAssignmentInput,
    signal: AbortSignal,
  ): Promise<SettingsLinklyTerminalSelectionSnapshot>;
}

/** Settings 页面只接触公开 health/终端摘要与选择；配对写能力不暴露给 Presenter。 */
export type SettingsLinklySetupControlPort = SettingsLinklySetupReadPort;

/** 配对写端口只由危险动作路径持有。 */
export interface SettingsLinklyPairingPort {
  pair(
    environment: PaymentEnvironment,
    terminalId: string,
    pairCode: string,
    signal: AbortSignal,
  ): Promise<SettingsLinklyPairResult>;
}

export type SettingsLinklyHealthResource = Readonly<{
  kind: "idle" | "loading" | "selection-required" | "ready" | "failed";
  value: SettingsLinklyHealthSnapshot | null;
}>;

export type SettingsLinklyLogonTestState = Readonly<{
  environment: PaymentEnvironment;
  status: "idle" | "running" | "passed" | "failed";
}>;

export type SettingsLinklyConnectionTestState = Readonly<{
  key: string;
  terminalId: string;
  kind: "running" | "ready" | "failed";
  result: SettingsLinklyConnectionTestResult | null;
  /** 只保留可本地化的原因，不把上游错误正文带入界面。 */
  failureReason?: "busy" | "changed-or-busy" | "changed" | "access" | "unknown";
}>;

export type SettingsLinklySetupState = Readonly<{
  health: SettingsLinklyHealthResource;
  terminals: Readonly<{
    kind: "idle" | "loading" | "ready" | "failed" | "switching";
    value: SettingsLinklyTerminalSelectionSnapshot | null;
  }>;
  logonTest: SettingsLinklyLogonTestState;
  connectionTests: Readonly<Record<string, SettingsLinklyConnectionTestState>>;
  /** 仅用于清空 UI 瞬态 PairCode，不保存 PairCode 本身。 */
  pairCodeResetToken: number;
}>;

export type SettingsExternalDisplaySnapshot = Readonly<{
  available: boolean;
  enabled: boolean;
  status: "connected" | "disconnected" | "unavailable";
}>;

export type SettingsHardwareSnapshot = Readonly<{
  printerStatus: "connected" | "disconnected" | "unavailable";
  scannerStatus: "ready" | "unavailable";
  externalDisplayStatus: "connected" | "disconnected" | "unavailable";
  lastScannerValue: string | null;
}>;

export type SettingsAppUpdateSnapshot = Readonly<{
  channel: string;
  currentVersion: string;
  availableVersion: string | null;
  updateRequired: boolean;
  restartAvailable: boolean;
}>;

export type SettingsDeviceSnapshot = Readonly<{
  deviceCode: string;
  storeCode: string;
  storeName: string;
  terminalName: string;
}>;

export type SettingsDeviceActivationPreviewResponse = Readonly<{
  isAllowed: boolean;
  reasonCode?: string | null;
  storeCode?: string | null;
  storeName?: string | null;
  deviceSystem?: string | null;
  expiresAtUtc?: string | null;
  message?: string | null;
}>;

export type SettingsDeviceActivationPreview = Readonly<{
  activationCode: string;
  storeCode: string;
  storeName: string;
  deviceSystem: string;
  expiresAtUtc: string;
}>;

export type SettingsSnapshot = Readonly<{
  apiBaseUrl: string;
  appUpdate: SettingsAppUpdateSnapshot;
  catalog: SettingsCatalogSnapshot;
  device: SettingsDeviceSnapshot;
  externalDisplay: SettingsExternalDisplaySnapshot;
  hardware: SettingsHardwareSnapshot;
  linkly: SettingsPaymentProviderSnapshot;
  paymentProvider: SettingsPaymentProvider | null;
  paymentMethods?: PaymentMethodSettings;
  printer: ReceiptPrinterSettings;
  square: SettingsSquareSnapshot;
}>;

export type SettingsReceiptProfileDraft = Readonly<{
  storeCode: string;
  brandName: string;
  storeName: string;
  address: string;
  phone: string;
  abn: string;
  returnPolicy: string;
  /** 退款券「VOUCHER TERMS」自定义正文；空串 = 未定制（默认文案）。 */
  voucherTerms: string;
  /** 分期小票「INSTALLMENT TERMS」自定义正文；空串 = 未定制（默认文案）。 */
  installmentTerms: string;
}>;

/** 「立即同步」失败原因归类：设置页只展示这几类文案。 */
export type SettingsReceiptProfileSyncFailure =
  | "offline"
  | "unauthorized"
  | "unsupported"
  | "invalid"
  | "failed";

/**
 * 总部下发小票资料的一次手动同步结果。
 * updated / up-to-date 带上同步后落盘的最新设置，设置页据此刷新只读展示的六项资料与版本。
 */
export type SettingsReceiptProfileSyncResult =
  | Readonly<{
      status: "updated" | "up-to-date";
      version: number;
      printer: ReceiptPrinterSettings;
    }>
  | Readonly<{ status: "not-published" }>
  | Readonly<{
      status: "failed";
      reason: SettingsReceiptProfileSyncFailure;
    }>;

export type SettingsPaymentDraft = Readonly<{
  square: Readonly<{
    environment: PaymentEnvironment;
    deviceId: string;
    locationId: string;
  }>;
  linkly: Readonly<{
    environment: PaymentEnvironment;
  }>;
}>;

export type SettingsPaymentSettingsInput =
  | Readonly<{
      provider: "square";
      square: SettingsPaymentDraft["square"];
      linkly: null;
    }>
  | Readonly<{
      provider: "linkly";
      square: null;
      linkly: SettingsPaymentDraft["linkly"];
    }>;

export type SettingsSquareRequestKind =
  | "disabled"
  | "idle"
  | "loading"
  | "ready"
  | "empty"
  | "failed";

export type SettingsSquareValueState<T> = Readonly<{
  kind: SettingsSquareRequestKind;
  value: T | null;
}>;

export type SettingsSquareListState<T> = Readonly<{
  kind: SettingsSquareRequestKind;
  items: readonly T[];
}>;

export type SettingsSquareSetupState = Readonly<{
  available: boolean;
  token: SettingsSquareValueState<SettingsSquareTokenStatus>;
  locations: SettingsSquareListState<SettingsSquareLocation>;
  devices: SettingsSquareListState<SettingsSquareDevice>;
  deviceCodes: SettingsSquareListState<SettingsSquareDeviceCode>;
  selectedLocationId: string;
  selectedDeviceId: string;
  selectedDeviceCodeId: string;
  devicesLoadedForLocationId: string | null;
  deviceCodesLoadedForLocationId: string | null;
}>;

/**
 * 设置 UI 不接触幂等键；组合根必须在每次 create 点击时生成一次并转发到底层 API。
 */
export interface SettingsSquareSetupControlPort
  extends Omit<SettingsSquareSetupPort, "createSquareDeviceCode"> {
  createSquareDeviceCode(
    environment: SettingsSquareEnvironment,
    locationId: string,
    name: string,
    signal: AbortSignal,
  ): Promise<SettingsSquareDeviceCode>;
}

export type SettingsPendingDataSnapshot = PendingWorkSnapshot;

export type SettingsDeviceReregistrationPreflightResult =
  | Readonly<{ status: "ready" }>
  | Readonly<{
      status: "blocked";
      reason: "pending-local-data";
      blockers: readonly PendingWorkBlocker[];
    }>
  | Readonly<{
      status: "blocked";
      reason: "safety-check-failed";
    }>;

export type SettingsDeviceReregistrationPreflightState =
  | Readonly<{ kind: "idle" | "checking" | "ready" | "failed" }>
  | Readonly<{
      kind: "blocked";
      blockers: readonly PendingWorkBlocker[];
    }>;

export type SettingsPrinterDevice = Readonly<{
  id: string;
  name: string;
  transport: string;
  preferred: boolean;
}>;

export const SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN =
  "SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN";

export type SettingsCashDrawerTestResult = Readonly<{
  status: "completed" | "unknown" | "failed";
  errorCode: string | null;
}>;

export type SettingsClearSavedPrinterResult = Readonly<{
  status: "completed" | "cleared-disconnect-failed";
  errorCode: string | null;
}>;

export type SettingsScannerTestResult = Readonly<{
  source: "camera" | "hid";
  value: string;
}>;

export type SettingsDangerousConfirmation =
  | Readonly<{
      kind: "change-api-address";
      apiBaseUrl: string;
    }>
  | Readonly<{
      kind: "change-payment-settings";
      input: SettingsPaymentSettingsInput;
    }>
  | Readonly<{
      kind: "pair-linkly";
      environment: PaymentEnvironment;
      terminalId: string;
      pairCode: string;
      terminalLabel?: string;
    }>
  | Readonly<{
      kind: "assign-linkly-terminal";
      environment: PaymentEnvironment;
      input: SettingsLinklyTerminalAssignmentInput;
      terminalLabel: string;
      targetDeviceCode: string | null;
      replacedTerminalLabel: string | null;
      affectsCurrentDevice: boolean;
    }>
  | Readonly<{ kind: "reset-catalog" }>
  | Readonly<{
      kind: "reregister-device";
      activationCode: string;
      currentStoreCode: string;
      preview: SettingsDeviceActivationPreview;
      terminalName?: string;
    }>
  | Readonly<{ kind: "reset-device-registration" }>
  | Readonly<{ kind: "restart-app" }>;

export type SettingsDangerousActionResult =
  | Readonly<{
      status: "blocked";
      reason: "candidate-unreachable" | "safety-check-failed";
    }>
  | Readonly<{
      status: "blocked";
      reason: "pending-local-data";
      blockers: readonly PendingWorkBlocker[];
    }>
  | Readonly<{
      status: "completed";
      kind:
        | "change-api-address"
        | "change-payment-settings"
        | "pair-linkly"
        | "reregister-device"
        | "reset-device-registration"
        | "restart-app";
    }>
  | Readonly<{
      status: "completed";
      kind: "assign-linkly-terminal";
      terminals: SettingsLinklyTerminalSelectionSnapshot;
    }>
  | Readonly<{
      status: "pending-recovery";
      kind: "reset-device-registration";
    }>
  | Readonly<{
      status: "committed-reload-required";
      kind: "reregister-device";
    }>
  | Readonly<{
      status: "unknown";
      kind: "pair-linkly";
    }>
  | Readonly<{
      status: "completed";
      kind: "reset-catalog";
      catalog: SettingsCatalogSnapshot;
    }>;

export interface SettingsControlPort {
  squareSetup?: SettingsSquareSetupControlPort | undefined;
  /** 只允许读取 health；配对必须经过 executeDangerousAction。 */
  linklySetup?: SettingsLinklySetupReadPort | undefined;
  loadSnapshot(signal: AbortSignal): Promise<SettingsSnapshot>;
  /** 组合根在互斥临界区内复核权限、cashier lease 与未决支付后保存。 */
  savePaymentMethods?(input: PaymentMethodSettings, signal: AbortSignal): Promise<void>;
  getCatalogRefreshState(): CatalogRefreshState;
  subscribeCatalogRefresh(listener: () => void): () => void;
  /** 凭据已不可逆提交后的脱敏终态通知；无 payload，必须由实现只发布一次。 */
  subscribeDeviceReregistrationCommitted(listener: () => void): () => void;
  downloadCatalog(signal: AbortSignal): Promise<SettingsCatalogSnapshot>;
  /**
   * 只探测候选地址，不保存配置、不重载运行时。
   */
  testApiAddress(
    apiBaseUrl: string,
    signal: AbortSignal,
  ): Promise<boolean>;
  previewDeviceActivationCode?:
    | ((
        activationCode: string,
        signal: AbortSignal,
      ) => Promise<SettingsDeviceActivationPreviewResponse>)
    | undefined;
  preflightDeviceReregistration(
    signal: AbortSignal,
  ): Promise<SettingsDeviceReregistrationPreflightResult>;
  testPaymentProvider(
    provider: "square" | "linkly",
    input: SettingsPaymentSettingsInput,
    signal: AbortSignal,
    terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
  ): Promise<void>;
  savePrinterSettings(
    settings: ReceiptPrinterSettings,
    signal: AbortSignal,
  ): Promise<void>;
  loadReceiptProfile(signal: AbortSignal): Promise<SettingsReceiptProfileDraft | null>;
  /**
   * 已应用总部下发资料（profileVersion > 0）时的「立即同步」：立刻跑一轮同步并返回结果。
   * 缺省表示该运行时不支持，设置页按同步失败处理。
   */
  syncReceiptProfile?:
    | ((signal: AbortSignal) => Promise<SettingsReceiptProfileSyncResult>)
    | undefined;
  scanPrinters(signal: AbortSignal): Promise<readonly SettingsPrinterDevice[]>;
  connectPrinter(peripheralId: string, signal: AbortSignal): Promise<void>;
  testPrinter(signal: AbortSignal): Promise<void>;
  /**
   * 可选能力由生产运行时转发到受 CashDrawer.Open 权限、lease 与审计保护的动作。
   */
  testCashDrawer?:
    | ((signal: AbortSignal) => Promise<SettingsCashDrawerTestResult>)
    | undefined;
  clearSavedPrinter?:
    | ((signal: AbortSignal) => Promise<SettingsClearSavedPrinterResult>)
    | undefined;
  testScanner(signal: AbortSignal): Promise<SettingsScannerTestResult>;
  setExternalDisplayEnabled(
    enabled: boolean,
    signal: AbortSignal,
  ): Promise<void>;
  testExternalDisplay(signal: AbortSignal): Promise<void>;
  checkForAppUpdate(signal: AbortSignal): Promise<SettingsAppUpdateSnapshot>;

  /**
   * 组合根必须在一个互斥临界区内重新读取活动购物车、待同步销售/退款、
   * 未决支付与耐久写入，并在临界区释放前执行动作，禁止新业务状态穿插。
   * API 切换还必须先 GET 候选地址的 /api/v1/health；失败时保留旧地址。
   * signal 中止时，硬件监听和协调器租约都必须立即释放。
   */
  executeDangerousAction(
    action: SettingsDangerousConfirmation,
    signal: AbortSignal,
    employeeBarcode?: string,
  ): Promise<SettingsDangerousActionResult>;
}

export type SettingsStatusCode =
  | "payment-method-settings-blocked"
  | "payment-method-settings-unavailable"
  | "api-address-saved"
  | "api-health-check-failed"
  | "api-health-check-passed"
  | "app-restart-requested"
  | "app-update-check-failed"
  | "app-update-checked"
  | "cash-drawer-test-failed"
  | "cash-drawer-test-passed"
  | "cash-drawer-test-unknown"
  | "catalog-download-failed"
  | "catalog-downloaded"
  | "catalog-reset"
  | "catalog-reset-failed"
  | "device-reregister-failed"
  | "device-reregister-restart-required"
  | "device-reregister-started"
  | "device-activation-preview-failed"
  | "device-registration-reset-barcode-required"
  | "device-registration-reset-completed"
  | "device-registration-reset-failed"
  | "device-registration-reset-pending-recovery"
  | "display-setting-failed"
  | "display-setting-saved"
  | "display-test-failed"
  | "display-test-passed"
  | "invalid-api-address"
  | "invalid-device-registration"
  | "load-failed"
  | "linkly-health-load-failed"
  | "linkly-pair-code-invalid"
  | "linkly-pair-failed"
  | "linkly-pair-code-rejected"
  | "linkly-pair-credentials-required"
  | "linkly-pair-busy"
  | "linkly-pair-unknown"
  | "linkly-paired"
  | "linkly-setup-required"
  | "linkly-terminal-selected"
  | "linkly-terminal-selection-blocked"
  | "linkly-terminal-assignment-conflict"
  | "linkly-terminal-session-active"
  | "linkly-terminal-revision-conflict"
  | "linkly-terminal-switch-failed"
  | "payment-settings-invalid"
  | "payment-settings-save-failed"
  | "payment-settings-saved"
  | "payment-test-failed"
  | "payment-test-unconfirmed"
  | "payment-test-passed"
  | "pending-local-data"
  | "permission-required"
  | "printer-connect-failed"
  | "printer-connected"
  | "printer-connected-save-failed"
  | "printer-clear-failed"
  | "printer-cleared"
  | "printer-cleared-disconnect-failed"
  | "printer-bluetooth-authorization-pending"
  | "printer-bluetooth-permission-required"
  | "printer-bluetooth-powered-off"
  | "printer-bluetooth-restricted"
  | "printer-scan-failed"
  | "printer-scan-finished"
  | "printer-settings-save-failed"
  | "printer-settings-saved"
  | "printer-test-failed"
  | "printer-test-passed"
  | "printer-test-unknown"
  | "receipt-profile-load-failed"
  | "receipt-profile-loaded"
  | "receipt-profile-not-published"
  | "receipt-profile-sync-failed"
  | "receipt-profile-sync-forbidden"
  | "receipt-profile-sync-invalid"
  | "receipt-profile-sync-offline"
  | "receipt-profile-sync-unsupported"
  | "receipt-profile-synced"
  | "receipt-profile-up-to-date"
  | "restart-failed"
  | "safety-check-failed"
  | "scanner-test-failed"
  | "scanner-test-passed";

export type SettingsState = Readonly<{
  access: SettingsAccess;
  activePane: SettingsPane;
  apiBaseUrl: string;
  apiAddressDraft: string;
  appUpdate: SettingsAppUpdateSnapshot;
  busy: boolean;
  catalog: SettingsCatalogSnapshot;
  catalogRefresh: CatalogRefreshState;
  confirmation: SettingsDangerousConfirmation | null;
  device: SettingsDeviceSnapshot;
  deviceActivationCodeDraft: string;
  deviceActivationPreview: SettingsDeviceActivationPreview | null;
  deviceReregistrationPreflight: SettingsDeviceReregistrationPreflightState;
  externalDisplay: SettingsExternalDisplaySnapshot;
  hardware: SettingsHardwareSnapshot;
  kind: "idle" | "loading" | "ready" | "unauthorized" | "failed";
  linkly: SettingsPaymentProviderSnapshot;
  linklyDraft: SettingsPaymentDraft["linkly"];
  linklySetup: SettingsLinklySetupState | null;
  paymentProvider: SettingsPaymentProvider | null;
  paymentProviderDraft: SettingsPaymentProvider | null;
  paymentMethods: PaymentMethodSettings;
  paymentMethodsDraft: PaymentMethodSettings;
  paymentMethodsAvailable: boolean;
  printer: ReceiptPrinterSettings;
  printerDevices: readonly SettingsPrinterDevice[];
  square: SettingsSquareSnapshot;
  squareDraft: SettingsPaymentDraft["square"];
  squareDeviceCodeNameDraft: string;
  squareSetup: SettingsSquareSetupState;
  statusCode: SettingsStatusCode | null;
  terminalNameDraft: string;
}>;

export type SettingsPresenterOptions = Readonly<{
  permissions: readonly string[];
  port: SettingsControlPort;
}>;

/**
 * Settings 只编排公开配置与窄硬件端口。支付凭据、设备密钥、数据库和 HTTP
 * transport 都不能进入 state；危险动作还必须通过待同步数据的 fail-closed 门禁。
 */
export class SettingsPresenter {
  private readonly listeners = new Set<() => void>();
  private readonly lifetime = new AbortController();
  private catalogRefreshUnsubscribe: () => void = () => undefined;
  private deviceReregistrationCommittedUnsubscribe: () => void =
    () => undefined;
  private state: SettingsState;
  private destroyed = false;
  private loadGeneration = 0;
  private squareTokenGeneration = 0;
  private squareLocationsGeneration = 0;
  private squareDevicesGeneration = 0;
  private squareDeviceCodesGeneration = 0;
  private linklySetupGeneration = 0;
  private actionInFlight: Promise<void> | null = null;
  private catalogRefreshInFlight: Promise<void> | null = null;

  public constructor(private readonly options: SettingsPresenterOptions) {
    const access = resolveSettingsAccess(options.permissions);
    this.state = initialState(
      access,
      options.port.getCatalogRefreshState(),
      options.port.squareSetup !== undefined,
      options.port.linklySetup !== undefined,
    );
    this.catalogRefreshUnsubscribe =
      options.port.subscribeCatalogRefresh(
        this.handleCatalogRefreshChanged,
      );
    this.deviceReregistrationCommittedUnsubscribe =
      options.port.subscribeDeviceReregistrationCommitted(() => {
        if (
          this.destroyed ||
          this.state.statusCode === "device-reregister-restart-required"
        ) {
          return;
        }
        this.patch({
          confirmation: null,
          statusCode: "device-reregister-restart-required",
        });
      });
  }

  public readonly getState = (): SettingsState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.loadGeneration += 1;
    this.linklySetupGeneration += 1;
    this.invalidateSquareRequests("environment");
    this.catalogRefreshUnsubscribe();
    this.catalogRefreshUnsubscribe = () => undefined;
    this.deviceReregistrationCommittedUnsubscribe();
    this.deviceReregistrationCommittedUnsubscribe = () => undefined;
    this.lifetime.abort();
    this.listeners.clear();
  }

  public async load(): Promise<void> {
    if (this.destroyed) return;
    if (!this.state.access.canView) {
      this.patch({
        kind: "unauthorized",
        statusCode: "permission-required",
      });
      return;
    }
    const generation = ++this.loadGeneration;
    this.patch({ kind: "loading", statusCode: null });
    try {
      const snapshot = normalizeSnapshot(
        await this.options.port.loadSnapshot(this.lifetime.signal),
      );
      if (!this.isCurrentLoad(generation)) return;
      this.patch({
        apiBaseUrl: snapshot.apiBaseUrl,
        apiAddressDraft: snapshot.apiBaseUrl,
        appUpdate: snapshot.appUpdate,
        catalog: catalogSnapshotForRefresh(
          snapshot.catalog,
          this.state.catalogRefresh,
        ),
        device: snapshot.device,
        deviceActivationCodeDraft: "",
        deviceActivationPreview: null,
        deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
        externalDisplay: snapshot.externalDisplay,
        hardware: snapshot.hardware,
        kind: "ready",
        linkly: snapshot.linkly,
        linklyDraft: {
          environment: snapshot.linkly.environment,
        },
        linklySetup: this.options.port.linklySetup
          ? initialLinklySetupState(snapshot.linkly.environment)
          : null,
        paymentProvider: snapshot.paymentProvider,
        paymentProviderDraft: snapshot.paymentProvider,
        paymentMethods: normalizePaymentMethodSettings(snapshot.paymentMethods),
        paymentMethodsDraft: normalizePaymentMethodSettings(snapshot.paymentMethods),
        paymentMethodsAvailable: this.options.port.savePaymentMethods !== undefined,
        printer: snapshot.printer,
        square: snapshot.square,
        squareDraft: {
          deviceId: snapshot.square.deviceId,
          environment: snapshot.square.environment,
          locationId: snapshot.square.locationId,
        },
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({
            kind:
              this.state.squareSetup.available &&
              snapshot.square.environment === "Production"
                ? "idle"
                : "disabled",
            items: Object.freeze([]),
          }),
          selectedDeviceId: snapshot.square.deviceId,
          selectedLocationId: snapshot.square.locationId,
          selectedDeviceCodeId: "",
          devicesLoadedForLocationId: null,
          deviceCodesLoadedForLocationId: null,
        }),
        statusCode: null,
        terminalNameDraft: snapshot.device.terminalName,
      });
      if (
        this.options.port.linklySetup &&
        this.state.access.canConfigurePayments
      ) {
        await this.loadLinklySetupState(
          snapshot.linkly.environment,
          generation,
          true,
        );
      }
    } catch {
      if (!this.isCurrentLoad(generation)) return;
      this.patch({ kind: "failed", statusCode: "load-failed" });
    }
  }

  public selectPane(pane: SettingsPane): boolean {
    if (this.destroyed || this.state.busy || this.state.confirmation !== null) {
      return false;
    }
    this.patch({ activePane: pane, statusCode: null });
    return true;
  }

  public setApiAddressDraft(value: string): void {
    if (!this.canEdit()) return;
    this.patch({ apiAddressDraft: value, statusCode: null });
  }

  public testApiAddress(): Promise<void> {
    if (!this.requirePermission(this.state.access.canReregisterDevice)) {
      return Promise.resolve();
    }
    let apiBaseUrl: string;
    try {
      apiBaseUrl = normalizeApiAddress(this.state.apiAddressDraft);
    } catch {
      this.patch({ statusCode: "invalid-api-address" });
      return Promise.resolve();
    }
    this.patch({ apiAddressDraft: apiBaseUrl });
    return this.runAction(async () => {
      try {
        const reachable = await this.options.port.testApiAddress(
          apiBaseUrl,
          this.lifetime.signal,
        );
        this.patch({
          statusCode: reachable
            ? "api-health-check-passed"
            : "api-health-check-failed",
        });
      } catch {
        this.patch({ statusCode: "api-health-check-failed" });
      }
    });
  }

  public setSquareEnvironment(environment: PaymentEnvironment): void {
    if (!this.canEditPayments()) return;
    if (environment === this.state.squareDraft.environment) return;
    this.invalidateSquareRequests("environment");
    const available = this.state.squareSetup.available;
    const idleKind: SettingsSquareRequestKind = available
      ? "idle"
      : "disabled";
    this.patch({
      squareDraft: { environment, locationId: "", deviceId: "" },
      squareDeviceCodeNameDraft: "HBPOS Terminal",
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        token: Object.freeze({ kind: idleKind, value: null }),
        locations: Object.freeze({
          kind: idleKind,
          items: Object.freeze([]),
        }),
        devices: Object.freeze({
          kind: idleKind,
          items: Object.freeze([]),
        }),
        deviceCodes: Object.freeze({
          kind:
            available && environment === "Production"
              ? "idle"
              : "disabled",
          items: Object.freeze([]),
        }),
        selectedLocationId: "",
        selectedDeviceId: "",
        selectedDeviceCodeId: "",
        devicesLoadedForLocationId: null,
        deviceCodesLoadedForLocationId: null,
      }),
      statusCode: null,
    });
  }

  public async loadSquareLocations(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return;
    }
    const squareSetup = this.options.port.squareSetup;
    if (!squareSetup) return;
    const environment = this.state.squareDraft.environment;
    const tokenGeneration = ++this.squareTokenGeneration;
    const locationsGeneration = ++this.squareLocationsGeneration;
    this.patch({
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        token: Object.freeze({
          ...this.state.squareSetup.token,
          kind: "loading",
        }),
        locations: Object.freeze({
          ...this.state.squareSetup.locations,
          kind: "loading",
        }),
      }),
      statusCode: null,
    });
    const [tokenResult, locationsResult] = await Promise.allSettled([
      squareSetup.getSquareTokenStatus(
        environment,
        this.lifetime.signal,
      ),
      squareSetup.listSquareLocations(
        environment,
        this.lifetime.signal,
      ),
    ]);
    if (
      this.isCurrentSquareEnvironmentRequest(
        environment,
        tokenGeneration,
        this.squareTokenGeneration,
      )
    ) {
      const token =
        tokenResult.status === "fulfilled" &&
        tokenResult.value.environment === environment
          ? tokenResult.value
          : null;
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          token: token
            ? Object.freeze({ kind: "ready", value: token })
            : Object.freeze({
                ...this.state.squareSetup.token,
                kind: "failed",
              }),
        }),
      });
    }
    if (
      !this.isCurrentSquareEnvironmentRequest(
        environment,
        locationsGeneration,
        this.squareLocationsGeneration,
      )
    ) {
      return;
    }
    if (locationsResult.status === "rejected") {
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          locations: Object.freeze({
            ...this.state.squareSetup.locations,
            kind: "failed",
          }),
        }),
      });
      return;
    }
    const locationItems = Object.freeze([...locationsResult.value]);
    if (locationItems.length === 0) {
      this.invalidateSquareRequests("location");
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          locations: Object.freeze({ kind: "empty", items: locationItems }),
          devices: Object.freeze({ kind: "idle", items: Object.freeze([]) }),
          deviceCodes: Object.freeze({
            kind: environment === "Production" ? "idle" : "disabled",
            items: Object.freeze([]),
          }),
          selectedLocationId: "",
          selectedDeviceId: "",
          selectedDeviceCodeId: "",
          devicesLoadedForLocationId: null,
          deviceCodesLoadedForLocationId: null,
        }),
      });
      return;
    }
    const matchedLocation = findSquareLocation(
      locationItems,
      this.state.squareDraft.locationId,
    );
    const selectedLocation =
      matchedLocation ??
      (environment === "Sandbox" && locationItems.length === 1
        ? locationItems[0]
        : null);
    const selectedLocationId = selectedLocation?.id ?? "";
    this.invalidateSquareRequests("location");
    this.patch({
      squareDraft: {
        ...this.state.squareDraft,
        locationId: selectedLocationId,
        ...(selectedLocationId ? {} : { deviceId: "" }),
      },
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        locations: Object.freeze({ kind: "ready", items: locationItems }),
        devices: Object.freeze({ kind: "idle", items: Object.freeze([]) }),
        deviceCodes: Object.freeze({
          kind: environment === "Production" ? "idle" : "disabled",
          items: Object.freeze([]),
        }),
        selectedLocationId,
        selectedDeviceId: selectedLocationId
          ? this.state.squareSetup.selectedDeviceId
          : "",
        selectedDeviceCodeId: "",
        devicesLoadedForLocationId: null,
        deviceCodesLoadedForLocationId: null,
      }),
    });
    if (environment === "Sandbox" && selectedLocationId) {
      await this.loadSquareDevices();
    }
  }

  public setSquareLocationId(locationId: string): void {
    if (!this.canEditPayments()) return;
    if (!this.state.squareSetup.available) {
      this.patch({
        squareDraft: { ...this.state.squareDraft, locationId },
        statusCode: null,
      });
      return;
    }
    const requestedId = safePublicIdentifier(locationId);
    const matched = findSquareLocation(
      this.state.squareSetup.locations.items,
      requestedId,
    );
    const selectedLocationId = matched?.id ?? "";
    if (
      selectedLocationId === this.state.squareSetup.selectedLocationId &&
      selectedLocationId === this.state.squareDraft.locationId
    ) {
      return;
    }
    this.invalidateSquareRequests("location");
    this.patch({
      squareDraft: {
        ...this.state.squareDraft,
        locationId: selectedLocationId,
        deviceId: "",
      },
      squareDeviceCodeNameDraft: "HBPOS Terminal",
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        devices: Object.freeze({ kind: "idle", items: Object.freeze([]) }),
        deviceCodes: Object.freeze({
          kind:
            this.state.squareDraft.environment === "Production"
              ? "idle"
              : "disabled",
          items: Object.freeze([]),
        }),
        selectedLocationId,
        selectedDeviceId: "",
        selectedDeviceCodeId: "",
        devicesLoadedForLocationId: null,
        deviceCodesLoadedForLocationId: null,
      }),
      statusCode: null,
    });
  }

  public setSquareDeviceId(deviceId: string): void {
    if (!this.canEditPayments()) return;
    if (!this.state.squareSetup.available) {
      this.patch({
        squareDraft: { ...this.state.squareDraft, deviceId },
        statusCode: null,
      });
      return;
    }
    const requestedId = normalizeSettingsSquareDeviceId(deviceId) ?? "";
    const matched = findSquareDevice(
      this.state.squareSetup.devices.items,
      requestedId,
    );
    const selectedDeviceId =
      matched && !isSquareDeviceDisabled(matched) ? matched.id : "";
    this.patch({
      squareDraft: { ...this.state.squareDraft, deviceId: selectedDeviceId },
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        selectedDeviceId,
      }),
      statusCode: null,
    });
  }

  public async loadSquareDevices(): Promise<void> {
    await this.loadSquareDevicesForSelection(null);
  }

  public setSquareDeviceCodeNameDraft(value: string): void {
    if (!this.canEditPayments()) return;
    this.patch({ squareDeviceCodeNameDraft: value, statusCode: null });
  }

  public setSquareDeviceCodeId(deviceCodeId: string): void {
    if (!this.canEditPayments()) return;
    const selected = findSquareDeviceCode(
      this.state.squareSetup.deviceCodes.items,
      deviceCodeId,
    );
    this.squareDeviceCodesGeneration += 1;
    this.patch({
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        selectedDeviceCodeId: selected?.id ?? "",
      }),
      statusCode: null,
    });
  }

  public async loadSquareDeviceCodes(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return;
    }
    const squareSetup = this.options.port.squareSetup;
    if (!squareSetup) return;
    const environment = this.state.squareDraft.environment;
    if (environment !== "Production") {
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({
            kind: "disabled",
            items: Object.freeze([]),
          }),
          selectedDeviceCodeId: "",
          deviceCodesLoadedForLocationId: null,
        }),
      });
      return;
    }
    const selectedLocation = findSquareLocation(
      this.state.squareSetup.locations.items,
      this.state.squareSetup.selectedLocationId,
    );
    if (!selectedLocation) return;
    const locationId = selectedLocation.id;
    const generation = ++this.squareDeviceCodesGeneration;
    this.patch({
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        deviceCodes: Object.freeze({
          ...this.state.squareSetup.deviceCodes,
          kind: "loading",
        }),
      }),
      statusCode: null,
    });
    try {
      const deviceCodes = Object.freeze([
        ...(await squareSetup.listSquareDeviceCodes(
          environment,
          locationId,
          this.lifetime.signal,
        )),
      ]);
      if (
        !this.isCurrentSquareLocationRequest(
          environment,
          locationId,
          generation,
          this.squareDeviceCodesGeneration,
        )
      ) {
        return;
      }
      const selectedDeviceCode =
        findSquareDeviceCode(
          deviceCodes,
          this.state.squareSetup.selectedDeviceCodeId,
        ) ??
        deviceCodes.find((deviceCode) =>
          equalSquareDeviceId(
            deviceCode.deviceId ?? "",
            this.state.squareDraft.deviceId || this.state.square.deviceId,
          ),
        ) ??
        null;
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({
            kind: deviceCodes.length === 0 ? "empty" : "ready",
            items: deviceCodes,
          }),
          selectedDeviceCodeId: selectedDeviceCode?.id ?? "",
          deviceCodesLoadedForLocationId: locationId,
        }),
      });
    } catch (error) {
      if (
        isAbortError(error) ||
        !this.isCurrentSquareLocationRequest(
          environment,
          locationId,
          generation,
          this.squareDeviceCodesGeneration,
        )
      ) {
        return;
      }
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({
            ...this.state.squareSetup.deviceCodes,
            kind: "failed",
          }),
        }),
      });
    }
  }

  public async createSquareDeviceCode(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return;
    }
    if (this.state.squareSetup.deviceCodes.kind === "loading") return;
    const squareSetup = this.options.port.squareSetup;
    if (!squareSetup || this.state.squareDraft.environment !== "Production") {
      return;
    }
    const selectedLocation = findSquareLocation(
      this.state.squareSetup.locations.items,
      this.state.squareSetup.selectedLocationId,
    );
    const name = safeSquareDeviceCodeName(
      this.state.squareDeviceCodeNameDraft,
    );
    if (!selectedLocation || !name) return;
    const environment = "Production" as const;
    const locationId = selectedLocation.id;
    return this.runAction(async () => {
      const generation = ++this.squareDeviceCodesGeneration;
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({
            ...this.state.squareSetup.deviceCodes,
            kind: "loading",
          }),
        }),
        statusCode: null,
      });
      try {
        const created = await squareSetup.createSquareDeviceCode(
          environment,
          locationId,
          name,
          this.lifetime.signal,
        );
        if (
          !safePublicIdentifier(created.id) ||
          !this.isCurrentSquareLocationRequest(
            environment,
            locationId,
            generation,
            this.squareDeviceCodesGeneration,
          )
        ) {
          return;
        }
        const items = replaceSquareDeviceCode(
          this.state.squareSetup.deviceCodes.items,
          created,
        );
        this.patch({
          squareSetup: Object.freeze({
            ...this.state.squareSetup,
            deviceCodes: Object.freeze({ kind: "ready", items }),
            selectedDeviceCodeId: created.id,
            deviceCodesLoadedForLocationId: locationId,
          }),
        });
      } catch (error) {
        if (
          isAbortError(error) ||
          !this.isCurrentSquareLocationRequest(
            environment,
            locationId,
            generation,
            this.squareDeviceCodesGeneration,
          )
        ) {
          return;
        }
        this.patch({
          squareSetup: Object.freeze({
            ...this.state.squareSetup,
            deviceCodes: Object.freeze({
              ...this.state.squareSetup.deviceCodes,
              kind: "failed",
            }),
          }),
        });
      }
    });
  }

  public async refreshSquareDeviceCode(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return;
    }
    const squareSetup = this.options.port.squareSetup;
    if (!squareSetup || this.state.squareDraft.environment !== "Production") {
      return;
    }
    const selectedLocation = findSquareLocation(
      this.state.squareSetup.locations.items,
      this.state.squareSetup.selectedLocationId,
    );
    const selectedDeviceCode = findSquareDeviceCode(
      this.state.squareSetup.deviceCodes.items,
      this.state.squareSetup.selectedDeviceCodeId,
    );
    if (!selectedLocation || !selectedDeviceCode) return;
    const environment = "Production" as const;
    const locationId = selectedLocation.id;
    const deviceCodeId = selectedDeviceCode.id;
    const generation = ++this.squareDeviceCodesGeneration;
    this.patch({
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        deviceCodes: Object.freeze({
          ...this.state.squareSetup.deviceCodes,
          kind: "loading",
        }),
      }),
      statusCode: null,
    });
    try {
      const refreshed = await squareSetup.getSquareDeviceCode(
        environment,
        deviceCodeId,
        this.lifetime.signal,
      );
      if (
        !safePublicIdentifier(refreshed.id) ||
        !this.isCurrentSquareLocationRequest(
          environment,
          locationId,
          generation,
          this.squareDeviceCodesGeneration,
        )
      ) {
        return;
      }
      const items = replaceSquareDeviceCode(
        this.state.squareSetup.deviceCodes.items,
        refreshed,
      );
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({ kind: "ready", items }),
          selectedDeviceCodeId: refreshed.id,
          deviceCodesLoadedForLocationId: locationId,
        }),
      });
      if (
        refreshed.status?.trim().toUpperCase() === "PAIRED" &&
        normalizeSettingsSquareDeviceId(refreshed.deviceId)
      ) {
        // 配对只更新候选设备；正式保存仍必须经过显式保存、确认和待同步数据门禁。
        await this.loadSquareDevicesForSelection(
          refreshed.deviceId,
          deviceCodeId,
        );
      }
    } catch (error) {
      if (
        isAbortError(error) ||
        !this.isCurrentSquareLocationRequest(
          environment,
          locationId,
          generation,
          this.squareDeviceCodesGeneration,
        )
      ) {
        return;
      }
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          deviceCodes: Object.freeze({
            ...this.state.squareSetup.deviceCodes,
            kind: "failed",
          }),
        }),
      });
    }
  }

  public setLinklyEnvironment(environment: PaymentEnvironment): void {
    if (!this.canEditPayments()) return;
    if (environment === this.state.linklyDraft.environment) return;
    const linklySetup = this.state.linklySetup
      ? resetLinklySetupState(
          this.state.linklySetup,
          environment,
          true,
          true,
        )
      : null;
    this.patch({
      linklyDraft: { environment },
      linklySetup,
      statusCode: null,
    });
    if (this.options.port.linklySetup) {
      void this.loadLinklySetupState(
        environment,
        this.loadGeneration,
        true,
      );
    }
  }

  public refreshLinklySetup(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return Promise.resolve();
    }
    if (!this.options.port.linklySetup) return Promise.resolve();
    const environment = this.state.linklyDraft.environment;
    return this.runAction(() =>
      this.loadLinklySetupState(environment, this.loadGeneration, false),
    );
  }

  public setPaymentProvider(provider: SettingsPaymentProvider): void {
    if (!this.canEditPayments()) return;
    if (
      provider === "linkly" &&
      this.options.port.linklySetup &&
      isLinklyBaseSelectable(this.state) &&
      !isLinklySetupReady(this.state, this.state.linklyDraft.environment)
    ) {
      this.patch({ statusCode: "linkly-setup-required" });
      return;
    }
    if (!isPaymentProviderSelectable(provider, this.state)) {
      this.patch({ statusCode: "payment-settings-invalid" });
      return;
    }
    this.patch({ paymentProviderDraft: provider, statusCode: null });
  }

  public setPrinterEnabled(enabled: boolean): void {
    if (!this.canEditPrinter()) return;
    this.patch({
      printer: { ...this.state.printer, printEnabled: enabled },
      statusCode: null,
    });
  }

  public setDrawerEnabled(enabled: boolean): void {
    if (!this.canEditPrinter()) return;
    this.patch({
      printer: { ...this.state.printer, drawerEnabled: enabled },
      statusCode: null,
    });
  }

  public setPrinterPeripheralId(peripheralId: string): void {
    if (!this.canEditPrinter()) return;
    this.patch({
      printer: { ...this.state.printer, peripheralId },
      statusCode: null,
    });
  }

  public setPrinterPaper(paper: ReceiptPrinterSettings["paper"]): void {
    if (!this.canEditPrinter()) return;
    this.patch({
      printer: { ...this.state.printer, paper },
      statusCode: null,
    });
  }

  public setPrinterLocale(locale: ReceiptPrinterSettings["locale"]): void {
    if (!this.canEditPrinter()) return;
    this.patch({
      printer: { ...this.state.printer, locale },
      statusCode: null,
    });
  }

  public setReceiptBrandName(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, brandName: value },
      statusCode: null,
    });
  }

  public setReceiptStoreName(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, storeName: value },
      statusCode: null,
    });
  }

  public setReceiptAddress(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, address: value },
      statusCode: null,
    });
  }

  public setReceiptPhone(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, phone: value },
      statusCode: null,
    });
  }

  public setReceiptAbn(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, abn: value },
      statusCode: null,
    });
  }

  public setReceiptReturnPolicy(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, returnPolicy: value },
      statusCode: null,
    });
  }

  /** 退款券「VOUCHER TERMS」自定义正文；与门店资料同口径：已下发只读，未下发可本机编辑。 */
  public setReceiptVoucherTerms(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, voucherTerms: value },
      statusCode: null,
    });
  }

  /** 分期小票「INSTALLMENT TERMS」自定义正文；规则同 setReceiptVoucherTerms。 */
  public setReceiptInstallmentTerms(value: string): void {
    if (!this.canEditReceiptProfile()) return;
    this.patch({
      printer: { ...this.state.printer, installmentTerms: value },
      statusCode: null,
    });
  }

  /**
   * 已应用总部下发资料时，「立即同步」：立刻跑一轮同步并显示结果。
   * 同步结果里的最新资料只替换八项资料（含券使用说明、分期条款）与下发版本，不动尚未保存的硬件草稿。
   */
  public syncReceiptProfile(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      const sync = this.options.port.syncReceiptProfile;
      if (!sync) {
        this.patch({ statusCode: "receipt-profile-sync-failed" });
        return;
      }
      let result: SettingsReceiptProfileSyncResult;
      try {
        result = await sync.call(this.options.port, this.lifetime.signal);
      } catch {
        this.patch({ statusCode: "receipt-profile-sync-failed" });
        return;
      }
      if (result.status === "not-published") {
        this.patch({ statusCode: "receipt-profile-not-published" });
        return;
      }
      if (result.status === "failed") {
        this.patch({ statusCode: receiptProfileSyncFailureStatus(result.reason) });
        return;
      }
      try {
        // 先在局部对象里完整规整，成功后一次性替换草稿里的资料部分。
        const synced = normalizePrinterSettings(result.printer);
        this.patch({
          printer: Object.freeze({
            ...this.state.printer,
            brandName: synced.brandName,
            storeName: synced.storeName,
            address: synced.address,
            phone: synced.phone,
            abn: synced.abn,
            returnPolicy: synced.returnPolicy,
            voucherTerms: synced.voucherTerms,
            installmentTerms: synced.installmentTerms,
            profileStoreCode: synced.profileStoreCode,
            profileVersion: synced.profileVersion,
            profileAckedVersion: synced.profileAckedVersion,
          }),
          statusCode:
            result.status === "updated"
              ? "receipt-profile-synced"
              : "receipt-profile-up-to-date",
        });
      } catch {
        this.patch({ statusCode: "receipt-profile-sync-invalid" });
      }
    });
  }

  public loadReceiptProfile(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    // 已应用下发资料后，旧的「载入门店资料」不得再覆盖只读资料，一律改走同步。
    if (this.state.printer.profileVersion > 0) {
      return this.syncReceiptProfile();
    }
    return this.runAction(async () => {
      let profile: SettingsReceiptProfileDraft | null;
      try {
        profile = await this.options.port.loadReceiptProfile(
          this.lifetime.signal,
        );
      } catch {
        this.patch({ statusCode: "receipt-profile-load-failed" });
        return;
      }
      if (!profile) {
        this.patch({ statusCode: "receipt-profile-load-failed" });
        return;
      }
      try {
        const normalized = normalizeReceiptProfileDraft(
          profile,
          this.state.device.storeCode,
        );
        this.patch({
          printer: {
            ...this.state.printer,
            profileStoreCode: normalized.storeCode,
            brandName: normalized.brandName,
            storeName: normalized.storeName,
            address: normalized.address,
            phone: normalized.phone,
            abn: normalized.abn,
            returnPolicy: normalized.returnPolicy,
            voucherTerms: normalized.voucherTerms,
            installmentTerms: normalized.installmentTerms,
          },
          statusCode: "receipt-profile-loaded",
        });
      } catch {
        this.patch({ statusCode: "receipt-profile-load-failed" });
      }
    });
  }

  public setDeviceActivationCode(value: string): void {
    if (!this.canEdit()) return;
    this.patch({
      deviceActivationCodeDraft: value,
      deviceActivationPreview: null,
      deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
      statusCode: null,
    });
  }

  public setTerminalName(value: string): void {
    if (!this.canEdit()) return;
    this.patch({ terminalNameDraft: value, statusCode: null });
  }

  public setUseManualCard(value: boolean): void {
    if (!this.canEditPayments() || !this.state.paymentMethodsAvailable) return;
    this.patch({ paymentMethodsDraft: Object.freeze({ ...this.state.paymentMethodsDraft, useManualCard: value }), statusCode: null });
  }

  public setGiftCardEnabled(value: boolean): void {
    if (!this.canEditPayments() || !this.state.paymentMethodsAvailable) return;
    this.patch({ paymentMethodsDraft: Object.freeze({ ...this.state.paymentMethodsDraft, giftCardEnabled: value }), statusCode: null });
  }

  public savePaymentMethods(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments) || !this.canEdit()) return Promise.resolve();
    const save = this.options.port.savePaymentMethods;
    if (!save || this.catalogRefreshRunning()) {
      this.patch({ statusCode: "safety-check-failed" });
      return Promise.resolve();
    }
    const input = this.state.paymentMethodsDraft;
    return this.runAction(async () => {
      try {
        await save.call(this.options.port, input, this.lifetime.signal);
        if (this.destroyed) return;
        // 仅耐久保存成功后更新生效值，失败时保留草稿供重新保存。
        this.patch({ paymentMethods: input, statusCode: "payment-settings-saved" });
      } catch (error) {
        if (!this.destroyed) {
          const code = error instanceof Error ? error.message : "";
          this.patch({ statusCode: code === "SETTINGS_PERMISSION_DENIED"
            ? "permission-required"
            : code === "PAYMENT_METHOD_SETTINGS_BLOCKED"
              ? "payment-method-settings-blocked"
              : code === "PAYMENT_METHOD_SETTINGS_UNAVAILABLE"
                ? "payment-method-settings-unavailable"
                : "payment-settings-save-failed" });
        }
      }
    });
  }

  public savePaymentSettings(): Promise<void> {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return Promise.resolve();
    }
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return Promise.resolve();
    }
    const input = normalizePaymentDraft(
      this.state.paymentProviderDraft,
      this.state.squareDraft,
      this.state.linklyDraft,
      isPaymentProviderSelectable("square", this.state),
      isLinklyBaseSelectable(this.state),
    );
    if (
      !input ||
      (input.provider === "square" &&
        !isLoadedSquareSelectionValid(this.state, input.square))
    ) {
      this.patch({ statusCode: "payment-settings-invalid" });
      return Promise.resolve();
    }
    const current = currentPaymentSettings(this.state);
    if (current && paymentSettingsEqual(input, current)) {
      this.patch({ statusCode: "payment-settings-saved" });
      return Promise.resolve();
    }
    if (
      input.provider === "linkly" &&
      this.options.port.linklySetup &&
      !isLinklySetupReady(this.state, input.linkly.environment)
    ) {
      this.patch({ statusCode: "linkly-setup-required" });
      return Promise.resolve();
    }
    this.requestConfirmation({
      kind: "change-payment-settings",
      input,
    });
    return Promise.resolve();
  }

  public requestLinklyPair(pairCode: string): boolean;
  public requestLinklyPair(terminalId: string, pairCode: string): boolean;
  public requestLinklyPair(
    terminalIdOrPairCode: string,
    explicitPairCode?: string,
  ): boolean {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return false;
    }
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return false;
    }
    const selectedTerminalId =
      this.state.linklySetup?.terminals.value?.selectedTerminalId ?? "";
    const terminalId = (
      explicitPairCode === undefined
        ? selectedTerminalId
        : terminalIdOrPairCode
    ).trim();
    const normalizedPairCode = (
      explicitPairCode ?? terminalIdOrPairCode
    ).trim();
    if (!/^\d{6}$/u.test(normalizedPairCode)) {
      this.patch({ statusCode: "linkly-pair-code-invalid" });
      return false;
    }
    const terminal = this.state.linklySetup?.terminals.value?.terminals.find(
      (item) => item.terminalId === terminalId,
    );
    if (!terminal || terminal.isBusy) {
      this.patch({ statusCode: terminal?.isBusy
        ? "linkly-terminal-session-active"
        : "linkly-terminal-revision-conflict" });
      return false;
    }
    if (
      !this.options.port.linklySetup ||
      !terminalId ||
      !hasLinklyCloudCredentials(
        this.state,
        this.state.linklyDraft.environment,
      )
    ) {
      this.patch({ statusCode: "linkly-setup-required" });
      return false;
    }
    return this.requestConfirmation({
      kind: "pair-linkly",
      environment: this.state.linklyDraft.environment,
      terminalId,
      pairCode: normalizedPairCode,
      terminalLabel: `${terminal.displayName} · Lane ${terminal.laneNo}`,
    });
  }

  public selectLinklyTerminal(terminalId: string): Promise<void> {
    const setup = this.options.port.linklySetup;
    const snapshot = this.state.linklySetup?.terminals.value;
    const normalizedTerminalId = terminalId.trim();
    const terminal = snapshot?.terminals.find(
      (candidate) => candidate.terminalId === normalizedTerminalId,
    );
    if (
      !this.requirePermission(this.state.access.canConfigurePayments) ||
      !setup?.selectTerminal ||
      !snapshot ||
      snapshot.lineManagementSupported === true ||
      snapshot.environment !== this.state.linklyDraft.environment ||
      !terminal ||
      !terminal.isReady ||
      terminal.isBusy
    ) {
      this.patch({ statusCode: "linkly-terminal-switch-failed" });
      return Promise.resolve();
    }
    if (snapshot.selectedTerminalId === normalizedTerminalId) {
      this.patch({ statusCode: "linkly-terminal-selected" });
      return Promise.resolve();
    }
    const environment = snapshot.environment;
    const loadGeneration = this.loadGeneration;
    const generation = ++this.linklySetupGeneration;
    this.patch({
      linklySetup: this.state.linklySetup
        ? Object.freeze({
            ...this.state.linklySetup,
            terminals: Object.freeze({
              kind: "switching" as const,
              value: snapshot,
            }),
            pairCodeResetToken:
              this.state.linklySetup.pairCodeResetToken + 1,
          })
        : null,
      statusCode: null,
    });
    return setup.selectTerminal(
      environment,
      normalizedTerminalId,
      snapshot.selectionRevision,
      this.lifetime.signal,
    ).then(async (next) => {
      if (!this.isCurrentLinklySetupRequest(
        environment,
        loadGeneration,
        generation,
      )) return;
      const selectionConfirmed =
        next.environment === environment &&
        next.selectedTerminalId === normalizedTerminalId &&
        next.selectionRevision > snapshot.selectionRevision;
      if (!selectionConfirmed) {
        throw Object.assign(new Error("Linkly terminal selection is unconfirmed."), {
          code: "LINKLY_SELECTION_REVISION_CONFLICT",
        });
      }
      this.patch({
        linklySetup: this.state.linklySetup
          ? Object.freeze({
              ...this.state.linklySetup,
              // 终端作用域改变后，旧 health 与登录测试均不可继续作为启用依据。
              health: Object.freeze({ kind: "idle" as const, value: null }),
              logonTest: Object.freeze({ environment, status: "idle" as const }),
              terminals: Object.freeze({ kind: "ready" as const, value: next }),
            })
          : null,
        statusCode: "linkly-terminal-selected",
      });
      // 选择确认后只读刷新新终端作用域，避免用户误用旧终端检查结果。
      await this.loadLinklySetupState(environment, loadGeneration, true);
    }).catch(async (error) => {
      if (isAbortError(error) || !this.isCurrentLinklySetupRequest(
        environment,
        loadGeneration,
        generation,
      )) return;
      let authoritative: SettingsLinklyTerminalSelectionSnapshot | null = null;
      if (setup.readTerminals) {
        try {
          const refreshed = await setup.readTerminals(
            environment,
            this.lifetime.signal,
          );
          if (refreshed.environment === environment) authoritative = refreshed;
        } catch {
          // PUT 可能已提交；第二次 GET 仍失败时不能把旧选择继续标记为已确认。
        }
      }
      if (!this.isCurrentLinklySetupRequest(
        environment,
        loadGeneration,
        generation,
      )) return;
      const selectionScopeChanged =
        authoritative === null ||
        authoritative.selectedTerminalId !== snapshot.selectedTerminalId ||
        authoritative.selectionRevision !== snapshot.selectionRevision;
      this.patch({
        linklySetup: this.state.linklySetup
          ? Object.freeze({
              ...this.state.linklySetup,
              // 只有权威选择变化或无法确认时才清理旧检查结果；明确未变化时保留。
              ...(selectionScopeChanged
                ? {
                    health: Object.freeze({ kind: "idle" as const, value: null }),
                    logonTest: Object.freeze({
                      environment,
                      status: "idle" as const,
                    }),
                  }
                : {}),
              terminals: authoritative
                ? Object.freeze({ kind: "ready" as const, value: authoritative })
                : Object.freeze({ kind: "failed" as const, value: null }),
            })
          : null,
        statusCode:
          authoritative?.selectedTerminalId === normalizedTerminalId
            ? "linkly-terminal-selected"
            : linklyTerminalSelectionFailureStatus(error),
      });
      if (authoritative?.selectedTerminalId === normalizedTerminalId) {
        // PUT 回包不明但权威 GET 已确认时，同样只读刷新新终端 health。
        await this.loadLinklySetupState(environment, loadGeneration, true);
      }
    });
  }

  public testLinklyTerminalConnection(terminalId: string): Promise<void> {
    const setup = this.options.port.linklySetup;
    const snapshot = this.state.linklySetup?.terminals.value;
    const terminal = snapshot?.terminals.find(
      (candidate) => candidate.terminalId === terminalId.trim(),
    );
    if (
      !this.requirePermission(this.state.access.canConfigurePayments) ||
      !setup?.testTerminalConnection ||
      !snapshot?.lineManagementSupported ||
      !terminal?.terminalVersion ||
      terminal.assignmentRevision === undefined
    ) {
      this.patch({ statusCode: "linkly-terminal-switch-failed" });
      return Promise.resolve();
    }
    const generation = this.linklySetupGeneration;
    const key = linklyConnectionTestKey(terminal, generation);
    this.patchLinklyConnectionTest(key, {
      key,
      terminalId: terminal.terminalId,
      kind: "running",
      result: null,
    });
    return setup.testTerminalConnection(
      snapshot.environment,
      {
        terminalId: terminal.terminalId,
        terminalVersion: terminal.terminalVersion,
        assignedDeviceCode: terminal.assignedDeviceCode ?? null,
        assignmentRevision: terminal.assignmentRevision,
      },
      this.lifetime.signal,
    ).then((result) => {
      if (!this.isCurrentLinklyTerminalScope(terminal, generation)) return;
      this.patchLinklyConnectionTest(key, {
        key,
        terminalId: terminal.terminalId,
        kind: "ready",
        result,
      });
    }).catch((error) => {
      if (isAbortError(error) || !this.isCurrentLinklyTerminalScope(terminal, generation)) return;
      this.patchLinklyConnectionTest(key, {
        key,
        terminalId: terminal.terminalId,
        kind: "failed",
        result: null,
        failureReason: linklyConnectionFailureReason(error),
      });
    });
  }

  public requestLinklyTerminalAssignment(
    terminalId: string,
    targetDeviceCode: string | null,
  ): boolean {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) return false;
    const snapshot = this.state.linklySetup?.terminals.value;
    const terminal = snapshot?.terminals.find(
      (candidate) => candidate.terminalId === terminalId.trim(),
    );
    const targetCode = targetDeviceCode?.trim() || null;
    const target = targetCode
      ? snapshot?.devices?.find((device) => device.deviceCode === targetCode)
      : null;
    if (
      this.options.port.linklySetup?.supportsTerminalAssignment !== true ||
      !snapshot?.lineManagementSupported ||
      !terminal?.terminalVersion ||
      terminal.assignmentRevision === undefined ||
      terminal.isBusy ||
      (targetCode !== null && (!target || !target.isAvailable))
    ) {
      this.patch({ statusCode: "linkly-terminal-switch-failed" });
      return false;
    }
    if ((terminal.assignedDeviceCode ?? null) === targetCode) {
      this.patch({ statusCode: "linkly-terminal-selected" });
      return false;
    }
    const replaced = target?.selectedTerminalId
      ? snapshot.terminals.find((item) => item.terminalId === target.selectedTerminalId)
      : null;
    const currentDeviceCode = this.state.device.deviceCode.trim();
    return this.requestConfirmation(Object.freeze({
      kind: "assign-linkly-terminal" as const,
      environment: snapshot.environment,
      input: Object.freeze({
        terminalId: terminal.terminalId,
        terminalVersion: terminal.terminalVersion,
        assignedDeviceCode: terminal.assignedDeviceCode ?? null,
        assignmentRevision: terminal.assignmentRevision,
        targetDeviceCode: targetCode,
        expectedTargetTerminalId: target?.selectedTerminalId ?? null,
        expectedTargetSelectionRevision: target?.selectionRevision ?? 0,
      }),
      terminalLabel: `${terminal.displayName} · Lane ${terminal.laneNo}`,
      targetDeviceCode: targetCode,
      replacedTerminalLabel: replaced
        ? `${replaced.displayName} · Lane ${replaced.laneNo}`
        : null,
      affectsCurrentDevice:
        terminal.assignedDeviceCode === currentDeviceCode || targetCode === currentDeviceCode,
    }));
  }

  public testPaymentProvider(provider: "square" | "linkly"): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return Promise.resolve();
    }
    if (provider !== "linkly" && provider !== this.state.paymentProviderDraft) {
      this.patch({ statusCode: "payment-settings-invalid" });
      return Promise.resolve();
    }
    if (
      provider === "linkly" &&
      this.options.port.linklySetup &&
      !isLinklyHealthReady(this.state, this.state.linklyDraft.environment)
    ) {
      this.patch({ statusCode: "linkly-setup-required" });
      return Promise.resolve();
    }
    const testProvider = provider === "linkly" ? "linkly" : this.state.paymentProviderDraft;
    const input = normalizePaymentDraft(
      testProvider,
      this.state.squareDraft,
      this.state.linklyDraft,
      isPaymentProviderSelectable("square", this.state),
      isLinklyBaseSelectable(this.state),
    );
    if (
      !input ||
      (provider === "square"
        ? input.square === null ||
          !isLoadedSquareSelectionValid(this.state, input.square)
        : input.linkly === null)
    ) {
      this.patch({ statusCode: "payment-settings-invalid" });
      return Promise.resolve();
    }
    const linklyTestSelection =
      provider === "linkly" && this.state.linklySetup?.terminals.value
        ? Object.freeze({
            environment: this.state.linklyDraft.environment,
            loadGeneration: this.loadGeneration,
            setupGeneration: this.linklySetupGeneration,
            selectedTerminalId:
              this.state.linklySetup.terminals.value.selectedTerminalId,
            selectionRevision:
              this.state.linklySetup.terminals.value.selectionRevision,
          })
        : null;
    const isCurrentLinklyTest = (): boolean => {
      if (!linklyTestSelection) return true;
      const current = this.state.linklySetup?.terminals.value;
      return (
        this.loadGeneration === linklyTestSelection.loadGeneration &&
        this.linklySetupGeneration === linklyTestSelection.setupGeneration &&
        this.state.linklyDraft.environment === linklyTestSelection.environment &&
        current?.environment === linklyTestSelection.environment &&
        current.selectedTerminalId === linklyTestSelection.selectedTerminalId &&
        current.selectionRevision === linklyTestSelection.selectionRevision
      );
    };
    return this.runAction(async () => {
      const linklyTestEnvironment =
        provider === "linkly" ? input.linkly?.environment : null;
      if (linklyTestEnvironment && this.state.linklySetup) {
        this.patch({
          linklySetup: updateLinklyLogonTest(
            this.state.linklySetup,
            linklyTestEnvironment,
            "running",
          ),
        });
      }
      try {
        await this.options.port.testPaymentProvider(
          provider,
          input,
          this.lifetime.signal,
          provider === "linkly"
            ? this.state.linklySetup?.terminals.value ?? null
            : undefined,
        );
        if (!isCurrentLinklyTest()) return;
        if (linklyTestEnvironment && this.state.linklySetup) {
          // 新的银行验证已成功，不能继续显示当前线路之前的 TF/失败结果。
          const selectedTerminalId = this.state.linklySetup.terminals.value?.selectedTerminalId;
          this.patch({
            linklySetup: { ...updateLinklyLogonTest(
              this.state.linklySetup,
              linklyTestEnvironment,
              "passed",
            ), connectionTests: Object.freeze(Object.fromEntries(
              Object.entries(this.state.linklySetup.connectionTests).filter(
                ([, value]) => value.terminalId !== selectedTerminalId,
              ),
            )) },
          });
        }
        if (linklyTestEnvironment && this.options.port.linklySetup) {
          // 读取银行验证后的最新线路健康，清除较早的 TF；选择变化时刷新会使资格失效。
          await this.loadLinklySetupState(linklyTestEnvironment, this.loadGeneration, false);
          if (this.state.linklySetup?.logonTest.status !== "passed") return;
        }
        this.patch({ statusCode: "payment-test-passed" });
      } catch (error) {
        if (!isCurrentLinklyTest()) return;
        if (linklyTestEnvironment && this.state.linklySetup) {
          this.patch({
            linklySetup: updateLinklyLogonTest(
              this.state.linklySetup,
              linklyTestEnvironment,
              "failed",
            ),
          });
        }
        this.patch({ statusCode: error && typeof error === "object" &&
          "code" in error && error.code === "LINKLY_TEST_UNCONFIRMED"
          ? "payment-test-unconfirmed" : "payment-test-failed" });
      }
    });
  }

  public savePrinterSettings(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    const settings = bindPrinterSettingsToCurrentStore(
      normalizePrinterSettings(this.state.printer),
      this.state.device.storeCode,
    );
    return this.runAction(async () => {
      try {
        await this.options.port.savePrinterSettings(
          settings,
          this.lifetime.signal,
        );
        this.patch({
          printer: settings,
          statusCode: "printer-settings-saved",
        });
      } catch {
        this.patch({ statusCode: "printer-settings-save-failed" });
      }
    });
  }

  public scanPrinters(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        const devices = await this.options.port.scanPrinters(
          this.lifetime.signal,
        );
        this.patch({
          printerDevices: Object.freeze(
            devices
              .map(normalizePrinterDevice)
              .sort(comparePrinterDevices),
          ),
          statusCode: "printer-scan-finished",
        });
      } catch (error) {
        this.patch({
          statusCode: printerScanFailureStatus(error),
        });
      }
    });
  }

  public connectPrinter(peripheralId: string): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    const normalizedId = peripheralId.trim();
    if (!normalizedId) {
      this.patch({ statusCode: "printer-connect-failed" });
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        await this.options.port.connectPrinter(
          normalizedId,
          this.lifetime.signal,
        );
      } catch {
        this.patch({ statusCode: "printer-connect-failed" });
        return;
      }

      const settings = bindPrinterSettingsToCurrentStore(
        normalizePrinterSettings({
          ...this.state.printer,
          peripheralId: normalizedId,
        }),
        this.state.device.storeCode,
      );
      this.patch({
        hardware: {
          ...this.state.hardware,
          printerStatus: "connected",
        },
        printer: settings,
      });
      try {
        await this.options.port.savePrinterSettings(
          settings,
          this.lifetime.signal,
        );
        this.patch({ statusCode: "printer-connected" });
      } catch {
        // 硬件连接已经成立；保存失败时保留真实连接态与 draft，供用户明确重试保存。
        this.patch({ statusCode: "printer-connected-save-failed" });
      }
    });
  }

  public testPrinter(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        await this.options.port.savePrinterSettings(
          bindPrinterSettingsToCurrentStore(
            normalizePrinterSettings(this.state.printer),
            this.state.device.storeCode,
          ),
          this.lifetime.signal,
        );
      } catch {
        this.patch({ statusCode: "printer-test-failed" });
        return;
      }
      try {
        await this.options.port.testPrinter(this.lifetime.signal);
        this.patch({ statusCode: "printer-test-passed" });
      } catch (error) {
        this.patch({
          statusCode: isPrinterTestOutcomeUnknown(error)
            ? "printer-test-unknown"
            : "printer-test-failed",
        });
      }
    });
  }

  public testCashDrawer(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      const testCashDrawer = this.options.port.testCashDrawer;
      if (!testCashDrawer) {
        this.patch({ statusCode: "cash-drawer-test-failed" });
        return;
      }
      try {
        // 测试必须先保存当前 draft；正式受控动作随后只读取持久设置，不能误用旧外设。
        await this.options.port.savePrinterSettings(
          bindPrinterSettingsToCurrentStore(
            normalizePrinterSettings(this.state.printer),
            this.state.device.storeCode,
          ),
          this.lifetime.signal,
        );
      } catch {
        this.patch({ statusCode: "cash-drawer-test-failed" });
        return;
      }
      try {
        const result = await testCashDrawer.call(
          this.options.port,
          this.lifetime.signal,
        );
        this.patch({ statusCode: cashDrawerTestStatus(result) });
      } catch {
        this.patch({ statusCode: "cash-drawer-test-failed" });
      }
    });
  }

  public clearSavedPrinter(): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePrinter)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      const clearSavedPrinter = this.options.port.clearSavedPrinter;
      if (!clearSavedPrinter) {
        this.patch({ statusCode: "printer-clear-failed" });
        return;
      }
      try {
        const result = await clearSavedPrinter.call(
          this.options.port,
          this.lifetime.signal,
        );
        const printer = bindPrinterSettingsToCurrentStore(
          normalizePrinterSettings({
            ...this.state.printer,
            peripheralId: null,
          }),
          this.state.device.storeCode,
        );
        if (result.status === "completed") {
          this.patch({
            printer,
            statusCode: "printer-cleared",
          });
          return;
        }
        // peripheralId 已先耐久清除；断开失败时仍必须让 draft 与持久状态一致。
        this.patch({
          printer,
          statusCode: "printer-cleared-disconnect-failed",
        });
      } catch {
        // 持久化失败时组合层不会断开，旧 ID 与当前硬件状态都必须保留。
        this.patch({ statusCode: "printer-clear-failed" });
      }
    });
  }

  public testScanner(): Promise<void> {
    if (!this.requirePermission(this.state.access.canTestScanner)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        const result = await this.options.port.testScanner(
          this.lifetime.signal,
        );
        const value = result.value.trim();
        if (!value || value.length > 512) {
          throw new Error("invalid scanner result");
        }
        this.patch({
          hardware: {
            ...this.state.hardware,
            // 硬件测试只保留长度与脱敏尾码，避免员工码、券码等误扫后进入 UI state。
            lastScannerValue: maskScannerValue(value),
            scannerStatus: "ready",
          },
          statusCode: "scanner-test-passed",
        });
      } catch {
        this.patch({ statusCode: "scanner-test-failed" });
      }
    });
  }

  public setExternalDisplayEnabled(enabled: boolean): Promise<void> {
    if (!this.requirePermission(this.state.access.canManageCustomerDisplay)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        await this.options.port.setExternalDisplayEnabled(
          enabled,
          this.lifetime.signal,
        );
        this.patch({
          externalDisplay: { ...this.state.externalDisplay, enabled },
          statusCode: "display-setting-saved",
        });
      } catch {
        this.patch({ statusCode: "display-setting-failed" });
      }
    });
  }

  public testExternalDisplay(): Promise<void> {
    if (!this.requirePermission(this.state.access.canManageCustomerDisplay)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        await this.options.port.testExternalDisplay(this.lifetime.signal);
        this.patch({ statusCode: "display-test-passed" });
      } catch {
        this.patch({ statusCode: "display-test-failed" });
      }
    });
  }

  public downloadCatalog(): Promise<void> {
    if (this.catalogRefreshInFlight) return this.catalogRefreshInFlight;
    if (!this.requirePermission(this.state.access.canDownloadCatalog)) {
      return Promise.resolve();
    }
    // 目录任务由应用级协调器持有；页面销毁不能中止其 signal。
    const signal = new AbortController().signal;
    const operation = (async () => {
      try {
        const catalog = normalizeCatalog(
          await this.options.port.downloadCatalog(signal),
        );
        if (this.destroyed) return;
        this.patch({
          catalog,
          statusCode: "catalog-downloaded",
        });
      } catch {
        if (this.destroyed) return;
        this.patch({ statusCode: "catalog-download-failed" });
      }
    })().finally(() => {
      if (this.catalogRefreshInFlight === operation) {
        this.catalogRefreshInFlight = null;
      }
    });
    this.catalogRefreshInFlight = operation;
    return operation;
  }

  public checkForAppUpdate(): Promise<void> {
    if (!this.requirePermission(this.state.access.canManageAppUpdate)) {
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        const appUpdate = normalizeAppUpdate(
          await this.options.port.checkForAppUpdate(this.lifetime.signal),
        );
        this.patch({
          appUpdate,
          statusCode: "app-update-checked",
        });
      } catch {
        this.patch({ statusCode: "app-update-check-failed" });
      }
    });
  }

  public requestApiAddressChange(): boolean {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return false;
    }
    if (!this.requirePermission(this.state.access.canReregisterDevice)) {
      return false;
    }
    let apiBaseUrl: string;
    try {
      apiBaseUrl = normalizeApiAddress(this.state.apiAddressDraft);
    } catch {
      this.patch({ confirmation: null, statusCode: "invalid-api-address" });
      return false;
    }
    return this.requestConfirmation({
      kind: "change-api-address",
      apiBaseUrl,
    });
  }

  public requestCatalogReset(): boolean {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return false;
    }
    if (!this.requirePermission(this.state.access.canResetCatalog)) {
      return false;
    }
    return this.requestConfirmation({ kind: "reset-catalog" });
  }

  public previewDeviceReregistration(): Promise<void> {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return Promise.resolve();
    }
    if (!this.requirePermission(this.state.access.canReregisterDevice)) {
      return Promise.resolve();
    }
    const activationCode = parseDeviceActivationCode(
      this.state.deviceActivationCodeDraft,
    );
    if (!activationCode) {
      this.patch({
        confirmation: null,
        deviceActivationPreview: null,
        deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
        statusCode: "invalid-device-registration",
      });
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        const previewDeviceActivationCode =
          this.options.port.previewDeviceActivationCode;
        if (!previewDeviceActivationCode) {
          throw new Error("Device activation preview is unavailable.");
        }
        const response = await previewDeviceActivationCode.call(
          this.options.port,
          activationCode,
          this.lifetime.signal,
        );
        const preview = normalizeDeviceActivationPreview(
          activationCode,
          response,
        );
        this.patch({
          deviceActivationCodeDraft: activationCode,
          deviceActivationPreview: preview,
          deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
          statusCode: null,
        });
      } catch {
        this.patch({
          deviceActivationPreview: null,
          deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
          statusCode: "device-activation-preview-failed",
        });
      }
    });
  }

  public requestDeviceReregistration(): Promise<void> {
    if (this.catalogRefreshRunning()) {
      this.patch({
        confirmation: null,
        deviceReregistrationPreflight: Object.freeze({ kind: "failed" }),
        statusCode: "safety-check-failed",
      });
      return Promise.resolve();
    }
    if (!this.requirePermission(this.state.access.canReregisterDevice)) {
      return Promise.resolve();
    }
    const activationCode = parseDeviceActivationCode(
      this.state.deviceActivationCodeDraft,
    );
    const preview = this.state.deviceActivationPreview;
    const terminalName = this.state.terminalNameDraft.trim();
    if (!activationCode || !preview || preview.activationCode !== activationCode) {
      this.patch({
        confirmation: null,
        deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
        statusCode: "invalid-device-registration",
      });
      return Promise.resolve();
    }
    const confirmation: SettingsDangerousConfirmation = Object.freeze({
      kind: "reregister-device",
      activationCode,
      currentStoreCode: this.state.device.storeCode,
      preview,
      ...(terminalName ? { terminalName } : {}),
    });
    return this.runAction(async () => {
      this.patch({
        confirmation: null,
        deviceReregistrationPreflight: Object.freeze({ kind: "checking" }),
      });
      try {
        const result = await this.options.port.preflightDeviceReregistration(
          this.lifetime.signal,
        );
        if (result.status === "ready") {
          this.patch({
            confirmation,
            deviceReregistrationPreflight: Object.freeze({ kind: "ready" }),
            statusCode: null,
          });
          return;
        }
        if (result.reason === "pending-local-data") {
          this.patch({
            confirmation: null,
            deviceReregistrationPreflight: Object.freeze({
              kind: "blocked",
              blockers: result.blockers,
            }),
            statusCode: "pending-local-data",
          });
          return;
        }
        this.patch({
          confirmation: null,
          deviceReregistrationPreflight: Object.freeze({ kind: "failed" }),
          statusCode: "safety-check-failed",
        });
      } catch {
        this.patch({
          confirmation: null,
          deviceReregistrationPreflight: Object.freeze({ kind: "failed" }),
          statusCode: "safety-check-failed",
        });
      }
    });
  }

  public dismissDeviceReregistrationPreflight(): void {
    if (!this.canEdit()) return;
    this.patch({
      deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
      statusCode: null,
    });
  }

  public requestDeviceRegistrationReset(): boolean {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return false;
    }
    if (!this.requirePermission(this.state.access.canReregisterDevice)) {
      return false;
    }
    return this.requestConfirmation({ kind: "reset-device-registration" });
  }

  public requestAppRestart(): boolean {
    if (this.catalogRefreshRunning()) {
      this.patch({ confirmation: null, statusCode: "safety-check-failed" });
      return false;
    }
    if (!this.requirePermission(this.state.access.canManageAppUpdate)) {
      return false;
    }
    return this.requestConfirmation({ kind: "restart-app" });
  }

  public cancelConfirmation(): void {
    if (this.destroyed || this.state.busy) return;
    this.patch({ confirmation: null, statusCode: null });
  }

  public confirmDangerousAction(employeeBarcode?: string): Promise<void> {
    const confirmation = this.state.confirmation;
    if (!confirmation || this.destroyed) return Promise.resolve();
    const resetEmployeeBarcode = employeeBarcode?.trim() ?? "";
    if (
      confirmation.kind === "reset-device-registration" &&
      !resetEmployeeBarcode
    ) {
      this.patch({
        statusCode: "device-registration-reset-barcode-required",
      });
      return Promise.resolve();
    }
    if (
      this.catalogRefreshRunning() &&
      conflictsWithCatalogRefresh(confirmation)
    ) {
      this.patch({
        confirmation: null,
        statusCode: "safety-check-failed",
      });
      return Promise.resolve();
    }
    return this.runAction(async () => {
      try {
        const result = await this.options.port.executeDangerousAction(
          confirmation,
          this.lifetime.signal,
          confirmation.kind === "reset-device-registration"
            ? resetEmployeeBarcode
            : undefined,
        );
        if (result.status === "blocked") {
          const reregistrationPreflightPatch =
            confirmation.kind === "reregister-device"
              ? result.reason === "pending-local-data"
                ? {
                    deviceReregistrationPreflight: Object.freeze({
                      kind: "blocked" as const,
                      blockers: result.blockers,
                    }),
                  }
                : result.reason === "safety-check-failed"
                  ? {
                      deviceReregistrationPreflight: Object.freeze({
                        kind: "failed" as const,
                      }),
                    }
                  : {}
              : {};
          this.patch({
            ...(confirmation.kind === "change-api-address"
              ? { apiAddressDraft: this.state.apiBaseUrl }
              : {}),
            confirmation: null,
            ...reregistrationPreflightPatch,
            statusCode:
              result.reason === "candidate-unreachable"
                ? confirmation.kind === "change-api-address"
                  ? "api-health-check-failed"
                  : "safety-check-failed"
                : result.reason,
          });
          return;
        }
        if (result.status === "unknown") {
          if (confirmation.kind !== "pair-linkly") {
            throw new Error("unexpected unknown dangerous action result");
          }
          this.patch({
            confirmation: null,
            linklySetup: this.state.linklySetup
              ? resetLinklySetupState(
                  this.state.linklySetup,
                  confirmation.environment,
                  true,
                  true,
                )
              : null,
            statusCode: "linkly-pair-unknown",
          });
          await this.loadLinklySetupState(
            confirmation.environment,
            this.loadGeneration,
            true,
          );
          if (this.state.linklySetup?.health.kind !== "failed") {
            this.patch({ statusCode: "linkly-pair-unknown" });
          }
          return;
        }
        if (result.status === "committed-reload-required") {
          if (confirmation.kind !== "reregister-device") {
            throw new Error(
              "unexpected committed reload dangerous action result",
            );
          }
          this.patch({
            confirmation: null,
            statusCode: "device-reregister-started",
          });
          return;
        }
        if (result.status === "pending-recovery") {
          this.patch({
            confirmation: null,
            statusCode: "device-registration-reset-pending-recovery",
          });
          return;
        }
        if (result.kind !== confirmation.kind) {
          throw new Error("dangerous action result mismatch");
        }
        if (confirmation.kind === "change-api-address") {
          this.patch({
            apiBaseUrl: confirmation.apiBaseUrl,
            apiAddressDraft: confirmation.apiBaseUrl,
            confirmation: null,
            statusCode: "api-address-saved",
          });
          return;
        }
        if (confirmation.kind === "change-payment-settings") {
          this.applySavedPaymentSettings(confirmation.input);
          return;
        }
        if (confirmation.kind === "pair-linkly") {
          this.patch({
            confirmation: null,
            linklySetup: this.state.linklySetup
              ? resetLinklySetupState(
                  this.state.linklySetup,
                  confirmation.environment,
                  true,
                  true,
                )
              : null,
          });
          await this.loadLinklySetupState(
            confirmation.environment,
            this.loadGeneration,
            true,
          );
          if (this.state.linklySetup?.health.kind !== "failed") {
            this.patch({ statusCode: "linkly-paired" });
          }
          return;
        }
        if (confirmation.kind === "assign-linkly-terminal" && result.kind === "assign-linkly-terminal") {
          this.patch({
            confirmation: null,
            linklySetup: this.state.linklySetup
              ? Object.freeze({
                  ...this.state.linklySetup,
                  terminals: Object.freeze({ kind: "ready" as const, value: result.terminals }),
                  ...(confirmation.affectsCurrentDevice
                    ? {
                        health: Object.freeze({ kind: "idle" as const, value: null }),
                        logonTest: Object.freeze({
                          environment: confirmation.environment,
                          status: "idle" as const,
                        }),
                      }
                    : {}),
                })
              : null,
            statusCode: "linkly-terminal-selected",
          });
          // 写入回包是事务快照；随后 GET 再核对，且不会自动重放 PUT。
          await this.loadLinklySetupState(
            confirmation.environment,
            this.loadGeneration,
            confirmation.affectsCurrentDevice,
          );
          return;
        }
        if (
          confirmation.kind === "reset-catalog" &&
          result.kind === "reset-catalog"
        ) {
          this.patch({
            catalog: normalizeCatalog(result.catalog),
            confirmation: null,
            statusCode: "catalog-reset",
          });
          return;
        }
        if (confirmation.kind === "reset-device-registration") {
          this.patch({
            confirmation: null,
            statusCode: "device-registration-reset-completed",
          });
          return;
        }
        this.patch({
          confirmation: null,
          statusCode:
            confirmation.kind === "reregister-device"
              ? "device-reregister-started"
              : "app-restart-requested",
        });
      } catch (error) {
        if (confirmation.kind === "assign-linkly-terminal") {
          const statusCode = linklyTerminalSelectionFailureStatus(error);
          this.patch({
            confirmation: null,
            linklySetup: this.state.linklySetup
              ? resetLinklySetupState(
                  this.state.linklySetup,
                  confirmation.environment,
                  confirmation.affectsCurrentDevice,
                  false,
                )
              : null,
          });
          await this.loadLinklySetupState(
            confirmation.environment,
            this.loadGeneration,
            confirmation.affectsCurrentDevice,
          );
          this.patch({ statusCode });
          return;
        }
        this.patch({
          ...(confirmation.kind === "change-api-address"
            ? { apiAddressDraft: this.state.apiBaseUrl }
            : {}),
          confirmation: null,
          statusCode: confirmation.kind === "pair-linkly"
            ? linklyPairFailureStatus(error)
            : dangerousActionFailureCode(confirmation.kind),
        });
      }
    });
  }

  private applySavedPaymentSettings(input: SettingsPaymentSettingsInput): void {
    this.patch({
      confirmation: null,
      paymentProvider: input.provider,
      paymentProviderDraft: input.provider,
      ...(input.linkly
        ? {
            linkly: {
              ...this.state.linkly,
              ...input.linkly,
              available: true,
              blockerCode: null,
            },
            linklyDraft: input.linkly,
          }
        : {}),
      ...(input.square
        ? {
            square: {
              ...this.state.square,
              ...input.square,
              available: true,
              blockerCode: null,
            },
            squareDraft: input.square,
          }
        : {}),
      statusCode: "payment-settings-saved",
    });
  }

  private requestConfirmation(
    confirmation: SettingsDangerousConfirmation,
  ): boolean {
    if (this.destroyed || this.state.busy || this.state.confirmation !== null) {
      return false;
    }
    this.patch({ confirmation, statusCode: null });
    return true;
  }

  private patchLinklyConnectionTest(
    key: string,
    value: SettingsLinklyConnectionTestState,
  ): void {
    if (!this.state.linklySetup) return;
    this.patch({
      linklySetup: Object.freeze({
        ...this.state.linklySetup,
        connectionTests: Object.freeze({
          ...this.state.linklySetup.connectionTests,
          [key]: Object.freeze(value),
        }),
      }),
    });
  }

  private isCurrentLinklyTerminalScope(
    terminal: SettingsLinklyTerminal,
    generation: number,
  ): boolean {
    const current = this.state.linklySetup?.terminals.value?.terminals.find(
      (candidate) => candidate.terminalId === terminal.terminalId,
    );
    return this.linklySetupGeneration === generation &&
      current?.terminalVersion === terminal.terminalVersion &&
      current?.assignedDeviceCode === terminal.assignedDeviceCode &&
      current?.assignmentRevision === terminal.assignmentRevision;
  }

  private requirePermission(granted: boolean): boolean {
    if (
      this.destroyed ||
      this.state.busy ||
      this.state.confirmation !== null ||
      this.state.kind !== "ready" ||
      !granted
    ) {
      if (!granted && this.state.confirmation === null) {
        this.patch({ statusCode: "permission-required" });
      }
      return false;
    }
    return true;
  }

  private async loadSquareDevicesForSelection(
    preferredDeviceId: string | null,
    expectedDeviceCodeId: string | null = null,
  ): Promise<void> {
    if (!this.requirePermission(this.state.access.canConfigurePayments)) {
      return;
    }
    const squareSetup = this.options.port.squareSetup;
    if (!squareSetup) return;
    const environment = this.state.squareDraft.environment;
    const selectedLocation = findSquareLocation(
      this.state.squareSetup.locations.items,
      this.state.squareSetup.selectedLocationId,
    );
    if (!selectedLocation) return;
    const locationId = selectedLocation.id;
    const generation = ++this.squareDevicesGeneration;
    this.patch({
      squareSetup: Object.freeze({
        ...this.state.squareSetup,
        devices: Object.freeze({
          ...this.state.squareSetup.devices,
          kind: "loading",
        }),
      }),
      statusCode: null,
    });
    try {
      const devices = mergeSettingsSquareDevices(
        environment,
        locationId,
        await squareSetup.listSquareDevices(
          environment,
          locationId,
          this.lifetime.signal,
        ),
      );
      if (
        !this.isCurrentSquareLocationRequest(
          environment,
          locationId,
          generation,
          this.squareDevicesGeneration,
        )
      ) {
        return;
      }
      if (devices.length === 0) {
        this.patch({
          squareSetup: Object.freeze({
            ...this.state.squareSetup,
            devices: Object.freeze({ kind: "empty", items: devices }),
            selectedDeviceId: "",
            devicesLoadedForLocationId: locationId,
          }),
        });
        return;
      }
      const preferredDeviceStillApplies =
        preferredDeviceId !== null &&
        (expectedDeviceCodeId === null ||
          equalPublicIdentifier(
            expectedDeviceCodeId,
            this.state.squareSetup.selectedDeviceCodeId,
          ));
      const candidateDeviceId = preferredDeviceStillApplies
        ? preferredDeviceId
        : this.state.squareDraft.deviceId ||
          (this.state.square.environment === environment
            ? this.state.square.deviceId
            : "");
      // Sandbox 首次配置没有历史终端时，默认使用官方成功信用卡测试终端。
      const selectedDevice = findSquareDevice(
        devices,
        candidateDeviceId ||
          (environment === "Sandbox"
            ? SETTINGS_SQUARE_SANDBOX_CHECKOUT_DEVICES[0].id
            : ""),
      );
      const selectedDeviceId = selectedDevice?.id ?? "";
      this.patch({
        squareDraft: {
          ...this.state.squareDraft,
          deviceId: selectedDeviceId,
        },
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          devices: Object.freeze({ kind: "ready", items: devices }),
          selectedDeviceId,
          devicesLoadedForLocationId: locationId,
        }),
      });
    } catch (error) {
      if (
        isAbortError(error) ||
        !this.isCurrentSquareLocationRequest(
          environment,
          locationId,
          generation,
          this.squareDevicesGeneration,
        )
      ) {
        return;
      }
      this.patch({
        squareSetup: Object.freeze({
          ...this.state.squareSetup,
          devices: Object.freeze({
            ...this.state.squareSetup.devices,
            kind: "failed",
          }),
        }),
      });
    }
  }

  private async loadLinklySetupState(
    environment: PaymentEnvironment,
    loadGeneration: number,
    resetLogonTest: boolean,
  ): Promise<void> {
    const setup = this.options.port.linklySetup;
    if (
      !setup ||
      this.destroyed ||
      loadGeneration !== this.loadGeneration
    ) {
      return;
    }
    const generation = ++this.linklySetupGeneration;
    const current =
      this.state.linklySetup ?? initialLinklySetupState(environment);
    const previousTerminals = current.terminals.value;
    this.patch({
      linklySetup: resetLinklySetupState(
        current,
        environment,
        resetLogonTest,
        false,
      ),
    });
    let terminals: SettingsLinklyTerminalSelectionSnapshot | null = null;
    // 目录读取失败时作用域无法确认；先按失效处理，完整同作用域读取成功后再保留。
    let activeSelectionChanged = previousTerminals !== null;
    try {
      // Active health 必须绑定同一次权威选择；先读目录可避免把旧终端状态带入检查。
      terminals = setup.readTerminals
        ? await setup.readTerminals(environment, this.lifetime.signal)
        : null;
      if (
        !this.isCurrentLinklySetupRequest(
          environment,
          loadGeneration,
          generation,
        )
      ) {
        return;
      }
      activeSelectionChanged = terminals
        ? previousTerminals === null ||
          terminals.environment !== previousTerminals.environment ||
          terminals.mode !== previousTerminals.mode ||
          terminals.selectedTerminalId !== previousTerminals.selectedTerminalId ||
          terminals.selectionRevision !== previousTerminals.selectionRevision
        : previousTerminals !== null;
      // 手动刷新只恢复已完成且仍属于当前线路版本的结果；迟到回包仍受 generation 隔离。
      // 网关断连未写入持久健康状态时，不能让较旧的“已连接”覆盖刚看到的检测失败。
      if (!resetLogonTest && terminals?.environment === environment &&
          terminals.mode === previousTerminals?.mode) {
        const connectionTests: Record<string, SettingsLinklyConnectionTestState> = {};
        for (const state of Object.values(current.connectionTests)) {
          const result = state.result;
          const terminal = terminals.terminals.find((item) => item.terminalId === state.terminalId);
          if (state.kind !== "ready" || !result || !terminal ||
              result.environment !== environment ||
              result.terminalId !== terminal.terminalId ||
              result.terminalVersion !== terminal.terminalVersion ||
              result.assignedDeviceCode !== (terminal.assignedDeviceCode ?? null) ||
              result.assignmentRevision !== terminal.assignmentRevision ||
              !Number.isFinite(Date.parse(result.checkedAt)) ||
              (terminal.lastHealthAt !== null &&
                Date.parse(terminal.lastHealthAt) >= Date.parse(result.checkedAt))) continue;
          const key = linklyConnectionTestKey(terminal, generation);
          connectionTests[key] = Object.freeze({ ...state, key });
        }
        this.patch({ linklySetup: Object.freeze({
          ...this.state.linklySetup!,
          connectionTests: Object.freeze(connectionTests),
        }) });
      }
      if (
        terminals?.environment === environment &&
        terminals.mode === "Active" &&
        terminals.selectedTerminalId === null &&
        terminals.selectionRevision === 0
      ) {
        // Active 目录允许尚未选择终端；此时 scoped health 没有合法参数，等待用户明确选择。
        this.patch({
          linklySetup: Object.freeze({
            ...this.state.linklySetup!,
            health: Object.freeze({
              kind: "selection-required" as const,
              value: null,
            }),
            terminals: Object.freeze({
              kind: "ready" as const,
              value: terminals,
            }),
            logonTest: activeSelectionChanged
              ? Object.freeze({ environment, status: "idle" as const })
              : this.state.linklySetup!.logonTest,
          }),
          statusCode:
            this.state.statusCode === "linkly-health-load-failed"
              ? null
              : this.state.statusCode,
        });
        return;
      }
      const health = await setup.readState(
        environment,
        this.lifetime.signal,
        terminals,
      );
      if (
        !this.isCurrentLinklySetupRequest(
          environment,
          loadGeneration,
          generation,
        ) ||
        health.environment !== environment
      ) {
        return;
      }
      this.patch({
        linklySetup: Object.freeze({
          ...this.state.linklySetup!,
          health: Object.freeze({ kind: "ready", value: health }),
          terminals: terminals
            ? Object.freeze({ kind: "ready" as const, value: terminals })
            : this.state.linklySetup!.terminals,
          logonTest: activeSelectionChanged
            ? Object.freeze({ environment, status: "idle" as const })
            : this.state.linklySetup!.logonTest,
        }),
      });
    } catch (error) {
      if (
        isAbortError(error) ||
        !this.isCurrentLinklySetupRequest(
          environment,
          loadGeneration,
          generation,
        )
      ) {
        return;
      }
      this.patch({
        linklySetup: Object.freeze({
          ...this.state.linklySetup!,
          health: Object.freeze({ kind: "failed", value: null }),
          // health 失败不抹掉已经读到的权威目录，确保仍可选择/修复终端。
          terminals:
            terminals?.environment === environment
              ? Object.freeze({ kind: "ready" as const, value: terminals })
              : Object.freeze({ kind: "failed" as const, value: null }),
          logonTest: activeSelectionChanged
            ? Object.freeze({ environment, status: "idle" as const })
            : this.state.linklySetup!.logonTest,
        }),
        statusCode: "linkly-health-load-failed",
      });
    }
  }

  private invalidateSquareRequests(
    scope: "environment" | "location",
  ): void {
    if (scope === "environment") {
      this.squareTokenGeneration += 1;
      this.squareLocationsGeneration += 1;
    }
    this.squareDevicesGeneration += 1;
    this.squareDeviceCodesGeneration += 1;
  }

  private isCurrentSquareEnvironmentRequest(
    environment: PaymentEnvironment,
    generation: number,
    currentGeneration: number,
  ): boolean {
    return (
      !this.destroyed &&
      environment === this.state.squareDraft.environment &&
      generation === currentGeneration
    );
  }

  private isCurrentSquareLocationRequest(
    environment: PaymentEnvironment,
    locationId: string,
    generation: number,
    currentGeneration: number,
  ): boolean {
    return (
      this.isCurrentSquareEnvironmentRequest(
        environment,
        generation,
        currentGeneration,
      ) &&
      equalPublicIdentifier(
        locationId,
        this.state.squareSetup.selectedLocationId,
      )
    );
  }

  private canEdit(): boolean {
    return (
      !this.destroyed &&
      !this.state.busy &&
      this.state.confirmation === null &&
      this.state.kind === "ready"
    );
  }

  private canEditPayments(): boolean {
    return this.canEdit() && this.state.access.canConfigurePayments;
  }

  private canEditPrinter(): boolean {
    return this.canEdit() && this.state.access.canConfigurePrinter;
  }

  /** 已应用总部下发资料后八项门店资料（含券使用说明、分期条款）只读，要改请到 Web 分店管理修改并重新下发。 */
  private canEditReceiptProfile(): boolean {
    return this.canEditPrinter() && this.state.printer.profileVersion === 0;
  }

  private runAction(action: () => Promise<void>): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.actionInFlight) return this.actionInFlight;
    this.patch({ busy: true, statusCode: null });
    const operation = action().finally(() => {
      if (this.actionInFlight === operation) {
        this.actionInFlight = null;
        this.patch({ busy: false });
      }
    });
    this.actionInFlight = operation;
    return operation;
  }

  private isCurrentLoad(generation: number): boolean {
    return !this.destroyed && this.loadGeneration === generation;
  }

  private isCurrentLinklySetupRequest(
    environment: PaymentEnvironment,
    loadGeneration: number,
    generation: number,
  ): boolean {
    return (
      this.isCurrentLoad(loadGeneration) &&
      this.linklySetupGeneration === generation &&
      this.state.linklyDraft.environment === environment
    );
  }

  private patch(patch: Partial<SettingsState>): void {
    if (this.destroyed) return;
    this.state = Object.freeze({ ...this.state, ...patch });
    for (const listener of this.listeners) listener();
  }

  private readonly handleCatalogRefreshChanged = (): void => {
    if (this.destroyed) return;
    const catalogRefresh = this.options.port.getCatalogRefreshState();
    this.patch({
      catalog: catalogSnapshotForRefresh(
        this.state.catalog,
        catalogRefresh,
      ),
      catalogRefresh,
    });
  };

  private catalogRefreshRunning(): boolean {
    return this.state.catalogRefresh.kind === "running";
  }
}

export function normalizeApiAddress(value: string): string {
  const source = value.trim();
  let parsed: URL;
  try {
    parsed = new URL(source);
  } catch {
    throw new Error("invalid API address");
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new Error("invalid API scheme");
  }
  if (
    parsed.protocol === "http:" &&
    !isLoopbackHostname(parsed.hostname) &&
    !isTrustedLocalHbposApiOrigin(parsed.origin)
  ) {
    throw new Error("remote API requires HTTPS");
  }
  if (parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error("unsafe API address");
  }
  const path = parsed.pathname.replace(/\/+$/, "");
  return `${parsed.origin}${path}`;
}

export function hasPendingLocalData(
  snapshot: SettingsPendingDataSnapshot,
): boolean {
  return (
    snapshot.hasActiveCart ||
    snapshot.hasFulfilmentInFlight ||
    snapshot.hasSyncOrAuditInFlight ||
    snapshot.paymentConfigurationSensitiveOrderCount > 0 ||
    snapshot.pendingDurableWriteCount > 0 ||
    snapshot.pendingReturnCount > 0 ||
    snapshot.pendingSaleCount > 0 ||
    snapshot.unresolvedPaymentCount > 0
  );
}

function initialState(
  access: SettingsAccess,
  catalogRefresh: CatalogRefreshState,
  squareSetupAvailable: boolean,
  linklySetupAvailable: boolean,
): SettingsState {
  const printer = Object.freeze({
    ...DEFAULT_RECEIPT_PRINTER_SETTINGS,
  });
  const square = Object.freeze({
    available: false,
    blockerCode: null,
    environment: "Production" as const,
    deviceId: "",
    locationId: "",
  });
  const linkly = Object.freeze({
    available: false,
    blockerCode: null,
    environment: "Production" as const,
  });
  return Object.freeze({
    access,
    activePane: "general",
    apiBaseUrl: "",
    apiAddressDraft: "",
    appUpdate: Object.freeze({
      channel: "production",
      currentVersion: "",
      availableVersion: null,
      updateRequired: false,
      restartAvailable: false,
    }),
    busy: false,
    catalog: Object.freeze({
      snapshotId: null,
      itemCount: 0,
      activatedAt: null,
    }),
    catalogRefresh,
    confirmation: null,
    device: Object.freeze({
      deviceCode: "",
      storeCode: "",
      storeName: "",
      terminalName: "",
    }),
    deviceActivationCodeDraft: "",
    deviceActivationPreview: null,
    deviceReregistrationPreflight: Object.freeze({ kind: "idle" }),
    externalDisplay: Object.freeze({
      available: false,
      enabled: false,
      status: "unavailable",
    }),
    hardware: Object.freeze({
      printerStatus: "unavailable",
      scannerStatus: "unavailable",
      externalDisplayStatus: "unavailable",
      lastScannerValue: null,
    }),
    kind: access.canView ? "idle" : "unauthorized",
    linkly,
    linklyDraft: Object.freeze({
      environment: linkly.environment,
    }),
    linklySetup: linklySetupAvailable
      ? initialLinklySetupState(linkly.environment)
      : null,
    paymentProvider: null,
    paymentProviderDraft: null,
    paymentMethods: DEFAULT_PAYMENT_METHOD_SETTINGS,
    paymentMethodsDraft: DEFAULT_PAYMENT_METHOD_SETTINGS,
    paymentMethodsAvailable: false,
    printer,
    printerDevices: Object.freeze([]),
    square,
    squareDraft: Object.freeze({
      deviceId: "",
      environment: square.environment,
      locationId: "",
    }),
    squareDeviceCodeNameDraft: "HBPOS Terminal",
    squareSetup: initialSquareSetupState(squareSetupAvailable),
    statusCode: access.canView ? null : "permission-required",
    terminalNameDraft: "",
  });
}

function initialLinklySetupState(
  environment: PaymentEnvironment,
): SettingsLinklySetupState {
  return Object.freeze({
    health: Object.freeze({ kind: "idle", value: null }),
    terminals: Object.freeze({ kind: "idle", value: null }),
    logonTest: Object.freeze({ environment, status: "idle" }),
    connectionTests: Object.freeze({}),
    pairCodeResetToken: 0,
  });
}

function resetLinklySetupState(
  state: SettingsLinklySetupState,
  environment: PaymentEnvironment,
  resetLogonTest: boolean,
  resetPairCode: boolean,
): SettingsLinklySetupState {
  return Object.freeze({
    health: Object.freeze({ kind: "loading", value: null }),
    terminals: Object.freeze({ kind: "loading", value: null }),
    logonTest: resetLogonTest
      ? Object.freeze({ environment, status: "idle" })
      : state.logonTest,
    connectionTests: Object.freeze({}),
    pairCodeResetToken: state.pairCodeResetToken + (resetPairCode ? 1 : 0),
  });
}

function updateLinklyLogonTest(
  state: SettingsLinklySetupState,
  environment: PaymentEnvironment,
  status: SettingsLinklyLogonTestState["status"],
): SettingsLinklySetupState {
  return Object.freeze({
    ...state,
    logonTest: Object.freeze({ environment, status }),
  });
}

// 界面与配对请求共用资格判断，避免已加载云端线路却被旧健康检查挡住。
export function hasLinklyCloudCredentials(
  state: Pick<SettingsState, "linklySetup">,
  environment: PaymentEnvironment,
): boolean {
  const terminals = state.linklySetup?.terminals;
  if (
    terminals?.kind === "ready" &&
    terminals.value?.environment === environment &&
    terminals.value.mode === "Active"
  ) {
    return terminals.value.terminals.length > 0;
  }
  const health = state.linklySetup?.health;
  const value = health?.value;
  return (
    health?.kind === "ready" &&
    value?.environment === environment &&
    value.checks.some(
      (check) =>
        check.code.trim().toUpperCase() === "STORE_CREDENTIAL" &&
        check.isReady,
    )
  );
}

export function isLinklySetupReady(
  state: Pick<SettingsState, "linklySetup">,
  environment: PaymentEnvironment,
): boolean {
  const terminals = state.linklySetup?.terminals;
  const selection = terminals?.value;
  const selected = selection?.terminals.find(
    (terminal) => terminal.terminalId === selection.selectedTerminalId,
  );
  const logonTest = state.linklySetup?.logonTest;
  if (
    terminals?.kind === "ready" &&
    selection?.environment === environment &&
    selection.mode === "Active"
  ) {
    return Boolean(
      selected?.isReady &&
      !selected.isBusy &&
      selected.pairingState === "Ready" &&
      logonTest?.environment === environment &&
      logonTest.status === "passed",
    );
  }
  const health = state.linklySetup?.health;
  const value = health?.value;
  return (
    health?.kind === "ready" &&
    value?.environment === environment &&
    value.isReady === true &&
    logonTest?.environment === environment &&
    logonTest.status === "passed"
  );
}

export function isLinklyHealthReady(
  state: Pick<SettingsState, "linklySetup">,
  environment: PaymentEnvironment,
): boolean {
  const terminals = state.linklySetup?.terminals;
  const selection = terminals?.value;
  const selected = selection?.terminals.find(
    (terminal) => terminal.terminalId === selection.selectedTerminalId,
  );
  if (
    terminals?.kind === "ready" &&
    selection?.environment === environment &&
    selection.mode === "Active"
  ) {
    return Boolean(
      selected?.isReady &&
      !selected.isBusy &&
      selected.pairingState === "Ready",
    );
  }
  const health = state.linklySetup?.health;
  const value = health?.value;
  return (
    health?.kind === "ready" &&
    value?.environment === environment &&
    value.isReady === true
  );
}

function initialSquareSetupState(
  available: boolean,
): SettingsSquareSetupState {
  const kind: SettingsSquareRequestKind = available ? "idle" : "disabled";
  return Object.freeze({
    available,
    token: Object.freeze({ kind, value: null }),
    locations: Object.freeze({ kind, items: Object.freeze([]) }),
    devices: Object.freeze({ kind, items: Object.freeze([]) }),
    deviceCodes: Object.freeze({ kind, items: Object.freeze([]) }),
    selectedLocationId: "",
    selectedDeviceId: "",
    selectedDeviceCodeId: "",
    devicesLoadedForLocationId: null,
    deviceCodesLoadedForLocationId: null,
  });
}

function catalogSnapshotForRefresh(
  fallback: SettingsCatalogSnapshot,
  refresh: CatalogRefreshState,
): SettingsCatalogSnapshot {
  if (refresh.kind !== "success" && refresh.kind !== "warning") {
    return fallback;
  }
  return normalizeCatalog({
    snapshotId: refresh.summary.snapshotId,
    itemCount: refresh.summary.itemCount,
    activatedAt: refresh.summary.activatedAt,
  });
}

function conflictsWithCatalogRefresh(
  _confirmation: SettingsDangerousConfirmation,
): boolean {
  return true;
}

function normalizeSnapshot(snapshot: SettingsSnapshot): SettingsSnapshot {
  const linkly = Object.freeze({
    available: booleanValue(snapshot.linkly.available),
    blockerCode: safeBlockerCode(snapshot.linkly.blockerCode),
    environment: paymentEnvironment(snapshot.linkly.environment),
  });
  const square = Object.freeze({
    available: booleanValue(snapshot.square.available),
    blockerCode: safeBlockerCode(snapshot.square.blockerCode),
    environment: paymentEnvironment(snapshot.square.environment),
    deviceId: boundedPublicIdentifier(snapshot.square.deviceId),
    locationId: boundedPublicIdentifier(snapshot.square.locationId),
  });
  const requestedProvider = settingsPaymentProvider(snapshot.paymentProvider);
  const paymentProvider =
    requestedProvider &&
    isNormalizedPaymentProviderConfigured(requestedProvider, square, linkly)
      ? requestedProvider
      : null;
  return Object.freeze({
    apiBaseUrl: normalizeApiAddress(snapshot.apiBaseUrl),
    appUpdate: normalizeAppUpdate(snapshot.appUpdate),
    catalog: normalizeCatalog(snapshot.catalog),
    device: Object.freeze({
      deviceCode: snapshot.device.deviceCode.trim(),
      storeCode: snapshot.device.storeCode.trim(),
      storeName: snapshot.device.storeName.trim(),
      terminalName: snapshot.device.terminalName.trim(),
    }),
    externalDisplay: Object.freeze({
      available: booleanValue(snapshot.externalDisplay.available),
      enabled: booleanValue(snapshot.externalDisplay.enabled),
      status: externalDisplayStatus(snapshot.externalDisplay.status),
    }),
    hardware: Object.freeze({
      printerStatus: externalDisplayStatus(snapshot.hardware.printerStatus),
      scannerStatus: scannerStatus(snapshot.hardware.scannerStatus),
      externalDisplayStatus: externalDisplayStatus(
        snapshot.hardware.externalDisplayStatus,
      ),
      lastScannerValue: safeMaskedScannerValue(
        snapshot.hardware.lastScannerValue,
      ),
    }),
    linkly,
    paymentProvider,
    paymentMethods: normalizePaymentMethodSettings(snapshot.paymentMethods),
    printer: normalizePrinterSettings(snapshot.printer),
    square,
  });
}

function normalizePaymentDraft(
  provider: SettingsPaymentProvider | null,
  square: SettingsPaymentDraft["square"],
  linkly: SettingsPaymentDraft["linkly"],
  squareAvailable: boolean,
  linklyAvailable: boolean,
): SettingsPaymentSettingsInput | null {
  if (provider === "square") {
    if (!squareAvailable) return null;
    const locationId = square.locationId.trim();
    const deviceId = square.deviceId.trim();
    if (!locationId || !deviceId) return null;
    return Object.freeze({
      provider,
      square: Object.freeze({
        environment: paymentEnvironment(square.environment),
        locationId: boundedPublicIdentifier(locationId),
        deviceId: boundedPublicIdentifier(deviceId),
      }),
      linkly: null,
    });
  }
  if (provider === "linkly") {
    if (!linklyAvailable) return null;
    return Object.freeze({
      provider,
      square: null,
      linkly: Object.freeze({
        environment: paymentEnvironment(linkly.environment),
      }),
    });
  }
  return null;
}

function normalizePrinterSettings(
  settings: ReceiptPrinterSettings,
): ReceiptPrinterSettings {
  return Object.freeze({
    printEnabled: booleanValue(settings.printEnabled),
    drawerEnabled: booleanValue(settings.drawerEnabled),
    peripheralId: settings.peripheralId
      ? boundedPublicIdentifier(settings.peripheralId)
      : null,
    paper: settings.paper === "58mm" ? "58mm" : "80mm",
    locale: settings.locale === "zh-CN" ? "zh-CN" : "en",
    brandName: boundedPublicText(settings.brandName, 120),
    storeName: boundedPublicText(settings.storeName, 120),
    address: boundedPublicMultilineText(settings.address, 240),
    phone: boundedPublicText(settings.phone, 60),
    abn: boundedPublicText(settings.abn, 32),
    returnPolicy: boundedPublicMultilineText(settings.returnPolicy, 500),
    // 券使用说明 / 分期条款：规则同退货政策，上限与打印层、本机存储共用同一个常量。
    voucherTerms: boundedPublicMultilineText(
      settings.voucherTerms,
      RECEIPT_TERMS_TEXT_MAX_LENGTH,
    ),
    installmentTerms: boundedPublicMultilineText(
      settings.installmentTerms,
      RECEIPT_TERMS_TEXT_MAX_LENGTH,
    ),
    profileStoreCode: boundedPublicText(settings.profileStoreCode, 128),
    profileVersion: publicVersion(settings.profileVersion),
    profileAckedVersion: publicVersion(settings.profileAckedVersion),
  });
}

/** 下发版本号：脏值（缺失、非整数、负数）回落为 0，与本机存储层口径一致。 */
function publicVersion(value: unknown): number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0
    ? value
    : 0;
}

function receiptProfileSyncFailureStatus(
  reason: SettingsReceiptProfileSyncFailure,
): SettingsStatusCode {
  switch (reason) {
    case "offline":
      return "receipt-profile-sync-offline";
    case "unauthorized":
      return "receipt-profile-sync-forbidden";
    case "unsupported":
      return "receipt-profile-sync-unsupported";
    case "invalid":
      return "receipt-profile-sync-invalid";
    default:
      return "receipt-profile-sync-failed";
  }
}

function bindPrinterSettingsToCurrentStore(
  settings: ReceiptPrinterSettings,
  storeCode: string,
): ReceiptPrinterSettings {
  return Object.freeze({
    ...settings,
    profileStoreCode: boundedPublicText(storeCode, 128),
  });
}

function normalizeReceiptProfileDraft(
  profile: SettingsReceiptProfileDraft,
  currentStoreCode: string,
): SettingsReceiptProfileDraft {
  const storeCode = boundedPublicText(profile.storeCode, 128);
  if (storeCode !== currentStoreCode.trim()) {
    throw new Error("receipt profile store code mismatch");
  }
  return Object.freeze({
    storeCode,
    brandName: boundedPublicText(profile.brandName, 120),
    storeName: boundedPublicText(profile.storeName, 120),
    address: boundedPublicMultilineText(profile.address, 240),
    phone: boundedPublicText(profile.phone, 60),
    abn: boundedPublicText(profile.abn, 32),
    returnPolicy: boundedPublicMultilineText(profile.returnPolicy, 500),
    voucherTerms: boundedPublicMultilineText(
      profile.voucherTerms,
      RECEIPT_TERMS_TEXT_MAX_LENGTH,
    ),
    installmentTerms: boundedPublicMultilineText(
      profile.installmentTerms,
      RECEIPT_TERMS_TEXT_MAX_LENGTH,
    ),
  });
}

function normalizeCatalog(
  catalog: SettingsCatalogSnapshot,
): SettingsCatalogSnapshot {
  if (!Number.isSafeInteger(catalog.itemCount) || catalog.itemCount < 0) {
    throw new Error("invalid catalog count");
  }
  return Object.freeze({
    snapshotId: catalog.snapshotId?.trim() || null,
    itemCount: catalog.itemCount,
    activatedAt: catalog.activatedAt?.trim() || null,
  });
}

function normalizeAppUpdate(
  update: SettingsAppUpdateSnapshot,
): SettingsAppUpdateSnapshot {
  return Object.freeze({
    channel: update.channel.trim() || "production",
    currentVersion: update.currentVersion.trim(),
    availableVersion: update.availableVersion?.trim() || null,
    updateRequired: update.updateRequired,
    restartAvailable: update.restartAvailable,
  });
}

function maskScannerValue(value: string): string {
  const characters = Array.from(value);
  const suffix =
    characters.length >= 8
      ? characters
          .slice(-4)
          .join("")
          .replace(/[^A-Za-z0-9]/gu, "•")
      : "";
  return `${suffix ? `••••${suffix}` : "••••"} · ${characters.length} chars`;
}

function currentPaymentSettings(
  state: SettingsState,
): SettingsPaymentSettingsInput | null {
  return normalizePaymentDraft(
    state.paymentProvider,
    {
      environment: state.square.environment,
      deviceId: state.square.deviceId,
      locationId: state.square.locationId,
    },
    { environment: state.linkly.environment },
    isPaymentProviderAvailable("square", state),
    isPaymentProviderAvailable("linkly", state),
  );
}

function paymentSettingsEqual(
  left: SettingsPaymentSettingsInput,
  right: SettingsPaymentSettingsInput,
): boolean {
  if (left.provider !== right.provider) return false;
  if (left.provider === "square" && right.provider === "square") {
    return (
      left.square.environment === right.square.environment &&
      left.square.deviceId === right.square.deviceId &&
      left.square.locationId === right.square.locationId
    );
  }
  return (
    left.provider === "linkly" &&
    right.provider === "linkly" &&
    left.linkly.environment === right.linkly.environment
  );
}

function isPaymentProviderAvailable(
  provider: SettingsPaymentProvider,
  state: Pick<SettingsState, "linkly" | "square">,
): boolean {
  const snapshot = provider === "square" ? state.square : state.linkly;
  return snapshot.available && snapshot.blockerCode === null;
}

function isLinklyBaseSelectable(
  state: Pick<SettingsState, "linkly" | "linklySetup" | "square" | "squareSetup">,
): boolean {
  if (
    !state.linkly.available &&
    state.linkly.blockerCode === "LINKLY_CONFIGURATION_MISSING"
  ) {
    return true;
  }
  return isPaymentProviderAvailable("linkly", state);
}

function isPaymentProviderSelectable(
  provider: SettingsPaymentProvider,
  state: Pick<
    SettingsState,
    "linkly" | "linklyDraft" | "linklySetup" | "square" | "squareSetup"
  >,
): boolean {
  if (provider === "square" && state.squareSetup.available) return true;
  if (provider === "linkly") {
    return (
      isLinklyBaseSelectable(state) &&
      (!state.linklySetup ||
        isLinklySetupReady(state, state.linklyDraft.environment))
    );
  }
  return isPaymentProviderAvailable(provider, state);
}

function isLoadedSquareSelectionValid(
  state: SettingsState,
  square: SettingsPaymentDraft["square"],
): boolean {
  if (!state.squareSetup.available) return true;
  const token = state.squareSetup.token.value;
  if (
    state.squareSetup.token.kind !== "ready" ||
    !token?.configured ||
    !token.enabled ||
    token.environment !== square.environment
  ) {
    return false;
  }
  const location = findSquareLocation(
    state.squareSetup.locations.items,
    square.locationId,
  );
  const device = findSquareDevice(
    state.squareSetup.devices.items,
    square.deviceId,
  );
  return (
    state.squareSetup.locations.kind === "ready" &&
    state.squareSetup.devices.kind === "ready" &&
    location !== null &&
    device !== null &&
    !isSquareDeviceDisabled(device) &&
    equalPublicIdentifier(
      state.squareSetup.selectedLocationId,
      location.id,
    ) &&
    equalSquareDeviceId(state.squareSetup.selectedDeviceId, device.id) &&
    equalPublicIdentifier(
      state.squareSetup.devicesLoadedForLocationId ?? "",
      location.id,
    ) &&
    equalPublicIdentifier(device.locationId ?? "", location.id)
  );
}

function isNormalizedPaymentProviderConfigured(
  provider: SettingsPaymentProvider,
  square: SettingsSquareSnapshot,
  linkly: SettingsPaymentProviderSnapshot,
): boolean {
  if (
    !isPaymentProviderAvailable(provider, {
      square,
      linkly,
    })
  ) {
    return false;
  }
  return (
    provider === "linkly" ||
    (square.locationId.length > 0 && square.deviceId.length > 0)
  );
}

function settingsPaymentProvider(
  value: unknown,
): SettingsPaymentProvider | null {
  return value === "square" || value === "linkly" ? value : null;
}

function findSquareLocation(
  locations: readonly SettingsSquareLocation[],
  locationId: string,
): SettingsSquareLocation | null {
  const requestedId = safePublicIdentifier(locationId);
  if (!requestedId) return null;
  return (
    locations.find((location) =>
      equalPublicIdentifier(location.id, requestedId),
    ) ?? null
  );
}

function findSquareDevice(
  devices: readonly SettingsSquareDevice[],
  deviceId: string,
): SettingsSquareDevice | null {
  const requestedId = normalizeSettingsSquareDeviceId(deviceId);
  if (!requestedId) return null;
  return (
    devices.find((device) => equalSquareDeviceId(device.id, requestedId)) ??
    null
  );
}

function findSquareDeviceCode(
  deviceCodes: readonly SettingsSquareDeviceCode[],
  deviceCodeId: string,
): SettingsSquareDeviceCode | null {
  const requestedId = safePublicIdentifier(deviceCodeId);
  if (!requestedId) return null;
  return (
    deviceCodes.find((deviceCode) =>
      equalPublicIdentifier(deviceCode.id, requestedId),
    ) ?? null
  );
}

function replaceSquareDeviceCode(
  deviceCodes: readonly SettingsSquareDeviceCode[],
  updated: SettingsSquareDeviceCode,
): readonly SettingsSquareDeviceCode[] {
  return Object.freeze([
    updated,
    ...deviceCodes.filter(
      (deviceCode) => !equalPublicIdentifier(deviceCode.id, updated.id),
    ),
  ]);
}

function equalPublicIdentifier(left: string, right: string): boolean {
  return (
    safePublicIdentifier(left).toLowerCase() ===
    safePublicIdentifier(right).toLowerCase()
  );
}

function equalSquareDeviceId(left: string, right: string): boolean {
  const normalizedLeft = normalizeSettingsSquareDeviceId(left);
  const normalizedRight = normalizeSettingsSquareDeviceId(right);
  return (
    normalizedLeft !== null &&
    normalizedRight !== null &&
    normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
  );
}

function safePublicIdentifier(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length > 128 ||
    /[\u0000-\u001F\u007F]/u.test(value)
  ) {
    return "";
  }
  return value.trim();
}

function safeSquareDeviceCodeName(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length > 120 ||
    /[\u0000-\u001F\u007F]/u.test(value)
  ) {
    return "";
  }
  return value.trim();
}

function isSquareDeviceDisabled(device: SettingsSquareDevice): boolean {
  return device.status?.trim().toUpperCase() === "DISABLED";
}

function isAbortError(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "name" in error &&
    error.name === "AbortError"
  );
}

function dangerousActionFailureCode(
  kind: SettingsDangerousConfirmation["kind"],
): SettingsStatusCode {
  switch (kind) {
    case "change-payment-settings":
      return "payment-settings-save-failed";
    case "pair-linkly":
      return "linkly-pair-failed";
    case "assign-linkly-terminal":
      return "linkly-terminal-switch-failed";
    case "reset-catalog":
      return "catalog-reset-failed";
    case "reregister-device":
      return "device-reregister-failed";
    case "reset-device-registration":
      return "device-registration-reset-failed";
    default:
      return "restart-failed";
  }
}

export function linklyConnectionTestKey(
  terminal: Pick<SettingsLinklyTerminal, "terminalId" | "terminalVersion" | "assignedDeviceCode" | "assignmentRevision">,
  generation: number,
): string {
  return [
    terminal.terminalId,
    terminal.terminalVersion ?? "missing-version",
    terminal.assignedDeviceCode ?? "unassigned",
    terminal.assignmentRevision ?? "missing-revision",
    generation,
  ].join("|");
}

function normalizeDeviceActivationPreview(
  activationCode: string,
  response: SettingsDeviceActivationPreviewResponse,
): SettingsDeviceActivationPreview {
  const storeCode = response.storeCode?.trim() ?? "";
  const storeName = response.storeName?.trim() ?? "";
  const deviceSystem = response.deviceSystem?.trim() ?? "";
  const expiresAtUtc = response.expiresAtUtc?.trim() ?? "";
  if (
    response.isAllowed !== true ||
    !storeCode ||
    !storeName ||
    !deviceSystem ||
    !expiresAtUtc
  ) {
    throw new Error("Device activation preview was rejected or incomplete.");
  }
  return Object.freeze({
    activationCode,
    storeCode,
    storeName,
    deviceSystem,
    expiresAtUtc,
  });
}

function isLoopbackHostname(hostname: string): boolean {
  const normalized = hostname.toLowerCase().replace(/\.$/u, "");
  return (
    normalized === "localhost" ||
    normalized.endsWith(".localhost") ||
    normalized === "[::1]" ||
    normalized === "::1" ||
    /^127(?:\.\d{1,3}){3}$/u.test(normalized)
  );
}

function booleanValue(value: unknown): boolean {
  if (typeof value !== "boolean") throw new Error("invalid boolean");
  return value;
}

function paymentEnvironment(value: unknown): PaymentEnvironment {
  if (value !== "Sandbox" && value !== "Production") {
    throw new Error("invalid payment environment");
  }
  return value;
}

function externalDisplayStatus(
  value: unknown,
): "connected" | "disconnected" | "unavailable" {
  if (
    value !== "connected" &&
    value !== "disconnected" &&
    value !== "unavailable"
  ) {
    throw new Error("invalid hardware status");
  }
  return value;
}

function scannerStatus(value: unknown): "ready" | "unavailable" {
  if (value !== "ready" && value !== "unavailable") {
    throw new Error("invalid scanner status");
  }
  return value;
}

const SAFE_PAYMENT_BLOCKER_CODES = new Set([
  "SQUARE_CONFIGURATION_MISSING",
  "SQUARE_CONFIGURATION_INVALID",
  "SQUARE_CONFIGURATION_LOAD_FAILED",
  "LINKLY_CONFIGURATION_MISSING",
  "LINKLY_CONFIGURATION_INVALID",
  "LINKLY_CONFIGURATION_LOAD_FAILED",
  "VOUCHER_CONFIGURATION_DISABLED",
  "VOUCHER_CONFIGURATION_LOAD_FAILED",
  "PAYMENT_PROVIDER_UNKNOWN",
  "invalid-provider-config",
]);

function safeBlockerCode(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== "string" || !SAFE_PAYMENT_BLOCKER_CODES.has(value)) {
    return "invalid-provider-config";
  }
  return value;
}

function boundedPublicIdentifier(value: unknown): string {
  if (
    typeof value !== "string" ||
    value.length > 128 ||
    /[\u0000-\u001F\u007F-\u009F]/u.test(value)
  ) {
    throw new Error("invalid public identifier");
  }
  return value.trim();
}

function boundedPublicText(value: unknown, maxLength: number): string {
  if (
    typeof value !== "string" ||
    value.length > maxLength ||
    /[\u0000-\u001F\u007F-\u009F]/u.test(value)
  ) {
    throw new Error("invalid public text");
  }
  return value.trim();
}

/** 地址与退货政策需要换行排版，仅放行 CR/LF/TAB，其余控制字符（含 C1）拒绝。 */
function boundedPublicMultilineText(value: unknown, maxLength: number): string {
  if (
    typeof value !== "string" ||
    value.length > maxLength ||
    /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/u.test(value)
  ) {
    throw new Error("invalid public text");
  }
  return value;
}

function safeMaskedScannerValue(value: unknown): string | null {
  if (value === null) return null;
  if (
    typeof value !== "string" ||
    !/^••••(?:[A-Za-z0-9•]{4})? · \d{1,3} chars$/u.test(value)
  ) {
    return null;
  }
  return value;
}

function normalizePrinterDevice(
  device: SettingsPrinterDevice,
): SettingsPrinterDevice {
  return Object.freeze({
    id: boundedPublicIdentifier(device.id),
    name: boundedPublicText(device.name, 120),
    transport: boundedPublicText(device.transport, 32),
    preferred: device.preferred === true,
  });
}

function comparePrinterDevices(
  left: SettingsPrinterDevice,
  right: SettingsPrinterDevice,
): number {
  if (left.preferred !== right.preferred) {
    return left.preferred ? -1 : 1;
  }
  return compareStableText(left.name, right.name) ||
    compareStableText(left.id, right.id);
}

function compareStableText(left: string, right: string): number {
  const foldedLeft = left.toLowerCase();
  const foldedRight = right.toLowerCase();
  if (foldedLeft < foldedRight) return -1;
  if (foldedLeft > foldedRight) return 1;
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

function isPrinterTestOutcomeUnknown(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    error.code === SETTINGS_PRINTER_TEST_OUTCOME_UNKNOWN
  );
}

function cashDrawerTestStatus(
  result: SettingsCashDrawerTestResult,
): SettingsStatusCode {
  switch (result.status) {
    case "completed":
      return "cash-drawer-test-passed";
    case "unknown":
      return "cash-drawer-test-unknown";
    case "failed":
      return "cash-drawer-test-failed";
  }
}

function printerScanFailureStatus(error: unknown): SettingsStatusCode {
  if (!error || typeof error !== "object" || !("code" in error)) {
    return "printer-scan-failed";
  }
  const code = typeof error.code === "string" ? error.code.trim() : "";
  const bluetoothStatusByCode: Readonly<Record<string, SettingsStatusCode>> = {
    PRINTER_BLUETOOTH_AUTHORIZATION_PENDING:
      "printer-bluetooth-authorization-pending",
    PRINTER_BLUETOOTH_PERMISSION_REQUIRED:
      "printer-bluetooth-permission-required",
    PRINTER_BLUETOOTH_POWERED_OFF: "printer-bluetooth-powered-off",
    PRINTER_BLUETOOTH_RESTRICTED: "printer-bluetooth-restricted",
  };
  return bluetoothStatusByCode[code] ?? "printer-scan-failed";
}

function linklyPairFailureStatus(error: unknown): SettingsStatusCode {
  const code = error && typeof error === "object" && "code" in error ? error.code : null;
  switch (code) {
    case "LINKLY_CLOUD_BACKEND_PAIR_REJECTED":
      return "linkly-pair-code-rejected";
    case "LINKLY_CLOUD_BACKEND_PAIR_CREDENTIAL_MISSING":
      return "linkly-pair-credentials-required";
    case "LINKLY_CLOUD_BACKEND_PAIR_IN_PROGRESS":
    case "LINKLY_CLOUD_TERMINAL_SESSION_ACTIVE":
      return "linkly-pair-busy";
    default:
      return "linkly-pair-failed";
  }
}

function linklyConnectionFailureReason(
  error: unknown,
): NonNullable<SettingsLinklyConnectionTestState["failureReason"]> {
  if (!error || typeof error !== "object") return "unknown";
  const code = "code" in error && typeof error.code === "string" ? error.code : "";
  if (code === "LINKLY_CLOUD_TERMINAL_SESSION_ACTIVE" ||
      code === "LINKLY_TERMINAL_SESSION_ACTIVE" ||
      code === "LINKLY_CLOUD_BACKEND_PAIR_IN_PROGRESS") return "busy";
  // 当前服务端用同一冲突码表达租约保护和版本变化，不能凭一个 409 猜测具体原因。
  if (code === "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT") return "changed-or-busy";
  if (code === "LINKLY_CONNECTION_TEST_SCOPE_CHANGED" ||
      code === "LINKLY_TERMINAL_REVISION_CONFLICT" ||
      code === "LINKLY_SELECTION_REVISION_CONFLICT") return "changed";
  if ("status" in error && (error.status === 401 || error.status === 403)) return "access";
  return "unknown";
}

function linklyTerminalSelectionFailureStatus(
  error: unknown,
): SettingsStatusCode {
  if (!error || typeof error !== "object") {
    return "linkly-terminal-switch-failed";
  }
  const rawCode =
    "code" in error && typeof error.code === "string"
      ? error.code
      : "status" in error && typeof error.status === "string"
        ? error.status
        : "";
  const statusByCode: Readonly<Record<string, SettingsStatusCode>> = {
    LINKLY_TERMINAL_SELECTION_BLOCKED: "linkly-terminal-selection-blocked",
    LINKLY_TERMINAL_ASSIGNMENT_CONFLICT:
      "linkly-terminal-assignment-conflict",
    LINKLY_TERMINAL_SESSION_ACTIVE: "linkly-terminal-session-active",
    LINKLY_TERMINAL_REVISION_CONFLICT: "linkly-terminal-revision-conflict",
    LINKLY_SELECTION_REVISION_CONFLICT: "linkly-terminal-revision-conflict",
  };
  return statusByCode[rawCode.trim()] ?? "linkly-terminal-switch-failed";
}
