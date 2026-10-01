import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { AxiosHeaders } from "axios";
import { sha256 } from "js-sha256";
import { TAB_PATHS } from "../navigation/default-route";
import {
  IOS_REVIEW_DOMAIN_NAMES,
  IOS_REVIEW_EXCLUDED_ROUTE_NAMES,
  IOS_REVIEW_LOCATION,
  IOS_REVIEW_MENU_ITEMS,
  IOS_REVIEW_PERMISSION_CODES,
  IOS_REVIEW_ROUTE_NAMES,
  IOS_REVIEW_SAMPLE_BARCODE,
  IOS_REVIEW_STORES,
  IOS_REVIEW_USERNAME,
  beginStandardAuthentication,
  clearIosReviewSession,
  configureIosReviewBuildGate,
  createIosReviewAxiosAdapter,
  createIosReviewDataStore,
  createIosReviewExport,
  createIosReviewTransport,
  createIosReviewUser,
  isIosReviewBuildEnabled,
  isIosReviewAuthenticatedSessionActive,
  isIosReviewSessionActive,
  isIosReviewUsername,
  normalizeIosReviewRequestPath,
  printIosReviewDocument,
  restoreIosReviewSession,
  reviewAwareFetch,
  setIosReviewSessionActive,
  subscribeIosReviewSession,
  tryAuthenticateIosReview,
} from "./index";
import type { IosReviewConfig, IosReviewMarkerStorage } from "./index";

async function run() {
const password = "ios-review-password-2026";
const reviewConfig: IosReviewConfig = {
  enabled: "true",
  passwordSha256: sha256(password),
};
const enabledBuild = {
  platform: "ios",
  buildProfile: "production",
  ...reviewConfig,
} as const;

assert.equal(
  isIosReviewBuildEnabled(enabledBuild),
  true,
  "审核模式只应在配置完整的 iOS production 构建启用"
);
for (const invalidBuild of [
  { ...enabledBuild, platform: "android" },
  { ...enabledBuild, buildProfile: "preview" },
  { ...enabledBuild, enabled: "false" },
  { ...enabledBuild, passwordSha256: "not-a-sha256" },
]) {
  assert.equal(
    isIosReviewBuildEnabled(invalidBuild),
    false,
    "平台、构建配置、开关或哈希任一不符合时都必须关闭审核模式"
  );
}

assert.equal(IOS_REVIEW_USERNAME, "ios_app_review");
assert.equal(isIosReviewUsername(" IOS_APP_REVIEW "), true);
assert.deepEqual(
  tryAuthenticateIosReview({
    username: IOS_REVIEW_USERNAME,
    password,
    buildContext: enabledBuild,
  }),
  { status: "authenticated" },
  "正确的本地审核账号应通过哈希校验"
);
assert.deepEqual(
  tryAuthenticateIosReview({
    username: IOS_REVIEW_USERNAME,
    password: "wrong",
    buildContext: enabledBuild,
  }),
  { status: "invalid-password" },
  "审核用户名命中但密码错误时必须本地失败，不能回落网络"
);
assert.deepEqual(
  tryAuthenticateIosReview({
    username: "normal-user",
    password,
    buildContext: enabledBuild,
  }),
  { status: "not-applicable" },
  "普通账号应交回现有认证流程"
);
assert.deepEqual(
  tryAuthenticateIosReview({
    username: IOS_REVIEW_USERNAME,
    password,
    buildContext: { ...enabledBuild, platform: "android" },
  }),
  { status: "not-applicable" },
  "非目标构建不得暴露本地审核账号"
);

class MemoryMarkerStorage implements IosReviewMarkerStorage {
  private values = new Map<string, string>();

  async getItemAsync(key: string) {
    return this.values.get(key) ?? null;
  }

  async setItemAsync(key: string, value: string) {
    this.values.set(key, value);
  }

  async deleteItemAsync(key: string) {
    this.values.delete(key);
  }
}

const markerStorage = new MemoryMarkerStorage();

configureIosReviewBuildGate(true);
assert.equal(
  isIosReviewAuthenticatedSessionActive(),
  false,
  "pre-auth 守卫不能伪装成已认证审核会话"
);
assert.equal(
  isIosReviewSessionActive(),
  true,
  "审核构建在明确选择普通认证前必须保持离线守卫"
);
assert.equal(
  await restoreIosReviewSession(markerStorage),
  false,
  "没有 marker 时不能恢复审核身份"
);
assert.equal(
  isIosReviewSessionActive(),
  true,
  "没有 marker 时审核构建仍必须保持 pre-auth 离线守卫"
);
beginStandardAuthentication();
assert.equal(
  isIosReviewSessionActive(),
  false,
  "明确开始普通认证后才允许解除 pre-auth 离线守卫"
);
configureIosReviewBuildGate(false);

const activeEvents: boolean[] = [];
const unsubscribe = subscribeIosReviewSession((active) => activeEvents.push(active));
await setIosReviewSessionActive(markerStorage);
assert.equal(isIosReviewAuthenticatedSessionActive(), true);
assert.equal(isIosReviewSessionActive(), true);
assert.equal(await restoreIosReviewSession(markerStorage), true);
await clearIosReviewSession(markerStorage);
assert.equal(isIosReviewSessionActive(), false);
assert.equal(isIosReviewAuthenticatedSessionActive(), false);
assert.equal(await restoreIosReviewSession(markerStorage), false);
unsubscribe();
assert.deepEqual(activeEvents, [true, false]);

configureIosReviewBuildGate(true);
await setIosReviewSessionActive(markerStorage);
await assert.rejects(
  clearIosReviewSession({
    getItemAsync: (key) => markerStorage.getItemAsync(key),
    setItemAsync: (key, value) => markerStorage.setItemAsync(key, value),
    deleteItemAsync: async () => {
      throw new Error("marker delete failed");
    },
  }),
  /marker delete failed/
);
assert.equal(
  isIosReviewSessionActive(),
  true,
  "marker 删除失败时审核构建必须继续 fail-closed"
);
configureIosReviewBuildGate(false);

const reviewUser = createIosReviewUser();
assert.deepEqual(reviewUser.roleNames, ["Admin"]);
assert.equal(reviewUser.username, IOS_REVIEW_USERNAME);
assert.equal(reviewUser.stores.length, 28, "审核账号必须覆盖 25–30 店的真实报表规模");
assert.deepEqual(
  reviewUser.stores.slice(0, 3).map((store) => store.storeCode),
  ["REV001", "REV002", "REVWH"],
  "既有三家演示门店的顺序与代码必须保持兼容",
);
assert.equal(
  IOS_REVIEW_STORES.every((store) => Boolean(store.storeGUID)),
  true,
  "每个演示门店都必须有稳定 storeGUID"
);
assert.equal(
  new Set(IOS_REVIEW_STORES.map((store) => store.storeGUID)).size,
  IOS_REVIEW_STORES.length,
  "28 家演示门店必须使用唯一 storeGUID",
);
assert.equal(
  new Set(IOS_REVIEW_STORES.map((store) => store.storeCode)).size,
  IOS_REVIEW_STORES.length,
  "28 家演示门店必须使用唯一门店代码",
);
assert.equal(
  IOS_REVIEW_PERMISSION_CODES.includes("Users.ManagePosTerminalPermissions"),
  true
);
assert.equal(
  new Set<string>(IOS_REVIEW_PERMISSION_CODES).has("Users.Create"),
  true,
  "Review 身份必须声明受分店范围约束的员工创建权限",
);
assert.equal(
  reviewUser.permissions.includes("Users.Create"),
  true,
  "Review 登录态必须能够演示新增店员流程",
);
assert.equal(
  IOS_REVIEW_PERMISSION_CODES.includes("Attendance.Admin.View"),
  true
);

assert.equal(IOS_REVIEW_MENU_ITEMS.length, 20);
assert.equal(new Set(IOS_REVIEW_ROUTE_NAMES).size, 20);
assert.equal(
  IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes("container-new-products"),
  true,
  "货柜新品依赖真实接口，审核菜单必须明确排除该入口",
);
assert.equal(
  IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes("app-install"),
  true,
  "App 安装依赖真实版本数据，审核菜单必须明确排除该入口",
);
assert.deepEqual(
  IOS_REVIEW_MENU_ITEMS.map((item) => item.routeName),
  IOS_REVIEW_ROUTE_NAMES,
  "本地审核菜单顺序应稳定并覆盖全部业务入口"
);
const backendNavigationSource = readFileSync(
  resolve(
    process.cwd(),
    "../../services/backend/BlazorApp.Api/Services/NavigationService.cs"
  ),
  "utf8"
);
const fullAppMenuStart = backendNavigationSource.indexOf(
  "private static readonly List<AppNavigationDefinition> FullAppMenu"
);
const fullAppMenuEnd = backendNavigationSource.indexOf(
  "private static",
  fullAppMenuStart + 1
);
assert.ok(fullAppMenuStart >= 0 && fullAppMenuEnd > fullAppMenuStart);
const backendRouteNames = Array.from(
  backendNavigationSource
    .slice(fullAppMenuStart, fullAppMenuEnd)
    .matchAll(/RouteName\s*=\s*"([^"]+)"/g),
  (match) => match[1]
);
const sortedReviewRoutes = [...IOS_REVIEW_ROUTE_NAMES].sort();
const expectedReviewRoutes = Object.keys(TAB_PATHS)
  .filter((routeName) => !IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes(
    routeName as (typeof IOS_REVIEW_EXCLUDED_ROUTE_NAMES)[number]
  ))
  .sort();
assert.deepEqual(
  sortedReviewRoutes,
  expectedReviewRoutes,
  "审核菜单必须覆盖可离线演示的 TAB_PATHS，并排除真实敏感资料审核入口"
);
assert.deepEqual(
  sortedReviewRoutes,
  [...new Set(backendRouteNames)]
    .filter((routeName) => !IOS_REVIEW_EXCLUDED_ROUTE_NAMES.includes(
      routeName as (typeof IOS_REVIEW_EXCLUDED_ROUTE_NAMES)[number]
    ))
    .sort(),
  "审核菜单必须与后端可离线演示的 FullAppMenu 保持集合一致"
);

const dataStore = createIosReviewDataStore(new Date("2026-07-16T00:00:00.000Z"));
for (const domain of IOS_REVIEW_DOMAIN_NAMES) {
  const initialRows = dataStore.list(domain);
  assert.ok(initialRows.length > 0, `${domain} 必须包含初始合成数据`);

  const created = dataStore.create(domain, { label: `${domain}-created` });
  assert.equal(dataStore.get(domain, created.id)?.label, `${domain}-created`);

  const updated = dataStore.update(domain, created.id, { status: "approved" });
  assert.equal(updated.status, "approved");
  assert.equal(dataStore.remove(domain, created.id), true);
  assert.equal(dataStore.get(domain, created.id), undefined);
}
const originalProductCount = dataStore.list("products").length;
dataStore.create("products", { label: "temporary" });
dataStore.reset();
assert.equal(dataStore.list("products").length, originalProductCount);
assert.equal(dataStore.list("products").some((item) => item.label === "temporary"), false);

const transport = createIosReviewTransport(dataStore);
transport.register({
  method: "GET",
  path: "/custom/ping",
  handle: () => ({ data: { ok: true }, status: 200 }),
});
transport.register({
  method: "GET",
  path: "/custom/params",
  handle: ({ query }) => ({ data: { storeCode: query.get("storeCode") } }),
});
const adapter = createIosReviewAxiosAdapter(transport);
const customResponse = await adapter({
  method: "get",
  url: "https://example.invalid/api/custom/ping?ignored=true",
  headers: new AxiosHeaders(),
});
assert.deepEqual(customResponse.data, { ok: true });
const paramsResponse = await adapter({
  method: "get",
  url: "/custom/params",
  params: { storeCode: "REV002" },
  headers: new AxiosHeaders(),
});
assert.deepEqual(
  paramsResponse.data,
  { storeCode: "REV002" },
  "adapter 必须把 Axios params 交给处理器"
);
assert.equal(
  normalizeIosReviewRequestPath("https://example.invalid/api/products?store=REV001"),
  "/products"
);
const listResponse = await adapter({
  method: "get",
  url: "/ios-review/products",
  headers: new AxiosHeaders(),
});
assert.ok(Array.isArray(listResponse.data));
const createResponse = await adapter({
  method: "post",
  url: "/ios-review/orders",
  headers: new AxiosHeaders(),
  data: JSON.stringify({ label: "adapter-created" }),
});
assert.equal(createResponse.status, 201);
assert.equal(createResponse.data.label, "adapter-created");
await assert.rejects(
  () =>
    adapter({
      method: "get",
      url: "/not-registered",
      headers: new AxiosHeaders(),
    }),
  (error: unknown) =>
    error instanceof Error && error.message.includes("IOS_REVIEW_UNHANDLED_REQUEST"),
  "未登记 endpoint 必须 fail-closed"
);

let delegatedUrl = "";
const fakeFetch: typeof fetch = async (input) => {
  delegatedUrl = String(input);
  return new Response("local");
};
await reviewAwareFetch("file:///tmp/review.png", undefined, fakeFetch, true);
assert.equal(delegatedUrl, "file:///tmp/review.png");
await assert.rejects(
  () => reviewAwareFetch("https://production.invalid/orders", undefined, fakeFetch, true),
  /IOS_REVIEW_NETWORK_BLOCKED/,
  "审核会话必须拒绝 HTTP 和 HTTPS"
);
await reviewAwareFetch("https://production.invalid/orders", undefined, fakeFetch, false);
assert.equal(delegatedUrl, "https://production.invalid/orders");

assert.equal(IOS_REVIEW_SAMPLE_BARCODE, "9330000000017");
assert.deepEqual(IOS_REVIEW_LOCATION, {
  latitude: -27.4698,
  longitude: 153.0251,
  accuracy: 5,
});
assert.deepEqual(await printIosReviewDocument("order-1"), {
  success: true,
  simulated: true,
  documentId: "order-1",
});
const exportResult = createIosReviewExport("sales-report", "csv");
assert.equal(exportResult.mimeType, "text/csv");
assert.ok(exportResult.uri.startsWith("data:text/csv"));

console.log("ios-review.test.ts: ok");
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
