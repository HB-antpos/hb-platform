import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// 源码契约测试：位置请求（服务端要求已上班员工在前台取一次位置）的接线约束。
// expo-location / apiClient / react-native 无法在 Node 下加载，这里用源码断言守住：
// 1. 根布局在 useLegacyLocationTrackingCleanup 同处挂载，且用同一个 sideEffectsEnabled；
// 2. 全程只用「使用 App 期间」前台权限：没有后台定位 API、没有缓存旧点、自动触发路径不会申请权限；
// 3. 资格守卫（个人账号 + Attendance.Punch.Self + 非 iOS 审核态）在任何网络请求之前；
// 4. 推送点击 / 前台收到 / 收件箱点击三处接线与触发模式（交互 / 非交互）划分；
// 5. 纯逻辑文件不依赖 React Native / 原生模块 / apiClient；日志不含坐标；没有引入新原生依赖；
// 6. 提示文案走 i18n，中英文齐全且没有死键。

const directory = dirname(fileURLToPath(import.meta.url));
const mobileRoot = join(directory, "..", "..", "..", "..");
const read = (path: string) => readFileSync(join(mobileRoot, path), "utf8");
const moduleSource = (name: string) => readFileSync(join(directory, name), "utf8");
const stripComments = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
const code = (name: string) => stripComments(moduleSource(name));

function collectProductionSources(relativeDir: string): string[] {
  const absolute = join(mobileRoot, relativeDir);
  return readdirSync(absolute, { withFileTypes: true }).flatMap((entry) => {
    const next = join(relativeDir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" ? [] : collectProductionSources(next);
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.(ts|tsx)$/.test(entry.name) ? [next] : [];
  });
}

const moduleFiles = readdirSync(directory).filter((name) => /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name));
/** 纯逻辑文件：不依赖 React Native / 原生模块 / apiClient / store，才能在 Node 下测试。 */
const PURE_FILES = [
  "location-request-logic.ts",
  "location-request-capture.ts",
  "location-request-consumer.ts",
  "location-request-api-core.ts",
  "location-request-notice-store.ts",
];

test("模块文件齐全：纯逻辑 / 取位置 / 消费器 / 接口 / 运行时 / hook / 提示组件", () => {
  for (const name of [...PURE_FILES, "location-request-api.ts", "location-request-runtime.ts", "use-location-request-consumer.ts", "LocationRequestNotice.tsx"]) {
    assert.ok(moduleFiles.includes(name), `缺少 ${name}`);
  }
});

// ───────── 1. 根布局挂载 ─────────

test("根布局：和 useLegacyLocationTrackingCleanup 同处挂载，用同一个 sideEffectsEnabled；提示组件在 PaperProvider 内渲染", () => {
  const layout = read("app/_layout.tsx");
  assert.match(layout, /import \{ useLocationRequestConsumer \} from "@\/modules\/attendance\/location-request\/use-location-request-consumer";/);
  assert.match(layout, /import \{ LocationRequestNotice \} from "@\/modules\/attendance\/location-request\/LocationRequestNotice";/);
  assert.match(layout, /useLocationRequestConsumer\(\{ enabled: sideEffectsEnabled \}\)/);
  assert.match(layout, /useLegacyLocationTrackingCleanup\(\{ enabled: sideEffectsEnabled \}\)/);
  const cleanupIndex = layout.indexOf("useLegacyLocationTrackingCleanup({");
  const consumerIndex = layout.indexOf("useLocationRequestConsumer({");
  const earlyReturn = layout.indexOf("if (startupError) {");
  assert.ok(cleanupIndex > 0 && consumerIndex > cleanupIndex && consumerIndex < earlyReturn, "hook 要紧跟清理 hook，且在提前 return 之前（hooks 顺序）");
  const paperStart = layout.indexOf("<PaperProvider");
  const noticeIndex = layout.indexOf("<LocationRequestNotice />");
  assert.ok(paperStart > 0 && noticeIndex > paperStart, "提示组件要在 PaperProvider / I18nextProvider 内（用到 Snackbar 与翻译）");
});

test("hook：挂载 / 登录后触发 launch，回前台触发 foreground；都是非交互；不依赖推送模块", () => {
  const hook = code("use-location-request-consumer.ts");
  assert.match(hook, /AppState\.addEventListener\("change"/);
  assert.match(hook, /state === "active"/);
  assert.match(hook, /consumeLocationRequests\(\{ trigger: "launch" \}\)/);
  assert.match(hook, /consumeLocationRequests\(\{ trigger: "foreground" \}\)/);
  assert.match(hook, /if \(!enabled \|\| !isAuthenticated \|\| !userGuid\)/, "enabled / 登录态 / 账号任一不满足就不触发");
  assert.match(hook, /\[enabled, isAuthenticated, userGuid\]/, "登录完成 / 切换账号会重新触发");
  assert.doesNotMatch(hook, /push-tap|inbox-tap/, "自动触发不得使用交互模式");
  assert.doesNotMatch(hook, /modules\/push/, "根布局 hook 不依赖推送模块（推送运行时只在进入主界面后才加载）");
});

// ───────── 2. 只用前台权限，不碰后台 ─────────

test("全应用生产源码仍没有后台定位调用（本模块只是不增加）", () => {
  const offenders = [...collectProductionSources("src"), ...collectProductionSources("app")].filter((file) =>
    /\b(startLocationUpdatesAsync|requestBackgroundPermissionsAsync|startGeofencingAsync)\b/.test(read(file)),
  );
  assert.deepEqual(offenders, []);
});

test("本模块不出现任何后台定位 / 任务 / 缓存旧点 / 持续监听 API，自动触发路径不申请权限", () => {
  const FORBIDDEN =
    /\b(startLocationUpdatesAsync|requestBackgroundPermissionsAsync|getBackgroundPermissionsAsync|startGeofencingAsync|hasStartedLocationUpdatesAsync|watchPositionAsync|getLastKnownPositionAsync|requestForegroundPermissionsAsync)\b|expo-task-manager|TaskManager|defineTask/;
  for (const name of moduleFiles) {
    assert.doesNotMatch(code(name), FORBIDDEN, `${name} 不得出现后台 / 缓存旧点 / 持续监听 / 直接申请权限的调用`);
  }
});

test("取位置只走 collectRequiredLocation（交互）与 getForegroundPermissionsAsync + 高精度 getCurrentPositionAsync（非交互）", () => {
  const runtime = code("location-request-runtime.ts");
  assert.match(runtime, /import \{ collectRequiredLocation, getAttendanceDeviceContext \} from "@\/modules\/attendance\/required-location";/);
  assert.match(runtime, /Location\.getForegroundPermissionsAsync\(\)/);
  assert.match(runtime, /Location\.getCurrentPositionAsync\(\{ accuracy: Location\.Accuracy\.High \}\)/);
  assert.match(runtime, /collectRequiredLocation,\s*\}/, "交互路径注入 collectRequiredLocation");
  assert.match(runtime, /isIosReviewSessionActive,/);

  // 非交互取点函数里没有 collectRequiredLocation（那里会申请权限）；交互分支里没有另外的权限查询。
  const capture = code("location-request-capture.ts");
  const silent = capture.slice(capture.indexOf("async function captureSilently"), capture.indexOf("export async function captureLocationForRequest"));
  assert.match(silent, /getForegroundPermissionsAsync\(\)/);
  assert.match(silent, /permission\.status !== "granted"/);
  assert.doesNotMatch(silent, /collectRequiredLocation/, "非交互路径不得走会申请权限的 collectRequiredLocation");
  const entry = capture.slice(capture.indexOf("export async function captureLocationForRequest"));
  const interactiveBranch = entry.slice(entry.indexOf("if (interactive) {"), entry.indexOf("return await withTimeout(captureSilently"));
  assert.match(interactiveBranch, /collectRequiredLocation\(\)/);
  assert.doesNotMatch(interactiveBranch, /getForegroundPermissionsAsync|getCurrentPositionAsync/);

  // required-location.ts 仍是高精度、先申请前台权限、不用缓存旧点（本模块复用它，这里钉住它没被改弱）
  const required = read("src/modules/attendance/required-location.ts");
  assert.match(required, /Location\.getCurrentPositionAsync\(\{\s*accuracy: Location\.Accuracy\.High,/);
  assert.match(required, /new Date\(position\.timestamp\)\.toISOString\(\)/, "采集时间取定位点自带时间戳");
});

test("iOS 审核态：取位置前先守卫，两层都不取真实位置", () => {
  const capture = code("location-request-capture.ts");
  const entry = capture.slice(capture.indexOf("export async function captureLocationForRequest"));
  const guard = entry.indexOf("bridge.isIosReviewSessionActive()");
  assert.ok(guard > 0 && guard < entry.indexOf("withTimeout("), "审核守卫必须在任何权限 / 取点调用之前");
  assert.match(entry, /return \{ status: "review-skipped" \}/);
  // 资格层：审核态根本不发请求
  const runtime = code("location-request-runtime.ts");
  assert.match(runtime, /iosReview: isIosReviewSessionActive\(\) \|\| auth\.iosReviewOfflineGuardActive \|\| auth\.sessionKind === "iosReview"/);
});

// ───────── 3. 资格守卫 ─────────

test("资格：个人账号会话 + Attendance.Punch.Self + 非审核态 + 不在强制改密；消费器第一步就判断，不满足不发请求", () => {
  const runtime = code("location-request-runtime.ts");
  assert.match(runtime, /hasPunchSelfPermission: auth\.access\.hasPermission\(PERMISSIONS\.Attendance\.PunchSelf\)/);
  assert.match(runtime, /isAuthenticated: auth\.isAuthenticated/);
  assert.match(runtime, /sessionKind: auth\.sessionKind/);
  assert.match(runtime, /mustChangePassword: auth\.user\?\.mustChangePassword === true/);
  assert.match(runtime, /resolveUserGuid: \(\) => resolveLocationRequestUserGuid\(readSessionSnapshot\(\)\)/);
  const logic = code("location-request-logic.ts");
  assert.match(logic, /snapshot\.sessionKind !== "account"/);
  assert.match(logic, /if \(snapshot\.iosReview\) return null;/);

  const consumer = code("location-request-consumer.ts");
  const consume = consumer.slice(consumer.indexOf("async function consume("));
  assert.match(consume, /const userGuid = deps\.resolveUserGuid\(\);\s*if \(!userGuid\) return \{ kind: "skipped", reason: "not-eligible" \};/);
  // 每个 await 之后复核账号
  assert.ok((consumer.match(/stillCurrent\(\)/g) ?? []).length >= 5, "取待办 / 取位置 / 取设备 / 逐条上报 / 提示之前都要复核账号没变");
});

test("接口：路径、账号绑定、静默头与后端控制器路由一致", () => {
  const logic = code("location-request-logic.ts");
  assert.match(logic, /LOCATION_REQUEST_ATTENDANCE_BASE = "\/react\/v1\/attendance"/);
  assert.match(logic, /`\$\{LOCATION_REQUEST_ATTENDANCE_BASE\}\/my\/location-requests\/pending`/);
  assert.match(logic, /\/my\/location-requests\/\$\{encodeURIComponent\(requestGuid\)\}\/respond/);
  const api = code("location-request-api-core.ts");
  assert.match(api, /accountBoundRequestConfig\(accountGuid, options\?\.signal\)/);
  assert.match(api, /"X-Skip-Auth-Redirect": "1"/);
  assert.match(api, /http\.get\(LOCATION_REQUEST_PENDING_PATH,/);
  assert.match(api, /http\.post\(locationRequestRespondPath\(requestGuid\), body,/);
  assert.match(code("location-request-api.ts"), /import \{ apiClient \} from "@\/shared\/api\/client"/);
});

// ───────── 4. 推送 / 收件箱接线 ─────────

test("推送运行时接线（push-runtime.ts / push-native.ts）", () => {
  const pushRuntime = stripComments(read("src/modules/push/push-runtime.ts"));
  assert.match(pushRuntime, /import \{ consumeLocationRequests \} from "@\/modules\/attendance\/location-request\/location-request-runtime";/);
  const navigate = pushRuntime.slice(pushRuntime.indexOf("function navigateToPushTarget"), pushRuntime.indexOf("const eventController"));
  assert.match(navigate, /target\.kind === "location-request"/);
  assert.match(navigate, /consumeLocationRequests\(\{ trigger: "push-tap" \}\)/, "点推送是交互模式");
  assert.ok(navigate.indexOf('"push-tap"') < navigate.lastIndexOf('router.push("/(shell)/notifications")'), "先消费、再落到收件箱");
  assert.match(pushRuntime, /onLocationRequestReceived: \(\) => consumeLocationRequests\(\{ trigger: "push-received" \}\)/, "前台收到推送是非交互");
  assert.match(pushRuntime, /native\.installForegroundHandler\(\(notification\) => eventController\.onForegroundNotification\(notification\)\)/);
  assert.doesNotMatch(pushRuntime, /trigger: "(launch|foreground)"/);

  const native = stripComments(read("src/modules/push/push-native.ts"));
  assert.match(native, /handleNotification: async \(notification\) =>/);
  assert.match(native, /onForeground\(toForegroundNotification\(notification\)\)/);
  assert.doesNotMatch(native, /content\??\.(title|body|subtitle)\b/, "前台只取 data.type，不读标题 / 正文");
});

test("通知收件箱：点位置请求通知 = 交互式消费（inbox-tap）", () => {
  const screen = stripComments(read("src/modules/notifications/NotificationsScreen.tsx"));
  assert.match(screen, /import \{ consumeLocationRequests \} from "@\/modules\/attendance\/location-request\/location-request-runtime";/);
  assert.match(screen, /target\.kind === "location-request"\) consumeLocationRequests\(\{ trigger: "inbox-tap" \}\)/);
});

test("只有 push-tap / inbox-tap 是交互触发（会弹系统权限框）；全应用只有这两处使用", () => {
  const users = [...collectProductionSources("src"), ...collectProductionSources("app")].filter((file) =>
    /trigger: "(push-tap|inbox-tap)"/.test(stripComments(read(file))),
  );
  assert.deepEqual(users.map((file) => file.replaceAll("\\", "/")).sort(), [
    "src/modules/notifications/NotificationsScreen.tsx",
    "src/modules/push/push-runtime.ts",
  ]);
});

// ───────── 5. 纯逻辑、日志、依赖 ─────────

test("纯逻辑文件不依赖 React Native / 原生模块 / apiClient / store / 全局运行时", () => {
  for (const name of PURE_FILES) {
    const source = code(name);
    assert.doesNotMatch(
      source,
      /from\s+["'](react-native|react-native-paper|expo-[a-z-]+|@\/store|@\/shared\/api\/client|@\/modules\/ios-review|@\/modules\/device|@\/modules\/push)/,
      `${name} 不得依赖 React Native / 原生模块 / apiClient / store`,
    );
  }
  // 唯一例外：notice store 用 zustand（纯 JS）
  assert.match(code("location-request-notice-store.ts"), /from "zustand"/);
  // required-location 只允许 type import（运行时 import 会把 expo-location 拉进 Node 测试）
  for (const name of ["location-request-logic.ts", "location-request-capture.ts", "location-request-consumer.ts"]) {
    for (const match of code(name).matchAll(/import\s+(type\s+)?[^;]*?from\s+"@\/modules\/attendance\/required-location"/g)) {
      assert.ok(match[1], `${name} 对 required-location 只能 import type`);
    }
  }
});

test("日志不含坐标：纯逻辑文件不写日志；运行时只有一处 console.warn 且经 warn 依赖转发短码与计数", () => {
  for (const name of PURE_FILES) {
    assert.doesNotMatch(code(name), /console\./, `${name} 不直接写日志`);
  }
  const runtime = code("location-request-runtime.ts");
  assert.equal((runtime.match(/console\./g) ?? []).length, 1);
  assert.match(runtime, /warn: \(message, detail\) => console\.warn\(message, detail\)/);

  // 传给 warn 的 detail 只有短码 / 状态 / 计数，不含任何坐标字段
  const consumer = code("location-request-consumer.ts");
  for (const match of consumer.matchAll(/deps\.warn\([\s\S]*?\);/g)) {
    assert.doesNotMatch(match[0], /locationLatitude|locationLongitude|locationAccuracy|coords|captured\.location|location\./, `warn 不得带坐标：${match[0].slice(0, 80)}`);
  }
  // 坐标不进缓存 / 存储
  for (const name of moduleFiles) {
    assert.doesNotMatch(code(name), /AsyncStorage|SecureStore|SecureStorage|setQueryData|queryClient/, `${name} 不落盘、不进缓存`);
  }
});

test("没有引入新原生依赖：本模块只引用已有依赖（react / react-native / paper / zustand / expo-location / axios 类型）", () => {
  const allowed = new Set(["react", "react-native", "react-native-paper", "zustand", "expo-location", "axios"]);
  for (const name of moduleFiles) {
    for (const match of code(name).matchAll(/from\s+["']([^"']+)["']/g)) {
      const specifier = match[1];
      if (specifier.startsWith("@/") || specifier.startsWith("./")) continue;
      assert.ok(allowed.has(specifier), `${name} 引用了未登记的依赖 ${specifier}`);
    }
  }
  const pkg = JSON.parse(read("package.json")) as { dependencies: Record<string, string> };
  for (const dependency of allowed) assert.ok(pkg.dependencies[dependency], `${dependency} 必须已在 package.json 依赖里`);
  // expo-location 只在运行时接线文件里出现
  for (const name of moduleFiles.filter((file) => file !== "location-request-runtime.ts")) {
    assert.doesNotMatch(code(name), /expo-location/, `${name} 不应直接引用 expo-location`);
  }
});

// ───────── 6. 文案 ─────────

type Tree = { [key: string]: string | Tree };
const CJK = /[㐀-鿿＀-￯]/;

test("提示组件不硬编码中文；locationRequest.* 文案键中英文齐全、插值一致、没有死键", () => {
  assert.equal(CJK.test(code("LocationRequestNotice.tsx")), false, "提示组件不得硬编码中文");
  const zh = (JSON.parse(read("src/locales/zh/screens/attendance.json")) as { locationRequest: Tree }).locationRequest;
  const en = (JSON.parse(read("src/locales/en/screens/attendance.json")) as { locationRequest: Tree }).locationRequest;
  const placeholders = (text: string) => Array.from(text.matchAll(/\{\{(\w+)\}\}/g), (m) => m[1]).sort().join(",");
  assert.deepEqual(Object.keys(zh).sort(), Object.keys(en).sort(), "中英文键必须相同");
  for (const [key, value] of Object.entries(zh)) {
    assert.equal(typeof value, "string");
    assert.equal(typeof en[key], "string", `英文缺少 locationRequest.${key}`);
    assert.equal(placeholders(value as string), placeholders(en[key] as string), `${key} 的插值参数中英文不一致`);
    assert.equal(CJK.test(en[key] as string), false, `英文 locationRequest.${key} 含中文`);
  }
  const component = code("LocationRequestNotice.tsx");
  const used = new Set(Array.from(component.matchAll(/t\("locationRequest\.([A-Za-z]+)"/g), (m) => m[1]));
  assert.deepEqual([...used].sort(), Object.keys(zh).sort(), "组件用到的键与文案键一一对应（没有缺键，也没有死键）");
  assert.match(component, /useAppTranslation\("attendance"\)/);
  assert.match(zh.reported as string, /\{\{name\}\}/);
  assert.match(zh.reportedMany as string, /\{\{count\}\}/);
});

test("文案不出现采集频率表述，也不说「后台」会取位置", () => {
  const frequency = /每隔|定期|每\s*\d+\s*(分钟|秒)|几分钟|分钟记录|every (few )?(minutes?|seconds?)|periodic|per minute|\d+\s*minutes? interval/i;
  for (const lang of ["zh", "en"]) {
    const copy = (JSON.parse(read(`src/locales/${lang}/screens/attendance.json`)) as { locationNotice: Tree; locationRequest: Tree });
    const flatten = (value: unknown): string[] => (typeof value === "string" ? [value] : Object.values(value as object).flatMap(flatten));
    for (const text of [...flatten(copy.locationNotice), ...flatten(copy.locationRequest)]) {
      assert.doesNotMatch(text, frequency, `${lang} 文案不得出现采集频率表述：${text}`);
    }
  }
});
