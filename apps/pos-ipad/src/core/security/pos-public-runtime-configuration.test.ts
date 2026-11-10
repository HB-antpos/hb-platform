import assert from "node:assert/strict";
import test from "node:test";

import {
  PosPublicRuntimeConfigurationStore,
  mergePosPaymentPublicConfiguration,
} from "./pos-public-runtime-configuration";
import { InMemorySecureStore } from "./secure-storage";

test("公开运行配置只保存 API 地址与支付终端白名单字段", async () => {
  const secureStore = new InMemorySecureStore();
  const subject = new PosPublicRuntimeConfigurationStore(
    secureStore,
    ["https://pos.example.test"],
  );

  await subject.save({
    apiBaseUrl: "https://pos.example.test/api",
    payments: {
      provider: "square",
      square: {
        environment: "Sandbox",
        deviceId: "SQ-DEVICE",
        locationId: "SQ-LOCATION",
      },
    },
  });

  assert.deepEqual(await subject.load(), {
    apiBaseUrl: "https://pos.example.test/api",
    payments: {
      provider: "square",
      square: {
        environment: "Sandbox",
        deviceId: "SQ-DEVICE",
        locationId: "SQ-LOCATION",
      },
    },
  });
  assert.equal(secureStore.lastWriteOptions?.requireThisDeviceOnly, true);
  assert.equal(
    JSON.stringify(await subject.load()).includes("token"),
    false,
  );
});

test("局部保存不会覆盖另一组公开配置", async () => {
  const subject = new PosPublicRuntimeConfigurationStore(
    new InMemorySecureStore(),
    ["https://one.example.test"],
  );
  await subject.saveApiBaseUrl("https://one.example.test/pos");
  await subject.savePayments({
    provider: "linkly",
    square: null,
    linkly: { environment: "Sandbox" },
  });

  assert.deepEqual(await subject.load(), {
    apiBaseUrl: "https://one.example.test/pos",
    payments: {
      provider: "linkly",
      linkly: { environment: "Sandbox" },
    },
  });
});

test("已确认的 API 地址会被下一次启动创建的新配置仓库读取", async () => {
  const secureStore = new InMemorySecureStore();
  const trustedOrigins = ["http://192.168.31.246:5003"];
  const currentLaunch = new PosPublicRuntimeConfigurationStore(
    secureStore,
    trustedOrigins,
  );
  await currentLaunch.saveApiBaseUrl("http://192.168.31.246:5003");

  const nextLaunch = new PosPublicRuntimeConfigurationStore(
    secureStore,
    trustedOrigins,
  );
  assert.equal(
    (await nextLaunch.load()).apiBaseUrl,
    "http://192.168.31.246:5003",
  );
});

test("构建白名单在迁移期间同时接受新旧本地后端端口", async () => {
  const subject = new PosPublicRuntimeConfigurationStore(
    new InMemorySecureStore(),
    [
      "http://192.168.31.246:5003",
      "http://192.168.31.246:5159",
    ],
  );

  await subject.saveApiBaseUrl("http://192.168.31.246:5159");
  assert.equal(
    (await subject.load()).apiBaseUrl,
    "http://192.168.31.246:5159",
  );
});

test("损坏、越权字段或不安全远程 HTTP 配置失败关闭", async () => {
  const secureStore = new InMemorySecureStore();
  const subject = new PosPublicRuntimeConfigurationStore(
    secureStore,
    ["https://trusted.example.test"],
  );
  await secureStore.set(
    PosPublicRuntimeConfigurationStore.storageKey,
    JSON.stringify({
      version: 1,
      apiBaseUrl: "http://remote.example.test/pos",
      token: "must-not-be-accepted",
    }),
    { requireThisDeviceOnly: true },
  );

  await assert.rejects(() => subject.load(), /unsupported|HTTPS/i);
});

test("持久化 provider 覆盖默认选择，同时保留另一终端配置用于旧 attempt 恢复", () => {
  assert.deepEqual(
    mergePosPaymentPublicConfiguration(
      {
        provider: "square",
        square: {
          environment: "Production",
          deviceId: "DEFAULT-DEVICE",
          locationId: "DEFAULT-LOCATION",
        },
        linkly: { environment: "Production" },
        voucher: { enabled: true },
      },
      {
        provider: "square",
        square: {
          environment: "Sandbox",
          deviceId: "STORE-DEVICE",
          locationId: "STORE-LOCATION",
        },
      },
    ),
    {
      provider: "square",
      square: {
        environment: "Sandbox",
        deviceId: "STORE-DEVICE",
        locationId: "STORE-LOCATION",
      },
      linkly: { environment: "Production" },
      voucher: { enabled: true },
    },
  );
});

test("支付配置缺少显式 provider 或试图同时持久化两种卡终端时失败关闭", async () => {
  const secureStore = new InMemorySecureStore();
  const subject = new PosPublicRuntimeConfigurationStore(
    secureStore,
    ["https://pos.example.test"],
  );

  await assert.rejects(
    () =>
      subject.save({
        payments: {
          square: {
            environment: "Sandbox",
            deviceId: "SQ-DEVICE",
            locationId: "SQ-LOCATION",
          },
        } as never,
      }),
    /provider/i,
  );
  await assert.rejects(
    () =>
      subject.save({
        payments: {
          provider: "square",
          square: {
            environment: "Sandbox",
            deviceId: "SQ-DEVICE",
            locationId: "SQ-LOCATION",
          },
          linkly: { environment: "Production" },
        } as never,
      }),
    /provider|selected/i,
  );
});

test("选中 Zeller 只保存通道本身；切回 Linkly 时 Zeller 的选择被替换，购物券开关保留", async () => {
  const secureStore = new InMemorySecureStore();
  const subject = new PosPublicRuntimeConfigurationStore(
    secureStore,
    ["https://pos.example.test"],
  );
  await subject.save({ payments: { voucher: { enabled: true } } });

  await subject.savePayments({ provider: "zeller", square: null, linkly: null });
  assert.deepEqual((await subject.load()).payments, {
    provider: "zeller",
    voucher: { enabled: true },
  });
  // 下一次启动创建的新仓库读到同样的选择。
  const nextLaunch = new PosPublicRuntimeConfigurationStore(secureStore, ["https://pos.example.test"]);
  assert.equal((await nextLaunch.load()).payments?.provider, "zeller");
  // 运行时合并：本机保存的 zeller 覆盖构建默认的通道。
  assert.equal(
    mergePosPaymentPublicConfiguration(
      { provider: "linkly", linkly: { environment: "Production" } },
      (await subject.load()).payments,
    ).provider,
    "zeller",
  );

  await subject.savePayments({
    provider: "linkly",
    square: null,
    linkly: { environment: "Production" },
  });
  assert.deepEqual((await subject.load()).payments, {
    provider: "linkly",
    linkly: { environment: "Production" },
    voucher: { enabled: true },
  });
});

test("选中 Zeller 时不能同时保存 Square 或 Linkly 的配置；不认识的通道失败关闭", async () => {
  const subject = new PosPublicRuntimeConfigurationStore(
    new InMemorySecureStore(),
    ["https://pos.example.test"],
  );
  await assert.rejects(
    () =>
      subject.save({
        payments: { provider: "zeller", linkly: { environment: "Production" } } as never,
      }),
    /selected/i,
  );
  await assert.rejects(
    () =>
      subject.save({
        payments: {
          provider: "zeller",
          square: { environment: "Sandbox", deviceId: "SQ-DEVICE", locationId: "SQ-LOCATION" },
        } as never,
      }),
    /selected/i,
  );
  await assert.rejects(
    () => subject.save({ payments: { provider: "stripe" } as never }),
    /provider/i,
  );
});

test("API 地址只能保存和加载构建签名内声明的可信 origin", async () => {
  const secureStore = new InMemorySecureStore();
  const subject = new PosPublicRuntimeConfigurationStore(
    secureStore,
    [
      "https://primary.example.test",
      "https://backup.example.test",
    ],
  );

  await subject.saveApiBaseUrl(
    "https://backup.example.test/hbpos",
  );
  assert.equal(
    (await subject.load()).apiBaseUrl,
    "https://backup.example.test/hbpos",
  );

  await assert.rejects(
    () => subject.saveApiBaseUrl("https://attacker.example/health"),
    /trusted|allowlist|origin/i,
  );

  await secureStore.set(
    PosPublicRuntimeConfigurationStore.storageKey,
    JSON.stringify({
      version: 1,
      apiBaseUrl: "https://attacker.example/pos",
    }),
    { requireThisDeviceOnly: true },
  );
  await assert.rejects(
    () => subject.load(),
    /trusted|allowlist|origin/i,
  );
});
