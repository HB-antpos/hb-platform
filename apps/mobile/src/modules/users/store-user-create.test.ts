import assert from "node:assert/strict";
import Module from "node:module";

interface RequestRecord {
  body?: unknown;
  method: "post";
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

  // Node 测试只验证店员创建 HTTP 合同，不加载 Expo 原生运行时。
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
  const { createStoreUser, toSafeStoreUserErrorLog } = await import("./api");
  const requests: RequestRecord[] = [];
  const originalPost = apiClient.post;

  apiClient.post = (async (url: string, body?: unknown) => {
    requests.push({ body, method: "post", url });
    return {
      data: {
        userGuid: "user-created",
        username: "staff001",
        fullName: "Staff One",
        status: 1,
        storeCode: "S001",
        storeName: "Store One",
        roleNames: ["StoreStaff"],
      },
    };
  }) as typeof apiClient.post;

  try {
    const created = await createStoreUser({
      username: "staff001",
      fullName: "Staff One",
      email: "staff@example.com",
      phone: "0400000000",
      password: "secret123",
      passwordFormat: "raw",
      status: 1,
      storeCode: " S001 ",
      roleNames: ["Admin"],
      employmentType: "casual",
    });

    assert.deepEqual(requests, [
      {
        method: "post",
        url: "/react/v1/store-users",
        body: {
          username: "staff001",
          fullName: "Staff One",
          email: "staff@example.com",
          phone: "0400000000",
          password: "secret123",
          passwordFormat: "raw",
          status: 1,
          storeCode: "S001",
          roleNames: ["StoreStaff"],
          employmentType: "casual",
        },
      },
    ]);
    assert.equal(created.userGUID, "user-created");
    assert.deepEqual(created.roleNames, ["StoreStaff"]);
    // 登录关闭时 loginEnabled:false 原样透传，且不带密码 / 邮件发密码字段。
    requests.length = 0;
    await createStoreUser({
      username: "staff003",
      email: "staff3@example.com",
      status: 1,
      storeCode: "S001",
      loginEnabled: false,
    });
    assert.equal((requests[0].body as Record<string, unknown>).loginEnabled, false);
    assert.equal("password" in (requests[0].body as Record<string, unknown>), false);
    assert.equal("sendPasswordSetupEmail" in (requests[0].body as Record<string, unknown>), false);
    requests.length = 0;

    const initialPassword = "must-never-enter-logs";
    const safeErrorLog = toSafeStoreUserErrorLog({
      name: "AxiosError",
      code: "ERR_BAD_REQUEST",
      isAxiosError: true,
      config: {
        data: JSON.stringify({ password: initialPassword }),
      },
      response: {
        status: 400,
      },
    });
    assert.deepEqual(safeErrorLog, {
      name: "AxiosError",
      code: "ERR_BAD_REQUEST",
      status: 400,
    });
    assert.equal(
      JSON.stringify(safeErrorLog).includes(initialPassword),
      false,
      "安全日志元数据不得包含 Axios 请求体中的初始密码"
    );

    const originalError = new Error("create failed");
    apiClient.post = (async () => {
      throw originalError;
    }) as typeof apiClient.post;

    await assert.rejects(
      () => createStoreUser({
        username: "staff002",
        password: "secret123",
        passwordFormat: "raw",
        status: 1,
        storeCode: "S001",
      }),
      (error) => error === originalError,
      "创建接口错误必须原样透传"
    );
  } finally {
    apiClient.post = originalPost;
  }
}

void run();
