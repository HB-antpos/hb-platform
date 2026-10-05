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

/** 与 use-automatic-native-app-update.test.ts 相同的加载方式：动态 import 转 CommonJS 后复用 require.cache mock。 */
function loadTranspiled<T>(relative: string): T {
  const file = require.resolve(relative);
  const loaded = new Module(file, module);
  loaded.filename = file;
  loaded.paths = module.paths;
  (loaded as Module & { _compile(source: string, filename: string): void })._compile(
    ts.transpileModule(readFileSync(file, "utf8"), {
      compilerOptions: {
        module: ts.ModuleKind.CommonJS,
        target: ts.ScriptTarget.ES2022,
        jsx: ts.JsxEmit.ReactJSX,
      },
    }).outputText,
    file,
  );
  return loaded.exports as T;
}

async function flush() {
  for (let index = 0; index < 20; index += 1) {
    await Promise.resolve();
  }
}

async function run() {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
  const rendererReact = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReact !== require.resolve("react")) mockModule(rendererReact, React);
  const { createRoot } = await import("test-renderer");
  const { appUpdateMutualExclusion } = await import("./app-update-mutual-exclusion");
  const prompts: unknown[][] = [];
  mockModule("react-native", {
    Platform: { OS: "android" },
    Alert: { alert: (...args: unknown[]) => prompts.push(args) },
    AppState: { currentState: "active", addEventListener: () => ({ remove() {} }) },
    View: "View",
    ScrollView: "ScrollView",
    StyleSheet: { create: (styles: unknown) => styles, hairlineWidth: 1 },
  });
  mockModule("react-native-paper", {
    Text: "Text", Button: "Button", ActivityIndicator: "ActivityIndicator", Surface: "Surface",
  });
  mockModule("react-native-safe-area-context", { SafeAreaView: "SafeAreaView" });
  mockModule("../../shared/i18n/i18n", { i18n: { t: (key: string) => key } });
  mockModule("../../shared/i18n/use-app-translation", {
    useAppTranslation: () => ({
      t: (key: string, params?: Record<string, unknown>) => (params ? `${key}:${JSON.stringify(params)}` : key),
    }),
  });

  let decisionPayload: unknown = {
    success: true,
    data: {
      state: "required",
      policyVersion: "3",
      minimumSupportedBuildNumber: 63,
      latestVersion: "1.0.10",
      latestBuildNumber: 63,
      releaseMessage: null,
    },
  };
  const decisionCalls: unknown[][] = [];
  mockModule("../../shared/api/client", {
    apiClient: {
      defaults: { baseURL: "https://hotbargain.vip/api" },
      get: async (...args: unknown[]) => {
        decisionCalls.push(args);
        return { data: decisionPayload };
      },
    },
  });
  const storage = new Map<string, string>();
  mockModule("../../shared/storage/async-storage", {
    AppAsyncStorage: {
      getString: async (key: string) => storage.get(key) ?? null,
      setString: async (key: string, value: string) => { storage.set(key, value); },
      removeItem: async (key: string) => { storage.delete(key); },
    },
  });
  mockModule("../../../modules/hb-app-installer/src/HBAppInstallerModule", { default: null });
  mockModule("expo-constants", { default: { expoConfig: { extra: { nativeAppInstallerEnabled: true } } } });
  mockModule("expo-application", { nativeBuildVersion: "56", applicationId: "com.hbweb.expo" });
  mockModule("expo-file-system/legacy", {
    cacheDirectory: "file:///cache",
    getContentUriAsync: async (uri: string) => `content://${uri}`,
  });
  const launches: unknown[][] = [];
  mockModule("expo-intent-launcher", {
    startActivityAsync: async (...args: unknown[]) => { launches.push(args); },
  });
  mockModule("./foreground-update-interval", { useForegroundUpdateCheckInterval() {} });

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

  const { useAutomaticNativeAppUpdate } = loadTranspiled<typeof import("./use-automatic-native-app-update")>(
    "./use-automatic-native-app-update",
  );
  const { AndroidNativeUpdateBoundary } = loadTranspiled<typeof import("./AndroidNativeUpdateBoundary")>(
    "./AndroidNativeUpdateBoundary",
  );

  let current!: ReturnType<typeof useAutomaticNativeAppUpdate>;
  function Harness() {
    current = useAutomaticNativeAppUpdate({ enabled: true });
    return React.createElement(AndroidNativeUpdateBoundary, {
      enabled: true,
      decision: current.requiredDecision,
      phase: current.phase,
      readyToInstall: current.readyToInstall,
      onInstall: current.installRequired,
      onRetry: current.retry,
    } as Parameters<typeof AndroidNativeUpdateBoundary>[0], React.createElement("Content", null, "业务页面"));
  }
  const root = createRoot();
  const view = () => JSON.stringify(root.container.toJSON());
  const originalWarn = console.warn;
  console.warn = () => {};
  try {
    // OTA 仍在初始化（单例默认 pending）：强制判定照常拉取，并打开原生强制门直接下载。
    await act(async () => { root.render(React.createElement(Harness)); await flush(); });
    assert.deepEqual(decisionCalls[0], [
      "/app-updates/mobile-android",
      { params: { build: "56" }, headers: { "X-Skip-Center-Log": "1" } },
    ]);
    assert.equal(appUpdateMutualExclusion.isNativeRequiredGateActive(), true);
    assert.equal(operations.length, 1, "原生 required 不等待 OTA 初始化");
    assert.equal(current.requiredDecision?.state, "required");
    assert.match(view(), /androidNativeUpdateRequiredTitle/);
    assert.doesNotMatch(view(), /业务页面/, "拦截页替换全部业务页面");
    assert.match(view(), /1\.0\.10 \(63\)/);
    assert.ok(storage.size > 0, "required 结果写入离线缓存");
    const installButton = () => root.container.queryAll((node) => node.type === "Button")[0];
    assert.equal(installButton().props.disabled, true, "安装包未就绪不能点安装");

    await act(async () => {
      operations[0].resolve({
        status: "downloaded",
        verification: "js",
        fileUri: "file:///cache/hb-63.apk",
        build: {
          easBuildId: "build-63", appVersion: "1.0.10", appBuildVersion: "63",
          artifactUrl: "", artifactSha256: "a".repeat(64), artifactSize: 1, buildProfile: "production",
        },
      });
      await flush();
    });
    assert.equal(prompts.length, 0, "强制更新不弹带「稍后」的安装框");
    assert.equal(current.readyToInstall, true);
    assert.match(view(), /androidNativeUpdateReady/);
    assert.equal(installButton().props.disabled, false);

    await act(async () => { installButton().props.onPress(); await flush(); });
    assert.equal(launches.length, 1, "立即安装拉起系统安装器");
    assert.match(String((launches[0][1] as { data: string }).data), /hb-63\.apk/);
    appUpdateMutualExclusion.clearNativeInstaller();

    // 管理端解除强制后，重试即恢复业务页面并关闭原生强制门。
    decisionPayload = {
      success: true,
      data: {
        state: "none", policyVersion: "4", minimumSupportedBuildNumber: null,
        latestVersion: "1.0.10", latestBuildNumber: 63, releaseMessage: null,
      },
    };
    await act(async () => { current.retry(); await flush(); });
    assert.equal(current.requiredDecision, null);
    assert.equal(appUpdateMutualExclusion.isNativeRequiredGateActive(), false);
    assert.match(view(), /业务页面/);
    assert.equal(storage.size, 0, "none 清除离线缓存");
  } finally {
    await act(async () => { root.unmount(); });
    console.warn = originalWarn;
  }
  console.log("use-automatic-native-app-update-required.test.ts: ok");
}

void run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
