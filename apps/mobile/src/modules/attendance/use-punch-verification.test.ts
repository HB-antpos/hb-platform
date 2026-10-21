import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import test from "node:test";
import ts from "typescript";

type Deferred<T> = {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (error: unknown) => void;
};

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve;
    reject = promiseReject;
  });
  return { promise, resolve, reject };
}

async function settle() {
  // 让 getStoredApiHost、health、watch 注册及 React 替身的同步重渲染全部推进。
  for (let index = 0; index < 8; index += 1) await Promise.resolve();
}

const moduleDirectory = dirname(fileURLToPath(import.meta.url));
const hookPath = resolve(moduleDirectory, "use-punch-verification.ts");
const capturePath = resolve(moduleDirectory, "attendance-location-capture.ts");

const health = {
  calls: 0,
  request: () => Promise.resolve<unknown>({ ok: true }),
};

const location = {
  watchCalls: 0,
  requestPermissionCalls: 0,
  getForegroundPermissionsAsync: () => Promise.resolve({ status: "granted" }),
  requestForegroundPermissionsAsync: () => {
    location.requestPermissionCalls += 1;
    return Promise.resolve({ status: "granted" });
  },
  watchPositionAsync: async (
    _options: unknown,
    onPosition: (position: unknown) => void,
    _onError: (reason: unknown) => void,
  ) => {
    location.watchCalls += 1;
    onPosition({
      timestamp: Date.now(),
      coords: { latitude: -27.47, longitude: 153.02, accuracy: 8 },
    });
    return { remove() {} };
  },
  Accuracy: { High: "high" },
};

const config = {
  getStoredApiHost: async () => "https://api.example.test/api",
  buildApiBaseUrl: (host: string) => host,
};

class TestRequiredLocationError extends Error {
  code = "LOCATION_REQUIRED";
}

const requiredLocation = {
  RequiredLocationError: TestRequiredLocationError,
  isRequiredLocationError: (error: unknown) => error instanceof TestRequiredLocationError,
  collectRequiredLocation: async () => ({
    locationLatitude: -27.47,
    locationLongitude: 153.02,
    locationAccuracy: 8,
    locationPermissionStatus: "granted" as const,
    locationCapturedAtUtc: new Date().toISOString(),
  }),
};

const session = { isIosReviewSessionActive: () => false };
const reviewNetwork = {
  reviewAwareFetch: (..._args: unknown[]) => {
    health.calls += 1;
    return health.request();
  },
};

type HookRuntime = {
  current: ReturnType<() => Record<string, any>>;
  render: () => void;
  blur: () => void;
  unmount: () => void;
  stateUpdateCount: (index: number) => number;
};

type HookModule = {
  usePunchVerification: () => Record<string, any>;
};

let activeRuntime: HookRuntimeImpl | undefined;

class HookRuntimeImpl implements HookRuntime {
  current = {} as Record<string, any>;
  private hookIndex = 0;
  private readonly hooks: any[] = [];
  private focusCleanup: (() => void) | undefined;
  private mounted = true;

  constructor(private readonly hook: HookModule) {}

  render() {
    if (!this.mounted) return;
    this.hookIndex = 0;
    activeRuntime = this;
    this.current = this.hook.usePunchVerification();
    activeRuntime = undefined;
  }

  useState<T>(initial: T): [T, (next: T | ((value: T) => T)) => void] {
    const index = this.hookIndex++;
    const hook = this.hooks[index] ?? { value: initial, updates: 0 };
    this.hooks[index] = hook;
    return [hook.value, (next) => {
      const value = typeof next === "function"
        ? (next as (previous: T) => T)(hook.value)
        : next;
      if (Object.is(value, hook.value)) return;
      hook.value = value;
      hook.updates += 1;
      this.render();
    }];
  }

  useRef<T>(initial: T) {
    const index = this.hookIndex++;
    this.hooks[index] ??= { current: initial };
    return this.hooks[index] as { current: T };
  }

  useCallback<T extends (...args: any[]) => any>(callback: T, dependencies: unknown[]) {
    const index = this.hookIndex++;
    const previous = this.hooks[index];
    if (previous && dependencies.length === previous.dependencies.length
      && dependencies.every((value, dependencyIndex) => Object.is(value, previous.dependencies[dependencyIndex]))) {
      return previous.callback as T;
    }
    this.hooks[index] = { callback, dependencies };
    return callback;
  }

  useFocusEffect(effect: () => void | (() => void)) {
    const index = this.hookIndex++;
    const previous = this.hooks[index];
    if (previous) return;
    this.hooks[index] = { effect };
    this.focusCleanup = effect() ?? undefined;
  }

  blur() {
    this.focusCleanup?.();
    this.focusCleanup = undefined;
  }

  unmount() {
    if (!this.mounted) return;
    this.mounted = false;
    this.blur();
  }

  stateUpdateCount(index: number) {
    return this.hooks[index]?.updates ?? 0;
  }
}

const react = {
  useState<T>(initial: T) {
    if (!activeRuntime) throw new Error("React hook called outside test render");
    return activeRuntime.useState(initial);
  },
  useRef<T>(initial: T) {
    if (!activeRuntime) throw new Error("React hook called outside test render");
    return activeRuntime.useRef(initial);
  },
  useCallback<T extends (...args: any[]) => any>(callback: T, dependencies: unknown[]) {
    if (!activeRuntime) throw new Error("React hook called outside test render");
    return activeRuntime.useCallback(callback, dependencies);
  },
};

const navigation = {
  useFocusEffect(effect: () => void | (() => void)) {
    if (!activeRuntime) throw new Error("Focus effect called outside test render");
    activeRuntime.useFocusEffect(effect);
  },
};

const stubs: Record<string, unknown> = {
  react,
  "expo-location": location,
  "@react-navigation/native": navigation,
  "@/shared/api/config": config,
  // 传输入口选择在这里直通：本测试只关心打卡校验的状态流转，入口切换另有 transport-failover 的专门测试。
  "@/shared/api/transport": {
    probeWithTransportFallback: async (host: string, attempt: (apiBaseUrl: string) => Promise<unknown>) =>
      attempt(config.buildApiBaseUrl(host)),
  },
  "@/modules/attendance/types": {},
  "@/modules/attendance/required-location": requiredLocation,
  "@/modules/ios-review/session": session,
  "@/modules/ios-review/network": reviewNetwork,
};

const moduleCache = new Map<string, any>();

function evaluateModule(filename: string, source: string): any {
  const cached = moduleCache.get(filename);
  if (cached) return cached.exports;
  const output = ts.transpileModule(source, {
    compilerOptions: {
      target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.CommonJS,
      esModuleInterop: true,
    },
    fileName: filename,
  }).outputText;
  const module = { exports: {} as Record<string, unknown> };
  moduleCache.set(filename, module);
  const requireModule = (specifier: string) => {
    const stub = stubs[specifier];
    if (stub) return stub;
    if (specifier === "./attendance-location-capture") return loadModule(capturePath);
    throw new Error(`Unexpected dependency in hook test: ${specifier}`);
  };
  const context = {
    ...globalThis,
    AbortController,
    Date,
    setTimeout,
    clearTimeout,
    module,
    exports: module.exports,
    require: requireModule,
    __filename: filename,
    __dirname: dirname(filename),
  };
  vm.runInNewContext(output, context, { filename });
  return module.exports;
}

function loadModule(filename: string): any {
  const cached = moduleCache.get(filename);
  if (cached) return cached.exports;
  return evaluateModule(filename, readFileSync(filename, "utf8"));
}

const hookModule = loadModule(hookPath) as HookModule;

function createRuntime() {
  const runtime = new HookRuntimeImpl(hookModule);
  runtime.render();
  return runtime;
}

function resetFakes() {
  health.calls = 0;
  health.request = () => Promise.resolve<unknown>({ ok: true });
  location.watchCalls = 0;
  location.requestPermissionCalls = 0;
  location.getForegroundPermissionsAsync = () => Promise.resolve({ status: "granted" });
  location.requestForegroundPermissionsAsync = () => {
    location.requestPermissionCalls += 1;
    return Promise.resolve({ status: "granted" });
  };
}

test("旧 health 晚回不能覆盖已成功的扫码 refresh", async () => {
  resetFakes();
  const oldHealth = deferred<unknown>();
  health.request = () => oldHealth.promise;
  const runtime = createRuntime();
  await settle();
  assert.equal(health.calls, 1, "页面 focus 必须确实发起 health 请求");
  runtime.current.prewarmLocation();
  await settle();

  const refreshed = await runtime.current.refreshVerification({ networkVerified: true });
  assert.equal(refreshed.network.status, "available");
  assert.equal(runtime.current.verification.network.status, "available");
  const updatesAfterRefresh = runtime.stateUpdateCount(0);

  oldHealth.reject(new Error("offline"));
  await settle();
  assert.equal(runtime.current.verification.network.status, "available");
  assert.equal(runtime.stateUpdateCount(0), updatesAfterRefresh,
    "迟到 health 不应再次写入 verification");
  runtime.unmount();
});

test("页面 blur 后预热权限迟到不会启动定位 watch", async () => {
  resetFakes();
  const permission = deferred<{ status: string }>();
  location.getForegroundPermissionsAsync = () => permission.promise;
  const runtime = createRuntime();
  await settle();

  runtime.current.prewarmLocation();
  await settle();
  runtime.blur();
  permission.resolve({ status: "granted" });
  await settle();

  assert.equal(location.watchCalls, 0, "blur 后迟到权限不能注册 watch");
  assert.equal(location.requestPermissionCalls, 0);
  runtime.unmount();
});

test("页面 blur 后进行中的 refresh 结果不能回写旧 verification", async () => {
  resetFakes();
  const runtime = createRuntime();
  await settle();
  runtime.current.prewarmLocation();
  await settle();
  await runtime.current.refreshVerification({ networkVerified: true });
  assert.equal(runtime.current.verification.location.status, "available");
  assert.equal(runtime.current.verification.network.status, "available");
  const updatesBeforeRefresh = runtime.stateUpdateCount(0);
  const watchCallsBeforeRefresh = location.watchCalls;

  const permission = deferred<{ status: string }>();
  location.getForegroundPermissionsAsync = () => permission.promise;

  const refresh = runtime.current.refreshVerification();
  await settle();
  runtime.blur();
  permission.resolve({ status: "granted" });
  const staleResult = await refresh;
  await settle();

  assert.equal(staleResult.location.status, "unavailable");
  assert.equal(runtime.stateUpdateCount(0), updatesBeforeRefresh,
    "失焦后的 refresh 结果不能覆盖页面上已有状态");
  assert.equal(runtime.current.verification.location.status, "available");
  assert.equal(runtime.current.verification.network.status, "available");
  assert.equal(location.watchCalls, watchCallsBeforeRefresh,
    "失焦后的 refresh 不能重新启动定位 watch");
  runtime.unmount();
});
