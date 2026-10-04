import assert from "node:assert/strict";
import Module, { createRequire } from "node:module";
import test from "node:test";

type ModuleLoader = (request: string, parent: unknown, isMain: boolean) => unknown;

test("认证门店响应保留严格布尔 POS 启用状态", async () => {
  const responses = new Map<string, unknown>([
    [
      "/auth/current",
      {
        UserGUID: "user-1",
        MustChangePassword: true,
        Stores: [
          { StoreCode: "PASCAL_FALSE", StoreName: "Pascal false", IsActive: false },
          { StoreCode: "CAMEL_FALSE", StoreName: "Camel false", isActive: false },
          { StoreCode: "MISSING", StoreName: "Missing" },
          { StoreCode: "STRING_FALSE", StoreName: "String false", IsActive: "false" },
        ],
      },
    ],
    [
      "/Users/guid/user%2F1/stores",
      [
        { StoreCode: "ACTIVE", StoreName: "Active", IsActive: true, IsPrimary: false },
        { StoreCode: "INACTIVE", StoreName: "Inactive", IsActive: false, IsPrimary: true },
      ],
    ],
  ]);
  const apiClient = {
    get: async (url: string) => ({ data: responses.get(url) }),
  };
  const moduleWithLoader = Module as unknown as { _load: ModuleLoader };
  const originalLoad = moduleWithLoader._load;
  const loadModule = createRequire(__filename);

  moduleWithLoader._load = function mockedLoad(request, parent, isMain) {
    if (request === "@/shared/api/client") {
      return { apiClient };
    }
    return originalLoad.call(this, request, parent, isMain);
  };

  try {
    const { getCurrentUserApi, getUserStoresApi } = loadModule("./api") as typeof import("./api");
    const currentUser = await getCurrentUserApi();
    const userStores = await getUserStoresApi("user/1");

    assert.equal(currentUser.mustChangePassword, true, "须改密标记必须从 PascalCase 响应映射出来");

    assert.deepEqual(
      currentUser.stores.map((store) => store.isActive),
      [false, false, undefined, undefined],
      "current-user stores must preserve false without coercing missing or string values",
    );
    assert.deepEqual(
      userStores.map(({ storeCode, isActive, isPrimary }) => ({ storeCode, isActive, isPrimary })),
      [
        { storeCode: "ACTIVE", isActive: true, isPrimary: false },
        { storeCode: "INACTIVE", isActive: false, isPrimary: true },
      ],
      "user-store endpoint must preserve active and primary flags independently",
    );
  } finally {
    moduleWithLoader._load = originalLoad;
  }
});
