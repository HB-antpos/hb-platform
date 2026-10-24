// 源码契约：守住客流统计页的几条不能悄悄改掉的约束（口径、合规、数据来源）。
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const read = (name: string) => readFileSync(join(directory, name), "utf8");
const productionFiles = readdirSync(directory).filter(
  (name) => /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) && name !== "fixtures.ts",
);

const body = read("TrafficBody.tsx");
const screen = read("TrafficStatsScreen.tsx");
const apiCore = read("api-core.ts");
const access = read("access.ts");

// 1) 估算说明常驻：两种门店配置各有一句，且页面主体无条件渲染这个 Notice。
assert.match(body, /notice\.estimateDedup/);
assert.match(body, /notice\.estimatePlain/);
assert.match(body, /<Notice[^>]*text=\{t\(dedup \? "notice\.estimateDedup" : "notice\.estimatePlain"\)\}/);

// 2) 独立访客任何展示位置都带「估算」标。
assert.equal((body.match(/kpi\.uniqueVisitors/g) ?? []).length, 1);
assert.match(body, /kpi\.uniqueVisitors"\), value: formatCount\(total\.uniqueVisitorCount\), estimateTag/);
assert.match(body, /kpi\.averageUnique"\)[\s\S]{0,160}estimateTag/);
assert.match(body, /key: "unique"[\s\S]{0,120}EstimateTag/);
// 独立访客只在开启去重的门店出现。
assert.match(body, /const dedup = overview\.trafficDedupEnabled/);

// 3) 只展示计数：模块里不出现图片 / 照片 / 缩略图 / 抓拍图相关的任何东西。
for (const name of productionFiles) {
  const source = read(name);
  assert.doesNotMatch(source, /<Image\b|expo-image|ImageBackground|thumbnail|captureImage|data:image/i, `${name} 不应处理图片`);
  assert.doesNotMatch(source, /console\./, `${name} 不应使用 console.*`);
}

// 4) 「今天 / 现在几点」只来自服务端（门店时区），不读手机时钟。
for (const name of productionFiles) {
  assert.doesNotMatch(read(name), /new Date\(\)|Date\.now\(/, `${name} 不应读手机时间`);
}

// 5) 接口路径与后端 FaceMatchController 的 traffic/overview 一致；请求不带 anchor 时由服务端取今天。
assert.match(apiCore, /"\/react\/v1\/face-match\/traffic\/overview"/);
assert.match(apiCore, /if \(query\.anchor\) params\.anchor = query\.anchor/);
assert.match(apiCore, /"Cache-Control": "no-store"/);

// 6) 入口守卫：登录、非审核壳、FaceMatch.Traffic.View；页面进入前先过守卫，再渲染内容。
assert.match(access, /isAuthenticated && !isReview && hasPermission\(PERMISSIONS\.FaceMatch\.Traffic\.View\)/);
assert.match(screen, /iosReviewOfflineGuardActive/);
assert.match(screen, /canViewTrafficStats\(isAuthenticated, hasPermission, review\)/);
assert.match(screen, /if \(blocked\) return <FaceScreenMessage/);

// 7) 对照期只比已结束的时间：指标层用 completedHourCount / 已结束日子，页面文案标注截止时刻。
const metrics = read("metrics.ts");
assert.match(metrics, /export function completedHourCount/);
assert.match(metrics, /compare\.hours|kind: "hours"/);
assert.match(body, /compare\.hours/);

// 8) 自动刷新只在页面前台、且当期（含今天）时进行。
const hook = read("use-traffic.ts");
assert.match(hook, /refetchInterval: \(query\) => \(focused && query\.state\.data\?\.isCurrent \? LIVE_REFRESH_MS : false\)/);

console.log("traffic-stats source-contract tests passed");
