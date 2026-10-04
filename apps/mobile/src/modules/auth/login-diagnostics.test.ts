import assert from "node:assert/strict";
import {
  appendLoginStage,
  buildLoginDiagnosticLogItems,
  createLoginTrace,
  finishLoginTrace,
  LOGIN_DIAGNOSTICS_STORAGE_KEY,
  LoginStepTimeoutError,
  MAX_STORED_LOGIN_TRACES,
  parseStoredLoginTraces,
  selectLoginTracesToUpload,
  SLOW_LOGIN_THRESHOLD_MS,
  uploadPendingLoginDiagnostics,
  upsertLoginTrace,
  withLoginStepTimeout,
  type LoginTrace,
} from "./login-diagnostics";

const t0 = Date.parse("2026-10-02T08:45:09.000Z");
const trace = (id: string, patch: Partial<LoginTrace> = {}): LoginTrace => ({
  ...createLoginTrace({ traceId: id, username: " admin ", nowMs: t0, reviewGateActive: false }),
  ...patch,
});

async function run() {
  // 记录阶段：耗时相对开始时间，带审核闸门状态；用户名去空白
  let current = trace("t-1", { appVersion: "1.0.7", appBuildVersion: "55" });
  assert.equal(current.username, "admin");
  current = appendLoginStage(current, "loginApi", t0 + 72, false);
  current = appendLoginStage(current, "saveAccessToken", t0 + 90, true);
  assert.deepEqual(current.stages.map((stage) => [stage.stage, stage.elapsedMs, stage.reviewGateActive]), [
    ["start", 0, false],
    ["loginApi", 72, false],
    ["saveAccessToken", 90, true],
  ]);

  // 超时：失败阶段取超时那一步，错误码保留
  const timedOut = finishLoginTrace(current, "timeout", t0 + 8100, new LoginStepTimeoutError("saveRefreshToken", 8000));
  assert.equal(timedOut.failedStage, "saveRefreshToken");
  assert.equal(timedOut.errorCode, "LOGIN_STEP_TIMEOUT");
  assert.equal(timedOut.totalMs, 8100);

  // 普通失败：不猜失败阶段（看 stages 最后一项），记录 HTTP 状态
  const rejected = finishLoginTrace(trace("t-2"), "failed", t0 + 100, { response: { status: 401 }, message: "用户名或密码错误" });
  assert.equal(rejected.failedStage, undefined);
  assert.equal(rejected.httpStatus, 401);

  // 上传筛选：未完成（且非进行中）、超时、非账号原因失败、慢登录要传；正常登录、输错密码、进行中的不传
  const abandoned = trace("abandoned", { stages: [...current.stages] });
  // 与真实流程一致：登录接口 401 时最后完成的是 localClear
  const wrongPassword = finishLoginTrace(appendLoginStage(trace("wrong"), "localClear", t0 + 5, false), "failed", t0 + 80, { response: { status: 401 } });
  // 生产实际形状：密码错误时登录接口返回 HTTP 200 + success:false，拦截器解包成带 apiBusinessError 的 Error，没有 response.status
  const businessError = (message: string) => Object.assign(new Error(message), { apiBusinessError: true });
  const wrongPasswordEnvelope = finishLoginTrace(appendLoginStage(trace("wrong-envelope"), "localClear", t0 + 5, false), "failed", t0 + 80, businessError("用户名或密码错误"));
  assert.equal(wrongPasswordEnvelope.httpStatus, undefined);
  assert.equal(wrongPasswordEnvelope.apiBusinessError, true);
  const serverError = finishLoginTrace(appendLoginStage(trace("server"), "localClear", t0 + 5, false), "failed", t0 + 80, { response: { status: 500 } });
  // 登录接口已成功、之后的 auth/current 返回 403：不是账号原因，要上传
  const laterForbidden = finishLoginTrace(appendLoginStage(trace("later403"), "loginApi", t0 + 70, false), "failed", t0 + 90, { response: { status: 403 } });
  // 登录接口已成功、之后的步骤返回业务失败：同样不是账号原因，要上传
  const laterBusiness = finishLoginTrace(appendLoginStage(trace("laterBusiness"), "loginApi", t0 + 70, false), "failed", t0 + 90, businessError("门店未分配"));
  const fast = finishLoginTrace(trace("fast"), "success", t0 + 300);
  const slow = finishLoginTrace(trace("slow"), "success", t0 + SLOW_LOGIN_THRESHOLD_MS);
  const menuSlow = { ...finishLoginTrace(trace("menu"), "success", t0 + 500), menuTimedOut: true };
  const inProgress = trace("in-progress");
  assert.deepEqual(
    selectLoginTracesToUpload(
      [abandoned, timedOut, wrongPassword, wrongPasswordEnvelope, serverError, laterForbidden, laterBusiness, fast, slow, menuSlow, inProgress],
      "in-progress",
    ).map((item) => item.traceId),
    ["abandoned", "t-1", "server", "later403", "laterBusiness", "slow", "menu"],
  );

  // 只保留最近几条，同一 traceId 覆盖
  let stored: LoginTrace[] = [];
  for (let index = 0; index < MAX_STORED_LOGIN_TRACES + 2; index += 1) stored = upsertLoginTrace(stored, trace(`n-${index}`));
  assert.equal(stored.length, MAX_STORED_LOGIN_TRACES);
  assert.equal(stored[0].traceId, "n-2");
  stored = upsertLoginTrace(stored, { ...trace("n-3"), outcome: "success" });
  assert.equal(stored.filter((item) => item.traceId === "n-3").length, 1);
  assert.equal(stored[stored.length - 1].outcome, "success");

  // 存储内容损坏时当作空
  assert.deepEqual(parseStoredLoginTraces("{oops"), []);
  assert.deepEqual(parseStoredLoginTraces(JSON.stringify([{ foo: 1 }, trace("ok")])).map((item) => item.traceId), ["ok"]);

  // 日志条目：traceId 作幂等键，未完成记为 abandoned 的 Warning
  const [abandonedItem] = buildLoginDiagnosticLogItems([abandoned], "production");
  assert.equal(abandonedItem.clientEventId, "abandoned");
  assert.equal(abandonedItem.level, "Warning");
  assert.equal(abandonedItem.category, "auth.login");
  assert.equal(abandonedItem.properties.outcome, "abandoned");
  assert.equal(abandonedItem.properties.lastStage, "saveAccessToken");
  assert.match(abandonedItem.message, /App 关闭前停在 saveAccessToken/);
  assert.equal(buildLoginDiagnosticLogItems([timedOut], "production")[0].appVersion, "1.0.7+55");

  // 补传：成功后只留进行中的记录
  const memory = new Map<string, string>();
  const storage = {
    getString: async (key: string) => memory.get(key) ?? null,
    setString: async (key: string, value: string) => void memory.set(key, value),
  };
  memory.set(LOGIN_DIAGNOSTICS_STORAGE_KEY, JSON.stringify([abandoned, fast, inProgress]));
  const uploaded: unknown[] = [];
  assert.equal(
    await uploadPendingLoginDiagnostics({
      storage,
      upload: async (items) => void uploaded.push(...items),
      environment: "production",
      currentTraceId: "in-progress",
    }),
    "uploaded",
  );
  assert.equal(uploaded.length, 1);
  assert.deepEqual(parseStoredLoginTraces(memory.get(LOGIN_DIAGNOSTICS_STORAGE_KEY)!).map((item) => item.traceId), ["in-progress"]);

  // 网络失败保留，服务端未启用（403）丢弃
  memory.set(LOGIN_DIAGNOSTICS_STORAGE_KEY, JSON.stringify([abandoned]));
  assert.equal(
    await uploadPendingLoginDiagnostics({ storage, upload: async () => { throw new Error("Network Error"); }, environment: "production" }),
    "kept",
  );
  assert.equal(parseStoredLoginTraces(memory.get(LOGIN_DIAGNOSTICS_STORAGE_KEY)!).length, 1);
  // 接口尚未上线（OTA 先于后端）也保留
  assert.equal(
    await uploadPendingLoginDiagnostics({ storage, upload: async () => { throw { response: { status: 404 } }; }, environment: "production" }),
    "kept",
  );
  assert.equal(
    await uploadPendingLoginDiagnostics({ storage, upload: async () => { throw { response: { status: 403 } }; }, environment: "production" }),
    "dropped",
  );
  assert.equal(parseStoredLoginTraces(memory.get(LOGIN_DIAGNOSTICS_STORAGE_KEY)!).length, 0);

  // 无需上传时清掉已结束的正常记录
  memory.set(LOGIN_DIAGNOSTICS_STORAGE_KEY, JSON.stringify([fast]));
  assert.equal(await uploadPendingLoginDiagnostics({ storage, upload: async () => assert.fail("不应上传"), environment: "production" }), "skipped");
  assert.equal(parseStoredLoginTraces(memory.get(LOGIN_DIAGNOSTICS_STORAGE_KEY)!).length, 0);

  // 步骤超时：卡住的 promise 会被超时打断，正常 promise 原样返回
  assert.equal(await withLoginStepTimeout(Promise.resolve("ok"), "saveUser", 50), "ok");
  await assert.rejects(
    withLoginStepTimeout(new Promise(() => undefined), "saveAccessToken", 20),
    (error: unknown) => error instanceof LoginStepTimeoutError && error.stage === "saveAccessToken" && error.code === "LOGIN_STEP_TIMEOUT",
  );

  console.log("login-diagnostics tests passed");
}

void run().catch((error) => {
  console.error(error);
  process.exit(1);
});
