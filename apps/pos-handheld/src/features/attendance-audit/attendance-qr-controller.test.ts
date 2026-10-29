import assert from "node:assert/strict";
import test from "node:test";

import {
  AttendanceQrController,
  type AttendanceConnectivityPort,
  type AttendanceDeviceContext,
  type AttendanceDeviceContextPort,
  type AttendanceQrCachePort,
  type AttendanceQrCryptoPort,
  type AttendanceQrProvisioning,
  type AttendanceSchedulerPort,
} from "./attendance-qr-controller";
import {
  AttendanceSecurityApiError,
  type AttendanceSecurityRemotePort,
  type EmergencyPublicKeyAckResult,
  type EmergencyPublicKeyFetchResult,
  type RegisteredAttendanceSigningKey,
} from "@hb/pos-api-client/features/attendance-audit/hbpos-attendance-security-api";

class FakeClock {
  public nowMs = Date.parse("2026-07-28T01:00:00.000Z");

  public readonly now = (): number => this.nowMs;
}

class FakeCache implements AttendanceQrCachePort {
  public value: AttendanceQrProvisioning | null = null;
  public readonly replacements: AttendanceQrProvisioning[] = [];
  public clearCount = 0;

  public async load(): Promise<AttendanceQrProvisioning | null> {
    return this.value;
  }

  public async replace(value: AttendanceQrProvisioning): Promise<void> {
    this.value = value;
    this.replacements.push(value);
  }

  public async clear(): Promise<void> {
    this.value = null;
    this.clearCount += 1;
  }
}

class FakeConnectivity implements AttendanceConnectivityPort {
  public online = true;
  public checks = 0;

  public async isOnline(): Promise<boolean> {
    this.checks += 1;
    return this.online;
  }
}

class FakeContext implements AttendanceDeviceContextPort {
  public value: AttendanceDeviceContext | null = context();

  public async getDeviceContext(): Promise<AttendanceDeviceContext | null> {
    return this.value;
  }
}

class FakeCrypto implements AttendanceQrCryptoPort {
  public readonly created: string[] = [];
  public readonly destroyed: string[] = [];
  public readonly issued: {
    deviceCode: string;
    issuedAtEpochMs: number;
    keyHandle: string;
    kid: string;
    storeCode: string;
  }[] = [];
  public missingHandles = new Set<string>();
  private sequence = 0;

  public async createA256Identity(): Promise<{
    keyHandle: string;
    kid: string;
  }> {
    this.sequence += 1;
    const keyHandle = `secure-key-${this.sequence}`;
    this.created.push(keyHandle);
    return { keyHandle, kid: `kid_${this.sequence}` };
  }

  public async hasA256Key(keyHandle: string): Promise<boolean> {
    return !this.missingHandles.has(keyHandle);
  }

  public async withRegistrationKey<T>(
    _keyHandle: string,
    consume: (keyMaterialBase64Url: string) => Promise<T>,
  ): Promise<T> {
    return consume("A".repeat(43));
  }

  public async issueAttendanceQr(input: {
    deviceCode: string;
    issuedAtEpochMs: number;
    keyHandle: string;
    kid: string;
    storeCode: string;
  }): Promise<{ imageUri: string }> {
    this.issued.push(input);
    return {
      imageUri: `data:image/png;base64,${Buffer.from(
        `qr-${this.issued.length}`,
      ).toString("base64")}`,
    };
  }

  public async destroyKey(keyHandle: string): Promise<void> {
    this.destroyed.push(keyHandle);
  }
}

class FakeRemote implements AttendanceSecurityRemotePort {
  public readonly registrations: string[] = [];
  public failures: unknown[] = [];
  public serverTimeMs = Date.parse("2026-07-28T02:00:00.000Z");

  public async registerAttendanceKey(input: {
    kid: string;
  }): Promise<RegisteredAttendanceSigningKey> {
    this.registrations.push(input.kid);
    const failure = this.failures.shift();
    if (failure) throw failure;
    return {
      kid: input.kid,
      registeredAtEpochMs: this.serverTimeMs,
      serverTimeEpochMs: this.serverTimeMs,
    };
  }

  public async fetchEmergencyPublicKeys(): Promise<EmergencyPublicKeyFetchResult> {
    throw new Error("not used");
  }

  public async acknowledgeEmergencyPublicKeys(): Promise<EmergencyPublicKeyAckResult> {
    throw new Error("not used");
  }
}

class FakeScheduler implements AttendanceSchedulerPort {
  public readonly intervals: number[] = [];
  public readonly tasks: (() => void)[] = [];
  public cancelled = 0;

  public every(intervalMs: number, task: () => void): () => void {
    this.intervals.push(intervalMs);
    this.tasks.push(task);
    return () => {
      this.cancelled += 1;
    };
  }
}

test("首次在线登记 A256GCM 密钥、校准可信时间并签发严格 15 秒二维码", async () => {
  const fixture = createFixture();

  await fixture.controller.refresh();

  const state = fixture.controller.getState();
  assert.equal(state.kind, "ready");
  assert.equal(state.online, true);
  assert.equal(state.secondsRemaining, 15);
  assert.match(state.qrImageUri ?? "", /^data:image\/png;base64,/u);
  assert.equal("qrToken" in state, false);
  assert.deepEqual(fixture.remote.registrations, ["kid_1"]);
  assert.equal(fixture.cache.replacements.length, 1);
  assert.deepEqual(fixture.crypto.issued, [
    {
      deviceCode: "IPAD-1",
      issuedAtEpochMs: fixture.remote.serverTimeMs,
      keyHandle: "secure-key-1",
      kid: "kid_1",
      storeCode: "S1",
    },
  ]);
});

test("二维码在第 10 秒提前轮换，并在下一次 15 秒边界继续轮换", async () => {
  const fixture = createFixture();

  await fixture.controller.refresh();
  const firstImage = fixture.controller.getState().qrImageUri;

  fixture.clock.nowMs += 9_000;
  await fixture.controller.tick();
  assert.equal(fixture.crypto.issued.length, 1);
  assert.equal(fixture.controller.getState().qrImageUri, firstImage);
  assert.equal(fixture.controller.getState().secondsRemaining, 6);

  fixture.clock.nowMs += 1_000;
  await fixture.controller.tick();
  const secondImage = fixture.controller.getState().qrImageUri;
  assert.equal(fixture.crypto.issued.length, 2);
  assert.notEqual(secondImage, firstImage);
  assert.equal(fixture.controller.getState().secondsRemaining, 15);

  fixture.clock.nowMs += 15_000;
  await fixture.controller.tick();
  assert.equal(fixture.crypto.issued.length, 3);
  assert.notEqual(fixture.controller.getState().qrImageUri, secondImage);
});

test("重复 tick 在提前轮换期间只签发一个二维码", async () => {
  const fixture = createFixture();

  await fixture.controller.refresh();
  const firstImage = fixture.controller.getState().qrImageUri;
  fixture.clock.nowMs += 10_000;

  await Promise.all([fixture.controller.tick(), fixture.controller.tick()]);

  assert.equal(fixture.crypto.issued.length, 2);
  assert.notEqual(fixture.controller.getState().qrImageUri, firstImage);
});

test("有可信缓存时离线继续轮换；首次离线无缓存绝不显示二维码", async () => {
  const cached = createFixture();
  cached.connectivity.online = false;
  cached.cache.value = provisioning({
    localEpochMs: cached.clock.nowMs - 5_000,
    serverEpochMs: Date.parse("2026-07-28T03:00:00.000Z"),
  });

  await cached.controller.refresh();

  assert.equal(cached.controller.getState().kind, "ready");
  assert.equal(cached.controller.getState().online, false);
  assert.equal(
    cached.crypto.issued[0]?.issuedAtEpochMs,
    Date.parse("2026-07-28T03:00:05.000Z"),
  );
  assert.equal(cached.remote.registrations.length, 0);

  const cold = createFixture();
  cold.connectivity.online = false;
  await cold.controller.refresh();

  assert.equal(cold.controller.getState().kind, "unavailable");
  assert.equal(cold.controller.getState().qrImageUri, null);
  assert.equal(cold.crypto.issued.length, 0);
});

test("任何本机 UTC 回拨立即清码并锁存，离线或时间恢复都不能解除", async () => {
  const fixture = createFixture();
  await fixture.controller.refresh();
  fixture.clock.nowMs += 2_000;
  await fixture.controller.tick();
  assert.equal(fixture.controller.getState().kind, "ready");

  fixture.clock.nowMs -= 10_000;
  await fixture.controller.tick();
  assert.equal(fixture.controller.getState().kind, "clock-invalid");
  assert.equal(fixture.controller.getState().qrImageUri, null);
  assert.equal(fixture.controller.getState().requiresOnlineResync, true);

  fixture.clock.nowMs += 20_000;
  await fixture.controller.tick();
  assert.equal(fixture.controller.getState().kind, "clock-invalid");

  fixture.connectivity.online = false;
  await fixture.controller.refresh();
  assert.equal(fixture.controller.getState().kind, "clock-invalid");

  fixture.connectivity.online = true;
  await fixture.controller.refresh();
  assert.equal(fixture.controller.getState().kind, "ready");
  assert.equal(fixture.controller.getState().requiresOnlineResync, false);
});

test("设备/门店/授权标记变化会销毁旧 key；kid 冲突仅轮换重试一次", async () => {
  const fixture = createFixture();
  fixture.cache.value = provisioning();
  fixture.deviceContext.value = context({
    authorizationMarker: "AUTH-B",
    storeCode: "S2",
  });
  fixture.remote.failures.push(
    new AttendanceSecurityApiError(
      "rejected",
      "conflict",
      undefined,
      "ATTENDANCE_QR_KEY_KID_CONFLICT",
    ),
  );

  await fixture.controller.refresh();

  assert.equal(fixture.cache.clearCount, 2);
  assert.deepEqual(fixture.crypto.destroyed, [
    "secure-key-cached",
    "secure-key-1",
  ]);
  assert.deepEqual(fixture.remote.registrations, ["kid_1", "kid_2"]);
  assert.equal(
    fixture.cache.value?.identity.storeCode,
    "S2",
  );
});

test("设备被禁用时清除缓存并销毁已持久化 key，离线缓存不能绕过门禁", async () => {
  const fixture = createFixture();
  fixture.cache.value = provisioning();
  fixture.deviceContext.value = context({ isAllowed: false });

  await fixture.controller.refresh();

  assert.equal(fixture.controller.getState().kind, "unavailable");
  assert.equal(fixture.cache.value, null);
  assert.deepEqual(fixture.crypto.destroyed, ["secure-key-cached"]);
  assert.equal(fixture.crypto.issued.length, 0);
});

test("tick 与在线 refresh 使用独立调度器，销毁后同时取消且清除可见二维码", async () => {
  const fixture = createFixture();

  fixture.controller.start();
  assert.deepEqual(fixture.scheduler.intervals, [1_000, 15_000]);
  await fixture.controller.refresh();
  assert.notEqual(fixture.controller.getState().qrImageUri, null);

  fixture.controller.destroy();

  assert.equal(fixture.scheduler.cancelled, 2);
  assert.equal(fixture.controller.getState().qrImageUri, null);
});

test("后台刷新自适应（对齐 WPF #927）：状态健康时 5 分钟一次，离线等异常 15 秒重试", async () => {
  const fixture = createFixture();
  fixture.controller.start();
  await fixture.controller.refresh();
  assert.equal(fixture.controller.getState().kind, "ready");
  assert.equal(fixture.controller.getState().online, true);
  const refreshTask = fixture.scheduler.tasks[1]!;
  const registrations = () => fixture.remote.registrations.length;

  // 健康：15 秒节拍唤醒也不重复登记。
  const baseline = registrations();
  fixture.clock.nowMs += 15_000;
  refreshTask();
  fixture.clock.nowMs += 15_000;
  refreshTask();
  await fixture.controller.refresh().catch(() => undefined);
  assert.equal(registrations(), baseline + 1, "只有显式 refresh 登记了一次，节拍唤醒被跳过");

  // 满 5 分钟后到期，节拍唤醒会再登记一次。
  fixture.clock.nowMs += 5 * 60_000;
  refreshTask();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(registrations(), baseline + 2);

  // 离线（异常）：回到 15 秒重试。
  fixture.connectivity.online = false;
  fixture.clock.nowMs += 5 * 60_000;
  refreshTask();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(fixture.controller.getState().online, false);
  fixture.clock.nowMs += 15_000;
  const before = fixture.connectivity.checks;
  refreshTask();
  await new Promise((resolve) => setImmediate(resolve));
  assert.ok(fixture.connectivity.checks > before, "异常状态 15 秒后再次检查连通性");
  fixture.controller.destroy();
});

function createFixture() {
  const cache = new FakeCache();
  const clock = new FakeClock();
  const connectivity = new FakeConnectivity();
  const crypto = new FakeCrypto();
  const deviceContext = new FakeContext();
  const remote = new FakeRemote();
  const scheduler = new FakeScheduler();
  const controller = new AttendanceQrController({
    cache,
    clock,
    connectivity,
    crypto,
    deviceContext,
    remote,
    scheduler,
  });
  return {
    cache,
    clock,
    connectivity,
    controller,
    crypto,
    deviceContext,
    remote,
    scheduler,
  };
}

function context(
  overrides: Partial<AttendanceDeviceContext> = {},
): AttendanceDeviceContext {
  return {
    authorizationMarker: "AUTH-A",
    deviceCode: "IPAD-1",
    hardwareId: "HW-1",
    isAllowed: true,
    storeCode: "S1",
    storeName: "Brisbane",
    ...overrides,
  };
}

function provisioning(
  overrides: Partial<AttendanceQrProvisioning["trustedTime"]> = {},
): AttendanceQrProvisioning {
  return {
    identity: {
      authorizationMarker: "AUTH-A",
      deviceCode: "IPAD-1",
      hardwareId: "HW-1",
      keyHandle: "secure-key-cached",
      kid: "kid_cached",
      registeredAtEpochMs: Date.parse("2026-07-28T00:00:00.000Z"),
      storeCode: "S1",
    },
    trustedTime: {
      localEpochMs: Date.parse("2026-07-28T01:00:00.000Z"),
      serverEpochMs: Date.parse("2026-07-28T02:00:00.000Z"),
      ...overrides,
    },
  };
}
