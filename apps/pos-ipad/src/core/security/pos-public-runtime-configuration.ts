import { isTrustedLocalHbposApiOrigin } from "@hb/pos-domain/core/security/pos-api-addresses";
import type { SecureStorePort } from "./secure-storage";

const THIS_DEVICE_ONLY = Object.freeze({
  requireThisDeviceOnly: true,
});

export type PosPaymentEnvironment = "Sandbox" | "Production";
/** zeller：Zeller 刷卡机 SDK 直连。它没有需要保存的公开配置（配对信息由 Zeller SDK 自己存在钥匙串里），只保存「选中了它」。 */
export type PosPublicCardProvider = "square" | "linkly" | "zeller";

export type PosPublicPaymentConfiguration = Readonly<{
  provider?: PosPublicCardProvider;
  square?: Readonly<{
    environment: PosPaymentEnvironment;
    deviceId: string;
    locationId: string;
  }>;
  linkly?: Readonly<{
    environment: PosPaymentEnvironment;
  }>;
  voucher?: Readonly<{
    enabled: boolean;
  }>;
}>;

export type PosPublicPaymentConfigurationInput = Readonly<{
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

export type PosPublicRuntimeConfiguration = Readonly<{
  apiBaseUrl?: string;
  payments?: PosPublicPaymentConfiguration;
}>;

export type PosPublicPaymentSettingsInput =
  | Readonly<{
      provider: "square";
      square: NonNullable<PosPublicPaymentConfiguration["square"]>;
      linkly: null;
    }>
  | Readonly<{
      provider: "linkly";
      square: null;
      linkly: NonNullable<PosPublicPaymentConfiguration["linkly"]>;
    }>
  | Readonly<{
      provider: "zeller";
      square: null;
      linkly: null;
    }>;

type StoredConfigurationV1 = Readonly<{
  version: 1;
  apiBaseUrl?: string;
  payments?: PosPublicPaymentConfiguration;
}>;

/**
 * 运行前必须读取的公开选择放入本机 Keychain；这里只允许 API 地址、终端环境
 * 与公开设备选择，任何 token、secret、授权码或支付恢复引用都会被拒绝。
 */
export class PosPublicRuntimeConfigurationStore {
  public static readonly storageKey =
    "hbpos.ipad.public-runtime-configuration.v1";

  private readonly trustedApiOrigins: ReadonlySet<string>;

  public constructor(
    private readonly secureStore: SecureStorePort,
    trustedApiOrigins: readonly string[],
  ) {
    this.trustedApiOrigins = new Set(
      normalizeTrustedApiOrigins(trustedApiOrigins),
    );
  }

  public async load(): Promise<PosPublicRuntimeConfiguration> {
    const raw = await this.secureStore.get(
      PosPublicRuntimeConfigurationStore.storageKey,
    );
    if (!raw) return Object.freeze({});

    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      throw new Error("Stored public runtime configuration is invalid.");
    }
    return configurationFromStored(parsed, this.trustedApiOrigins);
  }

  public async save(
    configuration: PosPublicRuntimeConfiguration,
  ): Promise<void> {
    const normalized = normalizeConfiguration(
      configuration,
      this.trustedApiOrigins,
    );
    const stored: StoredConfigurationV1 = Object.freeze({
      version: 1,
      ...normalized,
    });
    await this.secureStore.set(
      PosPublicRuntimeConfigurationStore.storageKey,
      JSON.stringify(stored),
      THIS_DEVICE_ONLY,
    );
  }

  public async saveApiBaseUrl(apiBaseUrl: string): Promise<void> {
    const current = await this.load();
    await this.save({
      ...current,
      apiBaseUrl,
    });
  }

  public async savePayments(
    payments: PosPublicPaymentSettingsInput,
  ): Promise<void> {
    const current = await this.load();
    const nextPayments: PosPublicPaymentConfiguration = {
      provider: payments.provider,
      ...(payments.square ? { square: payments.square } : {}),
      ...(payments.linkly ? { linkly: payments.linkly } : {}),
      ...(current.payments?.voucher
        ? { voucher: current.payments.voucher }
        : {}),
    };
    await this.save({
      ...current,
      payments: nextPayments,
    });
  }
}

export function mergePosPaymentPublicConfiguration(
  defaults: PosPublicPaymentConfigurationInput | null | undefined,
  override: PosPublicPaymentConfiguration | null | undefined,
): PosPublicPaymentConfigurationInput {
  const provider = override?.provider ?? defaults?.provider;
  return Object.freeze({
    ...(provider !== undefined ? { provider } : {}),
    ...(defaults?.square ? { square: defaults.square } : {}),
    ...(defaults?.linkly ? { linkly: defaults.linkly } : {}),
    ...(defaults?.voucher ? { voucher: defaults.voucher } : {}),
    ...(override?.square ? { square: override.square } : {}),
    ...(override?.linkly ? { linkly: override.linkly } : {}),
    ...(override?.voucher ? { voucher: override.voucher } : {}),
  });
}

export function normalizePublicRuntimeApiBaseUrl(
  value: string,
  trustedOrigins: ReadonlySet<string>,
): string {
  const source = requiredText(value, "API base URL", 2_048);
  let parsed: URL;
  try {
    parsed = new URL(source);
  } catch {
    throw new Error("API base URL must be an absolute URL.");
  }
  if (parsed.protocol !== "https:" && parsed.protocol !== "http:") {
    throw new Error("API base URL must use HTTP or HTTPS.");
  }
  if (
    parsed.protocol === "http:" &&
    !isLoopback(parsed.hostname) &&
    !isTrustedLocalHbposApiOrigin(parsed.origin)
  ) {
    throw new Error("Remote API base URL requires HTTPS.");
  }
  if (
    parsed.username ||
    parsed.password ||
    parsed.search ||
    parsed.hash
  ) {
    throw new Error("API base URL contains unsupported data.");
  }
  const path = parsed.pathname.replace(/\/+$/u, "");
  if (!trustedOrigins.has(parsed.origin)) {
    throw new Error(
      "API base URL origin is not in the trusted build allowlist.",
    );
  }
  return `${parsed.origin}${path}`;
}

function configurationFromStored(
  input: unknown,
  trustedOrigins: ReadonlySet<string>,
): PosPublicRuntimeConfiguration {
  const record = strictRecord(
    input,
    ["version", "apiBaseUrl", "payments"],
    "public runtime configuration",
  );
  if (record.version !== 1) {
    throw new Error("Stored public runtime configuration version is invalid.");
  }
  return normalizeConfiguration(
    {
      ...(record.apiBaseUrl !== undefined
        ? {
            apiBaseUrl: requiredText(
              record.apiBaseUrl,
              "API base URL",
              2_048,
            ),
          }
        : {}),
      ...(record.payments !== undefined
        ? { payments: normalizePayments(record.payments) }
        : {}),
    },
    trustedOrigins,
  );
}

function normalizeConfiguration(
  input: PosPublicRuntimeConfiguration,
  trustedOrigins: ReadonlySet<string>,
): PosPublicRuntimeConfiguration {
  return Object.freeze({
    ...(input.apiBaseUrl
      ? {
          apiBaseUrl: normalizePublicRuntimeApiBaseUrl(
            input.apiBaseUrl,
            trustedOrigins,
          ),
        }
      : {}),
    ...(input.payments
      ? { payments: normalizePayments(input.payments) }
      : {}),
  });
}

export function normalizeTrustedApiOrigins(
  values: readonly string[],
): readonly string[] {
  const origins = new Set<string>();
  for (const value of values) {
    const source = requiredText(value, "Trusted API origin", 2_048);
    let parsed: URL;
    try {
      parsed = new URL(source);
    } catch {
      throw new Error("Trusted API origin must be an absolute URL.");
    }
    if (
      (parsed.protocol !== "https:" &&
        !(
          parsed.protocol === "http:" &&
          (isLoopback(parsed.hostname) ||
            isTrustedLocalHbposApiOrigin(parsed.origin))
        )) ||
      parsed.username ||
      parsed.password ||
      parsed.search ||
      parsed.hash
    ) {
      throw new Error("Trusted API origin is invalid.");
    }
    origins.add(parsed.origin);
  }
  if (origins.size === 0) {
    throw new Error("At least one trusted API origin is required.");
  }
  return Object.freeze([...origins]);
}

function normalizePayments(input: unknown): PosPublicPaymentConfiguration {
  const record = strictRecord(
    input,
    ["provider", "square", "linkly", "voucher"],
    "payment configuration",
  );
  const provider =
    record.provider === undefined
      ? null
      : cardProvider(record.provider);
  const square =
    record.square === undefined
      ? null
      : normalizeSquare(record.square);
  const linkly =
    record.linkly === undefined
      ? null
      : normalizeLinkly(record.linkly);
  if (
    (square || linkly) &&
    provider === null
  ) {
    throw new Error("Payment card provider is required.");
  }
  if (
    (provider === "square" && (!square || linkly)) ||
    (provider === "linkly" && (!linkly || square)) ||
    // 选中 Zeller 时不保存别家的配置：切回 Square / Linkly 时由设置页重新保存它们自己的那一份。
    (provider === "zeller" && (square || linkly))
  ) {
    throw new Error(
      "Only the explicitly selected payment provider may be stored.",
    );
  }
  return Object.freeze({
    ...(provider ? { provider } : {}),
    ...(square ? { square } : {}),
    ...(linkly ? { linkly } : {}),
    ...(record.voucher !== undefined
      ? { voucher: normalizeVoucher(record.voucher) }
      : {}),
  });
}

function cardProvider(value: unknown): PosPublicCardProvider {
  if (value !== "square" && value !== "linkly" && value !== "zeller") {
    throw new Error("Payment card provider is invalid.");
  }
  return value;
}

function normalizeSquare(
  input: unknown,
): NonNullable<PosPublicPaymentConfiguration["square"]> {
  const record = strictRecord(
    input,
    ["environment", "deviceId", "locationId"],
    "Square configuration",
  );
  return Object.freeze({
    environment: paymentEnvironment(record.environment),
    deviceId: requiredText(record.deviceId, "Square device id", 256),
    locationId: requiredText(
      record.locationId,
      "Square location id",
      256,
    ),
  });
}

function normalizeLinkly(
  input: unknown,
): NonNullable<PosPublicPaymentConfiguration["linkly"]> {
  const record = strictRecord(
    input,
    ["environment"],
    "Linkly configuration",
  );
  return Object.freeze({
    environment: paymentEnvironment(record.environment),
  });
}

function normalizeVoucher(
  input: unknown,
): NonNullable<PosPublicPaymentConfiguration["voucher"]> {
  const record = strictRecord(
    input,
    ["enabled"],
    "voucher configuration",
  );
  if (typeof record.enabled !== "boolean") {
    throw new Error("Voucher enabled flag is invalid.");
  }
  return Object.freeze({ enabled: record.enabled });
}

function strictRecord(
  input: unknown,
  allowedKeys: readonly string[],
  label: string,
): Readonly<Record<string, unknown>> {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    throw new Error(`${label} is invalid.`);
  }
  const record = input as Readonly<Record<string, unknown>>;
  for (const key of Object.keys(record)) {
    if (!allowedKeys.includes(key)) {
      throw new Error(`${label} contains an unsupported field.`);
    }
  }
  return record;
}

function paymentEnvironment(value: unknown): PosPaymentEnvironment {
  if (value !== "Sandbox" && value !== "Production") {
    throw new Error("Payment environment is invalid.");
  }
  return value;
}

function requiredText(
  value: unknown,
  label: string,
  maximumLength: number,
): string {
  if (typeof value !== "string") {
    throw new Error(`${label} is invalid.`);
  }
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > maximumLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new Error(`${label} is invalid.`);
  }
  return normalized;
}

function isLoopback(hostname: string): boolean {
  const normalized = hostname.toLowerCase().replace(/^\[|\]$/gu, "");
  return (
    normalized === "localhost" ||
    normalized === "127.0.0.1" ||
    normalized === "::1"
  );
}
