import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { LogCenterConfig } from "@/shared/logging/log-center";
import { uploadAdvertisementAssetToSignedUrl } from "@/modules/advertisements/upload";
import { uploadAttendanceLeaveAttachmentToSignedUrl } from "@/modules/attendance/leave-attachment-upload";
import {
  __flushPendingLogsForTests,
  __getPendingLogCountForTests,
  __resetLogCenterRuntimeForTests,
  __setLogCenterConfigForTests,
  reportApplicationLog,
} from "@/shared/logging/log-center-runtime";
import { reviewAwareFetch } from "./network";
import {
  clearIosReviewSession,
  setIosReviewSessionActive,
  type IosReviewMarkerStorage,
} from "./session";

const originalFetch = globalThis.fetch;

function createMarkerStorage(): IosReviewMarkerStorage {
  const values = new Map<string, string>();
  return {
    getItemAsync: async (key) => values.get(key) ?? null,
    setItemAsync: async (key, value) => {
      values.set(key, value);
    },
    deleteItemAsync: async (key) => {
      values.delete(key);
    },
  };
}

function createEnabledLogConfig(): LogCenterConfig {
  return {
    enabled: true,
    endpoint: "https://logs.example.com/api/system/logs/ingest",
    projectCode: "HbwebExpo",
    key: "review-test-key",
    environment: "production",
    serviceName: "HbwebExpoApp",
    sourceType: "Mobile",
    batchSize: 10,
    maxQueueSize: 30,
    retryLimit: 1,
  };
}

function readMobileSource(relativePath: string) {
  return readFileSync(resolve(process.cwd(), relativePath), "utf8");
}

function assertGuardBefore(
  source: string,
  functionMarker: string,
  sideEffectMarker: string,
  label: string,
) {
  const functionIndex = source.indexOf(functionMarker);
  const guardIndex = source.indexOf("isIosReviewSessionActive()", functionIndex);
  const sideEffectIndex = source.indexOf(sideEffectMarker, functionIndex);
  assert.ok(functionIndex >= 0, `${label} 函数必须存在`);
  assert.ok(guardIndex > functionIndex, `${label} 必须检查审核会话`);
  assert.ok(
    sideEffectIndex < 0 || guardIndex < sideEffectIndex,
    `${label} 必须在真实副作用前完成审核分流`,
  );
}

async function run() {
  const markerStorage = createMarkerStorage();
  let fetchCalls = 0;

  try {
    await setIosReviewSessionActive(markerStorage);
    __resetLogCenterRuntimeForTests();
    __setLogCenterConfigForTests(createEnabledLogConfig());
    globalThis.fetch = (async () => {
      fetchCalls += 1;
      return new Response("ok", { status: 200 });
    }) as typeof fetch;

    reportApplicationLog({
      level: "Error",
      message: "审核模式日志不得进入发送队列",
      sourceType: "ios-review.test",
    });
    await __flushPendingLogsForTests();

    assert.equal(__getPendingLogCountForTests(), 0, "审核模式日志必须直接丢弃");
    assert.equal(fetchCalls, 0, "审核模式日志不得发送 HTTP 请求");

    await assert.rejects(
      reviewAwareFetch("https://production.example.com/health"),
      /IOS_REVIEW_NETWORK_BLOCKED/,
      "审核模式必须拒绝所有 HTTP 请求",
    );
    assert.equal(fetchCalls, 0, "被拒绝的 HTTP 请求不得触发底层 fetch");

    const advertisementUpload = await uploadAdvertisementAssetToSignedUrl(
      "file:///ios-review/advertisement.jpg",
      {
        url: "https://uploads.example.com/advertisement.jpg?signature=secret",
        objectKey: "ios-review/advertisement.jpg",
        headers: {},
      },
    );
    assert.equal(
      advertisementUpload.mediaUrl,
      "file:///ios-review/advertisement.jpg",
      "广告上传应保留本地预览 URI",
    );
    const attendanceUpload = await uploadAttendanceLeaveAttachmentToSignedUrl(
      "file:///ios-review/leave.jpg",
      {
        url: "https://uploads.example.com/leave.jpg?signature=secret",
        objectKey: "ios-review/leave.jpg",
        headers: {},
      },
    );
    assert.equal(
      attendanceUpload.downloadUrl,
      "file:///ios-review/leave.jpg",
      "请假附件上传应保留本地预览 URI",
    );
    assert.equal(fetchCalls, 0, "审核模式模拟上传不得调用底层 fetch");

    const localResponse = await reviewAwareFetch(
      "data:text/plain,demo",
      undefined,
      originalFetch,
    );
    assert.equal(await localResponse.text(), "demo", "审核模式仍应允许本地 data URI");

    const runtimeFiles = [
      "src/modules/attendance/leave-attachment-upload.ts",
      "src/modules/attendance/use-punch-verification.ts",
      "src/modules/employee-profile/image-processing.ts",
      "src/modules/employee-profile/image-upload.ts",
      "src/modules/employee-profile/api.ts",
      "src/modules/warehouse/api.ts",
      "src/modules/advertisements/upload.ts",
      "src/modules/advertisements/advertisements-screen.tsx",
      "src/shared/logging/log-center-runtime.ts",
      "src/components/attendance/LeaveManagementCard.tsx",
      "app/(shell)/warehouse.tsx",
    ];
    runtimeFiles.forEach((file) => {
      const source = readMobileSource(file);
      assert.doesNotMatch(source, /(^|[^A-Za-z])fetch\s*\(/m, `${file} 必须统一使用 reviewAwareFetch`);
    });

    const requiredLocationSource = readMobileSource(
      "src/modules/attendance/required-location.ts",
    );
    const reviewHelpersSource = readMobileSource("src/modules/ios-review/helpers.ts");
    assert.match(requiredLocationSource, /IOS_REVIEW_LOCATION/,
      "定位流程应复用统一的审核坐标定义");
    assert.match(reviewHelpersSource, /-27\.4698/,
      "审核模式应使用固定 Brisbane 纬度");
    assert.match(reviewHelpersSource, /153\.0251/,
      "审核模式应使用固定 Brisbane 经度");
    const trackingControlSource = readMobileSource(
      "src/modules/attendance/location-tracking-control.ts",
    );
    assert.match(
      trackingControlSource,
      /options\?\.force/,
      "审核会话恢复时必须允许强制停止遗留后台定位任务",
    );

    const productQuerySource = readMobileSource("app/(shell)/product-query.tsx");
    assert.match(productQuerySource, /9330000000017/,
      "商品查询页必须提供固定示例条码");

    const deviceStoreSource = readMobileSource("src/store/device-store.ts");
    assert.match(deviceStoreSource, /isIosReviewSessionActive\(\)/,
      "设备 store 必须在审核会话中绕过持久化和远程验证");
    const deviceHydrateSource = deviceStoreSource.slice(
      deviceStoreSource.indexOf("async hydrate()"),
      deviceStoreSource.indexOf("async register(payload)")
    );
    assert.ok(
      deviceHydrateSource.indexOf("isIosReviewAuthenticatedSessionActive()") <
        deviceHydrateSource.indexOf("DeviceStorage.getSession()"),
      "设备恢复必须只对已认证审核会话隐藏持久设备绑定"
    );
    assertGuardBefore(deviceStoreSource, "async register(payload)", "DeviceStorage.getInstallationId()", "设备注册");
    assertGuardBefore(deviceStoreSource, "async validate(auditPayload)", "DeviceStorage.getSession()", "设备验证");
    assertGuardBefore(deviceStoreSource, "async unbind()", "unbindDeviceApi", "设备解绑");

    const printerSource = readMobileSource("src/modules/printer/api.ts");
    assertGuardBefore(printerSource, "function scanPrinterDevices()", "scanPrinters()", "打印机扫描");
    assertGuardBefore(printerSource, "function connectSavedPrinter", "connectPrinter(", "打印机连接");
    assertGuardBefore(printerSource, "function testPrinterConnection", "printRawCommand(", "标签测试打印");
    assertGuardBefore(printerSource, "function testReceiptPrinterConnection", "printRawCommand(", "小票测试打印");
    assertGuardBefore(printerSource, "function printProductLabel(", "printNativeProductLabel", "商品标签打印");
    const hydrateSavedPrinterSource = printerSource.slice(
      printerSource.indexOf("export async function hydrateSavedPrinter"),
      printerSource.indexOf("export async function hydrateSavedReceiptPrinter"),
    );
    assert.equal(
      hydrateSavedPrinterSource.match(/if \(isIosReviewSessionActive\(\)\)/g)?.length,
      1,
      "普通会话必须能继续读取已保存的标签打印机",
    );
    assert.match(hydrateSavedPrinterSource, /PrinterStorage\.getPrinter\(\)/);

    // BLE 扫码枪：审核态不扫描、不连接（与打印机一致），设置页整行隐藏，根布局随副作用开关启停。
    const bleScannerSource = readMobileSource("src/modules/scanner/ble/ble-scanner-service.ts");
    assertGuardBefore(bleScannerSource, "export async function startBleScannerService", "getController()", "扫码枪启动");
    assertGuardBefore(bleScannerSource, "export async function startBleScannerDiscovery", ".startDiscovery(", "扫码枪搜索");
    assertGuardBefore(bleScannerSource, "export async function pairBleScanner", ".pair(", "扫码枪配对");
    assertGuardBefore(bleScannerSource, "export async function reconnectBleScanner", ".reconnect()", "扫码枪重连");
    assertGuardBefore(bleScannerSource, "export function resumeBleScannerService", ".resume()", "扫码枪回前台重连");
    assertGuardBefore(bleScannerSource, "function routeScan", "deliverBleScan(", "扫码枪分发");
    assert.match(
      bleScannerSource,
      /export function isBleScannerFeatureAvailable\(\)[\s\S]{0,120}!isIosReviewSessionActive\(\)/,
      "审核态必须隐藏蓝牙扫码枪入口",
    );
    const rootLayoutSource = readMobileSource("app/_layout.tsx");
    assert.match(
      rootLayoutSource,
      /useBleScannerAutoStart\(\{\s*enabled:\s*sideEffectsEnabled\s*\}\)/,
      "扫码枪自动连接必须跟随 sideEffectsEnabled（审核态关闭）",
    );

    const settingsSource = readMobileSource("app/(shell)/settings.tsx");
    assertGuardBefore(settingsSource, "const handleCheckUpdates", "await checkMobileOtaUpdate()", "手动 OTA");
    assert.match(
      settingsSource,
      /Updates are disabled in offline demo mode/,
      "Settings 必须明确提示离线 Demo 不检查更新",
    );
  } finally {
    globalThis.fetch = originalFetch;
    __resetLogCenterRuntimeForTests();
    await clearIosReviewSession(markerStorage);
  }
}

void run();
