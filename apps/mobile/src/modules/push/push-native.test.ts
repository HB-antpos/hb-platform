// 原生适配层：用假的 expo-notifications / expo-device 验证映射；真实加载器在 Node（没有原生模块）下必须得到 null，而不是抛错。
import assert from "node:assert/strict";
import { __resetPushNativeForTests, createPushNative, loadPushNative } from "./push-native";
import { PUSH_FOREGROUND_BEHAVIOR } from "./push-types";

type NotificationsArg = Parameters<typeof createPushNative>[0];

const DEFAULT_ACTION = "expo.modules.notifications.actions.DEFAULT";

function fakeNotifications(overrides: Record<string, unknown> = {}) {
  const calls: { name: string; args: unknown[] }[] = [];
  const record = <T>(name: string, result: T) => (...args: unknown[]) => {
    calls.push({ name, args });
    return result;
  };
  let handler: { handleNotification: () => Promise<unknown> } | null = null;
  let responseListener: ((raw: unknown) => void) | null = null;
  const module = {
    DEFAULT_ACTION_IDENTIFIER: DEFAULT_ACTION,
    AndroidImportance: { HIGH: 4 },
    setNotificationChannelAsync: async (...args: unknown[]) => record("setNotificationChannelAsync", null)(...args),
    getPermissionsAsync: async () => ({ status: "granted", granted: true }),
    requestPermissionsAsync: async () => ({ status: "granted", granted: true }),
    getExpoPushTokenAsync: async (options: unknown) => {
      calls.push({ name: "getExpoPushTokenAsync", args: [options] });
      return { type: "expo", data: "ExponentPushToken[abc]" };
    },
    setNotificationHandler: (next: typeof handler) => {
      handler = next;
    },
    addNotificationResponseReceivedListener: (listener: (raw: unknown) => void) => {
      responseListener = listener;
      return { remove: () => calls.push({ name: "remove", args: [] }) };
    },
    getLastNotificationResponse: () => null,
    clearLastNotificationResponse: () => {
      calls.push({ name: "clearLastNotificationResponse", args: [] });
    },
    ...overrides,
  };
  return {
    module: module as unknown as NotificationsArg,
    calls,
    handler: () => handler,
    emitResponse: (raw: unknown) => responseListener?.(raw),
  };
}

const rawResponse = (overrides: Record<string, unknown> = {}) => ({
  actionIdentifier: DEFAULT_ACTION,
  notification: {
    request: {
      identifier: "req-1",
      content: { title: "SECRET TITLE", body: "SECRET BODY", data: { notificationId: "n1", type: "face.unwelcome-hit" } },
    },
  },
  ...overrides,
});

async function run() {
  // ───────── 真机判断、Android 渠道 ─────────
  {
    const f = fakeNotifications();
    const android = createPushNative(f.module, { isDevice: true }, { platformOS: "android", getChannelName: () => "HB 通知" });
    assert.equal(android.isPhysicalDevice, true);
    await android.ensureAndroidChannel();
    assert.equal(f.calls[0].name, "setNotificationChannelAsync");
    assert.deepEqual(f.calls[0].args, ["default", { name: "HB 通知", importance: 4 }], "渠道 id 必须是 default（服务端发 channelId: default），重要性 HIGH");

    const g = fakeNotifications();
    const ios = createPushNative(g.module, { isDevice: false }, { platformOS: "ios", getChannelName: () => "x" });
    assert.equal(ios.isPhysicalDevice, false);
    await ios.ensureAndroidChannel();
    assert.equal(g.calls.length, 0, "iOS 没有通知渠道，不调用");
  }

  // ───────── 权限映射 ─────────
  {
    const cases: [Record<string, unknown>, string][] = [
      [{ status: "granted", granted: true }, "granted"],
      [{ status: "undetermined", granted: false }, "undetermined"],
      [{ status: "denied", granted: false }, "denied"],
      // Android 13+ 从未询问过：denied 但 canAskAgain 为 true → 必须当作未决定，否则永远不会弹授权框
      [{ status: "denied", granted: false, canAskAgain: true }, "undetermined"],
      [{ status: "denied", granted: false, canAskAgain: false }, "denied"],
      [{ status: "denied", granted: true }, "granted"],
      [{}, "denied"],
    ];
    for (const [response, expected] of cases) {
      const f = fakeNotifications({ getPermissionsAsync: async () => response, requestPermissionsAsync: async () => response });
      const native = createPushNative(f.module, { isDevice: true }, { platformOS: "ios", getChannelName: () => "x" });
      assert.equal(await native.getPermissionStatus(), expected);
      assert.equal(await native.requestPermission(), expected);
    }
  }

  // ───────── 取令牌：带 projectId，只交出令牌字符串 ─────────
  {
    const f = fakeNotifications();
    const native = createPushNative(f.module, { isDevice: true }, { platformOS: "android", getChannelName: () => "x" });
    assert.equal(await native.getExpoPushToken("proj-1"), "ExponentPushToken[abc]");
    assert.deepEqual(f.calls.find((c) => c.name === "getExpoPushTokenAsync")?.args, [{ projectId: "proj-1" }]);
  }

  // ───────── 前台处理器：不弹横幅、不响铃、不改角标，并触发刷新 ─────────
  {
    const f = fakeNotifications();
    const native = createPushNative(f.module, { isDevice: true }, { platformOS: "ios", getChannelName: () => "x" });
    let refreshed = 0;
    native.installForegroundHandler(() => {
      refreshed += 1;
    });
    const behavior = await f.handler()?.handleNotification();
    assert.deepEqual(behavior, { shouldShowBanner: false, shouldShowList: false, shouldPlaySound: false, shouldSetBadge: false });
    assert.deepEqual(behavior, { ...PUSH_FOREGROUND_BEHAVIOR });
    assert.equal(refreshed, 1);
    // 刷新回调抛错：仍返回「不展示」策略
    native.installForegroundHandler(() => {
      throw new Error("boom");
    });
    assert.deepEqual(await f.handler()?.handleNotification(), { ...PUSH_FOREGROUND_BEHAVIOR });
  }

  // ───────── 点击：归一化，原始标题 / 正文不越过适配层 ─────────
  {
    const f = fakeNotifications();
    const native = createPushNative(f.module, { isDevice: true }, { platformOS: "ios", getChannelName: () => "x" });
    const received: unknown[] = [];
    const subscription = native.addResponseListener((response) => received.push(response));
    f.emitResponse(rawResponse());
    f.emitResponse(rawResponse({ actionIdentifier: "expo.modules.notifications.actions.DISMISS" }));
    f.emitResponse({ actionIdentifier: DEFAULT_ACTION, notification: { request: { content: { data: "not-an-object" } } } });
    f.emitResponse({});
    assert.deepEqual(received[0], { id: "req-1", isDefaultAction: true, type: "face.unwelcome-hit", data: { notificationId: "n1", type: "face.unwelcome-hit" } });
    assert.equal((received[1] as { isDefaultAction: boolean }).isDefaultAction, false, "非默认动作不算点击通知本身");
    assert.deepEqual(received[2], { id: null, isDefaultAction: true, type: null, data: {} }, "data 不是对象 → 空");
    assert.deepEqual(received[3], { id: null, isDefaultAction: false, type: null, data: {} });
    assert.equal(JSON.stringify(received).includes("SECRET"), false, "标题 / 正文不会交给上层");
    subscription.remove();
    assert.ok(f.calls.some((c) => c.name === "remove"));
  }

  // ───────── 冷启动最近一次点击 ─────────
  {
    const f = fakeNotifications({ getLastNotificationResponse: () => rawResponse() });
    const native = createPushNative(f.module, { isDevice: true }, { platformOS: "ios", getChannelName: () => "x" });
    const last = native.getLastResponse();
    assert.equal(last?.id, "req-1");
    assert.equal(last?.type, "face.unwelcome-hit");
    native.clearLastResponse();
    assert.ok(f.calls.some((c) => c.name === "clearLastNotificationResponse"));

    // 原生不支持 / 抛错：返回 null，不抛
    const g = fakeNotifications({
      getLastNotificationResponse: () => {
        throw new Error("UnavailabilityError");
      },
      clearLastNotificationResponse: () => {
        throw new Error("UnavailabilityError");
      },
    });
    const nativeG = createPushNative(g.module, { isDevice: true }, { platformOS: "ios", getChannelName: () => "x" });
    assert.equal(nativeG.getLastResponse(), null);
    assert.doesNotThrow(() => nativeG.clearLastResponse());
  }

  // ───────── 真实加载器：当前环境没有原生模块 → null（旧安装包上的行为），且结果被缓存 ─────────
  {
    __resetPushNativeForTests();
    const first = await loadPushNative({ platformOS: "android", getChannelName: () => "x" });
    assert.equal(first, null, "没有原生模块时静默得到 null，不抛错");
    const second = loadPushNative({ platformOS: "android", getChannelName: () => "x" });
    assert.equal(await second, null);
    __resetPushNativeForTests();
  }

  console.log("push-native.test.ts: ok");
}

void run();
