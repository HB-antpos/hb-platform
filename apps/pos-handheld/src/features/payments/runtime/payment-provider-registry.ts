import { ManualCardPaymentAdapter } from "../manual/manual-card-payment-adapter";
import type { HbposTransport } from "@/core/api";
import type {
  OnlinePaymentPort,
  PaymentAttempt,
  PaymentProvider,
  PaymentProviderResult,
} from "@/core/contracts";
import {
  LinklyCloudBackendApi,
  LinklyCloudBackendProvider,
  type LinklyTerminalSelectionPort,
} from "@/features/payments/linkly/linkly-cloud-backend";
import {
  PaymentAttemptStateError,
  type PaymentProviderRegistryPort,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import {
  SquarePaymentAdapter,
  type SquareTerminalConfiguration,
} from "@/features/payments/square/square-payment-adapter";
import {
  VoucherHbposApi,
  VoucherPaymentAdapter,
  type VoucherPaymentContextProvider,
  type VoucherProtectedTokenPort,
} from "@/features/payments/voucher/voucher-payment-adapter";

export type PaymentRuntimeEnvironment = "Sandbox" | "Production";

export type SquareRuntimeConfiguration = Readonly<{
  environment: PaymentRuntimeEnvironment;
  deviceId: string;
  locationId: string;
}>;

export interface SquareRuntimeConfigurationPort {
  /**
   * 这里只能返回公开终端选择。Square access token 永远保留在 Hbpos.Api。
   */
  load(): Promise<SquareRuntimeConfiguration | null>;
}

export type LinklyRuntimeConfiguration = Readonly<{
  environment: PaymentRuntimeEnvironment;
}>;

export interface LinklyRuntimeConfigurationPort {
  /**
   * 手持 POS 只选择环境；Linkly secret、POS ID 与 vendor credential 不得下发。
   */
  load(): Promise<LinklyRuntimeConfiguration | null>;
}

export type VoucherRuntimeConfiguration = Readonly<{
  enabled: boolean;
}>;

export interface VoucherRuntimeConfigurationPort {
  load(): Promise<VoucherRuntimeConfiguration>;
}

export type PaymentProviderConfigurationBlocker =
  | "SQUARE_CONFIGURATION_MISSING"
  | "SQUARE_CONFIGURATION_INVALID"
  | "SQUARE_CONFIGURATION_LOAD_FAILED"
  | "LINKLY_CONFIGURATION_MISSING"
  | "LINKLY_CONFIGURATION_INVALID"
  | "LINKLY_CONFIGURATION_LOAD_FAILED"
  | "VOUCHER_CONFIGURATION_DISABLED"
  | "VOUCHER_CONFIGURATION_LOAD_FAILED"
  | "MANUAL_CARD_CONFIGURATION_DISABLED"
  | "MANUAL_CARD_CONFIGURATION_LOAD_FAILED"
  | "PAYMENT_PROVIDER_UNKNOWN";

export type PaymentProviderAvailability = Readonly<{
  provider: PaymentProvider;
  available: boolean;
  blocker: PaymentProviderConfigurationBlocker | null;
}>;

export interface PaymentProviderAvailabilityPort {
  getAvailability(provider: PaymentProvider): PaymentProviderAvailability;
  listAvailability(): readonly PaymentProviderAvailability[];
}

/**
 * 仅供可信运行时组合根使用。调用方只能传入脱敏 PaymentAttempt，
 * capability 不公开 adapter、券码、reservation token 或 provider reference。
 */
export type VoucherApprovedPurchaseReleasePort =
  | Readonly<{
      status: "available";
      release(attempt: PaymentAttempt): Promise<PaymentProviderResult>;
    }>
  | Readonly<{
      status: "unavailable";
      reason: Extract<
        PaymentProviderConfigurationBlocker,
        | "VOUCHER_CONFIGURATION_DISABLED"
        | "VOUCHER_CONFIGURATION_LOAD_FAILED"
        | "PAYMENT_PROVIDER_UNKNOWN"
      >;
    }>;

export class PaymentProviderUnavailableError extends PaymentAttemptStateError {
  public constructor(
    public readonly providerName: string,
    public readonly code: PaymentProviderConfigurationBlocker,
  ) {
    super(`Payment provider ${providerName || "<empty>"} is unavailable (${code}).`);
    this.name = "PaymentProviderUnavailableError";
  }
}

type ConfiguredProviderEntry = Readonly<{
  port: OnlinePaymentPort | null;
  availability: PaymentProviderAvailability;
}>;

/**
 * Registry 的声明面与执行面使用同一张冻结表：只有配置验证成功的 provider
 * 才能被列出或取得 adapter，避免 UI 显示可用而执行时回退到未知实现。
 */
export class ConfiguredPaymentProviderRegistry
  implements PaymentProviderRegistryPort, PaymentProviderAvailabilityPort
{
  private readonly entries: ReadonlyMap<PaymentProvider, ConfiguredProviderEntry>;

  public constructor(entries: ReadonlyMap<PaymentProvider, ConfiguredProviderEntry>) {
    this.entries = new Map(entries);
  }

  public get(provider: PaymentProvider): OnlinePaymentPort {
    const entry = this.entries.get(provider);
    if (!entry?.port) {
      throw new PaymentProviderUnavailableError(
        typeof provider === "string" ? provider : "",
        entry?.availability.blocker ?? "PAYMENT_PROVIDER_UNKNOWN",
      );
    }
    if (entry.port.provider !== provider) {
      throw new PaymentProviderUnavailableError(
        provider,
        "PAYMENT_PROVIDER_UNKNOWN",
      );
    }
    return entry.port;
  }

  public getAvailability(provider: PaymentProvider): PaymentProviderAvailability {
    return (
      this.entries.get(provider)?.availability ?? {
        provider,
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN",
      }
    );
  }

  public listAvailability(): readonly PaymentProviderAvailability[] {
    return PAYMENT_PROVIDERS.map((provider) =>
      this.getAvailability(provider),
    );
  }

  public listAvailableProviders(): readonly PaymentProvider[] {
    return this.listAvailability()
      .filter((entry) => entry.available)
      .map((entry) => entry.provider);
  }

  public getVoucherApprovedPurchaseReleasePort(): VoucherApprovedPurchaseReleasePort {
    const entry = this.entries.get("voucher");
    if (
      !entry?.port ||
      entry.availability.available !== true ||
      !(entry.port instanceof VoucherPaymentAdapter)
    ) {
      const blocker = entry?.availability.blocker;
      return {
        status: "unavailable",
        reason:
          blocker === "VOUCHER_CONFIGURATION_DISABLED" ||
          blocker === "VOUCHER_CONFIGURATION_LOAD_FAILED"
            ? blocker
            : "PAYMENT_PROVIDER_UNKNOWN",
      };
    }

    const adapter = entry.port;
    return {
      status: "available",
      release: (attempt) => {
        const rejection = validateVoucherApprovedPurchaseRelease(attempt);
        return rejection
          ? Promise.resolve(rejection)
          : adapter.releaseReservation(attempt);
      },
    };
  }
}

export type PaymentProviderRegistryDependencies = Readonly<{
  manualCardConfiguration?: { load(): Promise<{ enabled: boolean }> };
  transport: HbposTransport;
  squareConfiguration: SquareRuntimeConfigurationPort;
  linklyConfiguration: LinklyRuntimeConfigurationPort;
  voucherConfiguration: VoucherRuntimeConfigurationPort;
  voucherProtectedTokens: VoucherProtectedTokenPort;
  voucherContextProvider: VoucherPaymentContextProvider;
  linklyTerminalSelection?: LinklyTerminalSelectionPort;
}>;

export async function createConfiguredPaymentProviderRegistry(
  dependencies: PaymentProviderRegistryDependencies,
): Promise<ConfiguredPaymentProviderRegistry> {
  const [square, linkly, voucher] = await Promise.all([
    safelyLoad(() => dependencies.squareConfiguration.load()),
    safelyLoad(() => dependencies.linklyConfiguration.load()),
    safelyLoad(() => dependencies.voucherConfiguration.load()),
  ]);
  const entries = new Map<PaymentProvider, ConfiguredProviderEntry>();

  const squareConfiguration = normalizeSquareConfiguration(square);
  if (squareConfiguration.kind === "configured") {
    const frozen = Object.freeze(squareConfiguration.value);
    entries.set(
      "square",
      configured(
        "square",
        new SquarePaymentAdapter(
          dependencies.transport,
          async (): Promise<SquareTerminalConfiguration> => frozen,
          frozen.environment,
        ),
      ),
    );
  } else {
    entries.set("square", blocked("square", squareConfiguration.blocker));
  }

  const linklyConfiguration = normalizeLinklyConfiguration(linkly);
  if (linklyConfiguration.kind === "configured") {
    const linklyApi = new LinklyCloudBackendApi(dependencies.transport);
    entries.set(
      "linkly-cloud",
      configured(
        "linkly-cloud",
        new LinklyCloudBackendProvider(
          linklyApi,
          Object.freeze({
            ...linklyConfiguration.value,
            terminalSelection:
              dependencies.linklyTerminalSelection ?? linklyApi,
          }),
        ),
      ),
    );
  } else {
    entries.set(
      "linkly-cloud",
      blocked("linkly-cloud", linklyConfiguration.blocker),
    );
  }

  const voucherConfiguration = normalizeVoucherConfiguration(voucher);
  if (voucherConfiguration.kind === "configured") {
    entries.set(
      "voucher",
      configured(
        "voucher",
        new VoucherPaymentAdapter(
          new VoucherHbposApi(dependencies.transport),
          dependencies.voucherProtectedTokens,
          dependencies.voucherContextProvider,
        ),
      ),
    );
  } else {
    entries.set("voucher", blocked("voucher", voucherConfiguration.blocker));
  }

  const manual = await safelyLoad(
    () => dependencies.manualCardConfiguration?.load() ?? Promise.resolve({ enabled: false }),
  );
  // 关闭入口仍保留已确认交易的恢复能力，新付款由 availability 门禁保护。
  entries.set("manual-card", {
    port: new ManualCardPaymentAdapter(),
    availability: {
      provider: "manual-card",
      available: manual.kind === "loaded" && manual.value.enabled === true,
      blocker:
        manual.kind === "failed"
          ? "MANUAL_CARD_CONFIGURATION_LOAD_FAILED"
          : manual.value.enabled === true
            ? null
            : "MANUAL_CARD_CONFIGURATION_DISABLED",
    },
  });

  return new ConfiguredPaymentProviderRegistry(entries);
}

type Loaded<T> =
  | Readonly<{ kind: "loaded"; value: T }>
  | Readonly<{ kind: "failed" }>;

async function safelyLoad<T>(load: () => Promise<T>): Promise<Loaded<T>> {
  try {
    return { kind: "loaded", value: await load() };
  } catch {
    return { kind: "failed" };
  }
}

type Normalized<T, B extends PaymentProviderConfigurationBlocker> =
  | Readonly<{ kind: "configured"; value: T }>
  | Readonly<{ kind: "blocked"; blocker: B }>;

function normalizeSquareConfiguration(
  loaded: Loaded<SquareRuntimeConfiguration | null>,
): Normalized<
  SquareRuntimeConfiguration,
  Extract<
    PaymentProviderConfigurationBlocker,
    | "SQUARE_CONFIGURATION_MISSING"
    | "SQUARE_CONFIGURATION_INVALID"
    | "SQUARE_CONFIGURATION_LOAD_FAILED"
  >
> {
  if (loaded.kind === "failed") {
    return { kind: "blocked", blocker: "SQUARE_CONFIGURATION_LOAD_FAILED" };
  }
  if (loaded.value === null) {
    return { kind: "blocked", blocker: "SQUARE_CONFIGURATION_MISSING" };
  }
  const environment = normalizeEnvironment(loaded.value.environment);
  const deviceId = normalizedText(loaded.value.deviceId);
  const locationId = normalizedText(loaded.value.locationId);
  if (!environment || !deviceId || !locationId) {
    return { kind: "blocked", blocker: "SQUARE_CONFIGURATION_INVALID" };
  }
  return {
    kind: "configured",
    value: { environment, deviceId, locationId },
  };
}

function normalizeLinklyConfiguration(
  loaded: Loaded<LinklyRuntimeConfiguration | null>,
): Normalized<
  LinklyRuntimeConfiguration,
  Extract<
    PaymentProviderConfigurationBlocker,
    | "LINKLY_CONFIGURATION_MISSING"
    | "LINKLY_CONFIGURATION_INVALID"
    | "LINKLY_CONFIGURATION_LOAD_FAILED"
  >
> {
  if (loaded.kind === "failed") {
    return { kind: "blocked", blocker: "LINKLY_CONFIGURATION_LOAD_FAILED" };
  }
  if (loaded.value === null) {
    return { kind: "blocked", blocker: "LINKLY_CONFIGURATION_MISSING" };
  }
  const environment = normalizeEnvironment(loaded.value.environment);
  return environment
    ? { kind: "configured", value: { environment } }
    : { kind: "blocked", blocker: "LINKLY_CONFIGURATION_INVALID" };
}

function normalizeVoucherConfiguration(
  loaded: Loaded<VoucherRuntimeConfiguration>,
): Normalized<
  VoucherRuntimeConfiguration,
  Extract<
    PaymentProviderConfigurationBlocker,
    "VOUCHER_CONFIGURATION_DISABLED" | "VOUCHER_CONFIGURATION_LOAD_FAILED"
  >
> {
  if (loaded.kind === "failed") {
    return { kind: "blocked", blocker: "VOUCHER_CONFIGURATION_LOAD_FAILED" };
  }
  return loaded.value.enabled === true
    ? { kind: "configured", value: { enabled: true } }
    : { kind: "blocked", blocker: "VOUCHER_CONFIGURATION_DISABLED" };
}

function configured(
  provider: PaymentProvider,
  port: OnlinePaymentPort,
): ConfiguredProviderEntry {
  return {
    port,
    availability: { provider, available: true, blocker: null },
  };
}

function blocked(
  provider: PaymentProvider,
  blocker: PaymentProviderConfigurationBlocker,
): ConfiguredProviderEntry {
  return {
    port: null,
    availability: { provider, available: false, blocker },
  };
}

function normalizeEnvironment(value: string): PaymentRuntimeEnvironment | null {
  const normalized = normalizedText(value)?.toLowerCase();
  if (normalized === "sandbox") return "Sandbox";
  if (normalized === "production") return "Production";
  return null;
}

function normalizedText(value: string): string | null {
  const normalized = value.trim();
  return normalized || null;
}

function validateVoucherApprovedPurchaseRelease(
  attempt: PaymentAttempt,
): PaymentProviderResult | null {
  let responseCode: string | null = null;
  if (attempt.provider !== "voucher") {
    responseCode = "VOUCHER_PROVIDER_MISMATCH";
  } else if (attempt.operation !== "purchase") {
    responseCode = "VOUCHER_PURCHASE_OPERATION_REQUIRED";
  } else if (attempt.state !== "Approved") {
    responseCode = "VOUCHER_APPROVED_ATTEMPT_REQUIRED";
  }
  return responseCode
    ? {
        state: "Unknown",
        references: attempt.references,
        receiptText: null,
        responseCode,
      }
    : null;
}

const PAYMENT_PROVIDERS = [
  "square",
  "linkly-cloud",
  "voucher",
  "manual-card",
] as const satisfies readonly PaymentProvider[];
