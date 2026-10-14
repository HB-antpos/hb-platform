// 通知收件箱的源码契约测试：
// 1. 范围：本期只做拉取，不引入 SignalR 与系统推送；接口路径与后端控制器一致；不落盘、不写日志；
// 2. 文案：页面不硬编码中文，代码里用到的每个文案键中英文都有，且没有死键；通知标题与正文原样展示（不过翻译）；
// 3. 登记：路由薄壳、设备模式隐藏、iOS 审核模式排除、角色菜单目录与后端菜单口径一致。
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { getVisibleTabRouteNames } from "../navigation/default-route";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(moduleDir, "../../..");
const repoRoot = resolve(mobileRoot, "../..");
const read = (path: string) => readFileSync(path, "utf8");
const listFiles = (directory: string): string[] =>
  readdirSync(directory).flatMap((name) => {
    const full = join(directory, name);
    return statSync(full).isDirectory() ? listFiles(full) : [full];
  });
const isTest = (name: string) => /\.test\.tsx?$/.test(name);
const moduleFiles = listFiles(moduleDir).filter((file) => /\.tsx?$/.test(file) && !isTest(file));
const stripComments = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
const sourceOf = (name: string) => read(join(moduleDir, name));

// ───────── 1. 范围：只拉取，不落盘、不写日志 ─────────
{
  const FORBIDDEN = [
    "console.",
    "reportApplicationLog",
    "AsyncStorage",
    "SecureStore",
    "localStorage",
    "expo-notifications",
    "@microsoft/signalr",
    "signalr",
    "WebSocket",
    "EventSource",
    "registerForPushNotifications",
    "getExpoPushTokenAsync",
  ];
  for (const file of moduleFiles) {
    const code = stripComments(read(file));
    for (const term of FORBIDDEN) {
      assert.equal(code.toLowerCase().includes(term.toLowerCase()), false, `${file} 不应出现 ${term}（本期只做拉取，不做实时与推送）`);
    }
  }

  // 接口路径与后端控制器的路由一致
  const controller = read(join(repoRoot, "services/backend/BlazorApp.Api/Controllers/React/NotificationsController.cs"));
  assert.ok(controller.includes('[Route("api/react/v1/notifications")]'));
  for (const route of ['[HttpGet]', '[HttpGet("unread-count")]', '[HttpPost("read")]', '[HttpPost("read-all")]']) {
    assert.ok(controller.includes(route), `后端缺少路由 ${route}`);
  }
  assert.ok(controller.includes("[FromQuery] string? cursor"), "后端列表是游标分页");
  assert.ok(controller.includes("[FromQuery] int take"), "后端列表参数是 take");
  assert.ok(controller.includes("[FromQuery] bool unreadOnly"));
  const core = stripComments(sourceOf("api-core.ts"));
  assert.ok(core.includes('"/react/v1/notifications"'));
  assert.ok(core.includes("`${NOTIFICATIONS_API_BASE}/unread-count`"));
  assert.ok(core.includes("`${NOTIFICATIONS_API_BASE}/read`"));
  assert.ok(core.includes("`${NOTIFICATIONS_API_BASE}/read-all`"));
  // 一次最多标记数与后端 MaxMarkReadIds 一致
  const inbox = read(join(repoRoot, "services/backend/BlazorApp.Api/Features/Notifications/NotificationInboxService.cs"));
  const backendMax = /MaxMarkReadIds\s*=\s*(\d+)/.exec(inbox)?.[1];
  const clientMax = /MAX_MARK_READ_IDS\s*=\s*(\d+)/.exec(core)?.[1];
  assert.ok(backendMax && clientMax && backendMax === clientMax, "客户端分批上限须与后端 MaxMarkReadIds 一致");
  // 疑似命中的类型常量与后端一致
  const types = read(join(repoRoot, "services/backend/BlazorApp.Shared/Models/HBweb/AppNotification.cs"));
  assert.ok(types.includes('FaceUnwelcomeHit = "face.unwelcome-hit"'));
  assert.ok(stripComments(sourceOf("logic.ts")).includes('"face.unwelcome-hit"'));
}

// ───────── 2. 页面不硬编码中文；通知标题与正文原样展示 ─────────
const CJK = /[㐀-鿿＀-￯]/;
for (const file of moduleFiles.filter((name) => name.endsWith(".tsx"))) {
  const offending = stripComments(read(file)).split("\n").find((line) => CJK.test(line));
  assert.equal(offending, undefined, `${file} 硬编码了中文：${offending?.trim()}`);
}
{
  const screen = stripComments(sourceOf("NotificationsScreen.tsx"));
  assert.ok(/\{item\.title\}/.test(screen) && /\{item\.body\}/.test(screen), "标题与正文原样展示");
  assert.equal(/t\(\s*item\.(title|body)/.test(screen), false, "后端固定英文文案不能再过翻译");
}

type Tree = { [key: string]: string | Tree };
const zh = JSON.parse(read(join(mobileRoot, "src/locales/zh/screens/notifications.json"))) as Tree;
const en = JSON.parse(read(join(mobileRoot, "src/locales/en/screens/notifications.json"))) as Tree;
const lookup = (tree: Tree, key: string) =>
  key.split(".").reduce<string | Tree | undefined>((node, part) => (node && typeof node === "object" ? node[part] : undefined), tree);
const leaves = (tree: Tree, prefix = ""): [string, string][] =>
  Object.entries(tree).flatMap(([key, value]) => (typeof value === "string" ? [[`${prefix}${key}`, value] as [string, string]] : leaves(value, `${prefix}${key}.`)));
const placeholders = (text: string) => Array.from(text.matchAll(/\{\{(\w+)\}\}/g), (match) => match[1]).sort().join(",");
for (const [key, value] of leaves(zh)) {
  const other = lookup(en, key);
  assert.equal(typeof other, "string", `英文缺少 ${key}`);
  assert.equal(placeholders(value), placeholders(other as string), `${key} 的插值参数中英文不一致`);
  assert.equal(CJK.test(other as string), false, `英文 ${key} 里含有中文`);
}
assert.equal(leaves(zh).length, leaves(en).length, "中英文键数量必须相同");

// 代码里用到的文案键：notifications 命名空间的顶层键前缀
{
  const used = new Set<string>();
  const topLevel = new Set(Object.keys(zh));
  for (const file of moduleFiles) {
    for (const match of stripComments(read(file)).matchAll(/\bt\(\s*["'`]([A-Za-z][A-Za-z0-9_.]*)["'`]/g)) {
      if (topLevel.has(match[1].split(".")[0])) used.add(match[1]);
    }
  }
  // 不是 t("字面量") 形式的用法：条件表达式 t(review ? "a" : "b")、三元取时间文案、错误解析的 fallbackKey
  for (const key of ["messages.reviewUnavailable", "messages.notAllowed", "time.yesterday", "priority.high", "errors.generic"]) used.add(key);
  assert.ok(stripComments(sourceOf("NotificationsScreen.tsx")).includes('fallbackKey: "errors.generic"'));
  for (const key of used) assert.equal(typeof lookup(zh, key), "string", `中文缺少文案键 ${key}`);
  for (const [key] of leaves(zh)) assert.ok(used.has(key), `文案键 ${key} 没有任何代码使用（死键）`);
}

// ───────── 3. 登记 ─────────
{
  const route = join(mobileRoot, "app/(shell)/notifications.tsx");
  assert.ok(existsSync(route), "缺少路由文件 app/(shell)/notifications.tsx");
  const source = read(route);
  assert.ok(source.includes('from "@/modules/notifications/NotificationsScreen"') && source.includes("<NotificationsScreen />"));
  assert.ok(source.split("\n").length <= 8, "路由文件只做转发");

  // 后端菜单：所有登录账号可见（没有权限限制），设备模式菜单不包含
  const navigation = read(join(repoRoot, "services/backend/BlazorApp.Api/Services/NavigationService.cs"));
  const start = navigation.indexOf('RouteName = "notifications"');
  assert.ok(start >= 0, "后端 FullAppMenu 缺少 notifications");
  const block = navigation.slice(start, navigation.indexOf("Order =", start));
  assert.equal(/Permission\s*=|AnyPermissions/.test(block), false, "通知入口对所有登录账号可见，不能挂权限");
  const deviceBase = navigation.slice(navigation.indexOf("DeviceBaseRouteNames = new("), navigation.indexOf("WarehouseDeviceRouteNames = new("));
  assert.equal(deviceBase.includes('"notifications"'), false);

  // 设备模式隐藏、iOS 审核模式排除、角色菜单目录（固定可见）
  assert.equal(
    getVisibleTabRouteNames({ routeNames: ["notifications"], isDeviceMode: true }).includes("notifications"),
    false,
    "设备模式必须隐藏通知入口",
  );
  assert.equal(
    getVisibleTabRouteNames({ routeNames: ["notifications"], isDeviceMode: false }).includes("notifications"),
    true,
    "账号会话保留后端下发的通知入口",
  );
  const review = read(join(mobileRoot, "src/modules/ios-review/menu.ts"));
  assert.ok(/IOS_REVIEW_EXCLUDED_ROUTE_NAMES[\s\S]*"notifications"[\s\S]*\] as const/.test(review), "iOS 审核模式必须排除通知入口");
  const catalog = read(join(mobileRoot, "src/modules/identity-admin/role-menu-catalog.ts"));
  assert.ok(/\["notifications",[^\]]*\[\],\s*\{\s*fixed:\s*true\s*\}\]/.test(catalog), "角色菜单目录里通知是固定可见项");
}

console.log("source-contract.test.ts: ok");
