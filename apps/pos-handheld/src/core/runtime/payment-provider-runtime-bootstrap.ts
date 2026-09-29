import type { PaymentAttemptService } from "../../features/payments";
import type { PaymentAcknowledgementRuntimePort } from "@hb/pos-payments-core/features/payments/payment-acknowledgement-service";
import {
  LinklyCloudBackendApi,
  LinklyPaymentTerminalSelectionCoordinator,
  type LinklyPaymentTerminalSelectionBindingPort,
  type LinklyTerminalSelectionPort,
} from "../../features/payments/linkly";
import type { PaymentProviderRegistryPort } from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import {
  LinklyOperatorRuntime,
  type LinklyOperatorRuntimeOptions,
} from "../../features/payments/runtime/linkly-operator-runtime";
import type {
  PaymentPermissionGuard,
  PaymentTrustedSessionGuard,
} from "../../features/payments/runtime/payment-checkout-runtime";
import {
  createConfiguredPaymentProviderRegistry,
  type ConfiguredPaymentProviderRegistry,
  type PaymentProviderAvailability,
  type PaymentProviderAvailabilityPort,
  type VoucherApprovedPurchaseReleasePort,
} from "../../features/payments/runtime/payment-provider-registry";
import type {
  VoucherPaymentContextProvider,
  VoucherProtectedTokenPort,
} from "../../features/payments/voucher";
import type { HbposTransport } from "../api";
import type {
  OnlinePaymentPort,
  PaymentProvider,
} from "../contracts";

import {
  DEFAULT_PAYMENT_METHOD_SETTINGS,
  type PaymentMethodSettings,
} from "@/features/settings/payment-method-settings";

import { DeferredVoucherContextProvider } from "./deferred-voucher-context-provider";
import {
  configuredCardProvider,
  configuredLinklyEnvironment,
  createPaymentConfigurationSources,
  type PosPaymentPublicExtra,
} from "./payment-runtime-config";

export interface RuntimePaymentProviderRegistry
  extends PaymentProviderRegistryPort,
    PaymentProviderAvailabilityPort {
  listAvailableProviders(): readonly PaymentProvider[];
  getVoucherApprovedPurchaseReleasePort(): VoucherApprovedPurchaseReleasePort;
}

export type PaymentProviderRuntimeBootstrap = Readonly<{
  providers: RuntimePaymentProviderRegistry;
  /** 设置页读取真实配置能力；新交易必须只读取上面的受选择过滤 registry。 */
  configurationAvailability: PaymentProviderAvailabilityPort;
  linklyTerminals?: Readonly<{
    environment: string;
    port: LinklyTerminalSelectionPort;
  }> | null;
  linklyPaymentSelection?: LinklyPaymentTerminalSelectionBindingPort | null;
  bindVoucherContextProvider(provider: VoucherPaymentContextProvider): void;
  createLinklyOperator(input: Readonly<{
    attempts: PaymentAttemptService;
    acknowledgements: PaymentAcknowledgementRuntimePort;
    trustedSession: PaymentTrustedSessionGuard;
    permissions: PaymentPermissionGuard;
  }>): LinklyOperatorRuntime | null;
}>;

export type PaymentProviderRuntimeBootstrapWithVoucherRelease =
  PaymentProviderRuntimeBootstrap &
    Readonly<{
      voucherApprovedPurchaseRelease: VoucherApprovedPurchaseReleasePort;
    }>;

/**
 * provider registry 可以在异步 Expo 启动阶段构造；Voucher 的可信收银员上下文
 * 在同步生产组合根内稍后一次性绑定，解决构造环且不公开任何 secret。
 */
export async function createPaymentProviderRuntimeBootstrap(input: Readonly<{
  transport: HbposTransport;
  extra: PosPaymentPublicExtra | null | undefined;
  voucherProtectedTokens: VoucherProtectedTokenPort;
  paymentMethods?: PaymentMethodSettings;
  readPaymentMethods?: () => PaymentMethodSettings;
  allowManualCard?: boolean;
}>): Promise<PaymentProviderRuntimeBootstrapWithVoucherRelease> {
  const sources = createPaymentConfigurationSources(input.extra);
  const methods = () => input.readPaymentMethods?.() ?? input.paymentMethods ?? DEFAULT_PAYMENT_METHOD_SETTINGS;
  const voucherContext = new DeferredVoucherContextProvider();
  const linklyEnvironment = configuredLinklyEnvironment(input.extra);
  const linklyApiCandidate = linklyEnvironment
    ? new LinklyCloudBackendApi(input.transport)
    : null;
  const linklySelectionCandidate = linklyApiCandidate
    ? new LinklyPaymentTerminalSelectionCoordinator(linklyApiCandidate)
    : null;
  const providers = await createConfiguredPaymentProviderRegistry({
    transport: input.transport,
    squareConfiguration: sources.square,
    linklyConfiguration: sources.linkly,
    voucherConfiguration: sources.voucher,
    manualCardConfiguration: { async load() { return { enabled: true }; } },
    voucherProtectedTokens: input.voucherProtectedTokens,
    voucherContextProvider: voucherContext.provide,
    ...(linklySelectionCandidate
      ? { linklyTerminalSelection: linklySelectionCandidate }
      : {}),
  });
  const linklyApi =
    linklyEnvironment && providers.getAvailability("linkly-cloud").available
      ? linklyApiCandidate
      : null;
  const linklyPaymentSelection = linklyApi
    ? linklySelectionCandidate
    : null;
  const runtimeProviders = new SelectedCardProviderRegistry(
    providers,
    configuredCardProvider(input.extra),
    methods,
    input.allowManualCard !== false,
  );

  return {
    providers: runtimeProviders,
    configurationAvailability: providers,
    linklyTerminals:
      linklyEnvironment && linklyPaymentSelection
        ? Object.freeze({
            environment: linklyEnvironment,
            port: linklyPaymentSelection,
          })
        : null,
    linklyPaymentSelection,
    voucherApprovedPurchaseRelease:
      providers.getVoucherApprovedPurchaseReleasePort(),
    bindVoucherContextProvider: (provider) => voucherContext.bind(provider),
    createLinklyOperator: ({
      attempts,
      acknowledgements,
      trustedSession,
      permissions,
    }) => {
      if (
        !linklyEnvironment ||
        !linklyApi
      ) {
        return null;
      }
      const options: LinklyOperatorRuntimeOptions = {
        attempts,
        acknowledgements,
        api: linklyApi,
        configuration: { environment: linklyEnvironment },
        trustedSession,
        permissions,
      };
      return new LinklyOperatorRuntime(options);
    },
  };
}

class SelectedCardProviderRegistry
  implements RuntimePaymentProviderRegistry
{
  public constructor(
    private readonly configured: ConfiguredPaymentProviderRegistry,
    private readonly selected: "square" | "linkly-cloud" | null,
    private readonly readMethods: () => PaymentMethodSettings,
    private readonly allowManualCard: boolean,
  ) {}

  /**
   * 旧 attempt 的恢复必须继续取得它原来绑定的 provider；只有 availability
   * 面负责限制新交易，切换设置不能把 Unknown attempt 变成不可恢复。
   */
  public get(provider: PaymentProvider): OnlinePaymentPort {
    return this.configured.get(provider);
  }

  public getAvailability(
    provider: PaymentProvider,
  ): PaymentProviderAvailability {
    const configured = this.configured.getAvailability(provider);
    if (!configured.available) return configured;
    const methods = this.readMethods();
    if (provider === "manual-card" && (!methods.useManualCard || !this.allowManualCard)) {
      return Object.freeze({
        provider,
        available: false,
        blocker: "MANUAL_CARD_CONFIGURATION_DISABLED",
      });
    }
    if (
      (provider === "square" || provider === "linkly-cloud") &&
      ((this.allowManualCard && methods.useManualCard) || provider !== this.selected)
    ) {
      return Object.freeze({
        provider,
        available: false,
        blocker: "PAYMENT_PROVIDER_UNKNOWN",
      });
    }
    return configured;
  }

  public listAvailability(): readonly PaymentProviderAvailability[] {
    return this.configured
      .listAvailability()
      .map(({ provider }) => this.getAvailability(provider));
  }

  public listAvailableProviders(): readonly PaymentProvider[] {
    return this.listAvailability()
      .filter((entry) => entry.available)
      .map((entry) => entry.provider);
  }

  public getVoucherApprovedPurchaseReleasePort(): VoucherApprovedPurchaseReleasePort {
    return this.configured.getVoucherApprovedPurchaseReleasePort();
  }
}
