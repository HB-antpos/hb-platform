import assert from "node:assert/strict";
import test from "node:test";

import { CurrentCashierSession } from "./current-cashier-session";
import {
  createProductionAttendanceAuditRuntime,
  type ProductionAttendanceAuditRuntimeDependencies,
} from "./production-attendance-audit-runtime";

import type { CashierLoginResult } from "@/core/security/cashier-authentication";
import type {
  AttendanceQrCachePort,
  AttendanceQrCryptoPort,
  AttendanceQrProvisioning,
  AttendanceSchedulerPort,
} from "@/features/attendance-audit/attendance-qr-controller";
import type { AttendanceSecurityRemotePort } from "@hb/pos-api-client/features/attendance-audit/hbpos-attendance-security-api";
import {
  AUDIT_VIEW_PERMISSION,
  type OperationAuditRawRecord,
  type OperationAuditReadPort,
} from "@hb/pos-domain/features/attendance-audit/operation-audit-presenter";

const STORE_CODE = "STORE-1";
const DEVICE_CODE = "IPAD-1";

test("公开服务只提供零参数 presenter 工厂，初始 online 来自组合根连通性", () => {
  const harness = createHarness({ online: false });

  assert.deepEqual(Object.keys(harness.runtime), [
    "createPresenter",
    "createQrController",
  ]);
  assert.equal(harness.presenter.getState().audit.online, false);
});

test("旧 cashier lease 失效后不会访问 Keychain、缓存或远端考勤接口", async () => {
  const harness = createHarness({ online: true });
  harness.currentCashier.clear();

  await harness.presenter.refreshAttendanceQr();

  assert.equal(harness.device.calls, 0);
  assert.equal(harness.cache.loadCalls, 0);
  assert.equal(harness.security.registerCalls, 0);
});

test("缺少 Audit.View 仍显示离线签名考勤 QR，且不会读取审计", async () => {
  const harness = createHarness({ online: false, permissions: [] });
  harness.presenter.start();
  await flush();

  assert.equal(harness.presenter.getState().audit.access.canView, false);
  assert.equal(harness.presenter.getState().qr.kind, "ready");
  assert.equal(
    harness.presenter.getState().qr.qrImageUri,
    "data:image/png;base64,QQ==",
  );
  assert.equal(harness.localAudit.listCalls.length, 0);
});

test("审计读取固定可信门店和设备；跨门店返回会失败关闭", async () => {
  const harness = createHarness({
    online: true,
    localRecords: [auditRecord({ storeCode: "OTHER-STORE" })],
  });

  await harness.presenter.loadAudit();

  assert.deepEqual(harness.localAudit.listCalls[0], {
    deviceCode: DEVICE_CODE,
    keyword: null,
    limit: 100,
    source: "local",
    storeCode: STORE_CODE,
    uploadState: null,
  });
  assert.equal(harness.presenter.getState().audit.kind, "failed");
  assert.equal(harness.presenter.getState().audit.statusCode, "list-failed");
});

test("远程审计在 runtime 连通性离线时锁定，不调用远端读取 port", async () => {
  const harness = createHarness({ online: false });
  harness.presenter.setAuditSource("remote");

  await harness.presenter.loadAudit();

  assert.equal(harness.presenter.getState().audit.statusCode, "online-required");
  assert.equal(harness.remoteAudit.listCalls.length, 0);
});

test("destroy 只清理 QR scheduler，不删除 Keychain A256 密钥", async () => {
  const harness = createHarness({ online: false });
  harness.presenter.start();
  await flush();
  harness.presenter.destroy();

  assert.equal(harness.scheduler.cancellations, 2);
  assert.equal(harness.crypto.destroyCalls, 0);
});

test("收银页考勤码：离线时用缓存身份签发，destroy 只停定时器不删密钥", async () => {
  const harness = createHarness({ online: false });
  const qr = harness.runtime.createQrController!();
  qr.start();
  await flush();

  assert.equal(qr.getState().kind, "ready");
  assert.equal(qr.getState().statusCode, "offline-signed");
  assert.equal(qr.getState().qrImageUri, "data:image/png;base64,QQ==");
  const cancellationsBefore = harness.scheduler.cancellations;
  qr.destroy();
  assert.equal(harness.scheduler.cancellations - cancellationsBefore, 2);
  assert.equal(harness.crypto.destroyCalls, 0);
});

test("收银页考勤码：收银员登出后不再访问设备、缓存或远端登记", async () => {
  const harness = createHarness({ online: true });
  const qr = harness.runtime.createQrController!();
  const deviceCallsBefore = harness.device.calls;
  const cacheLoadsBefore = harness.cache.loadCalls;
  harness.currentCashier.clear();
  qr.start();
  await flush();

  assert.equal(harness.device.calls, deviceCallsBefore);
  assert.equal(harness.cache.loadCalls, cacheLoadsBefore);
  assert.equal(harness.security.registerCalls, 0);
  assert.equal(qr.getState().qrImageUri, null);
  qr.destroy();
});

test("收银页考勤码：没有已登录收银员时拒绝创建", () => {
  const harness = createHarness({ online: true });
  harness.currentCashier.clear();

  // 中文注释：与 createPresenter 相同，没有收银员时 lease 创建即失败，路由据此不显示考勤码。
  assert.throws(() => harness.runtime.createQrController!());
});

class MemoryCache implements AttendanceQrCachePort {
  public loadCalls = 0;

  public constructor(private value: AttendanceQrProvisioning | null) {}

  public async load(): Promise<AttendanceQrProvisioning | null> {
    this.loadCalls += 1;
    return this.value;
  }

  public async replace(value: AttendanceQrProvisioning): Promise<void> {
    this.value = value;
  }

  public async clear(): Promise<void> {
    this.value = null;
  }
}

class FakeCrypto implements AttendanceQrCryptoPort {
  public destroyCalls = 0;

  public async createA256Identity() {
    return { keyHandle: "key-handle", kid: "kid-1" };
  }

  public async hasA256Key(): Promise<boolean> {
    return true;
  }

  public async withRegistrationKey<T>(
    _keyHandle: string,
    runWithMaterial: (keyMaterialBase64Url: string) => Promise<T>,
  ): Promise<T> {
    return runWithMaterial("protected-key-material");
  }

  public async issueAttendanceQr() {
    return { imageUri: "data:image/png;base64,QQ==" };
  }

  public async destroyKey(): Promise<void> {
    this.destroyCalls += 1;
  }
}

class FakeScheduler implements AttendanceSchedulerPort {
  public cancellations = 0;
  public readonly intervals: number[] = [];

  public every(intervalMs: number, _task: () => void): () => void {
    this.intervals.push(intervalMs);
    let active = true;
    return () => {
      if (!active) return;
      active = false;
      this.cancellations += 1;
    };
  }
}

class FakeAuditRead implements OperationAuditReadPort {
  public readonly listCalls: Parameters<OperationAuditReadPort["list"]>[0][] = [];

  public constructor(private readonly records: readonly OperationAuditRawRecord[]) {}

  public async list(input: Parameters<OperationAuditReadPort["list"]>[0]) {
    this.listCalls.push(input);
    return this.records;
  }

  public async get(): Promise<OperationAuditRawRecord | null> {
    return null;
  }
}

class FakeSecurity implements AttendanceSecurityRemotePort {
  public registerCalls = 0;

  public async registerAttendanceKey() {
    this.registerCalls += 1;
    return {
      kid: "kid-1",
      registeredAtEpochMs: 1_000,
      serverTimeEpochMs: 1_000,
    };
  }

  public async fetchEmergencyPublicKeys() {
    return { kind: "not-modified" as const };
  }

  public async acknowledgeEmergencyPublicKeys() {
    return {
      acknowledged: true,
      serverTimeEpochMs: 1_000,
      serverVersion: 1,
    };
  }
}

function createHarness(options: Readonly<{
  online?: boolean;
  permissions?: readonly string[];
  localRecords?: readonly OperationAuditRawRecord[];
}> = {}) {
  const currentCashier = new CurrentCashierSession();
  activateCashier(currentCashier, options.permissions ?? [AUDIT_VIEW_PERMISSION]);
  const online = { value: options.online ?? false };
  const cache = new MemoryCache(provisioning());
  const crypto = new FakeCrypto();
  const scheduler = new FakeScheduler();
  const device = {
    calls: 0,
    getDeviceContext: async () => {
      device.calls += 1;
      return {
        authorizationMarker: "authorized",
        deviceCode: DEVICE_CODE,
        hardwareId: "hardware-1",
        isAllowed: true,
        storeCode: STORE_CODE,
        storeName: "Store One",
      };
    },
  };
  const security = new FakeSecurity();
  const localAudit = new FakeAuditRead(options.localRecords ?? [auditRecord()]);
  const remoteAudit = new FakeAuditRead([auditRecord()]);
  const dependencies: ProductionAttendanceAuditRuntimeDependencies = {
    currentCashier,
    terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    connectivity: {
      currentOnline: () => online.value,
      isOnline: async () => online.value,
    },
    deviceContext: device,
    qrCache: cache,
    qrCrypto: crypto,
    scheduler,
    attendanceSecurity: security,
    localAudit,
    remoteAudit,
    clock: { now: () => 1_000 },
  };
  const runtime = createProductionAttendanceAuditRuntime(dependencies);
  return {
    currentCashier,
    cache,
    crypto,
    device,
    scheduler,
    security,
    localAudit,
    remoteAudit,
    runtime,
    presenter: runtime.createPresenter(),
  };
}

function activateCashier(
  session: CurrentCashierSession,
  permissions: readonly string[],
): void {
  const epoch = session.beginAuthentication();
  session.activate(epoch, {
    source: "online",
    session: {
      cashierId: "CASHIER-1",
      userGuid: null,
      cashierName: "Cashier One",
      storeCode: STORE_CODE,
      deviceCode: DEVICE_CODE,
      permissionCodes: [...permissions],
    },
  } satisfies CashierLoginResult, {
    storeCode: STORE_CODE,
    deviceCode: DEVICE_CODE,
  });
}

function provisioning(): AttendanceQrProvisioning {
  return {
    identity: {
      authorizationMarker: "authorized",
      deviceCode: DEVICE_CODE,
      hardwareId: "hardware-1",
      keyHandle: "key-handle",
      kid: "kid-1",
      registeredAtEpochMs: 1_000,
      storeCode: STORE_CODE,
    },
    trustedTime: { localEpochMs: 1_000, serverEpochMs: 1_000 },
  };
}

function auditRecord(
  overrides: Partial<OperationAuditRawRecord> = {},
): OperationAuditRawRecord {
  return {
    cashierName: "Cashier One",
    correlationId: null,
    deviceCode: DEVICE_CODE,
    eventId: "10000000-0000-4000-8000-000000000001",
    items: [],
    occurredAtIso: "2026-07-28T00:00:00.000Z",
    operationType: "attendance",
    orderGuid: null,
    outcome: "success",
    paymentAmountCents: null,
    primaryProduct: null,
    productCount: 0,
    receiptNumber: null,
    safeMessage: null,
    storeCode: STORE_CODE,
    uploadState: "uploaded",
    ...overrides,
  };
}

function flush(): Promise<void> {
  return new Promise((resolve) => setImmediate(resolve));
}
