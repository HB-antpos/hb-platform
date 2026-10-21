import assert from "node:assert/strict";
import test from "node:test";

import {
  ApiTransportSelector,
  classifyTransportFailure,
  createFailoverFetch,
  entryOrigins,
  isFailoverBaseUrl,
  isTransportRetryableMethod,
  parseEntryUrl,
  rewriteEntryHost,
} from "./transport-failover";

const EDGE = "pos.hotbargain.vip";
const DIRECT = "hotbargain.vip";

function createClock(start = 1_000_000) {
  let current = start;
  return {
    now: () => current,
    advanceMinutes(minutes: number) {
      current += minutes * 60_000;
    },
    advanceSeconds(seconds: number) {
      current += seconds * 1_000;
    },
  };
}

test("入口组只认 https 默认端口 + /pos-api 路径下的两个固定域名", () => {
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip/pos-api"), true);
  assert.equal(isFailoverBaseUrl("https://pos.hotbargain.vip/pos-api/"), true);
  assert.equal(isFailoverBaseUrl("https://HotBargain.vip/pos-api/api/v1/health"), true);
  // 以下都不是：明文、带端口、别的域名、别的路径、带账号密码、非法字符串。
  assert.equal(isFailoverBaseUrl("http://hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip:8443/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.group/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip/api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip/pos-api-evil"), false);
  assert.equal(isFailoverBaseUrl("https://evil.hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://user:pass@hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("http://192.168.31.247:5003"), false);
  assert.equal(isFailoverBaseUrl("not a url"), false);
});

test("入口组内的地址可以换 host，其余原样不变；origin 列表首选在前", () => {
  assert.equal(
    rewriteEntryHost("https://hotbargain.vip/pos-api", EDGE),
    "https://pos.hotbargain.vip/pos-api",
  );
  assert.equal(
    rewriteEntryHost("https://pos.hotbargain.vip/pos-api/api/v1/health?x=1", DIRECT),
    "https://hotbargain.vip/pos-api/api/v1/health?x=1",
  );
  assert.equal(rewriteEntryHost("https://hbpos.example", EDGE), "https://hbpos.example");
  assert.deepEqual(entryOrigins("https://hotbargain.vip/pos-api"), [
    "https://pos.hotbargain.vip",
    "https://hotbargain.vip",
  ]);
  assert.deepEqual(entryOrigins("https://hbpos.example"), []);
  assert.equal(parseEntryUrl("https://hbpos.example"), null);
});

test("默认走边缘入口；备用入口总是另一个", () => {
  const selector = new ApiTransportSelector();
  assert.equal(selector.resolve(), EDGE);
  assert.equal(selector.fallbackFor(EDGE), DIRECT);
  assert.equal(selector.fallbackFor(DIRECT), EDGE);
  assert.equal(selector.isUsingFallback(), false);
});

test("边缘入口连接失败：切到直连，并按 2/4/8/16/30 分钟递增冷却", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  assert.equal(selector.reportFailure(EDGE, "network"), true);
  assert.equal(selector.resolve(), DIRECT);
  assert.equal(selector.isUsingFallback(), true);

  // 2 分钟冷却内一直是直连；到期后回到边缘试探。
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), DIRECT);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE);

  // 试探又失败：冷却翻倍到 4 分钟。
  assert.equal(selector.reportFailure(EDGE, "network"), true);
  clock.advanceMinutes(3);
  assert.equal(selector.resolve(), DIRECT);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE);

  // 继续失败：8、16、30（封顶）。
  for (const minutes of [8, 16, 30, 30]) {
    assert.equal(selector.reportFailure(EDGE, "edge"), true);
    clock.advanceMinutes(minutes - 1);
    assert.equal(selector.resolve(), DIRECT, `冷却 ${minutes} 分钟内仍应走直连`);
    clock.advanceMinutes(1);
    assert.equal(selector.resolve(), EDGE, `冷却 ${minutes} 分钟后应回到边缘`);
  }
});

test("并发请求迟到的失败不重复升级退避：一次故障只算一级，且仍允许读请求换入口重试", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  assert.equal(selector.reportFailure(EDGE, "network"), true);
  // 同一时刻在途的另外 5 个请求也陆续失败：已在冷却中，返回 true 让它们换入口重试，但不升级退避。
  for (let index = 0; index < 5; index += 1) {
    assert.equal(selector.reportFailure(EDGE, "network"), true);
  }

  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), DIRECT);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE, "仍是 2 分钟冷却，而不是被并发失败顶到 30 分钟");
});

test("边缘入口恢复成功：清零退避，下次失败又从 2 分钟开始", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  selector.reportFailure(EDGE, "network");
  clock.advanceMinutes(2);
  assert.equal(selector.resolve(), EDGE);
  selector.reportSuccess(EDGE);
  assert.equal(selector.isUsingFallback(), false);

  selector.reportFailure(EDGE, "network");
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), DIRECT);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE, "退避已清零，冷却应回到 2 分钟");
});

test("直连入口失败不会切换；其他 host 的成功也不影响状态", () => {
  const selector = new ApiTransportSelector();
  assert.equal(selector.reportFailure(DIRECT, "network"), false);
  assert.equal(selector.resolve(), EDGE);
  selector.reportSuccess("hbpos.example");
  assert.equal(selector.resolve(), EDGE);
});

test("超时只有在边缘入口 60 秒内没成功过时才算线路故障", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  // 刚成功过：这次超时更像服务器慢（报表、目录同步），不切换。
  selector.reportSuccess(EDGE);
  clock.advanceSeconds(30);
  assert.equal(selector.reportFailure(EDGE, "timeout"), false);
  assert.equal(selector.resolve(), EDGE);

  // 超过 60 秒没成功过：判定线路黑洞，切换。
  clock.advanceSeconds(31);
  assert.equal(selector.reportFailure(EDGE, "timeout"), true);
  assert.equal(selector.resolve(), DIRECT);
});

test("失败分类：只有网络层失败和 520~530 才算线路问题", () => {
  assert.equal(classifyTransportFailure({ code: "ERR_NETWORK" }), "network");
  assert.equal(classifyTransportFailure({ code: "ECONNABORTED" }), "timeout");
  assert.equal(classifyTransportFailure({ code: "ETIMEDOUT" }), "timeout");
  assert.equal(classifyTransportFailure({ status: 520 }), "edge");
  assert.equal(classifyTransportFailure({ status: 524 }), "edge");
  assert.equal(classifyTransportFailure({ status: 530 }), "edge");
  assert.equal(classifyTransportFailure({ status: 519 }), null);
  assert.equal(classifyTransportFailure({ status: 531 }), null);
  assert.equal(classifyTransportFailure({ status: 500 }), null);
  assert.equal(classifyTransportFailure({ status: 401 }), null);
  assert.equal(classifyTransportFailure({ code: "ERR_NETWORK", isCancel: true }), null);
  assert.equal(classifyTransportFailure({}), null);
});

test("只有 GET / HEAD 允许自动换入口重试", () => {
  assert.equal(isTransportRetryableMethod("GET"), true);
  assert.equal(isTransportRetryableMethod("head"), true);
  assert.equal(isTransportRetryableMethod(undefined), true);
  assert.equal(isTransportRetryableMethod("POST"), false);
  assert.equal(isTransportRetryableMethod("PUT"), false);
});

type ProbeInit = Readonly<{ method: "GET"; signal: AbortSignal }>;

function createProbe(
  behavior: (url: string) => Promise<Readonly<{ ok: boolean; status?: number }>>,
) {
  const urls: string[] = [];
  const fetcher = async (url: string, _init: ProbeInit) => {
    urls.push(url);
    return behavior(url);
  };
  return { urls, fetcher };
}

const HEALTH = "https://hotbargain.vip/pos-api/api/v1/health";
const EDGE_HEALTH = "https://pos.hotbargain.vip/pos-api/api/v1/health";

test("健康探测：边缘入口通则只探一次，不切换", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async () => ({ ok: true }));
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.deepEqual(probe.urls, [EDGE_HEALTH]);
  assert.equal(selector.isUsingFallback(), false);
});

test("健康探测：边缘入口抛错、直连通 → 返回直连结果，并让后续请求改走直连", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async (url) => {
    if (url.startsWith("https://pos.")) {
      throw new TypeError("Network request failed");
    }
    return { ok: true };
  });
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.deepEqual(probe.urls, [EDGE_HEALTH, HEALTH]);
  assert.equal(selector.isUsingFallback(), true);
});

test("健康探测：边缘入口返回 524、直连通 → 同样改走直连", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async (url) =>
    url.startsWith("https://pos.") ? { ok: false, status: 524 } : { ok: true },
  );
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.equal(selector.isUsingFallback(), true);
});

test("健康探测：两个入口都不通 → 抛出第一次的错误，不改变入口选择", async () => {
  const selector = new ApiTransportSelector();
  const firstError = new TypeError("first failed");
  let calls = 0;
  const probe = createProbe(async () => {
    calls += 1;
    throw calls === 1 ? firstError : new TypeError("second failed");
  });
  await assert.rejects(
    createFailoverFetch(probe.fetcher, selector)(HEALTH, {
      method: "GET",
      signal: new AbortController().signal,
    }),
    (error) => error === firstError,
  );
  assert.equal(calls, 2);
  assert.equal(selector.isUsingFallback(), false);
});

test("健康探测：两个入口都返回非 2xx → 返回第一次的响应，不改变入口选择", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async () => ({ ok: false, status: 503 }));
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.deepEqual(result, { ok: false, status: 503 });
  assert.equal(selector.isUsingFallback(), false);
});

test("健康探测：调用方超时（signal 已中止）不再重试，只上报超时", async () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  const controller = new AbortController();
  const abortError = Object.assign(new Error("aborted"), { name: "AbortError" });
  const probe = createProbe(async () => {
    controller.abort();
    throw abortError;
  });
  await assert.rejects(
    createFailoverFetch(probe.fetcher, selector)(HEALTH, {
      method: "GET",
      signal: controller.signal,
    }),
    (error) => error === abortError,
  );
  assert.equal(probe.urls.length, 1, "超时后不应再试另一个入口");
  // 该入口之前从没成功过：超时判定为线路黑洞，后续改走直连。
  assert.equal(selector.isUsingFallback(), true);
});

test("健康探测：不在入口组内的地址原样透传、只探一次", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async () => {
    throw new TypeError("lan down");
  });
  await assert.rejects(
    createFailoverFetch(probe.fetcher, selector)("http://192.168.31.247:5003/api/v1/health", {
      method: "GET",
      signal: new AbortController().signal,
    }),
    /lan down/,
  );
  assert.deepEqual(probe.urls, ["http://192.168.31.247:5003/api/v1/health"]);
});

test("健康探测：冷却期内当前是直连、直连失败而边缘已恢复 → 改回边缘", async () => {
  const selector = new ApiTransportSelector();
  selector.reportFailure(EDGE, "network");
  assert.equal(selector.isUsingFallback(), true);

  const probe = createProbe(async (url) => {
    if (url.startsWith("https://pos.")) {
      return { ok: true };
    }
    throw new TypeError("direct line is down");
  });
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.deepEqual(probe.urls, [HEALTH, EDGE_HEALTH]);
  assert.equal(selector.isUsingFallback(), false);
});

test("冷却结束后：写请求不当探路的，继续走直连；读请求 / 健康探测负责试探首选", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  selector.reportFailure(EDGE, "network");
  clock.advanceMinutes(5); // 2 分钟冷却已过

  assert.equal(selector.resolve({ probe: false }), DIRECT);
  assert.equal(selector.resolve({ probe: false }), DIRECT, "多次写请求都不会触发试探");
  assert.equal(selector.resolve(), EDGE, "读请求 / 健康探测去试探首选入口");
});
