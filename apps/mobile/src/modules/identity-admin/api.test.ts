import assert from "node:assert/strict";
import Module from "node:module";
import axios, { AxiosError } from "axios";

interface RequestRecord {
  method: "get" | "post" | "put" | "delete";
  url: string;
  body?: unknown;
  config?: unknown;
}

const secureValues = new Map<string, string>();
const asyncValues = new Map<string, string>();
let secureReadGate: Promise<void> | null = null;

function mockNativeModules() {
  Object.assign(globalThis, { __DEV__: false });
  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  // Node 测试只验证管理 API 契约，不启动 Expo 原生运行时。
  mockModule("expo-router", { router: { replace: () => undefined } });
  mockModule("react-native", {
    AppState: { addEventListener: () => ({ remove: () => undefined }) },
    NativeModules: {},
    Platform: { OS: "ios", select: <T>(values: { ios?: T; default?: T }) => values.ios ?? values.default },
  });
  mockModule("expo-secure-store", {
    getItemAsync: async (key: string) => {
      if (secureReadGate) await secureReadGate;
      return secureValues.get(key) ?? null;
    },
    setItemAsync: async (key: string, value: string) => { secureValues.set(key, value); },
    deleteItemAsync: async (key: string) => { secureValues.delete(key); },
  });
  mockModule("expo-location", {
    hasStartedLocationUpdatesAsync: async () => false,
    stopLocationUpdatesAsync: async () => undefined,
  });
  mockModule("@/shared/logging/log-center-runtime", { reportApplicationLog: () => undefined });
  const asyncStorage = {
    getItem: async (key: string) => asyncValues.get(key) ?? null,
    setItem: async (key: string, value: string) => { asyncValues.set(key, value); },
    removeItem: async (key: string) => { asyncValues.delete(key); },
  };
  mockModule("@react-native-async-storage/async-storage", Object.assign(asyncStorage, { default: asyncStorage, __esModule: true }));
}

async function run() {
  mockNativeModules();
  const { apiClient } = await import("@/shared/api/client");
  const { accountBoundRequestConfig } = await import("@/modules/auth/account-bound-request");
  const { clearIosReviewSession, setIosReviewSessionActive } = await import("@/modules/ios-review/session");
  const api = await import("./api");
  const requests: RequestRecord[] = [];
  const originals = {
    get: apiClient.get,
    post: apiClient.post,
    put: apiClient.put,
    delete: apiClient.delete,
  };

  const encode = (value: object) => Buffer.from(JSON.stringify(value)).toString("base64url");
  secureValues.set("hbweb_access_token", `${encode({ alg: "none" })}.${encode({ sub: "actor-b" })}.signature`);
  secureValues.set("hbweb_refresh_token", "refresh-b");
  let staleAdapterCalls = 0;
  const staleGuard = accountBoundRequestConfig("actor-a");
  await assert.rejects(
    apiClient.get("/identity-session-race", {
      ...staleGuard,
      headers: { ...staleGuard.headers, "X-Skip-Center-Log": "1" },
      adapter: async (config) => {
        staleAdapterCalls += 1;
        return { data: true, status: 200, statusText: "OK", headers: {}, config };
      },
    }),
    (error: unknown) => (error as { code?: string })?.code === "ACCOUNT_SESSION_CHANGED",
  );
  assert.equal(staleAdapterCalls, 0, "账号切换后旧身份请求不得触达网络 adapter");

  secureValues.set("hbweb_access_token", `${encode({ alg: "none" })}.${encode({ sub: "actor-a" })}.signature`);
  secureValues.set("hbweb_refresh_token", "refresh-a");
  asyncValues.set("hbmobile.auth-session-kind.v1", "deviceAccount");
  await assert.rejects(
    apiClient.get("/identity-device-account", accountBoundRequestConfig("actor-a")),
    (error: unknown) => (error as { code?: string })?.code === "ACCOUNT_SESSION_CHANGED",
  );

  asyncValues.set("hbmobile.auth-session-kind.v1", "account");
  let releaseSecureReads: () => void = () => undefined;
  secureReadGate = new Promise<void>((resolve) => { releaseSecureReads = resolve; });
  const reviewRaceRequest = apiClient.get("/identity-review-race", accountBoundRequestConfig("actor-a"));
  await Promise.resolve();
  const reviewMarkerStorage = {
    getItemAsync: async () => null,
    setItemAsync: async () => undefined,
    deleteItemAsync: async () => undefined,
  };
  await setIosReviewSessionActive(reviewMarkerStorage);
  releaseSecureReads();
  await assert.rejects(
    reviewRaceRequest,
    (error: unknown) => (error as { code?: string })?.code === "ACCOUNT_SESSION_CHANGED",
  );
  secureReadGate = null;
  await clearIosReviewSession(reviewMarkerStorage);

  secureValues.set("hbweb_access_token", `${encode({ alg: "none" })}.${encode({ sub: "actor-a" })}.signature`);
  secureValues.set("hbweb_refresh_token", "refresh-a");
  asyncValues.set("hbmobile.auth-session-kind.v1", "account");
  const originalAxiosPost = axios.post;
  let refreshCalls = 0;
  axios.post = (async () => {
    refreshCalls += 1;
    throw new Error("account-bound request must not refresh");
  }) as typeof axios.post;
  let unauthorizedAdapterCalls = 0;
  await assert.rejects(
    apiClient.get("/identity-expired", {
      ...accountBoundRequestConfig("actor-a"),
      adapter: async (config) => {
        unauthorizedAdapterCalls += 1;
        throw new AxiosError("Unauthorized", "ERR_BAD_REQUEST", config, undefined, {
          data: {},
          status: 401,
          statusText: "Unauthorized",
          headers: {},
          config,
        });
      },
    }),
    (error: unknown) => (error as { code?: string })?.code === "ACCOUNT_SESSION_CHANGED",
  );
  axios.post = originalAxiosPost;
  assert.equal(unauthorizedAdapterCalls, 1, "account-bound 401 不得重试原请求");
  assert.equal(refreshCalls, 0, "account-bound 401 不得进入自动刷新流程");
  secureValues.clear();
  asyncValues.clear();

  apiClient.get = (async (url: string, config?: unknown) => {
    requests.push({ method: "get", url, config });
    if (url === "/Users/optimized") {
      return {
        data: {
          Success: true,
          Data: {
            Items: [{
              UserGUID: "user-1",
              Username: "alice",
              Email: "alice@example.test",
              IsActive: true,
              CreatedAt: "2026-09-01T00:00:00Z",
              UpdatedAt: "2026-09-02T00:00:00Z",
              RoleNames: ["Admin"],
              StoreNames: ["Sunnybank"],
              Roles: [],
              Stores: [{ StoreGUID: "store-1", StoreCode: "S1", StoreName: "Sunnybank", IsPrimary: true }],
              Permissions: ["Users.View"],
              ExactPermissions: ["Users.View"],
            }],
            TotalCount: 21,
            PageIndex: 2,
            PageSize: 10,
            TotalPages: 3,
          },
        },
      };
    }
    if (url === "/Roles") {
      return { data: { items: [], total: 0, page: 1, pageSize: 20, totalPages: 0 } };
    }
    if (url === "/Roles/guid/role%2F1") {
      return {
        data: {
          roleGUID: "role/1",
          roleName: "Supervisor",
          isActive: true,
          createdAt: "2026-01-01",
          updatedAt: "2026-01-02",
          userCount: 1,
          users: [],
          permissions: ["Users.View"],
        },
      };
    }
    if (url === "/Users/guid/user%2F1/login-records") {
      return {
        data: {
          items: [{ sessionId: "s-1", loginAt: "2026-09-10", expiresAt: "2026-09-11", isRevoked: false, isExpired: true, status: "expired" }],
          total: 1,
          page: 1,
          pageSize: 20,
        },
      };
    }
    if (url === "/Roles/permissions/catalog") {
      return {
        data: {
          Categories: [{ Category: "Users", DisplayName: "Users", Permissions: [{ Name: "Users.View", DisplayName: "View users", Category: "Users", IsSystemPermission: true }] }],
          PermissionAliases: [{ CanonicalCode: "Users.View", AliasCodes: ["User.View"] }],
          RoleTemplates: [{ RoleName: "User", PermissionCodes: ["Users.View"] }],
          SuperAdminRoleNames: ["Admin"],
        },
      };
    }
    if (url === "/Roles/guid/role%2F1/permissions/state") {
      return { data: { RoleGuid: "role/1", RoleName: "Supervisor", IsSuperAdmin: false, ImplicitAllPermissions: false, ExplicitPermissionCodes: ["Users.View"], EffectivePermissionCodes: ["Users.View"] } };
    }
    if (url === "/Roles/sys-permissions") {
      return { data: { success: true, data: [
        { Id: "p-1", Code: "Users.View", Name: "查看用户", Category: "Users" },
        { id: "p-2", code: "StoreEvents.View", name: "查看门店活动", category: "StoreEvents", description: "自定义" },
      ] } };
    }
    if (url === "/Roles/permissions/role-counts") {
      return { data: { success: true, data: { "Users.View": 3, "StoreEvents.View": "1", "Bad.Count": -1, "": 9 } } };
    }
    if (url === "/Roles/permissions/Store.Events%2FView/roles") {
      return { data: { success: true, data: [{ RoleGUID: "role-1", RoleName: "Manager", IsActive: true, CreatedAt: "", UpdatedAt: "" }] } };
    }
    if (url === "/Roles/permissions/Store.Events%2FView/users") {
      return { data: { success: true, data: [{ UserGUID: "user-9", Username: "zoe", Email: "zoe@example.test", FullName: "Zoe", IsActive: false, AssignedAt: "2026-09-01T00:00:00" }] } };
    }
    if (url === "/Roles/permissions/Empty.Code/users") {
      return { data: { success: true, data: null } };
    }
    if (url === "/Roles/guid/role%2F1/users") {
      return { data: { items: [{ userGUID: "user-1", username: "alice", email: "alice@example.test", isActive: true, assignedAt: "2026-01-01" }], total: 1, page: 1, pageSize: 10_000 } };
    }
    throw new Error(`unexpected GET ${url}`);
  }) as typeof apiClient.get;

  apiClient.post = (async (url: string, body?: unknown) => {
    requests.push({ method: "post", url, body });
    if (url === "/Users") {
      return { data: { userGUID: "user-2", username: "bob", email: "bob@example.test", isActive: true, createdAt: "", updatedAt: "", roleNames: [], storeNames: [] } };
    }
    if (url === "/Roles") {
      return { data: { RoleGUID: "role-2", RoleName: "Buyer", IsActive: true, CreatedAt: "", UpdatedAt: "", UserCount: 0 } };
    }
    if (url === "/Roles/permissions") {
      return { data: { success: true, data: [{ Id: "p-3", Code: "StoreEvents.Create", Name: "门店活动 - 创建", Category: "StoreEvents" }] } };
    }
    return { data: { success: true, data: true } };
  }) as typeof apiClient.post;

  apiClient.put = (async (url: string, body?: unknown) => {
    requests.push({ method: "put", url, body });
    if (url.endsWith("/password")) return { data: true };
    if (url.startsWith("/Users/guid/")) {
      return { data: { userGUID: "user/1", username: "bob", email: "bob@example.test", isActive: false, createdAt: "", updatedAt: "", roleNames: [], storeNames: [] } };
    }
    return { data: { roleGUID: "role/1", roleName: "Supervisor", isActive: false, createdAt: "", updatedAt: "", userCount: 1, users: [], permissions: [] } };
  }) as typeof apiClient.put;

  apiClient.delete = (async (url: string) => {
    requests.push({ method: "delete", url });
    return { data: true };
  }) as typeof apiClient.delete;

  try {
    const users = await api.fetchIdentityUsers({
      page: 2,
      pageSize: 10,
      search: " alice ",
      storeGuid: " store-1 ",
      roleGuid: " role-1 ",
      isActive: false,
    }, "actor-1");
    assert.equal(users.total, 21);
    assert.equal(users.totalPages, 3);
    assert.equal(users.items[0]?.stores[0]?.isPrimary, true);
    assert.deepEqual((requests[0]?.config as { params: unknown }).params, {
      page: 2,
      pageSize: 10,
      search: "alice",
      roleGuid: "role-1",
      storeGuid: "store-1",
      isActive: false,
    });

    const emptyRoles = await api.fetchIdentityRoles({ page: 1, pageSize: 20 }, "actor-1");
    assert.deepEqual(emptyRoles, { items: [], total: 0, page: 1, pageSize: 20, totalPages: 0 });
    const role = await api.fetchIdentityRoleDetail("role/1", "actor-1");
    assert.equal(role.roleGUID, "role/1");

    const logins = await api.fetchIdentityUserLoginRecords("user/1", { page: 1, pageSize: 20 }, "actor-1");
    assert.equal(logins.items[0]?.status, "expired");

    const catalog = await api.fetchIdentityPermissionCatalog("actor-1");
    assert.equal(catalog.categories[0]?.permissions[0]?.name, "Users.View");
    assert.deepEqual(catalog.permissionAliases[0]?.aliasCodes, ["User.View"]);
    const permissionState = await api.fetchIdentityRolePermissionState("role/1", "actor-1");
    assert.deepEqual(permissionState.explicitPermissionCodes, ["Users.View"]);

    await api.createIdentityUser({
      username: " bob ",
      email: " bob@example.test ",
      password: "secret12",
      roleGuids: ["role-2"],
      storeGuids: ["store-2"],
    }, "actor-1");
    assert.deepEqual(requests.find((item) => item.method === "post" && item.url === "/Users")?.body, {
      username: "bob",
      email: "bob@example.test",
      password: "secret12",
      passwordFormat: "raw",
      fullName: null,
      isActive: true,
      roleGuids: ["role-2"],
      storeGuids: ["store-2"],
    });

    // 姓名四段：创建时只发有值的段（fullName 仍是合成值）；更新时只发有变化的段，空串 = 清空、null = 不改。
    await api.createIdentityUser({
      username: "carol",
      email: "carol@example.test",
      password: "secret12",
      fullName: "Carol Rose Lin",
      givenName: " Carol ",
      middleName: "Rose",
      familyName: "Lin",
      nickname: "",
    }, "actor-1");
    const createBodies = requests.filter((item) => item.method === "post" && item.url === "/Users").map((item) => item.body);
    assert.deepEqual(createBodies[1], {
      username: "carol",
      email: "carol@example.test",
      password: "secret12",
      passwordFormat: "raw",
      fullName: "Carol Rose Lin",
      givenName: "Carol",
      middleName: "Rose",
      familyName: "Lin",
      isActive: true,
      roleGuids: [],
      storeGuids: [],
    });
    assert.equal("nickname" in (createBodies[1] as Record<string, unknown>), false, "创建时空昵称不发送");
    assert.equal("givenName" in (createBodies[0] as Record<string, unknown>), false, "未提供姓名段时请求体与此前一致");

    await api.updateIdentityUser("user/1", {
      username: "bob",
      email: "bob@example.test",
      fullName: "Bob Lin",
      familyName: "Lin",
      nickname: "",
      middleName: null,
      isActive: false,
    }, "actor-1");
    assert.deepEqual(requests.find((item) => item.method === "put" && item.url === "/Users/guid/user%2F1")?.body, {
      username: "bob",
      email: "bob@example.test",
      fullName: "Bob Lin",
      familyName: "Lin",
      nickname: "",
      middleName: null,
      isActive: false,
    });

    await api.updateIdentityUserPassword("user/1", { newPassword: "secret34", forcePasswordChange: true }, "actor-1");
    assert.deepEqual(requests.find((item) => item.url.endsWith("/password"))?.body, {
      newPassword: "secret34",
      passwordFormat: "raw",
      forcePasswordChange: true,
    });

    await api.createIdentityRole({ roleName: " Buyer ", permissions: ["Orders.View"] }, "actor-1");
    assert.deepEqual(requests.find((item) => item.method === "post" && item.url === "/Roles")?.body, {
      roleName: "Buyer",
      description: null,
      isActive: true,
      permissions: ["Orders.View"],
    });
    await api.updateIdentityRole("role/1", { roleName: "Supervisor", isActive: false }, "actor-1");
    await api.addIdentityRoleUsers("role/1", [" user-1 ", "", "user-2"], "actor-1");
    await api.removeIdentityRoleUser("role/1", "user/2", "actor-1");
    await api.saveIdentityRolePermissions("role/1", [" Users.View ", "Users.View", "Roles.View"], "actor-1");
    assert.deepEqual(requests.find((item) => item.url.endsWith("/permissions") && item.method === "post")?.body, {
      permissions: ["Users.View", "Roles.View"],
    });
    assert.ok(requests.some((item) => item.url === "/Roles/guid/role%2F1/users/user%2F2"));

    // ---- 权限管理接口契约 ----
    const sysPermissions = await api.fetchIdentitySysPermissions("actor-1");
    assert.deepEqual(sysPermissions, [
      { id: "p-1", code: "Users.View", name: "查看用户", category: "Users" },
      { id: "p-2", code: "StoreEvents.View", name: "查看门店活动", category: "StoreEvents", description: "自定义" },
    ]);
    assert.deepEqual(api.normalizeIdentitySysPermissions({ success: true, data: null }), [], "data 为 null 视为空表");
    assert.throws(() => api.normalizeIdentitySysPermissions({ success: true, data: { code: "x" } }), /IDENTITY_ADMIN_RESPONSE_INVALID/);
    const roleCounts = await api.fetchIdentityPermissionRoleCounts("actor-1");
    assert.deepEqual(roleCounts, { "Users.View": 3, "StoreEvents.View": 1 }, "负数、空键与非数字计数一律丢弃");
    const permissionRoles = await api.fetchIdentityPermissionRoles(" Store.Events/View ", "actor-1");
    assert.equal(permissionRoles[0]?.roleGUID, "role-1");
    assert.equal(permissionRoles[0]?.userCount, 0);
    await api.assignIdentityPermissionRoles("Store.Events/View", [" role-1 ", "role-1", "", "role-2"], "actor-1");
    assert.deepEqual(requests.find((item) => item.method === "post" && item.url === "/Roles/permissions/Store.Events%2FView/roles")?.body, ["role-1", "role-2"], "权限代码须 URL 编码，角色 GUID 去重去空");
    const permissionUsers = await api.fetchIdentityPermissionUsers(" Store.Events/View ", "actor-1");
    assert.deepEqual(permissionUsers, [{ userGUID: "user-9", username: "zoe", email: "zoe@example.test", fullName: "Zoe", isActive: false, assignedAt: "2026-09-01T00:00:00" }]);
    assert.deepEqual(await api.fetchIdentityPermissionUsers("Empty.Code", "actor-1"), [], "data 为 null 视为无直接授权用户");
    await api.assignIdentityPermissionUsers("Store.Events/View", { addUserGuids: [" user-1 ", "user-1", ""], removeUserGuids: ["user-9"] }, "actor-1");
    assert.deepEqual(
      requests.find((item) => item.method === "post" && item.url === "/Roles/permissions/Store.Events%2FView/users")?.body,
      { addUserGuids: ["user-1"], removeUserGuids: ["user-9"] },
      "用户授权按增量提交，GUID 去重去空且权限代码 URL 编码",
    );
    await assert.rejects(api.assignIdentityPermissionUsers(" ", { addUserGuids: ["user-1"], removeUserGuids: [] }, "actor-1"), /Permission code is required/);
    await api.createIdentitySysPermission({ code: " StoreEvents ", name: " 门店活动 ", category: "StoreEvents", description: "  ", actions: ["Create", " Create ", ""] }, "actor-1");
    assert.deepEqual(requests.find((item) => item.method === "post" && item.url === "/Roles/permissions")?.body, {
      code: "StoreEvents", name: "门店活动", category: "StoreEvents", description: null, actions: ["Create"],
    });
    await api.createIdentitySysPermission({ code: "StoreEvents.Export", name: "导出", category: "StoreEvents", actions: [] }, "actor-1");
    assert.deepEqual(requests.filter((item) => item.method === "post" && item.url === "/Roles/permissions").at(-1)?.body, {
      code: "StoreEvents.Export", name: "导出", category: "StoreEvents", description: null,
    }, "未勾选动作时不得发送空 actions，否则服务端会走单个创建以外的分支");
    await api.deleteIdentitySysPermission("Store.Events/View", "actor-1");
    assert.ok(requests.some((item) => item.method === "delete" && item.url === "/Roles/permissions/Store.Events%2FView"));
    await assert.rejects(api.deleteIdentitySysPermission("   ", "actor-1"), /Permission code is required/);

    assert.throws(
      () => api.normalizeIdentityUsers({ Success: false, Message: "denied", ErrorCode: "FORBIDDEN" }),
      (error: unknown) => error instanceof Error
        && error.message === "denied"
        && (error as Error & { code?: string }).code === "FORBIDDEN"
        && (error as Error & { apiBusinessError?: boolean }).apiBusinessError === true,
    );
    assert.throws(() => api.normalizeIdentityUsers([]), /IDENTITY_ADMIN_RESPONSE_INVALID/);
    assert.throws(
      () => api.normalizeIdentityUsers({ total: 0, page: 1, pageSize: 20 }),
      /IDENTITY_ADMIN_RESPONSE_INVALID/,
    );
    assert.throws(
      () => api.normalizeIdentityUser({ username: "missing-guid", email: "x@example.test" }),
      /IDENTITY_ADMIN_RESPONSE_INVALID/,
    );

    const axiosError = new AxiosError("Forbidden", "ERR_BAD_REQUEST", undefined, undefined, {
      status: 403,
      statusText: "Forbidden",
      headers: {},
      config: { headers: {} } as never,
      data: { errorCode: "FORBIDDEN" },
    });
    assert.deepEqual(api.getIdentityAdminErrorMeta(axiosError), {
      message: "Forbidden",
      status: 403,
      code: "FORBIDDEN",
    });
    const businessError = Object.assign(new Error("Username exists"), {
      code: "USERNAME_EXISTS",
      apiBusinessError: true,
    });
    assert.deepEqual(api.getIdentityAdminErrorMeta(businessError), {
      message: "Username exists",
      code: "USERNAME_EXISTS",
    });
  } finally {
    apiClient.get = originals.get;
    apiClient.post = originals.post;
    apiClient.put = originals.put;
    apiClient.delete = originals.delete;
  }
}

void run();
