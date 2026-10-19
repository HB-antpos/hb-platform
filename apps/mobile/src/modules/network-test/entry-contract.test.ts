import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const mobileRoot = path.resolve(__dirname, "../../..");
const read = (relativePath: string) => readFileSync(path.join(mobileRoot, relativePath), "utf8");

test("网络测试页是根路由（与 /privacy 同级），不在登录守卫里，未登录也能打开", () => {
  const route = read("app/network-test.tsx");
  assert.match(route, /export \{ NetworkTestScreen as default \} from "@\/modules\/network-test\/NetworkTestScreen"/);
  assert.match(read("src/modules/network-test/index.ts"), /NETWORK_TEST_ROUTE = "\/network-test"/);
});

test("登录页：服务器设置旁常驻「网络测试」，三种登录卡片都有入口", () => {
  const login = read("app/(auth)/login.tsx");
  const toolRow = login.slice(login.indexOf("const renderToolRow"), login.indexOf("const renderRetryCard"));
  assert.match(toolRow, /apiHost\.settingsButton/);
  assert.match(toolRow, /networkTestAvailable \?[\s\S]{0,200}onPress=\{openNetworkTest\}/);
  // 设备登录与用户登录都用同一个工具行；设备绑定账号卡单独补入口。
  assert.equal(login.match(/\{renderToolRow\(\)\}/g)?.length, 2);
  assert.ok((login.match(/onPress=\{openNetworkTest\}/g)?.length ?? 0) >= 3);
});

test("登录页：可重试的网络类失败显示失败卡片（重试 + 网络测试），业务失败仍走提示条", () => {
  const login = read("app/(auth)/login.tsx");
  assert.match(login, /\{renderRetryCard\("user"\)\}/);
  assert.match(login, /\{renderRetryCard\("device"\)\}/);
  assert.match(login, /if \(errorRetry !== kind \|\| !error\) return null;/);
  assert.equal(login.match(/setSnackbarVisible\(!descriptor\?\.retryable\)/g)?.length, 2);
  assert.match(login, /visible=\{snackbarVisible && !errorRetry\}/);
});

test("悬浮按钮面板与设置页都有「网络测试」入口", () => {
  const panel = read("src/modules/floating-language/QuickJumpPanel.tsx");
  assert.match(panel, /renderChip\("network-test", "access-point-network", t\("networkTest:entry"\), onOpenNetworkTest\)/);
  const button = read("src/modules/floating-language/FloatingLanguageButton.tsx");
  assert.match(button, /onOpenNetworkTest=\{openNetworkTest\}/);
  assert.match(button, /router\.push\(NETWORK_TEST_ROUTE/);
  const settings = read("app/(shell)/settings.tsx");
  assert.match(settings, /networkTestAvailable \?[\s\S]{0,800}testID="settings-network-test"/);
});

test("iOS 审核会话与审核构建登录前不提供网络测试（测试直接用全局 fetch，不经过审核离线拦截）", () => {
  const availability = read("src/modules/network-test/availability.ts");
  assert.match(availability, /if \(isIosReviewSessionActive\(\)\) return false;/);
  assert.match(availability, /offlineGuardActive && isIosReviewBuildEnabled\(getIosReviewBuildContext\(\)\)/);
  const screen = read("src/modules/network-test/NetworkTestScreen.tsx");
  assert.match(screen, /const reviewSession = !useNetworkTestAvailable\(\);/);
  assert.match(screen, /if \(!context \|\| reviewSession \|\| autoStartedRef\.current\) return;/);
});

test("报告不含登录令牌：令牌只用于登录态接口的请求头", () => {
  const screen = read("src/modules/network-test/NetworkTestScreen.tsx");
  const report = read("src/modules/network-test/report.ts");
  assert.doesNotMatch(report, /accessToken/);
  assert.doesNotMatch(screen.slice(screen.indexOf("const copyReport"), screen.indexOf("const verdictTone")), /accessToken/);
});
