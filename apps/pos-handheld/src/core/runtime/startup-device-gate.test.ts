import assert from "node:assert/strict";
import test from "node:test";

import { HbposApiError } from "../api/hbpos-api";

import { resolveStartupDeviceGate } from "./startup-device-gate";

const readNoPendingDeviceActivation = async () => null;

test("设备注册重置 marker 待读取时在线与离线启动都直接锁定且不暴露注册输入", async () => {
  for (const internetReachable of [true, false]) {
    let calls = 0;
    const result = await resolveStartupDeviceGate({
      internetReachable,
      registrationResetPending: true,
      readPendingDeviceActivation: async () => {
        calls += 1;
        return null;
      },
      verifyCurrentDevice: async () => {
        calls += 1;
        return { status: "authorized" as const };
      },
      readLocalDevice: async () => {
        calls += 1;
        return "registration-required" as const;
      },
      lockDevice: async () => {
        calls += 1;
      },
    });

    assert.deepEqual(result, {
      backend: internetReachable ? "unverified" : "offline",
      device: "locked",
    });
    assert.equal(calls, 0);
  }
});

test("在线已授权设备必须以 verify 结果进入 authorized-online", async () => {
  let verifies = 0;
  const result = await resolveStartupDeviceGate({
    internetReachable: true,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    async verifyCurrentDevice() {
      verifies += 1;
      return { status: "authorized", deviceCode: "IPAD1", storeCode: "S1" };
    },
    readLocalDevice: async () => "authorized-local",
    async lockDevice() {},
  });

  assert.equal(verifies, 1);
  assert.deepEqual(result, {
    backend: "reachable",
    device: "authorized-online",
  });
});

test("待审批和业务禁用使用在线明确状态，不回退旧本地授权", async () => {
  const pending = await resolveStartupDeviceGate({
    internetReachable: true,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    verifyCurrentDevice: async () => ({ status: "pending-approval" }),
    readLocalDevice: async () => "authorized-local",
    async lockDevice() {},
  });
  const disabled = await resolveStartupDeviceGate({
    internetReachable: true,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    verifyCurrentDevice: async () => ({ status: "disabled" }),
    readLocalDevice: async () => "authorized-local",
    async lockDevice() {},
  });

  assert.deepEqual(pending, {
    backend: "reachable",
    device: "pending-approval",
  });
  assert.deepEqual(disabled, { backend: "rejected", device: "locked" });
});

test("已知离线和传输/5xx 失败才允许使用本地授权现金收银", async () => {
  let offlineVerifyCalls = 0;
  const knownOffline = await resolveStartupDeviceGate({
    internetReachable: false,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    async verifyCurrentDevice() {
      offlineVerifyCalls += 1;
      return { status: "authorized" };
    },
    readLocalDevice: async () => "authorized-local",
    async lockDevice() {},
  });
  const transportFailure = await resolveStartupDeviceGate({
    internetReachable: true,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    async verifyCurrentDevice() {
      throw new HbposApiError("network", { kind: "transport" });
    },
    readLocalDevice: async () => "authorized-local",
    async lockDevice() {},
  });
  const serverFailure = await resolveStartupDeviceGate({
    internetReachable: true,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    async verifyCurrentDevice() {
      throw new HbposApiError("server", { kind: "http", status: 503 });
    },
    readLocalDevice: async () => "authorized-local",
    async lockDevice() {},
  });

  assert.equal(offlineVerifyCalls, 0);
  assert.deepEqual(knownOffline, {
    backend: "offline",
    device: "authorized-local",
  });
  assert.deepEqual(transportFailure, knownOffline);
  assert.deepEqual(serverFailure, knownOffline);
});

test("在线带业务错误码的 ApiResult 403 明确拒绝锁机，旧本地凭据不得兜底", async () => {
  const locks: string[] = [];
  const result = await resolveStartupDeviceGate({
    internetReachable: true,
    readPendingDeviceActivation: readNoPendingDeviceActivation,
    async verifyCurrentDevice() {
      throw new HbposApiError("device rejected", {
        kind: "http",
        status: 403,
        code: "DEVICE_SCOPE_FORBIDDEN",
        apiEnvelope: true,
      });
    },
    readLocalDevice: async () => "authorized-local",
    async lockDevice(reason) {
      locks.push(reason);
    },
  });

  assert.deepEqual(result, { backend: "rejected", device: "locked" });
  assert.deepEqual(locks, ["device rejected"]);
});

test("网关 HTML 403、404、405、2xx 非信封等非 ApiResult 响应按服务不可用离线营业，绝不持久化锁机", async () => {
  const failures: readonly HbposApiError[] = [
    // EdgeOne/WAF 拦截页：transport 记录 apiEnvelope=false，且 HTML 正文没有错误码。
    new HbposApiError("Request failed with status code 403", {
      kind: "http",
      status: 403,
      apiEnvelope: false,
    }),
    new HbposApiError("Request failed with status code 404", {
      kind: "http",
      status: 404,
      apiEnvelope: false,
    }),
    new HbposApiError("Request failed with status code 405", {
      kind: "http",
      status: 405,
      apiEnvelope: false,
    }),
    // 2xx 却是 HTML / Wi-Fi 认证页：unwrapHbposEnvelope 抛出的非信封 envelope 错误。
    new HbposApiError("Hbpos API response is not an ApiResult envelope.", {
      kind: "envelope",
      apiEnvelope: false,
    }),
  ];

  for (const failure of failures) {
    let locks = 0;
    const result = await resolveStartupDeviceGate({
      internetReachable: true,
      readPendingDeviceActivation: readNoPendingDeviceActivation,
      async verifyCurrentDevice() {
        throw failure;
      },
      readLocalDevice: async () => "authorized-local",
      async lockDevice() {
        locks += 1;
      },
    });

    assert.deepEqual(
      result,
      { backend: "offline", device: "authorized-local" },
      `${failure.kind} ${failure.status ?? "2xx"} must fall back to offline`,
    );
    assert.equal(locks, 0);
  }
});

test("带信封但没有业务错误码的 403 不持久化锁机，也不放行旧授权", async () => {
  const failure = new HbposApiError("forbidden", {
    kind: "http",
    status: 403,
    apiEnvelope: true,
  });
  let locks = 0;
  let localReads = 0;
  await assert.rejects(
    resolveStartupDeviceGate({
      internetReachable: true,
      readPendingDeviceActivation: readNoPendingDeviceActivation,
      async verifyCurrentDevice() {
        throw failure;
      },
      async readLocalDevice() {
        localReads += 1;
        return "authorized-local";
      },
      async lockDevice() {
        locks += 1;
      },
    }),
    (error) => error === failure,
  );
  assert.equal(locks, 0);
  assert.equal(localReads, 0);
});

test("envelope、401、429、其他 4xx 和编程错误保持不可交易，不得伪装成离线", async () => {
  const failures: unknown[] = [
    new HbposApiError("business rejected", {
      kind: "envelope",
      code: "DEVICE_VERIFY_REJECTED",
    }),
    new HbposApiError("cashier authorization expired", {
      kind: "http",
      status: 401,
    }),
    new HbposApiError("bad request", { kind: "http", status: 400 }),
    new HbposApiError("rate limited", { kind: "http", status: 429 }),
    new Error("unexpected parser failure"),
  ];

  for (const failure of failures) {
    let localReads = 0;
    let locks = 0;
    await assert.rejects(
      resolveStartupDeviceGate({
        internetReachable: true,
        readPendingDeviceActivation: readNoPendingDeviceActivation,
        async verifyCurrentDevice() {
          throw failure;
        },
        async readLocalDevice() {
          localReads += 1;
          return "authorized-local";
        },
        async lockDevice() {
          locks += 1;
        },
      }),
      (error) => error === failure,
    );
    assert.equal(localReads, 0);
    assert.equal(locks, 0);
  }
});

test("待开通码存在或安全存储读取失败时离线启动必须失败关闭且不读取旧授权", async () => {
  let localReads = 0;
  const shared = {
    internetReachable: false,
    verifyCurrentDevice: async () => ({ status: "authorized" as const }),
    readLocalDevice: async () => {
      localReads += 1;
      return "authorized-local" as const;
    },
    async lockDevice() {},
  };

  await assert.rejects(
    resolveStartupDeviceGate({
      ...shared,
      readPendingDeviceActivation: async () => "HBDEV1-REDACTED",
    }),
    /activation recovery/i,
  );
  assert.equal(localReads, 0);

  await assert.rejects(
    resolveStartupDeviceGate({
      ...shared,
      internetReachable: true,
      readPendingDeviceActivation: async () => "HBDEV1-REDACTED",
      verifyCurrentDevice: async () => {
        throw new HbposApiError("network", { kind: "transport" });
      },
    }),
    /activation recovery/i,
  );
  assert.equal(localReads, 0);

  await assert.rejects(
    resolveStartupDeviceGate({
      ...shared,
      readPendingDeviceActivation: async () => {
        throw new Error("keychain decrypt failed");
      },
    }),
    /activation recovery/i,
  );
  assert.equal(localReads, 0);
});
