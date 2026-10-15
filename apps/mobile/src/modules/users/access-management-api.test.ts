import assert from "node:assert/strict";
import Module from "node:module";

interface RequestRecord {
  body?: unknown;
  method: "get" | "post";
  url: string;
}

async function run() {
  Object.assign(globalThis, { __DEV__: false });

  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };

  // Node 测试只验证 HTTP 契约，不加载 Expo 原生运行时。
  mockModule("expo-router", { router: { replace: () => undefined } });
  mockModule("react-native", {
    AppState: { addEventListener: () => ({ remove: () => undefined }) },
    NativeModules: {},
    Platform: {
      OS: "ios",
      select: <T>(values: { ios?: T; default?: T }) =>
        values.ios ?? values.default,
    },
  });
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
    default: {
      getItem: async () => null,
      setItem: async () => undefined,
      removeItem: async () => undefined,
    },
  });

  const { apiClient } = await import("../../shared/api/client");
  const {
    assignUserAccessRoles,
    assignUserAccessStores,
    assignUserDirectPermissions,
    copyUserDirectPermissions,
    fetchAccessPermissionCatalog,
    fetchAccessRoleCatalog,
    fetchAccessStoreCatalog,
    fetchUserAccessPermissionState,
    fetchUserAccessPermissionAccess,
    fetchUserAccessRoles,
    fetchUserAccessStores,
    normalizeAccessPermissionCatalog,
    normalizeAccessRoleList,
    normalizeUserAccessPermissionState,
    normalizeUserAccessPermissionAccess,
    normalizeUserAccessStoreList,
    normalizeUserDirectPermissionCopyResult,
    previewCopyUserDirectPermissions,
  } = await import("./access-management-api");
  const {
    accessPermissionCatalogQueryKey,
    accessRoleCatalogQueryKey,
    accessStoreCatalogQueryKey,
    userAccessPermissionStateQueryKey,
    userAccessPermissionAccessQueryKey,
    userAccessRolesQueryKey,
    userAccessStoresQueryKey,
  } = await import("./access-management-hooks");

  const requests: RequestRecord[] = [];
  const originalGet = apiClient.get;
  const originalPost = apiClient.post;
  apiClient.get = (async (url: string) => {
    requests.push({ method: "get", url });
    if (url.endsWith("/stores")) {
      return {
        data: {
          success: true,
          data: [
            {
              StoreGUID: "store-1",
              StoreCode: "S1",
              StoreName: "Store 1",
              IsActive: true,
              IsPrimary: true,
              AssignedAt: "2026-01-01T00:00:00Z",
            },
          ],
        },
      };
    }
    if (url.endsWith("/roles") && url.includes("/Users/")) {
      return {
        data: {
          success: true,
          data: [
            { RoleGUID: "role-1", RoleName: "StoreStaff", IsActive: true },
          ],
        },
      };
    }
    if (url.endsWith("/permissions/state")) {
      return {
        data: {
          success: true,
          data: {
            UserGuid: "user-1",
            IsSuperAdmin: false,
            ImplicitAllPermissions: false,
            InheritedPermissionCodes: ["Users.View"],
            DirectPermissionCodes: ["Users.Edit"],
            EffectivePermissionCodes: ["Users.View", "Users.Edit"],
            InheritedSources: [
              { RoleName: "StoreStaff", PermissionCodes: ["Users.View"] },
            ],
          },
        },
      };
    }
    if (url.endsWith("/access-permissions")) {
      return {
        data: {
          success: true,
          data: {
            state: {
              userGuid: "user-1",
              isSuperAdmin: false,
              implicitAllPermissions: false,
              inheritedPermissionCodes: ["Users.View"],
              directPermissionCodes: ["Users.Edit"],
              effectivePermissionCodes: ["Users.View", "Users.Edit"],
              inheritedSources: [
                { roleName: "StoreStaff", permissionCodes: ["Users.View"] },
              ],
            },
            categories: [
              {
                category: "用户管理",
                displayName: "用户管理",
                description: "用户管理相关权限",
                permissions: [
                  {
                    name: "Users.View",
                    displayName: "查看用户",
                    description: "页面 /system/users - 查看用户列表与详情",
                    category: "用户管理",
                    isSystemPermission: false,
                  },
                ],
              },
            ],
          },
        },
      };
    }
    if (url === "/Roles/active") {
      return { data: [{ roleGUID: "role-1", roleName: "StoreStaff" }] };
    }
    if (url === "/Roles/permissions") {
      return {
        data: [
          {
            category: "Users",
            displayName: "Users",
            permissions: [
              {
                name: "Users.View",
                displayName: "View users",
                category: "Users",
                isSystemPermission: true,
              },
            ],
          },
        ],
      };
    }
    throw new Error(`unexpected GET ${url}`);
  }) as typeof apiClient.get;
  apiClient.post = (async (url: string, body?: unknown) => {
    requests.push({ body, method: "post", url });
    if (url.endsWith("/permissions/copy")) {
      const payload = body as { preview: boolean; mode: string };
      return {
        data: {
          success: true,
          data: {
            SourceUserGuid: "src/1",
            Mode: payload.mode,
            Preview: payload.preview,
            SourceDirectPermissionCodes: ["A.View", "B.Edit", "C.Manage"],
            TransferablePermissionCodes: ["A.View", "B.Edit"],
            SkippedPermissionCodes: ["C.Manage"],
            Targets: [
              { UserGuid: "t-1", Added: ["A.View", "B.Edit"], Removed: [], UnchangedCount: 1 },
            ],
          },
        },
      };
    }
    return { data: { success: true, data: true } };
  }) as typeof apiClient.post;

  try {
    const encodedUserGuid = "user%2Fa%3F%20b";
    const userGuid = "user/a? b";
    const [stores, roles, state, roleCatalog, permissionCatalog] =
      await Promise.all([
        fetchUserAccessStores(userGuid),
        fetchUserAccessRoles(userGuid),
        fetchUserAccessPermissionState(userGuid),
        fetchAccessRoleCatalog(),
        fetchAccessPermissionCatalog(),
      ]);
    const storeCatalog = await fetchAccessStoreCatalog(async () => [
      {
        storeGUID: "catalog-store",
        storeCode: "CAT",
        storeName: "Catalog store",
      },
    ]);
    const permissionAccess = await fetchUserAccessPermissionAccess(userGuid);

    assert.deepEqual(stores, [
      {
        storeGUID: "store-1",
        storeCode: "S1",
        storeName: "Store 1",
        isActive: true,
        isManageable: true,
        assignedAt: "2026-01-01T00:00:00Z",
      },
    ]);
    assert.deepEqual(roles, [
      { roleGUID: "role-1", roleName: "StoreStaff", isActive: true },
    ]);
    assert.deepEqual(state, {
      userGuid: "user-1",
      isSuperAdmin: false,
      implicitAllPermissions: false,
      inheritedPermissionCodes: ["Users.View"],
      directPermissionCodes: ["Users.Edit"],
      effectivePermissionCodes: ["Users.View", "Users.Edit"],
      inheritedSources: [
        { roleName: "StoreStaff", permissionCodes: ["Users.View"] },
      ],
    });
    assert.equal(permissionAccess.state.userGuid, "user-1");
    assert.deepEqual(
      permissionAccess.categories.flatMap((category) =>
        category.permissions.map((permission) => permission.name),
      ),
      ["Users.View"],
      "聚合接口只暴露服务端已经按操作者范围裁剪的权限",
    );
    assert.deepEqual(
      normalizeUserAccessPermissionAccess({
        state: permissionAccess.state,
        categories: permissionAccess.categories,
      }),
      permissionAccess,
    );
    assert.equal(roleCatalog.length, 1);
    assert.equal(permissionCatalog[0]?.permissions[0]?.name, "Users.View");
    assert.deepEqual(storeCatalog, [
      {
        storeGUID: "catalog-store",
        storeCode: "CAT",
        storeName: "Catalog store",
      },
    ]);
    assert.deepEqual(userAccessStoresQueryKey(userGuid), [
      "userAccessManagement",
      "stores",
      userGuid,
    ]);
    assert.deepEqual(userAccessRolesQueryKey(userGuid), [
      "userAccessManagement",
      "roles",
      userGuid,
    ]);
    assert.deepEqual(userAccessPermissionStateQueryKey(userGuid), [
      "userAccessManagement",
      "permissionState",
      userGuid,
    ]);
    assert.deepEqual(userAccessPermissionAccessQueryKey(userGuid), [
      "userAccessManagement",
      "permissionAccess",
      userGuid,
    ]);
    assert.deepEqual(accessRoleCatalogQueryKey, [
      "userAccessManagement",
      "roleCatalog",
    ]);
    assert.deepEqual(accessPermissionCatalogQueryKey, [
      "userAccessManagement",
      "permissionCatalog",
    ]);
    assert.deepEqual(accessStoreCatalogQueryKey, [
      "userAccessManagement",
      "storeCatalog",
    ]);

    await assignUserAccessStores({
      userGuid,
      assignments: [
        { storeGUID: "store-1", isPrimary: false },
        { storeGUID: "store-2", isPrimary: true },
      ],
    });
    await assignUserAccessRoles({
      userGuid,
      roleGuids: ["role-store-manager", "role-1"],
      roleCatalog: [
        {
          roleGUID: "role-store-manager",
          roleName: "StoreManager",
          isActive: true,
        },
        { roleGUID: "role-1", roleName: "StoreStaff", isActive: true },
      ],
    });
    await assignUserDirectPermissions({
      userGuid,
      permissions: ["Users.View"],
    });

    assert.deepEqual(requests, [
      { method: "get", url: `/Users/guid/${encodedUserGuid}/stores` },
      { method: "get", url: `/Users/guid/${encodedUserGuid}/roles` },
      {
        method: "get",
        url: `/Users/guid/${encodedUserGuid}/permissions/state`,
      },
      { method: "get", url: "/Roles/active" },
      { method: "get", url: "/Roles/permissions" },
      {
        method: "get",
        url: `/Users/guid/${encodedUserGuid}/access-permissions`,
      },
      {
        method: "post",
        url: `/Users/guid/${encodedUserGuid}/stores`,
        body: [
          { StoreGUID: "store-1", IsPrimary: false },
          { StoreGUID: "store-2", IsPrimary: true },
        ],
      },
      {
        method: "post",
        url: `/Users/guid/${encodedUserGuid}/roles`,
        body: { RoleGuids: ["role-1"] },
      },
      {
        method: "post",
        url: `/Users/guid/${encodedUserGuid}/permissions`,
        body: { permissions: ["Users.View"] },
      },
    ]);
    // 复制权限：先预览（preview=true）再写入（preview=false），路径与请求体契约固定。
    requests.length = 0;
    const previewResult = await previewCopyUserDirectPermissions({
      sourceUserGuid: " src/1 ",
      targetUserGuids: ["t-1", " t-1 ", "SRC/1", "", "t-2"],
      mode: "merge",
    });
    const copyResult = await copyUserDirectPermissions({
      sourceUserGuid: "src/1",
      targetUserGuids: ["t-1"],
      mode: "replace",
    });
    assert.deepEqual(requests, [
      {
        method: "post",
        url: "/Users/guid/src%2F1/permissions/copy",
        body: { targetUserGuids: ["t-1", "t-2"], mode: "merge", preview: true },
      },
      {
        method: "post",
        url: "/Users/guid/src%2F1/permissions/copy",
        body: { targetUserGuids: ["t-1"], mode: "replace", preview: false },
      },
    ], "目标去重并剔除来源自身；预览与写入只差 preview 标记");
    assert.equal(previewResult.preview, true);
    assert.equal(copyResult.preview, false);
    assert.deepEqual(previewResult, {
      sourceUserGuid: "src/1",
      mode: "merge",
      preview: true,
      sourceDirectPermissionCodes: ["A.View", "B.Edit", "C.Manage"],
      transferablePermissionCodes: ["A.View", "B.Edit"],
      skippedPermissionCodes: ["C.Manage"],
      targets: [{ userGuid: "t-1", added: ["A.View", "B.Edit"], removed: [], unchangedCount: 1 }],
    }, "PascalCase 响应被规范化为 camelCase");
    await assert.rejects(
      () => previewCopyUserDirectPermissions({ sourceUserGuid: "src/1", targetUserGuids: ["src/1", " "], mode: "merge" }),
      /Target user GUID is required/,
      "没有有效目标时不发请求",
    );
    await assert.rejects(
      () => copyUserDirectPermissions({ sourceUserGuid: " ", targetUserGuids: ["t-1"], mode: "merge" }),
      /User GUID is required/,
    );
    await assert.rejects(
      () => copyUserDirectPermissions({ sourceUserGuid: "src/1", targetUserGuids: ["t-1"], mode: "append" as never }),
      /Copy mode is invalid/,
    );
    assert.equal(requests.length, 2, "非法入参不应产生请求");

    const validCopy = {
      sourceUserGuid: "s",
      mode: "merge",
      preview: true,
      sourceDirectPermissionCodes: [],
      transferablePermissionCodes: [],
      skippedPermissionCodes: [],
      targets: [{ userGuid: "t", added: [], removed: [], unchangedCount: 0 }],
    };
    assert.equal(normalizeUserDirectPermissionCopyResult(validCopy).targets[0]?.userGuid, "t");
    assert.throws(
      () => normalizeUserDirectPermissionCopyResult({ success: false, message: "denied" }),
      /denied/,
    );
    assert.throws(
      () => normalizeUserDirectPermissionCopyResult({ ...validCopy, mode: "other" }),
      /Invalid access management response/,
      "未知复制方式视为非法响应",
    );
    assert.throws(
      () => normalizeUserDirectPermissionCopyResult({ ...validCopy, targets: [{ userGuid: "t", added: [], removed: [] }] }),
      /Invalid access management response/,
      "缺少 unchangedCount 视为非法响应",
    );
    assert.throws(
      () => normalizeUserDirectPermissionCopyResult({ ...validCopy, preview: "yes" }),
      /Invalid access management response/,
    );
    assert.throws(
      () => normalizeUserDirectPermissionCopyResult({ ...validCopy, skippedPermissionCodes: "x" }),
      /Invalid access management response/,
    );

    requests.forEach(({ url }) => {
      assert.equal(url.startsWith("/api"), false, `${url} 不应重复添加 /api`);
    });

    assert.throws(
      () => normalizeUserAccessStoreList({ success: false, message: "denied" }),
      /denied/,
    );
    assert.throws(
      () => normalizeAccessRoleList([{ roleName: "missing-guid" }]),
      /Invalid access management response/,
    );
    assert.throws(
      () => normalizeAccessPermissionCatalog([{ category: "Users" }]),
      /Invalid access management response/,
    );
    assert.throws(
      () => normalizeUserAccessPermissionState(null),
      /Invalid access management response/,
    );
  } finally {
    apiClient.get = originalGet;
    apiClient.post = originalPost;
  }
}

run()
  .then(() => console.log("user access management API tests passed"))
  .catch((error) => {
    console.error(error);
    process.exitCode = 1;
  });
