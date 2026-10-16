import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";
import { AxiosError } from "axios";

// 管理员版人员详情「允许登录」开关的整页集成渲染测试：真实页面 + 真实规则 / 查询 / mutation，
// 只把 react-native / expo-router / 登录态 / 网络（apiClient）/ 密码与个人码弹层换成桩。
// 钉住：开关形态（可操作 / 本人置灰 / 只读 / 隐藏）、关闭先确认且红色危险按钮、开启直接提交、
// 失败按 errorCode 给固定文案（不显示服务端原文、日志脱敏）、开启后「设置密码」提示、头部「不能登录」标签随真实状态变化。
const bundles = new Map<string, Record<string, unknown>>();
function bundle(namespace: string) {
  if (!bundles.has(namespace)) {
    const file = namespace === "common" ? "../../../locales/zh/common.json" : `../../../locales/zh/screens/${namespace}.json`;
    bundles.set(namespace, JSON.parse(readFileSync(resolve(__dirname, file), "utf8")) as Record<string, unknown>);
  }
  return bundles.get(namespace) as Record<string, unknown>;
}

function resolveKey(namespace: string, path: string): unknown {
  return path
    .split(".")
    .reduce<unknown>(
      (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
      bundle(namespace),
    );
}

/** 简化版 t：页面默认命名空间是 userManagement，带 "ns:" 前缀的取对应命名空间；缺键直接返回键本身。 */
function lookup(key: string, options?: Record<string, unknown>): string {
  const [namespace, path] = key.includes(":") ? (key.split(":") as [string, string]) : (["userManagement", key] as const);
  const value = resolveKey(namespace, path);
  if (typeof value !== "string") return key;
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}
const detailText = (path: string, options?: Record<string, unknown>) => lookup(`peopleDetail:${path}`, options);

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  const rendererReactFilename = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReactFilename !== require.resolve("react")) mockModule(rendererReactFilename, React);

  const host = (type: string) => {
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props);
    Stub.displayName = type;
    return Stub;
  };
  const passThrough = ({ children }: { children?: React.ReactNode }) => React.createElement(React.Fragment, null, children);
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Text: host("NativeText"),
    Pressable: host("Pressable"),
    ScrollView: host("ScrollView"),
    TextInput: host("TextInput"),
    KeyboardAvoidingView: host("KeyboardAvoidingView"),
    ActivityIndicator: host("ActivityIndicator"),
    Keyboard: { addListener: () => ({ remove: () => undefined }) },
    Alert: { alert: () => undefined },
    useWindowDimensions: () => ({ width: 390, height: 844 }),
    AppState: { addEventListener: () => ({ remove: () => undefined }) },
    NativeModules: {},
    Platform: { OS: "ios", select: <T>(values: { ios?: T; default?: T }) => values.ios ?? values.default },
  });
  const SnackbarStub = (props: Record<string, unknown>) => (props.visible ? React.createElement("Snackbar", props) : null);
  mockModule("react-native-paper", {
    Text: host("Text"),
    Icon: host("Icon"),
    Button: host("Button"),
    Switch: host("Switch"),
    IconButton: host("IconButton"),
    ActivityIndicator: host("ActivityIndicator"),
    Portal: passThrough,
    Modal: host("Modal"),
    Dialog: Object.assign(host("Dialog"), { Title: host("DialogTitle"), Content: host("DialogContent"), Actions: host("DialogActions") }),
    Snackbar: SnackbarStub,
  });
  mockModule("react-native-safe-area-context", {
    SafeAreaView: host("SafeAreaView"),
    useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
  });
  mockModule("@react-navigation/native", { usePreventRemove: () => undefined });
  mockModule("expo-clipboard", { setStringAsync: async () => true });
  mockModule("expo-secure-store", {
    getItemAsync: async () => null,
    setItemAsync: async () => undefined,
    deleteItemAsync: async () => undefined,
  });
  mockModule("expo-location", {
    hasStartedLocationUpdatesAsync: async () => false,
    stopLocationUpdatesAsync: async () => undefined,
  });
  mockModule("@/shared/logging/log-center-runtime", { reportApplicationLog: () => undefined });
  mockModule("@react-native-async-storage/async-storage", {
    default: { getItem: async () => null, setItem: async () => undefined, removeItem: async () => undefined },
  });
  mockModule("expo-router", {
    useRouter: () => ({ push: () => undefined, replace: () => undefined, back: () => undefined, canGoBack: () => true }),
    useNavigation: () => ({ dispatch: () => undefined }),
    router: { replace: () => undefined },
  });
  mockModule(require.resolve("../../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });
  // 密码 / 个人码 / 员工侧 mutation / 打印机状态：与本测试无关的重依赖换成桩，只记录「设置密码」弹层是否被打开。
  mockModule(require.resolve("../../users/staff-password-setup-dialog"), {
    StaffPasswordSetupDialog: (props: Record<string, unknown>) =>
      React.createElement("PasswordDialog", { visible: props.visible }),
  });
  mockModule(require.resolve("../../users/staff-barcode/StaffBarcodeDialogs"), { StaffBarcodeDialog: () => null });
  mockModule(require.resolve("../../users"), {
    useStoreUserMutations: () => ({ setupEmailMutation: { isPending: false, mutateAsync: async () => ({ maskedEmail: "k***@x.test" }) } }),
    toSafeStoreUserErrorLog: () => ({}),
  });
  mockModule(require.resolve("../../printer/state"), {
    usePrinterStore: (selector: (state: Record<string, unknown>) => unknown) => selector({ savedPrinter: null, status: "idle" }),
  });

  // 登录态：假的 zustand 形状（页面只用 selector 与 getState）。
  const { buildAccess } = await import("../../../shared/utils/access");
  const authState: Record<string, unknown> = {
    user: null,
    access: buildAccess(null),
    isAuthenticated: true,
    sessionKind: "account",
    iosReviewOfflineGuardActive: false,
  };
  mockModule(require.resolve("../../../store/auth-store"), {
    useAuthStore: Object.assign(
      (selector: (state: Record<string, unknown>) => unknown) => selector(authState),
      { getState: () => authState },
    ),
  });
  const signIn = (kind: "admin" | "viewer" | "editor", guid = "admin-1") => {
    const base = { userGuid: guid, userGUID: guid, username: "actor", email: "a@x.test", storeNames: [], stores: [] };
    const user =
      kind === "admin"
        ? { ...base, permissions: [], roleNames: ["Admin"] }
        // viewer：只有查看权限，不能改；editor：有 Users.Edit 但没有重置密码权限。
        : kind === "viewer"
          ? { ...base, permissions: ["Users.View"], roleNames: ["Warehouse"] }
          : { ...base, permissions: ["Users.View", "Users.Edit"], roleNames: ["Warehouse"] };
    authState.user = user;
    authState.access = buildAccess(user as never);
  };

  // 网络桩：按 URL 返回假数据；PUT 的结果由 server.putMode 控制。
  const { apiClient } = await import("../../../shared/api/client");
  type PutMode = "ok" | "scope" | "self" | "http500" | "businessFail";
  const server = {
    guid: "u-kevin",
    roleNames: ["StoreManager"] as string[],
    loginEnabled: false as boolean | undefined,
    lastLoginAt: undefined as string | undefined,
    putMode: "ok" as PutMode,
    puts: [] as unknown[],
  };
  const RAW_SERVER_TEXT = "RAW-SERVER-TEXT 内部异常 SELECT * FROM Users";
  const axiosFailure = (status: number, errorCode: string) =>
    new AxiosError(`Request failed with status code ${status}`, "ERR_BAD_REQUEST", undefined, undefined, {
      status,
      statusText: String(status),
      headers: {},
      config: { headers: {} } as never,
      data: { success: false, errorCode, message: RAW_SERVER_TEXT },
    });
  const stub = apiClient as unknown as {
    get: (url: string, config?: unknown) => Promise<{ data: unknown }>;
    put: (url: string, body?: unknown, config?: unknown) => Promise<{ data: unknown }>;
  };
  stub.get = async (url: string) => {
    if (url === `/Users/guid/${server.guid}`) {
      return {
        data: {
          success: true,
          data: {
            userGUID: server.guid,
            username: "kevin",
            email: "kevin@x.test",
            fullName: "Kevin Wu",
            isActive: true,
            createdAt: "",
            updatedAt: "",
            roleNames: server.roleNames,
            storeNames: [],
            roles: [],
            stores: [],
            permissions: [],
            exactPermissions: [],
            ...(server.loginEnabled === undefined ? {} : { loginEnabled: server.loginEnabled }),
            ...(server.lastLoginAt ? { lastLoginAt: server.lastLoginAt } : {}),
          },
        },
      };
    }
    // 关联分店 / 直接权限 / 登录记录：本测试不关心，让它们走错误态，页面按「查看」兜底。
    throw new Error(`未桩的 GET ${url}`);
  };
  stub.put = async (url: string, body?: unknown) => {
    if (url !== `/Users/guid/${server.guid}/login-enabled`) throw new Error(`未桩的 PUT ${url}`);
    server.puts.push(body);
    if (server.putMode === "scope") throw axiosFailure(403, "USER_SCOPE_DENIED");
    if (server.putMode === "self") throw axiosFailure(403, "SELF_PROFILE_FIELDS_DENIED");
    if (server.putMode === "http500") throw axiosFailure(500, "INTERNAL_SERVER_ERROR");
    if (server.putMode === "businessFail") {
      return { data: { success: false, errorCode: "UPDATE_USER_LOGIN_ENABLED_FAILED", message: RAW_SERVER_TEXT } };
    }
    server.loginEnabled = (body as { loginEnabled: boolean }).loginEnabled;
    return { data: { success: true, data: true, message: "ok" } };
  };

  const { createRoot } = await import("test-renderer");
  // 必须与页面用同一份 react-query（CJS）：动态 import 会拿到 ESM 版本，两份 Context 互不相认。
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { QueryClient, QueryClientProvider } = require("@tanstack/react-query") as typeof import("@tanstack/react-query");
  const { AdminPersonDetail } = await import("./admin-person-detail");
  const { HB_COLORS } = await import("../../../shared/theme/tokens");

  const warnings: unknown[][] = [];
  const originalWarn = console.warn;
  console.warn = (...args: unknown[]) => {
    warnings.push(args);
  };

  const container = (root: { container: unknown }) =>
    root.container as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  async function mount(env: { isDeviceMode: boolean } = { isDeviceMode: false }) {
    const root = createRoot();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } });
    await act(() =>
      root.render(
        React.createElement(
          QueryClientProvider,
          { client },
          React.createElement(AdminPersonDetail, {
            userGuid: server.guid,
            prefill: { roleNames: [] },
            env: { ...env, stores: [], manageableStores: [], posEnabledStores: [] },
          }),
        ),
      ),
    );
    const api = {
      all: (type: string) => container(root).queryAll((node) => node.type === type),
      byTestId: (type: string, testID: string) => api.all(type).find((node) => node.props.testID === testID),
      texts: () => api.all("Text").map(textOf),
      loginSwitch: () => api.all("Switch").find((node) => node.props.accessibilityLabel === detailText("loginToggle.label")),
      snackbar: () => {
        const node = api.all("Snackbar")[0];
        return node ? node.children.map(textOf).join("") : null;
      },
      confirmDialog: () => api.byTestId("Dialog", "person-login-confirm"),
      settle: async (condition: () => boolean, label: string) => {
        for (let index = 0; index < 200; index += 1) {
          if (condition()) return;
          await act(async () => {
            await new Promise((done) => setTimeout(done, 5));
          });
        }
        throw new Error(`等待超时：${label}`);
      },
      unmount: () => act(() => root.unmount()),
    };
    await api.settle(() => api.texts().includes("Kevin Wu") || api.all("ActivityIndicator").length === 0, "详情加载完成");
    return api;
  }
  const reset = () => {
    server.guid = "u-kevin";
    server.roleNames = ["StoreManager"];
    server.loginEnabled = false;
    server.lastLoginAt = undefined;
    server.putMode = "ok";
    server.puts = [];
    warnings.length = 0;
  };
  const restrictedTag = lookup("statuses.loginDisabled");

  try {
    // ───────────── A. kevin：被限制登录的店长账号，管理员解开、再关上 ─────────────
    reset();
    signIn("admin");
    {
      const page = await mount();
      assert.ok(page.loginSwitch(), "管理员版账号与安全里有「允许登录」开关");
      assert.equal(page.loginSwitch()!.props.value, false, "当前被限制登录");
      assert.equal(page.loginSwitch()!.props.disabled, false, "管理员对他人账号可操作");
      assert.ok(page.texts().includes(detailText("loginToggle.offHint")), "关着时写明现状");
      // 头部：「不能登录」amber 标签 + 锁图标，与红色「已停用」区分。
      assert.ok(page.texts().includes(restrictedTag), "头部标出「不能登录」");
      assert.ok(page.all("Icon").some((node) => node.props.source === "lock-outline"), "标签带锁图标");
      assert.ok(!page.texts().includes(lookup("statuses.disabled")), "账号仍是启用中，不显示「已停用」");
      assert.equal(page.byTestId("View", "person-login-password-hint"), undefined, "还没开启时没有密码提示");

      // 开启：不弹确认框，直接提交 { loginEnabled: true }。
      await act(async () => {
        page.loginSwitch()!.props.onValueChange(true);
      });
      assert.equal(page.confirmDialog(), undefined, "开启不需要确认");
      await page.settle(() => page.loginSwitch()?.props.value === true && page.snackbar() !== null, "开启完成并刷新");
      assert.deepEqual(server.puts, [{ loginEnabled: true }], "只提交 loginEnabled 布尔");
      assert.equal(page.snackbar(), detailText("loginToggle.enabledDone"));
      assert.ok(!page.texts().includes(restrictedTag), "刷新后头部「不能登录」标签消失");
      // 从没登录过：卡内提示 + 「设置密码」按钮，点击打开页面已有的重置密码弹层。
      assert.ok(page.byTestId("View", "person-login-password-hint"), "从没登录过的账号开启后给出密码提示");
      assert.ok(page.texts().some((text) => text.includes("该账号还没登录过，可能没有可用密码。")));
      assert.equal(page.all("PasswordDialog")[0]?.props.visible, false, "此时重置密码弹层未打开");
      await act(async () => {
        page.byTestId("Button", "person-login-set-password")!.props.onPress();
      });
      assert.equal(page.all("PasswordDialog")[0]?.props.visible, true, "「设置密码」打开已有的重置密码弹层");

      // 关闭：先弹居中确认框（取消在左、红色危险按钮在右），确认前不提交。
      await act(async () => {
        page.loginSwitch()!.props.onValueChange(false);
      });
      const dialog = page.confirmDialog();
      assert.ok(dialog, "关闭登录先弹确认框");
      assert.equal(server.puts.length, 1, "确认前不发请求");
      assert.equal(textOf(dialog!.children.find((child) => typeof child !== "string" && child.type === "DialogTitle")!), "关闭 Kevin Wu 的登录权限？");
      assert.ok(textOf(dialog!).includes("关闭后该账号将无法登录，已登录的会话也会失效。"), "写明后果");
      const buttons = page.all("Button").filter((node) => ["取消", "关闭登录"].includes(textOf(node)));
      assert.deepEqual(buttons.map(textOf), ["取消", "关闭登录"], "取消在左，确认在右");
      assert.equal(buttons[1].props.buttonColor, HB_COLORS.danger, "确认按钮是红色危险按钮");
      assert.equal(buttons[1].props.mode, "contained");
      // 取消：不提交，开关仍是开。
      await act(async () => {
        buttons[0].props.onPress();
      });
      assert.equal(page.confirmDialog(), undefined);
      assert.equal(server.puts.length, 1, "取消不提交");
      assert.equal(page.loginSwitch()!.props.value, true);
      // 再关一次并确认：提交 { loginEnabled: false }，刷新后回到「不能登录」。
      await act(async () => {
        page.loginSwitch()!.props.onValueChange(false);
      });
      await act(async () => {
        page.all("Button").find((node) => textOf(node) === "关闭登录")!.props.onPress();
      });
      await page.settle(() => page.loginSwitch()?.props.value === false && page.confirmDialog() === undefined, "关闭完成并刷新");
      assert.deepEqual(server.puts, [{ loginEnabled: true }, { loginEnabled: false }]);
      assert.equal(page.snackbar(), detailText("loginToggle.disabledDone"));
      assert.ok(page.texts().includes(restrictedTag), "关闭后头部重新标出「不能登录」");
      assert.equal(page.byTestId("View", "person-login-password-hint"), undefined, "已再次关闭，密码提示收起");
      await page.unmount();
    }

    // ───────────── B. 失败：按 errorCode 给固定文案，不显示服务端原文，日志脱敏 ─────────────
    const failureCases: { mode: PutMode; next: boolean; expected: string }[] = [
      { mode: "scope", next: true, expected: detailText("loginToggle.errors.scopeDenied") },
      { mode: "self", next: false, expected: detailText("loginToggle.errors.self") },
      { mode: "http500", next: true, expected: detailText("loginToggle.errors.uncertain") },
      { mode: "businessFail", next: true, expected: detailText("loginToggle.errors.generic") },
    ];
    for (const failure of failureCases) {
      reset();
      signIn("admin");
      server.loginEnabled = !failure.next;
      server.putMode = failure.mode;
      const page = await mount();
      await act(async () => {
        page.loginSwitch()!.props.onValueChange(failure.next);
      });
      if (!failure.next) {
        await act(async () => {
          page.all("Button").find((node) => textOf(node) === "关闭登录")!.props.onPress();
        });
      }
      await page.settle(() => page.snackbar() !== null, `${failure.mode} 失败提示`);
      assert.equal(page.snackbar(), failure.expected, `${failure.mode}：提示固定文案`);
      assert.ok(!(page.snackbar() ?? "").includes("RAW-SERVER-TEXT"), "不得显示服务端原文");
      assert.equal(page.confirmDialog(), undefined, "失败后确认框已关闭，提示不被盖住");
      assert.equal(page.loginSwitch()!.props.value, !failure.next, "失败后开关保持服务端真实状态");
      assert.equal(page.loginSwitch()!.props.disabled, false, "失败后可以再试");
      assert.equal(page.byTestId("View", "person-login-password-hint"), undefined, "失败不给密码提示");
      const logged = warnings.filter((args) => String(args[0]).includes("login enabled failed"));
      assert.equal(logged.length, 1, "失败记一条日志");
      const logText = JSON.stringify(logged[0]);
      assert.ok(!logText.includes("RAW-SERVER-TEXT"), "日志不含服务端原文");
      assert.ok(!logText.includes(server.guid), "日志不含账号 GUID");
      assert.ok(logged[0].length === 2 && Object.keys(logged[0][1] as object).every((key) => ["status", "code"].includes(key)), "日志只有状态码 / 错误码");
      await page.unmount();
    }

    // ───────────── C. 本人：开关置灰并说明 ─────────────
    reset();
    signIn("admin", "u-kevin");
    server.loginEnabled = true;
    {
      const page = await mount();
      assert.ok(page.loginSwitch(), "本人详情仍显示开关");
      assert.equal(page.loginSwitch()!.props.disabled, true, "本人开关置灰");
      assert.equal(page.loginSwitch()!.props.value, true);
      assert.ok(page.texts().includes("不能关闭本人账号的登录权限"), "写明原因");
      assert.equal(server.puts.length, 0);
      await page.unmount();
    }

    // ───────────── D. 没有 Users.Edit：只读显示，没有可点的开关 ─────────────
    reset();
    signIn("viewer");
    server.roleNames = ["StoreStaff"];
    {
      const page = await mount();
      assert.equal(page.loginSwitch(), undefined, "没有编辑权限不渲染开关");
      assert.equal(textOf(page.byTestId("Text", "person-login-readonly")!), "不允许", "只读显示当前值");
      assert.ok(page.texts().includes(lookup("statuses.loginDisabled")), "头部仍标出「不能登录」");
      await page.unmount();
    }

    // ───────────── E. 接口没返回 loginEnabled（旧后端）：既不标也不显示开关 ─────────────
    reset();
    signIn("admin");
    server.loginEnabled = undefined;
    {
      const page = await mount();
      assert.equal(page.loginSwitch(), undefined, "未知状态不显示开关");
      assert.equal(page.byTestId("Text", "person-login-readonly"), undefined, "也没有只读行");
      assert.ok(!page.texts().includes(restrictedTag), "未知不等于不能登录，不标");
      await page.unmount();
    }

    // ───────────── F. 设备模式：不出现开关 ─────────────
    reset();
    signIn("admin");
    {
      const page = await mount({ isDeviceMode: true });
      assert.equal(page.loginSwitch(), undefined, "设备模式不出现开关");
      assert.equal(page.byTestId("Text", "person-login-readonly"), undefined);
      await page.unmount();
    }

    // ───────────── G. 有编辑权限但没有重置密码权限：开启后只给提示，不给「设置密码」按钮 ─────────────
    reset();
    signIn("editor");
    server.roleNames = ["StoreStaff"];
    {
      const page = await mount();
      assert.equal(page.loginSwitch()?.props.disabled, false, "持有 Users.Edit 的账号可操作");
      await act(async () => {
        page.loginSwitch()!.props.onValueChange(true);
      });
      await page.settle(() => page.loginSwitch()?.props.value === true && page.snackbar() !== null, "开启完成");
      assert.ok(page.byTestId("View", "person-login-password-hint"), "仍给出提示");
      assert.equal(page.byTestId("Button", "person-login-set-password"), undefined, "没有重置密码权限不给入口");
      await page.unmount();
    }

    // ───────────── H. 登录过的账号开启后不提示设置密码 ─────────────
    reset();
    signIn("admin");
    server.lastLoginAt = "2026-10-01T00:00:00Z";
    {
      const page = await mount();
      await act(async () => {
        page.loginSwitch()!.props.onValueChange(true);
      });
      await page.settle(() => page.loginSwitch()?.props.value === true && page.snackbar() !== null, "开启完成");
      assert.equal(page.byTestId("View", "person-login-password-hint"), undefined, "登录过的账号不提示");
      await page.unmount();
    }
  } finally {
    console.warn = originalWarn;
  }

  console.log("admin-login-toggle-render.test.ts: ok");
}

void run();
