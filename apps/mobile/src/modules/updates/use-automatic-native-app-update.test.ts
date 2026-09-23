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
  const platform = { OS: "android" };
  const prompts: unknown[][] = [];
  mockModule("react-native", {
    Platform: platform,
    Alert: { alert: (...args: unknown[]) => prompts.push(args) },
    AppState: { currentState: "active", addEventListener: () => ({ remove() {} }) },
    View: "View",
    StyleSheet: { create: (styles: unknown) => styles, hairlineWidth: 1 },
  });
  mockModule("react-native-paper", { Text: "Text", Button: "Button", ActivityIndicator: "ActivityIndicator" });
  mockModule("react-native-safe-area-context", { SafeAreaView: "SafeAreaView" });
  mockModule("../../shared/i18n/i18n", { i18n: { t: (key: string) => key } });
  mockModule("../../shared/i18n/use-app-translation", { useAppTranslation: () => ({ t: (key: string) => key }) });
  mockModule("../../shared/api/client", { apiClient: { defaults: { baseURL: "https://hotbargain.vip/api" } } });
  mockModule("../../../modules/hb-app-installer/src/HBAppInstallerModule", { default: null });
  mockModule("expo-constants", { default: { expoConfig: { extra: { nativeAppInstallerEnabled: true } } } });
  mockModule("expo-application", { nativeBuildVersion: "56", applicationId: "com.hbweb.expo" });
  mockModule("expo-file-system/legacy", { cacheDirectory: "file:///cache" });
  mockModule("./foreground-update-interval", { useForegroundUpdateCheckInterval() {} });

  const operations: {
    dependencies: NativeAppUpdateDependencies;
    resolve: (result: NativeAppUpdateCheckResult) => void;
    reject: (error: Error) => void;
  }[] = [];
  mockModule("./native-app-update", {
    getBuildBoundNativeAppDownloadUrl: () => "https://hotbargain.vip/api/download",
    checkAndDownloadNativeAppUpdate: (dependencies: NativeAppUpdateDependencies) => new Promise((resolve, reject) => {
      operations.push({ dependencies, resolve, reject });
    }),
  });
  // 将动态 import 转为 CommonJS，沿用仓库的 require.cache mock，避免 Node 直接解析 RN Flow 源码。
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
  const { NativeAppUpdateStatus } = await import("./NativeAppUpdateStatus");
  let current!: ReturnType<typeof useAutomaticNativeAppUpdate>;
  function Harness({ enabled }: { enabled: boolean }) {
    current = useAutomaticNativeAppUpdate({ enabled });
    return React.createElement(React.Fragment, null,
      React.createElement(NativeAppUpdateStatus, {
        phase: current.phase, onRetry: current.retry, onDismiss: current.dismiss,
      }),
      React.createElement("Content", null, "业务页面始终保留"),
    );
  }
  const root = createRoot();
  const view = () => JSON.stringify(root.container.toJSON());
  const render = async (enabled: boolean) => {
    await act(async () => { root.render(React.createElement(Harness, { enabled })); });
  };
  const originalWarn = console.warn;
  const warnings: unknown[][] = [];
  console.warn = (...args: unknown[]) => { warnings.push(args); };
  try {
    await render(false);
    assert.equal(current.phase, null);
    assert.equal(operations.length, 0, "测试包/禁用时不检查");

    await render(true);
    assert.equal(operations.length, 0, "OTA 初始化门禁未放行前不得抢跑 APK");
    await act(async () => { appUpdateMutualExclusion.setOtaInitializationPending(false); });
    assert.equal(current.phase, "checking", String(warnings.flat()));
    assert.match(view(), /nativeUpdateChecking/);
    assert.equal(operations.length, 1);

    await act(async () => { operations[0].dependencies.onPhase?.("downloading"); });
    assert.equal(current.phase, "downloading");
    assert.match(view(), /nativeUpdateDownloading/);
    assert.match(view(), /业务页面始终保留/);
    assert.equal(prompts.length, 0, "下载中不弹安装框");
    await act(async () => { current.retry(); });
    assert.equal(operations.length, 1, "重复点击不能产生并行下载");

    await act(async () => { operations[0].dependencies.onPhase?.("verifying"); });
    assert.match(view(), /nativeUpdateVerifying/);
    await act(async () => { operations[0].reject(new Error("network interrupted")); });
    assert.equal(current.phase, "failed");
    assert.match(view(), /nativeUpdateFailedHelper/);
    assert.equal(root.container.queryAll((node) => node.type === "ActivityIndicator").length, 0);
    assert.equal(prompts.length, 0, "失败不得提示安装");
    const buttons = root.container.queryAll((node) => node.type === "Button");
    await act(async () => { buttons[0].props.onPress(); });
    assert.equal(current.phase, null, "关闭提示不影响业务页面");
    await act(async () => { buttons[1].props.onPress(); });
    assert.equal(operations.length, 2);
    assert.equal(current.phase, "checking");

    await act(async () => { operations[1].resolve({ status: "not-available" }); });
    assert.equal(current.phase, null, "无新版时清除状态条");
    assert.doesNotMatch(view(), /nativeUpdate/);

    await act(async () => { current.retry(); });
    await act(async () => {
      operations[2].resolve({
        status: "downloaded", verification: "js", fileUri: "file:///cache/test.apk",
        build: { easBuildId: "new-build", appVersion: "1.0.9", appBuildVersion: "61", artifactUrl: "", artifactSha256: "", artifactSize: 1, buildProfile: "production" },
      });
    });
    assert.equal(current.phase, null, "完成后移除状态条并保留原有安装提示");
    assert.equal(prompts.length, 1);
    appUpdateMutualExclusion.releasePrompt("native");

    appUpdateMutualExclusion.setOtaRequiredGate(true);
    await act(async () => { current.retry(); });
    assert.equal(operations.length, 3, "重试不能绕过强制 OTA 门禁");
    await render(false);
    appUpdateMutualExclusion.setOtaRequiredGate(false);
    assert.equal(current.phase, null);
    platform.OS = "ios";
    await render(true);
    assert.equal(current.phase, null, "iOS 不出现 APK 状态条");
    assert.equal(operations.length, 3);
  } finally {
    await act(async () => { root.unmount(); });
    console.warn = originalWarn;
  }
  console.log("use-automatic-native-app-update.test.ts: ok");
}

void run();
