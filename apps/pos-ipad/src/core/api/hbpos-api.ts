import type { components } from "@hb/pos-api-client/openapi";
import {
  HbposApiError,
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@hb/pos-api-client/transport";

export { HbposApiError, unwrapHbposEnvelope };
export type {
  HbposApiErrorKind,
  HbposEnvelope,
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "@hb/pos-api-client/transport";

export const HBPOS_DEVICE_SYSTEM = "iPadOS" as const;

export type DeviceRegisterRequest = components["schemas"]["DeviceRegisterRequest"];
export type DeviceRegisterResponse = components["schemas"]["DeviceRegisterResponse"];
export type DeviceActivationPreviewResponse = Readonly<{
  isAllowed: boolean;
  reasonCode?: string | null;
  storeCode?: string | null;
  storeName?: string | null;
  deviceSystem?: string | null;
  expiresAtUtc?: string | null;
  message?: string | null;
}>;
export type DeviceActivationRedeemResponse = DeviceRegisterResponse &
  Readonly<{ reasonCode?: string | null }>;
export type DeviceActivationRedeemOptions = Readonly<{
  recoveryOnly?: boolean;
}>;
export type DeviceActivationRebindResponse = DeviceReregisterResponse &
  Readonly<{ reasonCode?: string | null }>;
export type DeviceVerifyRequest = components["schemas"]["DeviceVerifyRequest"];
export type DeviceVerifyResponse = components["schemas"]["DeviceVerifyResponse"];
export type DeviceReregisterRequest = components["schemas"]["DeviceReregisterRequest"];
export type DeviceReregisterResponse = components["schemas"]["DeviceReregisterResponse"];
export type DeviceRegistrationResetRequest = Readonly<{
  operationId: string;
}>;
export type DeviceRegistrationResetResponse = Readonly<{
  operationId: string;
  deviceCode: string;
  storeCode: string;
  disabledAtUtc: string;
}>;
export type CashierBarcodeLoginRequest = components["schemas"]["CashierBarcodeLoginRequest"];
export type CashierSessionDto = components["schemas"]["CashierSessionDto"];
type StoreDto = components["schemas"]["StoreDto"];

export type DeviceRegistrationStore = Readonly<{
  storeCode: string;
  storeName: string;
}>;

export class HbposDeviceApi {
  public constructor(
    private readonly transport: HbposTransport,
    private readonly anonymousTransport: HbposTransport = transport,
  ) {}

  public async listRegistrationStores(): Promise<
    readonly DeviceRegistrationStore[]
  > {
    const response = await this.anonymousTransport.request<
      HbposEnvelope<readonly StoreDto[]>
    >({
      method: "GET",
      url: "/api/v1/catalog/stores"
    });

    // 未注册设备只能使用匿名目录；客户端仍严格过滤不完整或非活动记录。
    return unwrapHbposEnvelope(response.data)
      .flatMap((store): DeviceRegistrationStore[] => {
        const storeCode = store.storeCode?.trim() ?? "";
        const storeName = store.storeName?.trim() ?? "";
        return store.isActive === true && storeCode && storeName
          ? [{ storeCode, storeName }]
          : [];
      })
      .sort(
        (left, right) =>
          left.storeName.localeCompare(right.storeName) ||
          left.storeCode.localeCompare(right.storeCode)
      );
  }

  public async register(
    input: Omit<DeviceRegisterRequest, "deviceSystem" | "provisioningCode">
  ): Promise<DeviceRegisterResponse> {
    const response = await this.anonymousTransport.request<HbposEnvelope<DeviceRegisterResponse>>({
      method: "POST",
      url: "/api/v1/devices/register",
      data: { ...input, deviceSystem: HBPOS_DEVICE_SYSTEM }
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async registerAppReview(
    input: Omit<DeviceRegisterRequest, "deviceSystem"> &
      Readonly<{ provisioningCode: string }>,
  ): Promise<DeviceRegisterResponse> {
    const response = await this.anonymousTransport.request<
      HbposEnvelope<DeviceRegisterResponse>
    >({
      method: "POST",
      url: "/api/v1/devices/app-review-register",
      data: { ...input, deviceSystem: HBPOS_DEVICE_SYSTEM },
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async previewActivationCode(input: Readonly<{
    activationCode: string;
  }>): Promise<DeviceActivationPreviewResponse> {
    const response = await this.anonymousTransport.request<
      HbposEnvelope<DeviceActivationPreviewResponse>
    >({
      method: "POST",
      url: "/api/v1/devices/activation-code/preview",
      data: { ...input, deviceSystem: HBPOS_DEVICE_SYSTEM },
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async redeemActivationCode(input: Readonly<{
    activationCode: string;
    hardwareId: string;
    terminalName?: string;
  }>, options: DeviceActivationRedeemOptions = {}): Promise<DeviceActivationRedeemResponse> {
    const response = await this.anonymousTransport.request<
      HbposEnvelope<DeviceActivationRedeemResponse>
    >({
      method: "POST",
      url: "/api/v1/devices/activation-code/redeem",
      ...(options.recoveryOnly === true
        ? { headers: { "X-HBPOS-Activation-Recovery-Only": "true" } }
        : {}),
      data: { ...input, deviceSystem: HBPOS_DEVICE_SYSTEM },
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async rebindActivationCode(input: Readonly<{
    activationCode: string;
    terminalName?: string;
  }>): Promise<DeviceActivationRebindResponse> {
    const response = await this.transport.request<
      HbposEnvelope<DeviceActivationRebindResponse>
    >({
      method: "POST",
      url: "/api/v1/devices/activation-code/rebind",
      // 当前设备身份、硬件和平台由认证 transport 与服务端 claims 决定。
      data: input,
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async verify(
    input: Omit<DeviceVerifyRequest, "deviceSystem">
  ): Promise<DeviceVerifyResponse> {
    const response = await this.transport.request<HbposEnvelope<DeviceVerifyResponse>>({
      method: "POST",
      url: "/api/v1/devices/verify",
      data: { ...input, deviceSystem: HBPOS_DEVICE_SYSTEM }
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async reregister(input: DeviceReregisterRequest): Promise<DeviceReregisterResponse> {
    const response = await this.transport.request<HbposEnvelope<DeviceReregisterResponse>>({
      method: "POST",
      url: "/api/v1/devices/reregister",
      data: input
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async resetRegistration(
    input: DeviceRegistrationResetRequest,
    freshCashierAuthorization: string,
  ): Promise<DeviceRegistrationResetResponse> {
    const token = freshCashierAuthorization.trim();
    if (!token) {
      throw new TypeError("Fresh cashier authorization is required.");
    }
    const response = await this.transport.request<
      HbposEnvelope<DeviceRegistrationResetResponse>
    >({
      method: "POST",
      url: "/api/v1/devices/reset-registration",
      data: input,
      headers: {
        "X-HBPOS-Cashier-Authorization": token,
      },
    });
    return unwrapHbposEnvelope(response.data);
  }
}

export class HbposCashierApi {
  public constructor(private readonly transport: HbposTransport) {}

  public async barcodeLogin(input: CashierBarcodeLoginRequest): Promise<CashierSessionDto> {
    const response = await this.transport.request<HbposEnvelope<CashierSessionDto>>({
      method: "POST",
      url: "/api/v1/cashiers/barcode-login",
      data: input,
      authenticationFailurePolicy: "suppress-unauthorized",
    });
    return unwrapHbposEnvelope(response.data);
  }
}


export type StoreReceiptProfile = Readonly<{
  storeCode: string;
  storeName: string;
  brandName: string;
  address: string;
  phone: string;
  abn: string;
  returnPolicy: string;
}>;

export class HbposStoreApi {
  public constructor(private readonly transport: HbposTransport) {}

  public async getCurrentReceiptProfile(
    signal: AbortSignal,
  ): Promise<StoreReceiptProfile | null> {
    const response = await this.transport.request<
      HbposEnvelope<components["schemas"]["StoreReceiptProfileDto"]>
    >({
      method: "GET",
      url: "/api/v1/stores/current/receipt-profile",
      signal,
    });
    const data = unwrapHbposEnvelope(response.data);
    if (!data) return null;
    return Object.freeze({
      storeCode: data.storeCode ?? "",
      storeName: data.storeName ?? "",
      brandName: data.brandName ?? "",
      address: data.address ?? "",
      phone: data.phone ?? "",
      abn: data.abn ?? "",
      returnPolicy: data.returnPolicy ?? "",
    });
  }

  /**
   * 轻量轮询总部下发的小票资料：本机已应用版本作为 knownVersion，服务端版本没变时只回
   * {changed:false}。门店与设备只取认证声明，这里不拼任何门店/设备参数。
   * 生成类型里的字段全是可选的，这里容错归一：changed 缺失按 false，version 缺失按 0。
   */
  public async syncReceiptProfile(
    knownVersion: number,
    signal?: AbortSignal,
  ): Promise<StoreReceiptProfileSyncResult> {
    const response = await this.transport.request<
      HbposEnvelope<components["schemas"]["StoreReceiptProfileSyncDto"]>
    >({
      method: "GET",
      url: "/api/v1/stores/current/receipt-profile/sync",
      params: { knownVersion: nonNegativeInteger(knownVersion) },
      ...(signal ? { signal } : {}),
    });
    const data = unwrapHbposEnvelope(response.data);
    const profile = data.profile;
    return Object.freeze({
      changed: data.changed === true,
      version: nonNegativeInteger(data.version),
      profile: profile
        ? Object.freeze({
            version: nonNegativeInteger(profile.version),
            storeCode: profile.storeCode ?? "",
            storeName: profile.storeName ?? "",
            brandName: profile.brandName ?? "",
            address: profile.address ?? "",
            phone: profile.phone ?? "",
            abn: profile.abn ?? "",
            returnPolicy: profile.returnPolicy ?? "",
          })
        : null,
    });
  }

  /** 回报「本机已应用到版本 N」；服务端对版本不认账会返回 400（RECEIPT_PROFILE_VERSION_INVALID）。 */
  public async ackReceiptProfile(
    version: number,
    signal?: AbortSignal,
  ): Promise<void> {
    const response = await this.transport.request<
      HbposEnvelope<components["schemas"]["StoreReceiptProfileAckResultDto"]>
    >({
      method: "POST",
      url: "/api/v1/stores/current/receipt-profile/ack",
      data: { version: nonNegativeInteger(version) },
      ...(signal ? { signal } : {}),
    });
    unwrapHbposEnvelope(response.data);
  }
}

/** 一次 sync 的容错结果；profile 带下发版本号（缺失按 0，由同步控制器判为无效响应）。 */
export type StoreReceiptProfileSyncResult = Readonly<{
  changed: boolean;
  version: number;
  profile:
    | (StoreReceiptProfile & Readonly<{ version: number }>)
    | null;
}>;

function nonNegativeInteger(value: unknown): number {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0
    ? value
    : 0;
}
