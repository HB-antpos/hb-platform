import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";

/**
 * 订货助手的源码契约（移动端没有 jest，按源码文本校验关键约束）。
 *
 * 中文注释：
 * 1. react-native-webview 一加载就强制要求原生模块；main 的 JS 会 OTA 到没装 WebView 的旧原生包，
 *    所以只有页面文件可以顶层 import 它，路由必须先探测原生模块再延迟 require 页面。
 * 2. 菜单在旧原生包上要隐藏入口。
 * 3. 设备会话、iOS 审核模式都不展示入口。
 */
const mobileRoot = resolve(__dirname, "../../..");
const read = (path: string) => readFileSync(resolve(mobileRoot, path), "utf8");

const SCREEN_FILE = "src/modules/supplier-assistant/supplier-assistant-screen.tsx";

// 1. 全 App 只有订货助手页面文件 import react-native-webview。
function walk(directory: string, out: string[]) {
  for (const name of readdirSync(directory)) {
    if (name === "node_modules" || name.startsWith(".")) continue;
    const full = join(directory, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (/\.(ts|tsx|js|jsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name)) out.push(full);
  }
}
const sources: string[] = [];
walk(resolve(mobileRoot, "src"), sources);
walk(resolve(mobileRoot, "app"), sources);
const webviewImporters = sources
  .filter((file) => /from\s+["']react-native-webview["']|require\(\s*["']react-native-webview["']\s*\)/.test(readFileSync(file, "utf8")))
  .map((file) => relative(mobileRoot, file).split("\\").join("/"));
assert.deepEqual(webviewImporters, [SCREEN_FILE], "只有订货助手页面可以 import react-native-webview");

// 页面文件也不能被别处顶层 import（否则等于顶层 import 了 WebView）。
const screenImporters = sources
  .filter((file) => /from\s+["'][^"']*supplier-assistant-screen["']/.test(readFileSync(file, "utf8")))
  .map((file) => relative(mobileRoot, file));
assert.deepEqual(screenImporters, [], "订货助手页面只能由路由在探测原生模块后延迟 require");

// 2. 路由：先探测，再 require 页面；探测不到显示「请更新 App」。
const route = read("app/(shell)/supplier-assistant.tsx");
assert.match(route, /isSupplierAssistantNativeAvailable\(\)/);
assert.match(route, /require\("@\/modules\/supplier-assistant\/supplier-assistant-screen"\)/);
assert.match(route, /state\.updateRequired/);
assert.ok(
  route.indexOf("isSupplierAssistantNativeAvailable()") < route.indexOf("loadSupplierAssistantScreen();"),
  "必须先探测原生模块再加载页面",
);

// 原生探测用非强制的 TurboModuleRegistry.get，不能用 getEnforcing（探测本身会抛错）。
const availability = read("src/modules/supplier-assistant/native-availability.ts");
assert.match(availability, /TurboModuleRegistry\.get\("RNCWebViewModule"\)/);
assert.doesNotMatch(availability, /getEnforcing/);

// 3. 菜单：旧原生包上隐藏订货助手入口。
const navigationApi = read("src/modules/navigation/api.ts");
assert.match(navigationApi, /isSupplierAssistantNativeAvailable\(\)/);
assert.match(navigationApi, /SUPPLIER_ASSISTANT_ROUTE_NAME/);

// 4. 设备会话与 iOS 审核模式不展示入口。
assert.match(read("src/modules/navigation/default-route.ts"), /\/\/[^\n]*设备模式不展示入口。\n\s+"supplier-assistant",/);
assert.match(read("src/modules/ios-review/menu.ts"), /"supplier-assistant",\n\s+"workbench",/);

// 5. 页面：令牌只在原生层，请求一律经 apiClient 代发；扫码经 BLE 租约交给网页。
const screen = read(SCREEN_FILE);
assert.match(screen, /createSupplierAssistantRequest\(apiClientHttp/);
assert.doesNotMatch(screen, /Authorization/);
assert.match(screen, /acquireBleScanLease/);
assert.match(screen, /BARCODE_SCANNED/);
assert.match(screen, /originWhitelist=\{\["https:\/\/\*"\]\}/);

console.log("supplier-assistant source-contract: ok");
