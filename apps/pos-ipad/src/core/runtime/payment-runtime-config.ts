import type {
  LinklyRuntimeConfigurationPort,
  SquareRuntimeConfigurationPort,
  VoucherRuntimeConfigurationPort,
} from "../../features/payments/runtime/payment-provider-registry";

export type PosPaymentPublicExtra = Readonly<{
  provider?: unknown;
  square?: Readonly<{
    environment?: unknown;
    deviceId?: unknown;
    locationId?: unknown;
  }>;
  linkly?: Readonly<{
    environment?: unknown;
  }>;
  voucher?: Readonly<{
    enabled?: unknown;
  }>;
}>;

export type ConfiguredCardProvider = "square" | "linkly-cloud" | "zeller";

export type PosPaymentConfigurationSources = Readonly<{
  square: SquareRuntimeConfigurationPort;
  linkly: LinklyRuntimeConfigurationPort;
  voucher: VoucherRuntimeConfigurationPort;
}>;

/**
 * Expo extra 仅允许公开的终端选择。这里刻意不接受 token、secret、merchant
 * credential 或 POS vendor credential；真正的支付凭据始终留在 Hbpos.Api。
 */
export function createPaymentConfigurationSources(
  extra: PosPaymentPublicExtra | null | undefined,
): PosPaymentConfigurationSources {
  const square = extra?.square;
  const linkly = extra?.linkly;
  const voucher = extra?.voucher;

  return {
    square: {
      async load() {
        if (!hasAnyValue(square)) return null;
        return {
          // Provider registry 会再次进行白名单校验并区分 missing/invalid。
          environment: text(square?.environment) as "Sandbox" | "Production",
          deviceId: text(square?.deviceId),
          locationId: text(square?.locationId),
        };
      },
    },
    linkly: {
      async load() {
        if (!hasAnyValue(linkly)) return null;
        return {
          environment: text(linkly?.environment) as "Sandbox" | "Production",
        };
      },
    },
    voucher: {
      async load() {
        return { enabled: voucher?.enabled === true };
      },
    },
  };
}

export function configuredLinklyEnvironment(
  extra: PosPaymentPublicExtra | null | undefined,
): "Sandbox" | "Production" | null {
  const environment = text(extra?.linkly?.environment);
  return environment === "Sandbox" || environment === "Production"
    ? environment
    : null;
}

export function configuredCardProvider(
  extra: PosPaymentPublicExtra | null | undefined,
): ConfiguredCardProvider | null {
  if (extra?.provider === "square") return "square";
  if (extra?.provider === "linkly") return "linkly-cloud";
  if (extra?.provider === "zeller") return "zeller";
  return null;
}

function hasAnyValue(value: object | undefined): boolean {
  if (!value) return false;
  return Object.values(value).some(
    (item) =>
      (typeof item === "string" && item.trim().length > 0) ||
      typeof item === "boolean",
  );
}

function text(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}
