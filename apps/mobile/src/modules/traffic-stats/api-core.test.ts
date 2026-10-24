import assert from "node:assert/strict";
import type { AxiosRequestConfig } from "axios";
import { TRAFFIC_OVERVIEW_PATH, createTrafficStatsApi, type TrafficHttp } from "./api-core";
import { canViewTrafficStats } from "./access";

interface Call {
  url: string;
  config?: AxiosRequestConfig;
}

async function run() {
  const calls: Call[] = [];
  const http: TrafficHttp = {
    get: async (url, config) => {
      calls.push({ url, config });
      return {
        data: {
          success: true,
          data: { storeCode: "1008", period: "week", from: "2026-10-19", to: "2026-10-25", today: "2026-10-24", isCurrent: true, countingEnabled: true },
        },
      };
    },
  };
  const api = createTrafficStatsApi(http);

  // 缺省 anchor：不带 anchor 参数（由服务端取门店本地今天）
  const controller = new AbortController();
  const overview = await api.fetchOverview({ storeCode: "1008", period: "week" }, controller.signal);
  assert.equal(calls[0].url, TRAFFIC_OVERVIEW_PATH);
  assert.deepEqual(calls[0].config?.params, { store: "1008", period: "week" });
  assert.equal(calls[0].config?.signal, controller.signal);
  assert.equal((calls[0].config?.headers as Record<string, string>)["Cache-Control"], "no-store");
  // 信封被解开，规范化后可用
  assert.equal(overview.storeCode, "1008");
  assert.equal(overview.countingEnabled, true);
  assert.equal(overview.current.hours.length, 24);

  // 带 anchor
  await api.fetchOverview({ storeCode: "1008", period: "day", anchor: "2026-10-10" });
  assert.deepEqual(calls[1].config?.params, { store: "1008", period: "day", anchor: "2026-10-10" });

  // 入口守卫
  const allow = (code: string) => code === "FaceMatch.Traffic.View";
  assert.equal(canViewTrafficStats(true, allow, false), true);
  assert.equal(canViewTrafficStats(false, allow, false), false, "未登录");
  assert.equal(canViewTrafficStats(true, allow, true), false, "iOS 审核壳");
  assert.equal(canViewTrafficStats(true, () => false, false), false, "无权限");

  console.log("traffic-stats api-core tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
