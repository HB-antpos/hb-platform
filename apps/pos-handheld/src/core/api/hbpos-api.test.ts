import assert from "node:assert/strict";
import test from "node:test";

import {
  HbposApiError,
  HbposDeviceApi,
  HbposStoreApi,
  resolveHbposDeviceSystem,
  unwrapHbposEnvelope,
  type HbposTransport,
  type HbposTransportRequest
} from "./hbpos-api";

test("EXPO_OS 只解析 iOS 与 Android，未知平台 fail-closed", () => {
  assert.equal(resolveHbposDeviceSystem("ios"), "iOS");
  assert.equal(resolveHbposDeviceSystem("android"), "Android");
  assert.throws(
    () => resolveHbposDeviceSystem("web"),
    /unsupported handheld platform/i,
  );
  assert.throws(
    () => resolveHbposDeviceSystem(undefined),
    /unsupported handheld platform/i,
  );
});

test("设备注册与验证按构造时注入的平台发送严格 payload", async () => {
  const calls: { url: string; data?: unknown }[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push({ url: config.url, data: config.data });
      return {
        status: 200,
        data: {
          success: true,
          data: {
            deviceCode: "POS_1003_1011",
            storeCode: "1003",
            storeName: "Chermside",
            deviceStatus: -1,
            isAllowed: false
          }
        } as T
      };
    }
  };

  for (const deviceSystem of ["iOS", "Android"] as const) {
    const api = new HbposDeviceApi(transport, deviceSystem);
    const response = await api.register({
      storeCode: "1003",
      hardwareId: `INSTALL-${deviceSystem}`
    });
    await api.verify({
      deviceCode: "POS_1003_1011",
      storeCode: "1003",
      hardwareId: `INSTALL-${deviceSystem}`,
    });

    assert.equal(response.deviceCode, "POS_1003_1011");
  }
  assert.deepEqual(calls, [
    {
      url: "/api/v1/devices/register",
      data: {
        storeCode: "1003",
        hardwareId: "INSTALL-iOS",
        deviceSystem: "iOS"
      }
    },
    {
      url: "/api/v1/devices/verify",
      data: {
        deviceCode: "POS_1003_1011",
        storeCode: "1003",
        hardwareId: "INSTALL-iOS",
        deviceSystem: "iOS"
      }
    },
    {
      url: "/api/v1/devices/register",
      data: {
        storeCode: "1003",
        hardwareId: "INSTALL-Android",
        deviceSystem: "Android"
      }
    },
    {
      url: "/api/v1/devices/verify",
      data: {
        deviceCode: "POS_1003_1011",
        storeCode: "1003",
        hardwareId: "INSTALL-Android",
        deviceSystem: "Android"
      }
    },
  ]);
});

test("注册分店列表使用匿名 catalog 路径，并只返回有效活动分店", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return {
        status: 200,
        data: {
          success: true,
          data: [
            { storeCode: " 1003 ", storeName: " Chermside ", isActive: true },
            { storeCode: "1002", storeName: "Aspley", isActive: true },
            { storeCode: "1001", storeName: "Disabled", isActive: false },
            { storeCode: null, storeName: "Invalid", isActive: true }
          ]
        } as T
      };
    }
  };

  const stores = await new HbposDeviceApi(
    transport,
    "iOS",
  ).listRegistrationStores();

  assert.deepEqual(calls, [{
    method: "GET",
    url: "/api/v1/catalog/stores"
  }]);
  assert.deepEqual(stores, [
    { storeCode: "1002", storeName: "Aspley" },
    { storeCode: "1003", storeName: "Chermside" }
  ]);
});

test("设备开通码预览、兑换和换店使用精确合同且不提交客户端分店", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return {
        status: 200,
        data: {
          success: true,
          data: config.url.endsWith("/preview")
            ? {
                isAllowed: true,
                storeCode: "1042",
                storeName: "Sunnybank",
                deviceSystem: "Android",
                expiresAtUtc: "2026-08-27T12:00:00.000Z",
              }
            : {
                isAllowed: true,
                deviceCode: "ANDROID-1042-01",
                storeCode: "1042",
                storeName: "Sunnybank",
                deviceStatus: 1,
                authorizationCode: "device-secret",
              },
        } as T,
      };
    },
  };
  const activationCode =
    "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";

  for (const deviceSystem of ["iOS", "Android"] as const) {
    const api = new HbposDeviceApi(transport, deviceSystem);
    await api.previewActivationCode({ activationCode });
    await api.redeemActivationCode({
      activationCode,
      hardwareId: `INSTALL-${deviceSystem}`,
    });
    await api.rebindActivationCode({
      activationCode,
      terminalName: `${deviceSystem} Handheld`,
    });
  }

  assert.deepEqual(calls.map(({ url, data }) => ({ url, data })), [
    {
      url: "/api/v1/devices/activation-code/preview",
      data: { activationCode, deviceSystem: "iOS" },
    },
    {
      url: "/api/v1/devices/activation-code/redeem",
      data: {
        activationCode,
        hardwareId: "INSTALL-iOS",
        deviceSystem: "iOS",
      },
    },
    {
      url: "/api/v1/devices/activation-code/rebind",
      data: {
        activationCode,
        terminalName: "iOS Handheld",
      },
    },
    {
      url: "/api/v1/devices/activation-code/preview",
      data: { activationCode, deviceSystem: "Android" },
    },
    {
      url: "/api/v1/devices/activation-code/redeem",
      data: {
        activationCode,
        hardwareId: "INSTALL-Android",
        deviceSystem: "Android",
      },
    },
    {
      url: "/api/v1/devices/activation-code/rebind",
      data: {
        activationCode,
        terminalName: "Android Handheld",
      },
    },
  ]);
});

test("兑换与预览固定走匿名 transport，换店只走当前设备认证 transport", async () => {
  const authenticated: string[] = [];
  const anonymous: string[] = [];
  const response = {
    status: 200,
    data: { success: true, data: { isAllowed: false, deviceStatus: 1 } },
  } as const;
  const authenticatedTransport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      authenticated.push(config.url);
      return response as unknown as { status: number; data: T };
    },
  };
  const anonymousTransport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      anonymous.push(config.url);
      return response as unknown as { status: number; data: T };
    },
  };
  const api = new HbposDeviceApi(
    authenticatedTransport,
    "Android",
    anonymousTransport,
  );
  const activationCode =
    "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";

  await api.previewActivationCode({ activationCode });
  await api.redeemActivationCode({ activationCode, hardwareId: "INSTALL-001" });
  await api.rebindActivationCode({ activationCode });

  assert.deepEqual(anonymous, [
    "/api/v1/devices/activation-code/preview",
    "/api/v1/devices/activation-code/redeem",
  ]);
  assert.deepEqual(authenticated, [
    "/api/v1/devices/activation-code/rebind",
  ]);
});

test("重绑丢响应恢复沿用匿名兑换 body，但必须发送 recovery-only header", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return {
        status: 200,
        data: {
          success: true,
          data: { isAllowed: false, reasonCode: "ACTIVATION_CODE_NOT_AVAILABLE" },
        } as T,
      };
    },
  };
  const activationCode =
    "HBDEV1-0123456789ABCDEFGHJKMNPQRS-STVWXYZ0123456789ABCDEFGHJ";

  await new HbposDeviceApi(transport, "Android").redeemActivationCode(
    { activationCode, hardwareId: "INSTALL-ANDROID-001" },
    { recoveryOnly: true },
  );

  assert.deepEqual(calls, [{
    method: "POST",
    url: "/api/v1/devices/activation-code/redeem",
    headers: { "X-HBPOS-Activation-Recovery-Only": "true" },
    data: {
      activationCode,
      hardwareId: "INSTALL-ANDROID-001",
      deviceSystem: "Android",
    },
  }]);
});

test("设备注册重置只发送 operationId 并使用本次在线员工票据", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            operationId: "10000000-0000-4000-8000-000000000001",
            deviceCode: "HANDHELD-1042-01",
            storeCode: "1042",
            disabledAtUtc: "2026-08-18T02:00:00.000Z",
          },
        } as T,
      };
    },
  };

  const result = await new HbposDeviceApi(transport, "Android").resetRegistration(
    { operationId: "10000000-0000-4000-8000-000000000001" },
    "fresh-online-ticket",
  );

  assert.equal(result.storeCode, "1042");
  assert.deepEqual(calls, [
    {
      method: "POST",
      url: "/api/v1/devices/reset-registration",
      data: { operationId: "10000000-0000-4000-8000-000000000001" },
      headers: {
        "X-HBPOS-Cashier-Authorization": "fresh-online-ticket",
      },
    },
  ]);
});

test("当前门店小票资料使用认证 transport 的固定 GET 路径并完整保留空值", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "BNE-01",
            storeName: "Brisbane",
            brandName: "Hot Bargain",
            address: "",
            phone: "07 3000 0000",
            abn: "",
            returnPolicy: "Refunds within 14 days.",
            // 券使用说明有值、分期条款为 null：null 归一为空串（未定制）。
            voucherTerms: "Valid at all stores.\nNo cash refunds.",
            installmentTerms: null,
          },
        } as T,
      };
    },
  };
  const controller = new AbortController();

  const profile = await new HbposStoreApi(transport).getCurrentReceiptProfile(
    controller.signal,
  );

  assert.deepEqual(calls, [{
    method: "GET",
    url: "/api/v1/stores/current/receipt-profile",
    signal: controller.signal,
  }]);
  assert.deepEqual(profile, {
    storeCode: "BNE-01",
    storeName: "Brisbane",
    brandName: "Hot Bargain",
    address: "",
    phone: "07 3000 0000",
    abn: "",
    returnPolicy: "Refunds within 14 days.",
    voucherTerms: "Valid at all stores.\nNo cash refunds.",
    installmentTerms: "",
  });
});

test("下发资料轮询：固定 GET sync 路径只带 knownVersion，不拼门店/设备参数，并透传取消信号", async () => {
  const calls: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            changed: true,
            version: 4,
            profile: {
              storeCode: "BNE-01",
              storeName: "Brisbane",
              brandName: null,
              address: "1 Queen St",
              phone: null,
              abn: "12 345 678 901",
              returnPolicy: null,
              voucherTerms: null,
              installmentTerms: "Deposit $30 minimum.\r\nLater payments from $10.",
              version: 4,
              publishedAt: "2026-10-07T00:00:00Z",
            },
          },
        } as T,
      };
    },
  };
  const controller = new AbortController();

  const result = await new HbposStoreApi(transport).syncReceiptProfile(3, controller.signal);

  assert.deepEqual(calls, [{
    method: "GET",
    url: "/api/v1/stores/current/receipt-profile/sync",
    params: { knownVersion: 3 },
    signal: controller.signal,
  }]);
  assert.deepEqual(result, {
    changed: true,
    version: 4,
    profile: {
      version: 4,
      storeCode: "BNE-01",
      storeName: "Brisbane",
      brandName: "",
      address: "1 Queen St",
      phone: "",
      abn: "12 345 678 901",
      returnPolicy: "",
      voucherTerms: "",
      installmentTerms: "Deposit $30 minimum.\r\nLater payments from $10.",
    },
  });
});

test("下发资料轮询对生成类型里全部可选的字段容错：changed 缺失按 false、version 缺失按 0、profile 缺失按无资料", async () => {
  const respond = (data: unknown): HbposTransport => ({
    async request<T>() {
      return { status: 200, data: { success: true, data } as T };
    },
  });

  assert.deepEqual(await new HbposStoreApi(respond({})).syncReceiptProfile(0), {
    changed: false,
    version: 0,
    profile: null,
  });
  assert.deepEqual(
    await new HbposStoreApi(respond({ version: 9 })).syncReceiptProfile(9),
    { changed: false, version: 9, profile: null },
  );
  // 服务端若漏掉 profile.version，归一为 0，由同步控制器判为无效响应
  const missingVersion = await new HbposStoreApi(
    respond({ changed: true, version: 2, profile: { storeCode: "S1", storeName: "Shop" } }),
  ).syncReceiptProfile(0);
  assert.equal(missingVersion.changed, true);
  assert.equal(missingVersion.profile?.version, 0);
  // 旧服务端不返回两个条款字段：归一为空串（未定制），照常使用默认文案
  assert.equal(missingVersion.profile?.voucherTerms, "");
  assert.equal(missingVersion.profile?.installmentTerms, "");
  // 非布尔 changed 不当作 true
  assert.equal(
    (await new HbposStoreApi(respond({ changed: "true", version: 1 })).syncReceiptProfile(0)).changed,
    false,
  );
});

test("下发资料轮询的 knownVersion 脏值（负数/小数/非数字）一律按 0 发送", async () => {
  const sent: unknown[] = [];
  const transport: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      sent.push(config.params);
      return { status: 200, data: { success: true, data: { changed: false, version: 0 } } as T };
    },
  };
  const api = new HbposStoreApi(transport);
  for (const dirty of [-1, 1.5, Number.NaN, Number.POSITIVE_INFINITY, 2 ** 60]) {
    await api.syncReceiptProfile(dirty);
  }
  assert.deepEqual(sent, Array.from({ length: 5 }, () => ({ knownVersion: 0 })));
});

test("下发资料回执：POST ack 只发送 version；HTTP 错误与业务 envelope 失败都不被吞掉", async () => {
  const calls: HbposTransportRequest[] = [];
  const ok: HbposTransport = {
    async request<T>(config: HbposTransportRequest) {
      calls.push(config);
      return { status: 200, data: { success: true, data: { appliedVersion: 5 } } as T };
    },
  };
  const controller = new AbortController();
  await new HbposStoreApi(ok).ackReceiptProfile(5, controller.signal);
  assert.deepEqual(calls, [{
    method: "POST",
    url: "/api/v1/stores/current/receipt-profile/ack",
    data: { version: 5 },
    signal: controller.signal,
  }]);

  const rejected: HbposTransport = {
    async request() {
      throw new HbposApiError("version invalid", {
        kind: "http",
        status: 400,
        code: "RECEIPT_PROFILE_VERSION_INVALID",
      });
    },
  };
  await assert.rejects(
    () => new HbposStoreApi(rejected).ackReceiptProfile(9),
    (error: unknown) =>
      error instanceof HbposApiError &&
      error.kind === "http" &&
      error.status === 400 &&
      error.code === "RECEIPT_PROFILE_VERSION_INVALID",
  );

  const envelopeFailure: HbposTransport = {
    async request<T>() {
      return {
        status: 200,
        data: { success: false, errorCode: "RECEIPT_PROFILE_VERSION_INVALID", message: "bad" } as T,
      };
    },
  };
  await assert.rejects(
    () => new HbposStoreApi(envelopeFailure).ackReceiptProfile(9),
    (error: unknown) => error instanceof HbposApiError && error.kind === "envelope",
  );
});

test("业务 envelope 失败以非传输错误抛出，不能被离线回退吞掉", () => {
  assert.throws(
    () => unwrapHbposEnvelope({ success: false, errorCode: "CASHIER_LOGIN_FAILED", message: "denied" }),
    (error: unknown) => error instanceof HbposApiError
      && error.kind === "envelope"
      && error.code === "CASHIER_LOGIN_FAILED"
  );
});
