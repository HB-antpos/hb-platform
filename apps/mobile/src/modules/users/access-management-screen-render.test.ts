import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 统一授权页的整页集成渲染测试：真实页面 + 真实编辑器 / 查询 / 保存编排，
// 只把 react-native / expo-router / 登录态 / 网络（apiClient）换成桩。
// 钉住：目标用户自取（深链无参数也要做受保护校验）、分段与计数、吸底保存栏的改动汇总、
// 跨分段保存的顺序、部分失败的明确提示与草稿保留、店长收窄、收银权限不在直接权限里、section / copy 参数。
const zh = JSON.parse(
  readFileSync(resolve(__dirname, "../../locales/zh/screens/peopleAccess.json"), "utf8"),
) as Record<string, unknown>;

function resolveKey(key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
      zh,
    );
}

function lookup(key: string, options?: Record<string, unknown>): string {
  if (key.startsWith("common:")) return key;
  let value = resolveKey(key);
  if (options && typeof options.count === "number") {
    const plural = resolveKey(`${key}_other`);
    if (typeof plural === "string") value = plural;
  }
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

const STORES = [
  { StoreGUID: "g-bankstown", StoreCode: "1008", StoreName: "Bankstown", IsActive: true },
  { StoreGUID: "g-liverpool", StoreCode: "1012", StoreName: "Liverpool", IsActive: true },
  { StoreGUID: "g-parramatta", StoreCode: "1003", StoreName: "Parramatta", IsActive: true },
  { StoreGUID: "g-campbelltown", StoreCode: "1015", StoreName: "Campbelltown", IsActive: true },
  { StoreGUID: "g-warehouse", StoreCode: "WH01", StoreName: "仓库", IsActive: false },
];
const ROLES = [
  { RoleGUID: "r-manager", RoleName: "StoreManager", Description: "店长" },
  { RoleGUID: "r-staff", RoleName: "StoreStaff", Description: "收银、商品查询、价格和标签" },
  { RoleGUID: "r-finance", RoleName: "Finance", Description: "报表、现金管理、日结" },
  { RoleGUID: "r-admin", RoleName: "Admin", Description: "全部功能（高权限）" },
];
const perm = (name: string, displayName: string, category: string) => ({
  Name: name,
  DisplayName: displayName,
  Category: category,
  IsSystemPermission: false,
});
const CATEGORIES = [
  {
    Category: "报表",
    DisplayName: "报表",
    Permissions: [
      perm("Reports.Revenue.View", "营业额报表", "报表"),
      perm("Cash.Manage", "现金管理", "报表"),
      perm("Reports.Hours.View", "员工工时报表", "报表"),
    ],
  },
  { Category: "员工", DisplayName: "员工", Permissions: [perm("Staff.ReviewProfile", "员工资料审核", "员工")] },
  {
    Category: "收银",
    DisplayName: "收银",
    Permissions: [perm("PosTerminal.Sales.AddItem", "收银加商品", "收银")],
  },
];

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
  const alerts: { title: string; message: string }[] = [];
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
    ScrollView: host("ScrollView"),
    ActivityIndicator: host("ActivityIndicator"),
    Alert: {
      alert: (title: string, message?: string) => alerts.push({ title, message: message ?? "" }),
    },
    useWindowDimensions: () => ({ width: 390, height: 844 }),
    AppState: { addEventListener: () => ({ remove: () => undefined }) },
    NativeModules: {},
    Platform: { OS: "ios", select: <T>(values: { ios?: T; default?: T }) => values.ios ?? values.default },
  });
  const SnackbarStub = (props: Record<string, unknown>) =>
    props.visible ? React.createElement("Snackbar", props) : null;
  mockModule("react-native-paper", {
    Text: host("Text"),
    Icon: host("Icon"),
    Button: host("Button"),
    Switch: host("Switch"),
    Searchbar: host("Searchbar"),
    IconButton: host("IconButton"),
    ActivityIndicator: host("ActivityIndicator"),
    Snackbar: SnackbarStub,
  });
  mockModule("react-native-safe-area-context", { SafeAreaView: host("SafeAreaView") });
  mockModule("@react-navigation/native", { usePreventRemove: () => undefined });
  mockModule("expo-secure-store", {
    getItemAsync: async () => null,
    setItemAsync: async () => undefined,
    deleteItemAsync: async () => undefined,
  });
  mockModule("expo-location", {
    hasStartedLocationUpdatesAsync: async () => false,
    stopLocationUpdatesAsync: async () => undefined,
  });
  mockModule("@react-native-async-storage/async-storage", {
    default: { getItem: async () => null, setItem: async () => undefined, removeItem: async () => undefined },
  });
  const routerCalls: unknown[] = [];
  const routeParams: Record<string, string> = {};
  const StackStub = Object.assign(host("Stack"), { Screen: host("StackScreen") });
  mockModule("expo-router", {
    Stack: StackStub,
    useLocalSearchParams: () => routeParams,
    useNavigation: () => ({ dispatch: () => undefined }),
    useRouter: () => ({
      push: (value: unknown) => routerCalls.push(value),
      replace: (value: unknown) => routerCalls.push({ replace: value }),
      back: () => routerCalls.push("back"),
      canGoBack: () => true,
    }),
    router: { replace: () => undefined },
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });
  mockModule(require.resolve("./pos-permission-sheet"), {
    POS_PERMISSION_COLORS: { brandSoft: "#e", infoSoft: "#e" },
    PosPermissionSheet: (props: Record<string, unknown>) =>
      React.createElement(
        "Sheet",
        { title: props.title, subtitle: props.subtitle },
        props.header as React.ReactNode,
        props.children as React.ReactNode,
        props.footer as React.ReactNode,
      ),
  });

  // 登录态：假的 zustand 形状（页面只用 selector 与 getState）。
  const { buildAccess } = await import("../../shared/utils/access");
  const authState: Record<string, unknown> = {
    user: null,
    access: buildAccess(null),
    isAuthenticated: true,
    sessionKind: "account",
    iosReviewOfflineGuardActive: false,
  };
  mockModule(require.resolve("../../store/auth-store"), {
    useAuthStore: Object.assign(
      (selector: (state: Record<string, unknown>) => unknown) => selector(authState),
      { getState: () => authState },
    ),
  });
  const signIn = (kind: "admin" | "manager") => {
    const user =
      kind === "admin"
        ? { userGuid: "admin-1", userGUID: "admin-1", username: "admin", email: "a@x.test", permissions: [], roleNames: ["Admin"], storeNames: [], stores: [] }
        : {
            userGuid: "mgr-1",
            userGUID: "mgr-1",
            username: "mgr",
            email: "m@x.test",
            permissions: ["Users.ViewStoreStaff", "Users.ManageStores", "Users.ManageRoles", "Users.ManagePosTerminalPermissions"],
            roleNames: ["StoreManager"],
            storeNames: ["Bankstown", "Liverpool"],
            stores: [
              { storeGUID: "g-bankstown", storeCode: "1008", storeName: "Bankstown", isPrimary: true, isActive: true },
              { storeGUID: "g-liverpool", storeCode: "1012", storeName: "Liverpool", isPrimary: true, isActive: true },
            ],
          };
    authState.user = user;
    authState.access = buildAccess(user as never);
  };

  // 网络桩：按 URL 返回假数据；记录写入请求的先后顺序。
  const { apiClient } = await import("../../shared/api/client");
  const posts: string[] = [];
  const server = {
    failPost: "" as string,
    storeUserNotFound: false,
    targetRoles: ["StoreStaff"] as string[],
    assignedStores: ["g-bankstown", "g-liverpool"],
    assignedRoles: [ROLES[1]] as typeof ROLES,
    direct: ["Cash.Manage"],
  };
  const serverError = () => Object.assign(new Error("Server Error"), { response: { status: 500 } });
  const stub = apiClient as unknown as {
    get: (url: string, config?: unknown) => Promise<{ data: unknown }>;
    post: (url: string, body?: unknown, config?: unknown) => Promise<{ data: unknown }>;
  };
  stub.get = async (url: string) => {
    if (/\/Users\/guid\/[^/]+$/.test(url)) {
      return {
        data: {
          userGUID: "u-emily", username: "emily.chen", email: "emily@x.test", fullName: "Emily Rose Chen",
          givenName: "Emily", middleName: "Rose", familyName: "Chen", nickname: "Em", isActive: true,
          createdAt: "", updatedAt: "", roleNames: server.targetRoles, storeNames: [], roles: [], stores: [], permissions: [], exactPermissions: [],
        },
      };
    }
    if (/store-users\/[^/]+$/.test(url)) {
      if (server.storeUserNotFound) {
        throw Object.assign(new Error("未找到可管理的店员账号"), { apiBusinessError: true, code: "NOT_FOUND" });
      }
      return { data: { userGUID: "u-wang", username: "wang.fang", fullName: "王芳", givenName: "芳", familyName: "王", status: 1, roleNames: ["StoreStaff"] } };
    }
    if (url.endsWith("/stores") && url.includes("/Users/guid/")) {
      return { data: STORES.filter((store) => server.assignedStores.includes(store.StoreGUID)).map((store) => ({ ...store, IsPrimary: false })) };
    }
    if (url.endsWith("/roles") && url.includes("/Users/guid/")) return { data: server.assignedRoles };
    if (url === "/Roles/active") return { data: ROLES };
    if (url === "/stores/all-by-name") {
      return { data: STORES.map((s) => ({ storeGUID: s.StoreGUID, storeCode: s.StoreCode, storeName: s.StoreName, isActive: s.IsActive })) };
    }
    if (url.endsWith("/access-permissions")) {
      return {
        data: {
          state: {
            UserGuid: "u-emily", IsSuperAdmin: false, ImplicitAllPermissions: false,
            InheritedPermissionCodes: ["Reports.Revenue.View", "PosTerminal.Sales.AddItem"],
            DirectPermissionCodes: server.direct, EffectivePermissionCodes: [],
            InheritedSources: [{ RoleName: "StoreStaff", PermissionCodes: ["Reports.Revenue.View"] }],
          },
          categories: CATEGORIES,
        },
      };
    }
    throw new Error(`未桩的 GET ${url}`);
  };
  stub.post = async (url: string, body?: unknown) => {
    if (url.endsWith("/react/v1/store-users/grid")) {
      return { data: { items: [{ userGUID: "u-jack", username: "jack", fullName: "Jack Nguyen", status: 1, roleNames: ["StoreStaff"] }], total: 1 } };
    }
    const kind = url.endsWith("/stores") ? "stores" : url.endsWith("/roles") ? "roles" : url.endsWith("/permissions") ? "permissions" : "";
    if (!kind) throw new Error(`未桩的 POST ${url}`);
    posts.push(kind);
    if (server.failPost === kind) throw serverError();
    if (kind === "stores") server.assignedStores = (body as { StoreGUID: string }[]).map((item) => item.StoreGUID);
    if (kind === "permissions") server.direct = (body as { permissions: string[] }).permissions;
    return { data: true };
  };

  const { createRoot } = await import("test-renderer");
  // 必须与页面用同一份 react-query（CJS）：动态 import 会拿到 ESM 版本，两份 Context 互不相认。
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { QueryClient, QueryClientProvider } = require("@tanstack/react-query") as typeof import("@tanstack/react-query");
  const { default: AccessManagementScreen } = await import("./access-management-screen");

  const container = (root: { container: unknown }) =>
    root.container as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  async function mount(params: Record<string, string>) {
    for (const key of Object.keys(routeParams)) delete routeParams[key];
    Object.assign(routeParams, params);
    alerts.length = 0;
    posts.length = 0;
    routerCalls.length = 0;
    const root = createRoot();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } });
    await act(() =>
      root.render(
        React.createElement(QueryClientProvider, { client }, React.createElement(AccessManagementScreen)),
      ),
    );
    const api = {
      root,
      all: (type: string) => container(root).queryAll((node) => node.type === type),
      texts: () => api.all("Text").map(textOf),
      tab: (prefix: string) =>
        api.all("Pressable").find((node) => node.props.accessibilityRole === "tab" && textOf(node).startsWith(prefix)),
      tabs: () => api.all("Pressable").filter((node) => node.props.accessibilityRole === "tab").map(textOf),
      byLabel: (type: string, label: string) =>
        api.all(type).find((node) => node.props.accessibilityLabel === label),
      saveButton: () => api.all("Button").find((node) => node.props.testID === "access-save-button"),
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
    return api;
  }
  const reset = () => {
    server.failPost = "";
    server.storeUserNotFound = false;
    server.targetRoles = ["StoreStaff"];
    server.assignedStores = ["g-bankstown", "g-liverpool"];
    server.assignedRoles = [ROLES[1]];
    server.direct = ["Cash.Manage"];
  };

  // ───────────── A. 管理员深链进入：路由参数全部缺失，页面自己拉取目标 ─────────────
  reset();
  signIn("admin");
  {
    const page = await mount({ userGuid: "u-emily" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    assert.ok(
      page.texts().some((text) => text.includes("Emily Rose Chen") && text.includes("@emily.chen")),
      "标题显示服务端拉到的姓名与账号",
    );
    assert.ok(page.texts().includes(" 「Em」"), "姓名后带昵称小字");
    // 分段计数：分店 2、角色 1、直接权限 1（收银权限不算）、管理员多一个菜单；
    // 4 个分段在 390 宽的屏幕上放不下「直接权限」，缩写成「权限」。
    assert.deepEqual(page.tabs().slice(0, 3), ["分店 · 2", "角色 · 1", "权限 · 1"]);
    assert.ok(page.tabs()[3].startsWith("菜单 · "), "管理员多一个菜单分段");
    assert.ok(page.texts().includes("无改动"));
    assert.equal(page.saveButton()!.props.disabled, true, "没改动时保存灰掉");
    // 直接权限不含收银权限：「收银加商品」不会出现；继承来的开关置灰。
    await act(() => page.tab("权限")!.props.onPress());
    assert.equal(page.byLabel("Switch", "收银加商品"), undefined, "收银权限统一去收银权限页");
    assert.equal(page.byLabel("Switch", "营业额报表")?.props.disabled, true, "继承来的权限不能在这里改");
    assert.ok(page.texts().includes(lookup("saveBar.hintPermissions")));

    // 分店：把 Liverpool 改成「未分配」→ 吸底栏写明改了什么。
    await act(() => page.tab("分店")!.props.onPress());
    await act(() => page.byLabel("Pressable", "Liverpool 未分配")!.props.onPress());
    assert.ok(page.texts().includes("1 项改动未保存"));
    assert.ok(page.texts().includes("Liverpool：查看 → 未分配"), "写明改了什么");
    assert.equal(page.saveButton()!.props.disabled, false);
    assert.equal(page.tabs()[0], "分店 · 1", "分段计数随草稿变化");
    // 角色：勾选 Finance → 2 项；店长行点了是引导，不会勾选。
    await act(() => page.tab("角色")!.props.onPress());
    await act(() =>
      page.all("Pressable").find((node) => node.props.accessibilityRole === "checkbox" && node.props.accessibilityLabel === "Finance")!.props.onPress(),
    );
    assert.ok(page.texts().includes("2 项改动未保存"));
    // 点「店长」行：只有分店已有 1 家时直接在草稿里设为「管理」并切到分店分段。
    await act(() =>
      page.all("Pressable").find((node) => node.props.accessibilityRole === "checkbox" && node.props.accessibilityLabel === "店长")!.props.onPress(),
    );
    assert.equal(page.all("Snackbar")[0]?.children.map(textOf).join(""), lookup("roles.grantManager.set", { store: "Bankstown" }));
    assert.equal(
      page.byLabel("Pressable", "Bankstown 管理")?.props.accessibilityState.selected,
      true,
      "引导后切到分店分段，Bankstown 已在草稿里设为管理",
    );
    assert.ok(page.texts().includes("3 项改动未保存"));
    assert.ok(page.texts().includes(lookup("saveBar.managerGain")) || page.texts().some((text) => text.includes(lookup("saveBar.managerGain"))), "写明店长角色将自动获得");

    // 保存：分店 → 角色 依序提交，成功后基线推进、保存灰掉。
    await act(async () => {
      await page.saveButton()!.props.onPress();
    });
    await page.settle(() => page.texts().includes("无改动"), "保存完成");
    assert.deepEqual(posts, ["stores", "roles"], "跨分段的改动按「分店 → 角色」顺序依次提交");
    assert.equal(page.all("Snackbar")[0]?.children.map(textOf).join(""), lookup("save.success"));
    assert.equal(alerts.length, 0);
    assert.equal(page.saveButton()!.props.disabled, true);
    await page.unmount();
  }

  // ───────────── B. 部分失败：分店成功、角色失败 ─────────────
  reset();
  server.failPost = "roles";
  {
    const page = await mount({ userGuid: "u-emily" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    await act(() => page.byLabel("Pressable", "Liverpool 未分配")!.props.onPress());
    await act(() => page.tab("角色")!.props.onPress());
    await act(() =>
      page.all("Pressable").find((node) => node.props.accessibilityRole === "checkbox" && node.props.accessibilityLabel === "Finance")!.props.onPress(),
    );
    await act(async () => {
      await page.saveButton()!.props.onPress();
    });
    await page.settle(() => alerts.length > 0, "出现失败提示");
    assert.deepEqual(posts, ["stores", "roles"], "角色失败后不再往下写");
    assert.equal(alerts[0].title, lookup("save.partialTitle"));
    assert.ok(alerts[0].message.includes("已保存：分店"), "说明哪些已保存");
    assert.ok(alerts[0].message.includes("保存失败：角色（"), "说明哪一步失败");
    assert.ok(alerts[0].message.includes(lookup("save.retryHint")), "说明草稿仍保留可重试");
    // 分店已保存 → 只剩角色的 1 项改动，页面里也留着失败说明。
    assert.ok(page.texts().includes("1 项改动未保存"), "已保存的分店基线推进，只剩角色这一项");
    assert.ok(page.texts().some((text) => text.includes("保存失败：角色")), "页面内保留失败说明");
    assert.equal(page.saveButton()!.props.disabled, false, "可以直接重试");
    // 重试成功。
    server.failPost = "";
    posts.length = 0;
    await act(async () => {
      await page.saveButton()!.props.onPress();
    });
    await page.settle(() => page.texts().includes("无改动"), "重试后保存完成");
    assert.deepEqual(posts, ["roles"], "重试只提交还没保存的角色");
    assert.ok(!page.texts().some((text) => text.includes("保存失败：角色")), "保存成功后失败说明消失");
    await page.unmount();
  }

  // ───────────── C. 店长：分店 / 角色收窄，没有管理档，没有菜单分段 ─────────────
  reset();
  signIn("manager");
  server.assignedStores = ["g-bankstown", "g-parramatta"];
  {
    const page = await mount({ userGuid: "u-wang", storeCode: "1008" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    assert.ok(page.texts().some((text) => text.includes("王芳")), "店员接口拉到目标姓名");
    assert.deepEqual(page.tabs(), ["分店 · 1", "角色 · 1", "直接权限 · 1"], "店长没有菜单分段；分店只数自己管理范围内的");
    assert.equal(page.byLabel("Pressable", "Bankstown 管理"), undefined, "店长看不到「管理」档");
    assert.equal(page.byLabel("Pressable", "Parramatta 查看"), undefined, "店长只列自己管理的分店");
    assert.ok(page.byLabel("Pressable", "Liverpool 查看"));
    await act(() => page.tab("角色")!.props.onPress());
    const roleRows = page.all("Pressable").filter((node) => node.props.accessibilityRole === "checkbox");
    assert.deepEqual(roleRows.map((node) => node.props.accessibilityLabel), ["店员"], "店长只看得到「店员」一行");
    assert.equal(page.all("Searchbar").length, 0);
    await page.unmount();
  }

  // ───────────── D. 受保护目标：深链无参数也要被拦住 ─────────────
  reset();
  signIn("manager");
  server.assignedRoles = [ROLES[1], ROLES[3]];
  {
    const page = await mount({ userGuid: "u-wang" });
    await page.settle(() => page.texts().includes(lookup("errors.privilegedTitle")), "受保护角色校验生效");
    assert.equal(page.saveButton(), undefined, "被拦住时没有保存栏");
    await page.unmount();
  }
  // 店员接口找不到目标（不在范围 / 高权限目标被后端隐藏）。
  reset();
  server.storeUserNotFound = true;
  {
    const page = await mount({ userGuid: "u-wang" });
    await page.settle(() => page.texts().includes(lookup("errors.notFoundTitle")), "找不到目标");
    await page.unmount();
  }
  // 本人：不能管理自己的授权。
  reset();
  signIn("manager");
  {
    const page = await mount({ userGuid: "mgr-1" });
    await page.settle(() => page.texts().includes(lookup("errors.selfTitle")), "本人校验生效");
    await page.unmount();
  }
  // 设备模式：直接拒绝。
  reset();
  signIn("manager");
  authState.sessionKind = "device";
  {
    const page = await mount({ userGuid: "u-wang" });
    await page.settle(() => page.texts().includes(lookup("errors.deviceTitle")), "设备模式校验生效");
    await page.unmount();
  }
  authState.sessionKind = "account";

  // ───────────── E. 路由参数：section=menu / mobile、copy=1 ─────────────
  reset();
  signIn("admin");
  {
    const page = await mount({ userGuid: "u-emily", section: "menu" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    assert.ok(page.texts().includes(lookup("menu.hint")), "section=menu 直接落在菜单分段");
    await page.unmount();
  }
  {
    const page = await mount({ userGuid: "u-emily", section: "mobile" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    assert.ok(page.texts().includes(lookup("menu.hint")), "旧管理员路由的 section=mobile 也落在菜单分段");
    await page.unmount();
  }
  {
    const page = await mount({ userGuid: "u-emily", copy: "1" });
    await page.settle(() => page.all("Sheet").length > 0, "复制弹层自动打开");
    assert.equal(page.all("Sheet")[0].props.title, lookup("copy.toOthersTitle", { name: "Emily Rose Chen" }));
    assert.ok(
      page.texts().some((text) => text.startsWith("收银权限 · 账号默认值")),
      "copy=1 同时落在直接权限分段（底部有收银权限账号默认值分组）",
    );
    await page.unmount();
  }
  // 复制前有未保存的权限草稿：必须先拦截。
  {
    const page = await mount({ userGuid: "u-emily", section: "permissions" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    await act(() => page.byLabel("Switch", "员工工时报表")!.props.onValueChange(true));
    await act(() => page.byLabel("Button", "复制给他人")!.props.onPress());
    assert.equal(page.all("Sheet").length, 0, "有未保存权限草稿时不能打开复制");
    assert.equal(page.all("Snackbar")[0]?.children.map(textOf).join(""), lookup("copy.dirtyBlocked"));
    await page.unmount();
  }
  // 收银权限入口：只对已保存的分店开放，storeCode 必填。
  {
    const page = await mount({ userGuid: "u-emily" });
    await page.settle(() => page.saveButton() !== undefined, "页面就绪");
    await act(() => page.byLabel("Pressable", "收银权限 Bankstown")!.props.onPress());
    const pushed = routerCalls.at(-1) as { pathname: string; params: Record<string, string> };
    assert.equal(pushed.pathname, "/users/[userGuid]/pos-terminal-permissions");
    assert.equal(pushed.params.storeCode, "1008", "收银权限页要求 storeCode 必填");
    assert.equal(pushed.params.userGuid, "u-emily");
    await page.unmount();
  }
}

run()
  .then(() => {
    console.log("access-management-screen-render.test.ts: ok");
  })
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
