// 退出登录注销推送设备：3 秒时限、失败吞掉、不弹授权框、审核态零请求、等待进行中的注册。
import assert from "node:assert/strict";
import { registerPushToken } from "./push-registration-core";
import { PUSH_UNREGISTER_TIMEOUT_MS, unregisterPushToken, type PushUnregisterDeps, type PushUnregisterResult } from "./push-unregister-core";
import { ACCOUNT_A, ACCOUNT_B, TEST_TOKEN, createRegistrationHarness } from "./test-support";

type Harness = ReturnType<typeof createRegistrationHarness>;

function buildDeps(h: Harness, deleteToken?: PushUnregisterDeps["deleteToken"], timeoutMs = 200) {
  const deletes: { token: string; accountGuid: string; signal: AbortSignal }[] = [];
  const deps: PushUnregisterDeps = {
    state: h.state,
    projectId: h.deps.projectId,
    now: h.deps.now,
    getSession: () => h.session.current,
    loadNative: h.deps.loadNative,
    deleteToken: async (token, accountGuid, signal) => {
      deletes.push({ token, accountGuid, signal });
      await deleteToken?.(token, accountGuid, signal);
    },
    timeoutMs,
  };
  return { deps, deletes };
}

const skipped = (reason: string): PushUnregisterResult => ({ status: "skipped", reason: reason as never });

async function run() {
  assert.equal(PUSH_UNREGISTER_TIMEOUT_MS, 3000, "注销时限是 3 秒");

  // ───────── 注册后注销：用内存里的令牌，随后清空本进程记录 ─────────
  {
    const h = createRegistrationHarness();
    await registerPushToken({ deps: h.deps });
    const epochBefore = h.state.epoch;
    const { deps, deletes } = buildDeps(h);
    h.fake.calls.length = 0;
    const startedAt = Date.now();
    assert.deepEqual(await unregisterPushToken({ deps }), { status: "unregistered" });
    assert.equal(deletes.length, 1);
    assert.equal(deletes[0].token, TEST_TOKEN);
    assert.equal(deletes[0].accountGuid, ACCOUNT_A);
    assert.equal(deletes[0].signal.aborted, false, "成功时不中止请求");
    assert.deepEqual(h.fake.calls, [], "内存里已有令牌：不再访问原生 / Expo 服务");
    assert.equal(h.state.uploaded, null);
    assert.equal(h.state.held, null);
    assert.equal(h.state.epoch, epochBefore + 1);
    assert.ok(Date.now() - startedAt < 150, "正常路径立即返回，不等超时计时器");

    // 注销后同一账号再登录：重新上报
    assert.deepEqual(await registerPushToken({ deps: h.deps }), { status: "registered" });
    assert.equal(h.counters.uploads.length, 2);
  }

  // ───────── 超时 3 秒：放弃并中止请求，不阻塞 ─────────
  {
    const h = createRegistrationHarness();
    await registerPushToken({ deps: h.deps });
    let seenSignal: AbortSignal | null = null;
    const { deps } = buildDeps(h, (_token, _guid, signal) => {
      seenSignal = signal;
      return new Promise<void>(() => undefined); // 永不返回
    }, 40);
    const startedAt = Date.now();
    assert.deepEqual(await unregisterPushToken({ deps }), { status: "failed", reason: "timeout" });
    const elapsed = Date.now() - startedAt;
    assert.ok(elapsed >= 35 && elapsed < 400, `按时限放弃（实际 ${elapsed}ms）`);
    assert.equal((seenSignal as AbortSignal | null)?.aborted, true, "超时后中止请求");
    assert.equal(h.state.held, null, "超时也要丢弃本进程记录");
    assert.equal(h.state.uploaded, null);
  }

  // ───────── 失败一律吞掉（404、后端未上线、网络） ─────────
  for (const failure of [
    Object.assign(new Error("Request failed with status code 404"), { response: { status: 404 } }),
    new Error("Network Error"),
    Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" }),
  ]) {
    const h = createRegistrationHarness();
    await registerPushToken({ deps: h.deps });
    const { deps } = buildDeps(h, async () => {
      throw failure;
    });
    assert.deepEqual(await unregisterPushToken({ deps }), { status: "failed", reason: "request-failed" }, "失败不抛出");
    assert.equal(h.state.held, null);
  }
  {
    // getSession 抛异常也不能向外抛
    const h = createRegistrationHarness();
    const { deps } = buildDeps(h);
    deps.getSession = () => {
      throw new Error("boom");
    };
    assert.deepEqual(await unregisterPushToken({ deps }), { status: "failed", reason: "request-failed" });
  }

  // ───────── iOS 审核态 / 未登录：零请求 ─────────
  {
    const h = createRegistrationHarness();
    await registerPushToken({ deps: h.deps });
    h.session.current = { iosReview: true, accountGuid: ACCOUNT_A };
    h.counters.loadNative = 0;
    h.fake.calls.length = 0;
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), skipped("ios-review"));
    assert.equal(deletes.length, 0, "审核态零网络请求");
    assert.equal(h.counters.loadNative, 0);
    assert.deepEqual(h.fake.calls, []);
  }
  {
    const h = createRegistrationHarness({ session: { iosReview: false, accountGuid: null } });
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), skipped("not-signed-in"));
    assert.equal(deletes.length, 0);
    assert.equal(h.counters.loadNative, 0);
  }

  // ───────── 内存里没有令牌：只有「权限早已授予」才补取，且绝不弹授权框 ─────────
  {
    const h = createRegistrationHarness({ permission: "granted" });
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), { status: "unregistered" });
    assert.equal(deletes[0].token, TEST_TOKEN);
    assert.deepEqual(h.fake.calls, ["getPermission", "getToken"]);
  }
  for (const permission of ["undetermined", "denied"] as const) {
    const h = createRegistrationHarness({ permission });
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), skipped("no-token"));
    assert.equal(deletes.length, 0);
    assert.equal(h.fake.calls.includes("requestPermission"), false, "注销时绝不弹授权框");
    assert.equal(h.fake.calls.includes("getToken"), false, "权限未授予不取令牌");
  }
  {
    // 原生模块缺失 / 模拟器：没有令牌可注销
    for (const options of [{ load: "missing" as const }, { isPhysicalDevice: false }]) {
      const h = createRegistrationHarness(options);
      const { deps, deletes } = buildDeps(h);
      assert.deepEqual(await unregisterPushToken({ deps }), skipped("no-token"));
      assert.equal(deletes.length, 0);
    }
  }
  {
    // 取令牌失败的闸门（PDA）：节流期内 / 达到次数上限后不再尝试取令牌
    const h = createRegistrationHarness({ token: new Error("SERVICE_NOT_AVAILABLE") });
    await registerPushToken({ deps: h.deps });
    h.fake.calls.length = 0;
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), skipped("no-token"));
    assert.deepEqual(h.fake.calls, [], "刚失败过：退出登录时不再折腾 PDA");
    assert.equal(deletes.length, 0);
  }
  {
    // 补取令牌时抛错：吞掉
    const h = createRegistrationHarness({ permission: "granted", token: new Error("offline") });
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), skipped("no-token"));
    assert.equal(deletes.length, 0);
  }
  {
    // 内存里的令牌属于另一个账号：不能拿去注销当前账号
    const h = createRegistrationHarness({ permission: "denied" });
    h.state.held = { accountGuid: ACCOUNT_B, token: "ExponentPushToken[other]" };
    const { deps, deletes } = buildDeps(h);
    assert.deepEqual(await unregisterPushToken({ deps }), skipped("no-token"));
    assert.equal(deletes.length, 0);
  }

  // ───────── 进行中的注册先落定，保证先 POST 后 DELETE ─────────
  {
    const order: string[] = [];
    let releaseUpload: () => void = () => undefined;
    const h = createRegistrationHarness({
      upload: () =>
        new Promise<void>((resolve) => {
          releaseUpload = () => {
            order.push("post-done");
            resolve();
          };
        }),
      uploadTimeoutMs: 1000,
    });
    const registering = registerPushToken({ deps: h.deps });
    await new Promise((resolve) => setTimeout(resolve, 10));
    const { deps, deletes } = buildDeps(h, async () => {
      order.push("delete");
    }, 1000);
    const unregistering = unregisterPushToken({ deps });
    await new Promise((resolve) => setTimeout(resolve, 10));
    assert.equal(deletes.length, 0, "注册还没结束，DELETE 先不发");
    releaseUpload();
    assert.deepEqual(await registering, { status: "registered" });
    assert.deepEqual(await unregistering, { status: "unregistered" });
    assert.deepEqual(order, ["post-done", "delete"]);
    assert.equal(h.state.uploaded, null, "注销后不会因为注册晚完成而又留下记录");
    assert.equal(h.state.held, null);
  }
  {
    // 进行中的注册迟迟不结束：注销按 3 秒时限放弃，不被拖住
    const h = createRegistrationHarness({ upload: () => new Promise<void>(() => undefined), uploadTimeoutMs: 300 });
    void registerPushToken({ deps: h.deps });
    await new Promise((resolve) => setTimeout(resolve, 10));
    const { deps, deletes } = buildDeps(h, undefined, 40);
    const startedAt = Date.now();
    assert.deepEqual(await unregisterPushToken({ deps }), { status: "failed", reason: "timeout" });
    assert.ok(Date.now() - startedAt < 400);
    assert.equal(deletes.length, 0, "超时后不会发出 DELETE");
    // 等后台那次注册的上报超时（300ms）自然结束，再确认超时放弃的注销之后也没有补发 DELETE（signal 已中止）
    await new Promise((resolve) => setTimeout(resolve, 400));
    assert.equal(deletes.length, 0, "进行中的注册结束后，也不会补发 DELETE");
  }

  console.log("push-unregister-core.test.ts: ok");
}

void run();
