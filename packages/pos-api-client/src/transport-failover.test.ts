import assert from "node:assert/strict";
import test from "node:test";

import {
  API_ENTRY_HOSTS,
  ApiTransportSelector,
  buildEntryOrder,
  classifyTransportFailure,
  createFailoverFetch,
  entryOrigins,
  isApiEntryMode,
  isFailoverBaseUrl,
  isTransportRetryableMethod,
  modeForHost,
  parseApiEntryMode,
  parseEntryUrl,
  rewriteEntryHost,
} from "./transport-failover";

const EDGE = "pos.hotbargain.vip";
const AWS = "pos-syd.hotbargain.vip";
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

test("入口组只认 https 默认端口 + /pos-api 路径下的三个固定域名", () => {
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip/pos-api"), true);
  assert.equal(isFailoverBaseUrl("https://pos.hotbargain.vip/pos-api/"), true);
  assert.equal(isFailoverBaseUrl("https://pos-syd.hotbargain.vip/pos-api/"), true);
  assert.equal(isFailoverBaseUrl("https://HotBargain.vip/pos-api/api/v1/health"), true);
  // 以下都不是：明文、带端口、别的域名、别的路径、带账号密码、非法字符串。
  assert.equal(isFailoverBaseUrl("http://hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("http://pos-syd.hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip:8443/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://pos-syd.hotbargain.vip:8443/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.group/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip/api"), false);
  assert.equal(isFailoverBaseUrl("https://hotbargain.vip/pos-api-evil"), false);
  assert.equal(isFailoverBaseUrl("https://evil.hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://pos-syd.hotbargain.vip.evil.com/pos-api"), false);
  assert.equal(isFailoverBaseUrl("https://user:pass@hotbargain.vip/pos-api"), false);
  assert.equal(isFailoverBaseUrl("http://192.168.31.247:5003"), false);
  assert.equal(isFailoverBaseUrl("not a url"), false);
});

test("入口组内的地址可以换 host，其余原样不变；origin 列表覆盖三个入口", () => {
  assert.equal(
    rewriteEntryHost("https://hotbargain.vip/pos-api", EDGE),
    "https://pos.hotbargain.vip/pos-api",
  );
  assert.equal(
    rewriteEntryHost("https://hotbargain.vip/pos-api", AWS),
    "https://pos-syd.hotbargain.vip/pos-api",
  );
  assert.equal(
    rewriteEntryHost("https://pos.hotbargain.vip/pos-api/api/v1/health?x=1", DIRECT),
    "https://hotbargain.vip/pos-api/api/v1/health?x=1",
  );
  assert.equal(
    rewriteEntryHost("https://pos-syd.hotbargain.vip/pos-api/api/v1/health", EDGE),
    "https://pos.hotbargain.vip/pos-api/api/v1/health",
  );
  assert.equal(rewriteEntryHost("https://hbpos.example", EDGE), "https://hbpos.example");
  assert.deepEqual(entryOrigins("https://hotbargain.vip/pos-api"), [
    "https://pos.hotbargain.vip",
    "https://pos-syd.hotbargain.vip",
    "https://hotbargain.vip",
  ]);
  assert.deepEqual(entryOrigins("https://hbpos.example"), []);
  assert.equal(parseEntryUrl("https://hbpos.example"), null);
});

test("入口顺序规则：首选在前，其余固定 aws → edge → direct", () => {
  assert.deepEqual(buildEntryOrder("edge"), ["edge", "aws", "direct"]);
  assert.deepEqual(buildEntryOrder("aws"), ["aws", "edge", "direct"]);
  assert.deepEqual(buildEntryOrder("direct"), ["direct", "aws", "edge"]);
});

test("入口代号：严格匹配；null / 缺失按 edge；未知值返回 undefined（调用方保持当前设置）", () => {
  assert.equal(isApiEntryMode("edge"), true);
  assert.equal(isApiEntryMode("EDGE"), false);
  assert.equal(isApiEntryMode(" aws"), false);
  assert.equal(isApiEntryMode(null), false);
  assert.equal(parseApiEntryMode("aws"), "aws");
  assert.equal(parseApiEntryMode("direct"), "direct");
  assert.equal(parseApiEntryMode(null), "edge");
  assert.equal(parseApiEntryMode(undefined), "edge");
  assert.equal(parseApiEntryMode("cloudflare"), undefined);
  assert.equal(parseApiEntryMode(""), undefined);
  assert.equal(parseApiEntryMode(1), undefined);
  assert.equal(modeForHost("POS-SYD.hotbargain.vip."), "aws");
  assert.equal(modeForHost("example.com"), null);
  assert.equal(API_ENTRY_HOSTS.aws, AWS);
});

test("默认首选 edge；备用按 edge → aws → direct → edge 循环取下一个", () => {
  const selector = new ApiTransportSelector();
  assert.equal(selector.getPreferredMode(), "edge");
  assert.equal(selector.resolve(), EDGE);
  assert.equal(selector.fallbackFor(EDGE), AWS);
  assert.equal(selector.fallbackFor(AWS), DIRECT);
  assert.equal(selector.fallbackFor(DIRECT), EDGE);
  assert.equal(selector.fallbackFor("hbpos.example"), EDGE, "组外 host 返回顺序中的第一个");
  assert.equal(selector.isUsingFallback(), false);
  assert.equal(selector.getActiveMode(), "edge");
});

test("首选入口失败：切到顺序里的下一个，并按 2/4/8/16/30 分钟递增冷却", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  assert.equal(selector.reportFailure(EDGE, "network"), true);
  assert.equal(selector.resolve(), AWS);
  assert.equal(selector.isUsingFallback(), true);
  assert.equal(selector.getActiveMode(), "aws");

  // 2 分钟冷却内一直是 AWS；到期后回到首选试探。
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), AWS);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE);

  // 试探又失败：冷却翻倍到 4 分钟。
  assert.equal(selector.reportFailure(EDGE, "network"), true);
  clock.advanceMinutes(3);
  assert.equal(selector.resolve(), AWS);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE);

  // 继续失败：8、16、30（封顶）。
  for (const minutes of [8, 16, 30, 30]) {
    assert.equal(selector.reportFailure(EDGE, "edge"), true);
    clock.advanceMinutes(minutes - 1);
    assert.equal(selector.resolve(), AWS, `冷却 ${minutes} 分钟内仍应走备用`);
    clock.advanceMinutes(1);
    assert.equal(selector.resolve(), EDGE, `冷却 ${minutes} 分钟后应回到首选`);
  }
});

test("每个入口各自记冷却与退避：首选、AWS 先后故障 → 落到直连；各自恢复各自清零", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  selector.reportFailure(EDGE, "network"); // edge 冷却 2 分钟
  assert.equal(selector.resolve(), AWS);
  selector.reportFailure(AWS, "network"); // aws 冷却 2 分钟
  assert.equal(selector.resolve(), DIRECT);
  assert.equal(selector.isUsingFallback(), true);

  // 再升一级 edge：先等它冷却结束，再失败 → 4 分钟；aws 仍只是 2 分钟那一级。
  clock.advanceMinutes(2);
  assert.equal(selector.resolve(), EDGE, "两个冷却同时结束，按顺序先试首选");
  selector.reportFailure(EDGE, "network"); // edge 第二级：4 分钟
  assert.equal(selector.resolve(), AWS, "aws 冷却已结束，轮到它试探");
  selector.reportSuccess(AWS); // aws 恢复：只清它自己
  assert.equal(selector.resolve(), AWS);
  clock.advanceMinutes(3);
  assert.equal(selector.resolve(), AWS, "edge 还在 4 分钟冷却里");
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE);
  selector.reportSuccess(EDGE);
  assert.equal(selector.isUsingFallback(), false);

  // aws 之前恢复过，所以再失败又从 2 分钟开始。
  selector.reportFailure(AWS, "network");
  assert.equal(selector.resolve(), EDGE, "aws 不是首选，失败后首选照常");
  selector.reportFailure(EDGE, "network");
  assert.equal(selector.resolve(), DIRECT);
  clock.advanceMinutes(2);
  assert.equal(selector.resolve(), EDGE, "edge 与 aws 都是 2 分钟，到期后首选在前");
});

test("三个入口都在冷却：取冷却最早结束的，并列按顺序靠前", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  selector.reportFailure(DIRECT, "network"); // 2 分钟
  clock.advanceSeconds(10);
  selector.reportFailure(AWS, "network"); // 2 分钟（比 direct 晚 10 秒结束）
  clock.advanceSeconds(10);
  selector.reportFailure(EDGE, "network"); // 2 分钟（最晚结束）
  assert.equal(selector.resolve(), DIRECT);
  assert.equal(selector.resolve({ probe: false }), DIRECT);
  assert.equal(selector.isUsingFallback(), true);
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
  assert.equal(selector.resolve(), AWS);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE, "仍是 2 分钟冷却，而不是被并发失败顶到 30 分钟");
});

test("首选入口恢复成功：清零退避，下次失败又从 2 分钟开始", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });

  selector.reportFailure(EDGE, "network");
  clock.advanceMinutes(2);
  assert.equal(selector.resolve(), EDGE);
  selector.reportSuccess(EDGE);
  assert.equal(selector.isUsingFallback(), false);

  selector.reportFailure(EDGE, "network");
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), AWS);
  clock.advanceMinutes(1);
  assert.equal(selector.resolve(), EDGE, "退避已清零，冷却应回到 2 分钟");
});

test("备用入口失败也进入冷却，但不影响仍健康的首选；组外 host 的上报被忽略", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  assert.equal(selector.reportFailure(DIRECT, "network"), true, "告诉调用方这个入口不可用，读请求可换下一个");
  assert.equal(selector.resolve(), EDGE);
  assert.equal(selector.isUsingFallback(), false, "首选没有冷却，不算回退");
  // 冷却的备用入口不会被选为 edge 失败后的备用：edge 失败 → aws（未冷却）。
  selector.reportFailure(EDGE, "network");
  assert.equal(selector.resolve(), AWS);

  assert.equal(selector.reportFailure("hbpos.example", "network"), false);
  selector.reportSuccess("hbpos.example");
  assert.equal(selector.resolve(), AWS);
});

test("fallbackFor 跳过仍在冷却的入口；都在冷却则取顺序上的下一个", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  selector.reportFailure(AWS, "network");
  // edge 之后的下一个是 aws，但它刚被判定故障 → 直接给 direct。
  assert.equal(selector.fallbackFor(EDGE), DIRECT);
  selector.reportFailure(DIRECT, "network");
  assert.equal(selector.fallbackFor(EDGE), AWS, "都在冷却：顺序上的下一个");
  assert.equal(selector.fallbackFor(DIRECT), EDGE, "循环回到顺序里的第一个");
});

test("超时只有在该入口 60 秒内没成功过时才算线路故障（备用入口同样适用）", () => {
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
  assert.equal(selector.resolve(), AWS);

  // AWS 刚成功过则其超时同样不算故障。
  selector.reportSuccess(AWS);
  clock.advanceSeconds(10);
  assert.equal(selector.reportFailure(AWS, "timeout"), false);
  assert.equal(selector.resolve(), AWS);
});

test("setPreferredMode：改变首选并清零各入口冷却；null 回到 edge；未知值与相同值不动", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  selector.reportFailure(EDGE, "network");
  selector.reportFailure(AWS, "network");
  assert.equal(selector.resolve(), DIRECT);

  assert.equal(selector.setPreferredMode("direct"), true);
  assert.equal(selector.getPreferredMode(), "direct");
  assert.deepEqual(selector.getEntryOrder(), ["direct", "aws", "edge"]);
  assert.equal(selector.resolve(), DIRECT);
  assert.equal(selector.isUsingFallback(), false);
  // 旧首选 edge 遗留的冷却已清零：direct 失败后下一个是 aws（而不是仍被冷却挡住）。
  selector.reportFailure(DIRECT, "network");
  assert.equal(selector.resolve(), AWS);

  // 相同值：什么都不做（冷却保留）。
  assert.equal(selector.setPreferredMode("direct"), false);
  assert.equal(selector.resolve(), AWS);

  // 未知值：忽略，保持当前设置。
  assert.equal(selector.setPreferredMode("cloudflare" as never), false);
  assert.equal(selector.setPreferredMode(7 as never), false);
  assert.equal(selector.getPreferredMode(), "direct");

  // null = 后台未设置 → edge，并清零冷却。
  assert.equal(selector.setPreferredMode(null), true);
  assert.equal(selector.getPreferredMode(), "edge");
  assert.equal(selector.resolve(), EDGE);
  assert.equal(selector.isUsingFallback(), false);
});

test("首选 aws / direct 时的回退次序与 isUsingFallback", () => {
  const clock = createClock();
  const aws = new ApiTransportSelector({ now: clock.now });
  aws.setPreferredMode("aws");
  assert.equal(aws.resolve(), AWS);
  aws.reportFailure(AWS, "network");
  assert.equal(aws.resolve(), EDGE, "aws 首选失败 → edge");
  assert.equal(aws.isUsingFallback(), true);
  aws.reportFailure(EDGE, "network");
  assert.equal(aws.resolve(), DIRECT);

  const direct = new ApiTransportSelector({ now: clock.now });
  direct.setPreferredMode("direct");
  assert.equal(direct.resolve(), DIRECT);
  direct.reportFailure(DIRECT, "network");
  assert.equal(direct.resolve(), AWS, "直连失败先走 AWS");
  direct.reportFailure(AWS, "network");
  assert.equal(direct.resolve(), EDGE, "最后才 EdgeOne");
  direct.reportSuccess(DIRECT);
  assert.equal(direct.resolve(), DIRECT);
  assert.equal(direct.isUsingFallback(), false);
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
const AWS_HEALTH = "https://pos-syd.hotbargain.vip/pos-api/api/v1/health";

const isHost = (url: string, host: string) => new URL(url).hostname === host;

test("健康探测：首选入口通则只探一次，不切换", async () => {
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

test("健康探测：首选入口抛错、下一个入口（AWS）通 → 返回其结果，并让后续请求改走它", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async (url) => {
    if (isHost(url, EDGE)) {
      throw new TypeError("Network request failed");
    }
    return { ok: true };
  });
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.deepEqual(probe.urls, [EDGE_HEALTH, AWS_HEALTH]);
  assert.equal(selector.isUsingFallback(), true);
  assert.equal(selector.resolve(), AWS);
});

test("健康探测：首选返回 524、AWS 通 → 同样改走 AWS", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async (url) =>
    isHost(url, EDGE) ? { ok: false, status: 524 } : { ok: true },
  );
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.equal(selector.isUsingFallback(), true);
});

test("健康探测：最多再换一个——首选和下一个都不通时不会去试第三个，抛出第一次的错误且不改变入口选择", async () => {
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
  assert.equal(calls, 2, "三个入口里最多试两个");
  assert.deepEqual(probe.urls, [EDGE_HEALTH, AWS_HEALTH]);
  assert.equal(selector.isUsingFallback(), false);
});

test("健康探测：两次都返回非 2xx → 返回第一次的响应，不改变入口选择", async () => {
  const selector = new ApiTransportSelector();
  const probe = createProbe(async () => ({ ok: false, status: 503 }));
  const result = await createFailoverFetch(probe.fetcher, selector)(HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.deepEqual(result, { ok: false, status: 503 });
  assert.equal(probe.urls.length, 2);
  assert.equal(selector.isUsingFallback(), false);
});

test("健康探测：按后台首选的顺序——首选 direct 时先探直连、失败再探 AWS", async () => {
  const selector = new ApiTransportSelector();
  selector.setPreferredMode("direct");
  const probe = createProbe(async (url) =>
    isHost(url, DIRECT) ? { ok: false, status: 502 } : { ok: true },
  );
  const result = await createFailoverFetch(probe.fetcher, selector)(EDGE_HEALTH, {
    method: "GET",
    signal: new AbortController().signal,
  });
  assert.equal(result.ok, true);
  assert.deepEqual(probe.urls, [HEALTH, AWS_HEALTH]);
  assert.equal(selector.resolve(), AWS);
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
  // 该入口之前从没成功过：超时判定为线路黑洞，后续改走下一个入口。
  assert.equal(selector.isUsingFallback(), true);
  assert.equal(selector.resolve(), AWS);
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

test("健康探测：首选与 AWS 都在冷却、当前是直连且直连失败 → 换回顺序里的第一个（首选）", async () => {
  const selector = new ApiTransportSelector();
  selector.reportFailure(EDGE, "network");
  selector.reportFailure(AWS, "network");
  assert.equal(selector.isUsingFallback(), true);

  const probe = createProbe(async (url) => {
    if (isHost(url, EDGE)) {
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

test("冷却结束后：写请求不当探路的，继续走已确认可用的入口；读请求 / 健康探测负责试探首选", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  selector.reportFailure(EDGE, "network");
  clock.advanceMinutes(5); // 2 分钟冷却已过

  assert.equal(selector.resolve({ probe: false }), AWS);
  assert.equal(selector.resolve({ probe: false }), AWS, "多次写请求都不会触发试探");
  assert.equal(selector.resolve(), EDGE, "读请求 / 健康探测去试探首选入口");
});

test("写请求面对的入口全部「待试探」时仍取冷却最早结束的，不会无入口可用", () => {
  const clock = createClock();
  const selector = new ApiTransportSelector({ now: clock.now });
  selector.reportFailure(EDGE, "network");
  clock.advanceSeconds(5);
  selector.reportFailure(AWS, "network");
  clock.advanceSeconds(5);
  selector.reportFailure(DIRECT, "network");
  clock.advanceMinutes(10); // 全部冷却已过，但都没被确认恢复
  assert.equal(selector.resolve({ probe: false }), EDGE);
  assert.equal(selector.resolve(), EDGE);
});
