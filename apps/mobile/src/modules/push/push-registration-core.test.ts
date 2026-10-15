// 推送令牌注册：跳过原因矩阵、节流、去重、载荷字段、静默失败。
import assert from "node:assert/strict";
import {
  PUSH_MAX_UNAVAILABLE_ATTEMPTS,
  PUSH_UNAVAILABLE_RETRY_AFTER_MS,
  registerPushToken,
  type PushRegistrationResult,
} from "./push-registration-core";
import { ACCOUNT_A, ACCOUNT_B, TEST_TOKEN, createRegistrationHarness } from "./test-support";

const HOUR = PUSH_UNAVAILABLE_RETRY_AFTER_MS;
const skipped = (reason: string): PushRegistrationResult => ({ status: "skipped", reason: reason as never });
const NO_GMS = new Error("java.io.IOException: SERVICE_NOT_AVAILABLE");

async function run() {
  assert.equal(HOUR, 60 * 60 * 1000, "节流间隔是 1 小时");
  assert.equal(PUSH_MAX_UNAVAILABLE_ATTEMPTS, 2, "首次 + 最多再试 1 次");

  // ───────── 正常注册：顺序与载荷 ─────────
  {
    const h = createRegistrationHarness({ platform: "android" });
    const result = await registerPushToken({ deps: h.deps });
    assert.deepEqual(result, { status: "registered" });
    // 先查权限，再建渠道、取令牌；权限已授予不弹窗
    assert.deepEqual(h.fake.calls, ["getPermission", "channel", "getToken"]);
    assert.deepEqual(h.fake.projectIds, ["project-1"]);
    assert.equal(h.counters.uploads.length, 1);
    assert.deepEqual(h.counters.uploads[0], {
      accountGuid: ACCOUNT_A,
      payload: { expoPushToken: TEST_TOKEN, platform: "android", appVersion: "1.0.11", runtimeVersion: "1.0.11" },
    });
    assert.equal(h.state.lastResult, "registered");
    assert.equal(h.state.lastResult?.includes("ExponentPushToken"), false, "状态短码里没有令牌");
  }
  {
    const h = createRegistrationHarness({ platform: "ios" });
    await registerPushToken({ deps: h.deps });
    assert.equal(h.counters.uploads[0].payload.platform, "ios");
  }
  {
    // 版本信息缺失时不出现在载荷里
    const h = createRegistrationHarness();
    h.deps.getAppInfo = () => ({ appVersion: null, runtimeVersion: undefined });
    await registerPushToken({ deps: h.deps });
    assert.deepEqual(h.counters.uploads[0].payload, { expoPushToken: TEST_TOKEN, platform: "android" });
  }

  // ───────── 跳过原因矩阵 ─────────
  {
    // iOS 审核态：零网络请求、零原生加载（连原生模块都不碰）
    const h = createRegistrationHarness({ session: { iosReview: true, accountGuid: ACCOUNT_A }, platform: "ios" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("ios-review"));
    assert.equal(h.counters.loadNative, 0, "审核态不得加载原生模块");
    assert.deepEqual(h.fake.calls, [], "审核态不得触碰任何原生能力（取令牌会访问 Expo 服务）");
    assert.equal(h.counters.uploads.length, 0, "审核态零上报");
  }
  {
    // 审核态优先于未登录判断，两者都满足时仍记审核态
    const h = createRegistrationHarness({ session: { iosReview: true, accountGuid: null } });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("ios-review"));
  }
  {
    // 未登录 / 设备会话（accountGuid 为 null）
    const h = createRegistrationHarness({ session: { iosReview: false, accountGuid: null } });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("not-signed-in"));
    assert.equal(h.counters.loadNative, 0);
    assert.equal(h.counters.uploads.length, 0);
  }
  {
    const h = createRegistrationHarness({ platform: null });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unsupported-platform"));
    assert.equal(h.counters.loadNative, 0);
  }
  {
    // 原生模块缺失（旧安装包）：静默跳过，不节流计数
    const h = createRegistrationHarness({ load: "missing" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("native-module-missing"));
    assert.equal(h.counters.uploads.length, 0);
    assert.equal(h.state.unavailableCount, 0, "原生模块缺失不属于「取令牌失败」");
  }
  {
    // 加载抛出异常也不能向外抛
    const h = createRegistrationHarness({ load: "throw" });
    const result = await registerPushToken({ deps: h.deps });
    assert.equal(result.status, "skipped");
    assert.equal(h.counters.uploads.length, 0);
  }
  {
    // 模拟器 / 模拟机
    const h = createRegistrationHarness({ isPhysicalDevice: false });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("not-device"));
    assert.deepEqual(h.fake.calls, []);
  }
  {
    const h = createRegistrationHarness({ projectId: null });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("no-project-id"));
    assert.deepEqual(h.fake.calls, []);
  }
  {
    // 权限已被拒绝：不再弹窗、不取令牌、不上报
    const h = createRegistrationHarness({ permission: "denied" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("permission-denied"));
    assert.deepEqual(h.fake.calls, ["getPermission"]);
    assert.equal(h.counters.uploads.length, 0);
    // 再来一次仍然不弹
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("permission-denied"));
    assert.equal(h.fake.calls.filter((c) => c === "requestPermission").length, 0);
  }
  {
    // 读权限状态抛错：按「不可用」处理
    const h = createRegistrationHarness({ permissionError: new Error("native") });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unavailable"));
    assert.equal(h.state.unavailableCount, 1);
  }

  // ───────── 权限「未决定」：先取令牌，再弹系统授权框 ─────────
  {
    const h = createRegistrationHarness({ permission: "undetermined", permissionAfterRequest: "granted" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.deepEqual(h.fake.calls, ["getPermission", "channel", "getToken", "requestPermission"], "渠道在弹窗前建好（Android 13+ 的要求），令牌在弹窗前取");
    assert.equal(h.counters.uploads.length, 1);
  }
  {
    // iOS：真机实测权限没被询问过时苹果不回调设备令牌（取令牌悬着），必须先请求权限、再取令牌
    const h = createRegistrationHarness({ platform: "ios", permission: "undetermined", permissionAfterRequest: "granted" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.deepEqual(h.fake.calls, ["getPermission", "requestPermission", "channel", "getToken"], "iOS 先弹授权框，授权后才取令牌");
    assert.equal(h.counters.uploads.length, 1);
  }
  {
    // iOS 授权框被拒绝：不取令牌、不上报，之后不再弹
    const h = createRegistrationHarness({ platform: "ios", permission: "undetermined", permissionAfterRequest: "denied" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("permission-denied"));
    assert.equal(h.fake.calls.includes("getToken"), false, "没授权就不取令牌");
    assert.equal(h.counters.uploads.length, 0);
    h.fake.calls.length = 0;
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("permission-denied"));
    assert.deepEqual(h.fake.calls, ["getPermission"], "被拒绝后不再反复弹窗");
  }
  {
    // iOS 已授权：不再请求权限，直接取令牌
    const h = createRegistrationHarness({ platform: "ios", permission: "granted" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.equal(h.fake.calls.includes("requestPermission"), false);
  }
  {
    // 弹窗被拒绝：不上报；之后状态变成 denied，不再弹
    const h = createRegistrationHarness({ permission: "undetermined", permissionAfterRequest: "denied" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("permission-denied"));
    assert.equal(h.counters.uploads.length, 0);
    assert.equal(h.state.held, null, "没授权就不保留令牌");
    h.fake.calls.length = 0;
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("permission-denied"));
    assert.deepEqual(h.fake.calls, ["getPermission"], "被拒绝后不再反复弹窗");
  }
  {
    // 没有 Google Play 服务的 PDA：取令牌失败，永远不会看到授权框
    const h = createRegistrationHarness({ permission: "undetermined", token: NO_GMS });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unavailable"));
    assert.equal(h.fake.calls.includes("requestPermission"), false, "取令牌失败时不弹授权框");
    assert.equal(h.counters.uploads.length, 0);
  }
  {
    // 渠道创建失败不影响取令牌与上报
    const h = createRegistrationHarness({ channelError: new Error("no channel") });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
  }

  // ───────── 取令牌失败：静默跳过 + 1 小时节流 + 最多再试 1 次 ─────────
  {
    const h = createRegistrationHarness({ token: NO_GMS });
    const first = await registerPushToken({ deps: h.deps });
    assert.deepEqual(first, skipped("unavailable"), "取令牌抛错 → 静默跳过，不抛");
    assert.equal(h.counters.uploads.length, 0);
    assert.equal(h.state.unavailableCount, 1);

    const tokenCalls = () => h.fake.calls.filter((c) => c === "getToken").length;
    assert.equal(tokenCalls(), 1);

    // 1 小时内（含回到前台）不再试：不碰原生
    for (const elapsed of [0, 60_000, 30 * 60_000, HOUR - 1]) {
      h.clock.nowMs = 1_700_000_000_000 + elapsed;
      const callsBefore = h.fake.calls.length;
      assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("throttled"), `距上次失败 ${elapsed}ms 仍节流`);
      assert.equal(h.fake.calls.length, callsBefore, "节流期间不得有任何原生调用");
      assert.equal(h.counters.loadNative, 1, "节流期间连加载原生模块都不做");
    }

    // 满 1 小时：允许再试 1 次，仍失败
    h.clock.nowMs = 1_700_000_000_000 + HOUR;
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unavailable"));
    assert.equal(tokenCalls(), 2, "总共只尝试 2 次");
    assert.equal(h.state.unavailableCount, 2);

    // 之后本进程内不再尝试，无论过去多久
    for (const elapsed of [HOUR, 2 * HOUR, 10 * HOUR, 100 * HOUR]) {
      h.clock.nowMs = 1_700_000_000_000 + HOUR + elapsed;
      assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unavailable"));
    }
    assert.equal(tokenCalls(), 2, "达到次数上限后不再重试");
    assert.equal(h.counters.uploads.length, 0, "全程没有任何上报");
  }
  {
    // 第二次成功：失败记录作废，正常注册
    const h = createRegistrationHarness({ token: NO_GMS });
    await registerPushToken({ deps: h.deps });
    h.clock.nowMs += HOUR;
    h.fake.state.token = TEST_TOKEN;
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.equal(h.state.unavailableCount, 0);
    assert.equal(h.state.lastUnavailableAtMs, null);
  }
  {
    // 令牌形态不对（空串、乱码）也算取令牌失败
    for (const bad of ["", "not-a-token", "ExponentPushToken[]", "ExponentPushToken[a b]"]) {
      const h = createRegistrationHarness({ token: bad });
      assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unavailable"), `令牌 ${JSON.stringify(bad)} 无效`);
      assert.equal(h.counters.uploads.length, 0);
    }
    const h = createRegistrationHarness({ token: "ExpoPushToken[xyz123]" });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" }, "ExpoPushToken[...] 形态也接受");
  }
  {
    // 取令牌悬挂（原生调用不返回）：超时后按 unavailable 处理，且不会卡住后续调用
    const h = createRegistrationHarness({ token: "hang", tokenTimeoutMs: 20 });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("unavailable"));
    assert.equal(h.state.inFlight, null);
  }

  // ───────── 会话内去重：同账号同令牌只上报一次 ─────────
  {
    const h = createRegistrationHarness();
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    const callsAfterFirst = h.fake.calls.length;
    for (let i = 0; i < 3; i += 1) {
      assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("already-registered"));
    }
    assert.equal(h.counters.uploads.length, 1, "回到前台多次也只上报一次");
    assert.equal(h.fake.calls.length, callsAfterFirst, "去重命中时不再访问原生（也就不再访问 Expo 服务）");

    // 换账号：重新上报同一令牌（后端把它转给当前账号）
    h.session.current = { iosReview: false, accountGuid: ACCOUNT_B };
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.equal(h.counters.uploads.length, 2);
    assert.equal(h.counters.uploads[1].accountGuid, ACCOUNT_B);
    // 再换回第一个账号：去重记录只保留最近一次，仍会重新上报，保证令牌最终归属当前账号
    h.session.current = { iosReview: false, accountGuid: ACCOUNT_A };
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.equal(h.counters.uploads.length, 3);
  }

  // ───────── 上报失败（含 404、超时）：静默，下次再试，且不重复取令牌 ─────────
  {
    let attempt = 0;
    const h = createRegistrationHarness({
      upload: async () => {
        attempt += 1;
        if (attempt === 1) throw Object.assign(new Error("Request failed with status code 404"), { response: { status: 404 } });
        if (attempt === 2) throw new Error("Network Error");
      },
    });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("upload-failed"), "404 静默");
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("upload-failed"), "网络失败静默");
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" }, "后端上线后下次回到前台就成功");
    assert.equal(h.counters.uploads.length, 3);
    assert.equal(h.fake.calls.filter((c) => c === "getToken").length, 1, "上报重试复用已取到的令牌，不重复访问 Expo 服务");
    assert.equal(h.state.unavailableCount, 0, "上报失败不计入取令牌失败，不触发 1 小时节流");
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("already-registered"));
  }
  {
    const h = createRegistrationHarness({ upload: () => new Promise<void>(() => undefined), uploadTimeoutMs: 20 });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("upload-failed"), "上报悬挂 → 超时静默");
    assert.equal(h.state.inFlight, null);
  }

  // ───────── 并发：后到的直接返回 in-flight ─────────
  {
    let release: () => void = () => undefined;
    const h = createRegistrationHarness({ upload: () => new Promise<void>((resolve) => (release = resolve)), uploadTimeoutMs: 1000 });
    const first = registerPushToken({ deps: h.deps });
    await new Promise((resolve) => setTimeout(resolve, 10));
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("in-flight"));
    release();
    assert.deepEqual(await first, { status: "registered" });
    assert.equal(h.counters.uploads.length, 1);
  }

  // ───────── 异步期间会话变化 ─────────
  {
    // 取令牌期间切换账号：不上报，也不记录去重
    const h = createRegistrationHarness();
    const original = h.fake.native.getExpoPushToken;
    h.fake.native.getExpoPushToken = async (projectId) => {
      h.session.current = { iosReview: false, accountGuid: ACCOUNT_B };
      return original(projectId);
    };
    assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("session-changed"));
    assert.equal(h.counters.uploads.length, 0);
    assert.equal(h.state.uploaded, null);
  }
  {
    // 取令牌期间进入审核态 / 登出：同样不上报
    for (const next of [{ iosReview: true, accountGuid: ACCOUNT_A }, { iosReview: false, accountGuid: null }]) {
      const h = createRegistrationHarness();
      const original = h.fake.native.getExpoPushToken;
      h.fake.native.getExpoPushToken = async (projectId) => {
        h.session.current = next;
        return original(projectId);
      };
      assert.deepEqual(await registerPushToken({ deps: h.deps }), skipped("session-changed"));
      assert.equal(h.counters.uploads.length, 0);
    }
  }
  {
    // 上报进行中发生了注销（epoch 变化）：完成后不得再记录 uploaded / held
    const h = createRegistrationHarness({
      upload: async () => {
        h.state.epoch += 1;
        h.state.uploaded = null;
        h.state.held = null;
      },
    });
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.equal(h.state.uploaded, null);
    assert.equal(h.state.held, null);
  }

  console.log("push-registration-core.test.ts: ok");
}

void run();
