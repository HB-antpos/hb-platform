/**
 * API 传输入口选择与故障转移（transport-failover.ts）单元测试。
 * 纯逻辑，注入假时钟，不依赖 React Native。
 */
import { test } from "node:test";
import assert from "node:assert/strict";

import {
  ApiTransportSelector,
  classifyTransportFailure,
  DIRECT_API_HOST,
  EDGE_API_HOST,
  isTransportRetryableMethod,
  transportCandidates,
  type PersistedTransportState,
} from "./transport-failover";

const MINUTE = 60_000;

function createSelector(startAt = 1_000_000) {
  const clock = { now: startAt };
  const saved: PersistedTransportState[] = [];
  const selector = new ApiTransportSelector({
    now: () => clock.now,
    persist: (state) => saved.push(state),
  });
  return { selector, clock, saved };
}

test("入口组只包含生产的边缘与直连两个 host，其余 host 只有它自己", () => {
  assert.deepEqual(transportCandidates("hotbargain.vip"), [EDGE_API_HOST, DIRECT_API_HOST]);
  assert.deepEqual(transportCandidates("pos.hotbargain.vip"), [EDGE_API_HOST, DIRECT_API_HOST]);
  assert.deepEqual(transportCandidates("HotBargain.VIP."), [EDGE_API_HOST, DIRECT_API_HOST]);
  assert.deepEqual(transportCandidates("hotbargain.group"), ["hotbargain.group"]);
  assert.deepEqual(transportCandidates("192.168.31.247"), ["192.168.31.247"]);
  assert.deepEqual(transportCandidates("evil-hotbargain.vip"), ["evil-hotbargain.vip"]);
  assert.deepEqual(transportCandidates("pos.hotbargain.vip.evil.com"), ["pos.hotbargain.vip.evil.com"]);
});

test("默认首选边缘入口；入口组之外的 host 原样返回且永远不切换", () => {
  const { selector } = createSelector();
  assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST);

  const flipped = selector.reportFailure("192.168.31.247", "192.168.31.247", "network");
  assert.equal(flipped, false);
  assert.equal(selector.resolve("192.168.31.247"), "192.168.31.247");
  assert.equal(selector.fallbackFor("192.168.31.247", "192.168.31.247"), null);
  assert.equal(selector.isUsingFallback("192.168.31.247"), false);
});

test("首选入口网络失败后切到直连，冷却期内一直用直连，冷却结束再试探首选", () => {
  const { selector, clock } = createSelector();
  assert.equal(selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network"), true);

  assert.equal(selector.resolve("hotbargain.vip"), DIRECT_API_HOST);
  assert.equal(selector.isUsingFallback("hotbargain.vip"), true);

  clock.now += 2 * MINUTE - 1000;
  assert.equal(selector.resolve("hotbargain.vip"), DIRECT_API_HOST);

  clock.now += 2000;
  assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST);
  assert.equal(selector.isUsingFallback("hotbargain.vip"), false);
});

test("试探首选又失败时冷却时间翻倍（2→4→8→16→30 分钟封顶）", () => {
  const { selector, clock } = createSelector();
  const expectedMinutes = [2, 4, 8, 16, 30, 30];
  for (const minutes of expectedMinutes) {
    assert.equal(selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network"), true);
    clock.now += minutes * MINUTE - 1000;
    assert.equal(selector.resolve("hotbargain.vip"), DIRECT_API_HOST, `${minutes} 分钟内仍用直连`);
    clock.now += 2000;
    assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST, `${minutes} 分钟后试探首选`);
  }
});

test("首选入口成功后清零退避：再次失败又只冷却 2 分钟", () => {
  const { selector, clock } = createSelector();
  selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network");
  clock.now += 2 * MINUTE + 1000;
  selector.resolve("hotbargain.vip");
  selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network");
  clock.now += 4 * MINUTE + 1000;
  assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST);
  selector.reportSuccess("hotbargain.vip", EDGE_API_HOST);

  selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network");
  clock.now += 2 * MINUTE + 1000;

  assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST);
});

test("直连入口上的失败不会改变当前入口", () => {
  const { selector } = createSelector();
  selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network");

  assert.equal(selector.reportFailure("hotbargain.vip", DIRECT_API_HOST, "network"), false);

  assert.equal(selector.resolve("hotbargain.vip"), DIRECT_API_HOST);
  assert.equal(selector.fallbackFor("hotbargain.vip", DIRECT_API_HOST), EDGE_API_HOST);
});

test("超时：该入口最近成功过则只是服务器慢，不切换；最近没成功过才判定线路故障", () => {
  const { selector, clock } = createSelector();
  selector.reportSuccess("hotbargain.vip", EDGE_API_HOST);

  clock.now += 30_000;
  assert.equal(selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "timeout"), false);
  assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST);

  clock.now += 31_000;
  assert.equal(selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "timeout"), true);
  assert.equal(selector.resolve("hotbargain.vip"), DIRECT_API_HOST);
});

test("冷启动后从未成功过时，超时就判定为线路故障（线路黑洞）", () => {
  const { selector } = createSelector();
  assert.equal(selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "timeout"), true);
});

test("边缘回源失败（520~530）按网络失败同样处理", () => {
  const { selector } = createSelector();
  assert.equal(selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "edge"), true);
});

test("状态变化时落盘，重启后恢复冷却期内的备用入口", () => {
  const first = createSelector();
  first.selector.reportFailure("hotbargain.vip", EDGE_API_HOST, "network");
  const persisted = first.saved.at(-1)!;
  assert.equal(persisted.activeIndex, 1);

  const second = createSelector(first.clock.now + MINUTE);
  second.selector.hydrate(persisted);
  assert.equal(second.selector.resolve("hotbargain.vip"), DIRECT_API_HOST);

  second.clock.now += 2 * MINUTE;
  assert.equal(second.selector.resolve("hotbargain.vip"), EDGE_API_HOST);
});

test("hydrate 拒绝损坏的落盘数据", () => {
  const { selector } = createSelector();
  selector.hydrate({ activeIndex: 7, stickyUntil: 1, backoffStep: 0 });
  selector.hydrate({ activeIndex: 1, stickyUntil: Number.NaN, backoffStep: 0 });
  selector.hydrate({ activeIndex: 1, stickyUntil: 1, backoffStep: -3 });
  assert.equal(selector.resolve("hotbargain.vip"), EDGE_API_HOST);
});

test("错误分类：只有无响应的网络错误/超时与边缘 520~530 才算传输层失败", () => {
  assert.equal(classifyTransportFailure({ code: "ERR_NETWORK" }), "network");
  assert.equal(classifyTransportFailure({ code: "ECONNABORTED" }), "timeout");
  assert.equal(classifyTransportFailure({ code: "ETIMEDOUT" }), "timeout");
  assert.equal(classifyTransportFailure({ status: 522 }), "edge");
  assert.equal(classifyTransportFailure({ status: 524 }), "edge");
  assert.equal(classifyTransportFailure({ status: 530 }), "edge");

  // 服务器自己的回答（链路是通的）不触发切换。
  assert.equal(classifyTransportFailure({ status: 401 }), null);
  assert.equal(classifyTransportFailure({ status: 404 }), null);
  assert.equal(classifyTransportFailure({ status: 500 }), null);
  assert.equal(classifyTransportFailure({ status: 503 }), null);
  assert.equal(classifyTransportFailure({ status: 519 }), null);
  assert.equal(classifyTransportFailure({ status: 531 }), null);
  // 主动取消、未知错误码不算。
  assert.equal(classifyTransportFailure({ code: "ERR_NETWORK", isCancel: true }), null);
  assert.equal(classifyTransportFailure({ code: "ERR_CANCELED" }), null);
  assert.equal(classifyTransportFailure({}), null);
});

test("只有 GET/HEAD 允许自动换入口重试，写请求绝不重放", () => {
  assert.equal(isTransportRetryableMethod("get"), true);
  assert.equal(isTransportRetryableMethod("GET"), true);
  assert.equal(isTransportRetryableMethod("head"), true);
  assert.equal(isTransportRetryableMethod(undefined), true);
  assert.equal(isTransportRetryableMethod("post"), false);
  assert.equal(isTransportRetryableMethod("PUT"), false);
  assert.equal(isTransportRetryableMethod("delete"), false);
  assert.equal(isTransportRetryableMethod("patch"), false);
});
