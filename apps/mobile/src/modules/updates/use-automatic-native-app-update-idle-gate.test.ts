import assert from "node:assert/strict";
import Module from "node:module";
import { readFileSync } from "node:fs";
import React, { act } from "react";
import ts from "typescript";
import type { NativeAppUpdateCheckResult, NativeAppUpdateDependencies } from "./native-app-update";

function mockModule(name: string, exports: object) {
  const filename = require.resolve(name);
  const module = new Module(filename);
  module.filename = filename;
  module.loaded = true;
  module.exports = exports;
  require.cache[filename] = module;
}

async function run() {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
  const rendererReact = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReact !== require.resolve("react")) mockModule(rendererReact, React);
  const { createRoot } = await import("test-renderer");
  const { appUpdateMutualExclusion } = await import("./app-update-mutual-exclusion");
  const {
    BACKGROUND_DOWNLOAD_BANDWIDTH_SHARE,
    BACKGROUND_DOWNLOAD_IDLE_MS,
    backgroundDownloadGate,
  } = await import("./background-download-gate");

  // 闸门单例在调用时才读 Date.now / setTimeout：只接管 30 秒空闲计时器，其它定时器照常交给系统。
  let now = 1_000_000;
  const idleTimers: (() => void)[] = [];
  const originalNow = Date.now;
  const originalSetTimeout = globalThis.setTimeout;
  Date.now = () => now;
  globalThis.setTimeout = ((callback: () => void, delay?: number, ...args: unknown[]) => {
    if (delay === BACKGROUND_DOWNLOAD_IDLE_MS) {
      idleTimers.push(callback);
      return idleTimers.length as unknown as ReturnType<typeof setTimeout>;
    }
    return originalSetTimeout(callback, delay, ...args);
  }) as typeof setTimeout;

  mockModule("react-native", {
    Platform: { OS: "android" },
    Alert: { alert: () => undefined },
    AppState: { currentState: "active", addEventListener: () => ({ remove() {} }) },
  });
  mockModule("../../shared/i18n/i18n", { i18n: { t: (key: string) => key } });
  mockModule("../../shared/api/client", { apiClient: { defaults: { baseURL: "https://hotbargain.vip/api" } } });
  mockModule("../../shared/storage/async-storage", {
    AppAsyncStorage: { getString: async () => null, setString: async () => {}, removeItem: async () => {} },
  });
  mockModule("../../../modules/hb-app-installer/src/HBAppInstallerModule", { default: null });
  mockModule("expo-constants", { default: { expoConfig: { extra: { nativeAppInstallerEnabled: true } } } });
  mockModule("expo-application", { nativeBuildVersion: "63", applicationId: "com.hbweb.expo" });
  mockModule("expo-file-system/legacy", { cacheDirectory: "file:///cache" });
  mockModule("./foreground-update-interval", { useForegroundUpdateCheckInterval() {} });

  // 强制判定：用一个变量控制服务器是否要求必须更新。
  let requiredDecision: { required: boolean } | null = null;
  mockModule("./android-native-required-update", {
    shouldCheckAndroidNativeRequiredUpdate: () => true,
    parseInstalledAndroidBuildNumber: () => 63,
    fetchAndroidNativeUpdateDecision: async () => null,
    resolveAndroidNativeUpdateDecision: async () => ({ decision: requiredDecision }),
    isAndroidNativeUpdateRequired: (decision: { required?: boolean } | null) => decision?.required === true,
  });

  const operations: {
    dependencies: NativeAppUpdateDependencies;
    resolve: (result: NativeAppUpdateCheckResult) => void;
  }[] = [];
  mockModule("./native-app-update", {
    getBuildBoundNativeAppDownloadUrl: () => "https://hotbargain.vip/api/download",
    checkAndDownloadNativeAppUpdate: (dependencies: NativeAppUpdateDependencies) => new Promise((resolve) => {
      operations.push({ dependencies, resolve });
    }),
  });

  const hookFile = require.resolve("./use-automatic-native-app-update");
  const hookModule = new Module(hookFile, module);
  hookModule.filename = hookFile;
  hookModule.paths = module.paths;
  (hookModule as Module & { _compile(source: string, filename: string): void })._compile(
    ts.transpileModule(readFileSync(hookFile, "utf8"), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
    }).outputText,
    hookFile,
  );
  const { useAutomaticNativeAppUpdate } = hookModule.exports as typeof import("./use-automatic-native-app-update");
  let current!: ReturnType<typeof useAutomaticNativeAppUpdate>;
  function Harness() {
    current = useAutomaticNativeAppUpdate({ enabled: true });
    return null;
  }
  const flush = async () => {
    await act(async () => {
      for (let index = 0; index < 5; index += 1) {
        await new Promise((resolve) => setImmediate(resolve));
      }
    });
  };

  const root = createRoot();
  try {
    // OTA 初始化已结束，原生通道只剩「登录后空闲」这一道闸门。
    appUpdateMutualExclusion.setOtaInitializationPending(false);
    backgroundDownloadGate.bind(false);
    await act(async () => { root.render(React.createElement(Harness)); });
    await flush();
    assert.equal(operations.length, 0, "登录页上不检查也不下载可选安装包");

    await act(async () => { backgroundDownloadGate.setAuthenticated(true); });
    await flush();
    assert.equal(operations.length, 0, "刚登录时不和登录后的首屏请求抢带宽");
    assert.equal(idleTimers.length, 1);

    now += BACKGROUND_DOWNLOAD_IDLE_MS;
    await act(async () => { idleTimers.shift()?.(); });
    await flush();
    assert.equal(operations.length, 1, "登录满 30 秒后自动开始被挡下的下载");
    assert.equal(
      operations[0].dependencies.getDownloadBandwidthShare?.(),
      BACKGROUND_DOWNLOAD_BANDWIDTH_SHARE,
      "后台下载只占一部分带宽",
    );
    await act(async () => { operations[0].resolve({ status: "not-available" }); });
    await flush();

    await act(async () => { backgroundDownloadGate.setAuthenticated(false); });
    await act(async () => { current.retry(); });
    await flush();
    assert.equal(operations.length, 1, "登出回到登录页后再次挡住可选下载");

    // 强制更新：用户被拦着等，必须立即全速下载，不受闸门影响。
    requiredDecision = { required: true };
    await act(async () => { current.retry(); });
    await flush();
    assert.equal(operations.length, 2, "强制更新绕过闸门");
    assert.equal(operations[1].dependencies.getDownloadBandwidthShare?.(), null, "强制更新全速下载");
    await act(async () => { operations[1].resolve({ status: "not-available" }); });
    await flush();
  } finally {
    await act(async () => { root.unmount(); });
    backgroundDownloadGate.unbind();
    Date.now = originalNow;
    globalThis.setTimeout = originalSetTimeout;
  }
  console.log("use-automatic-native-app-update-idle-gate.test.ts: ok");
}

void run();
