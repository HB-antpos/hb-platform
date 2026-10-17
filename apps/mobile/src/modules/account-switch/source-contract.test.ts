import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

// 多账号的接线契约：这些约束一旦被改坏，会出现「串号」「误登出」「把别人的数据留给新账号」等严重问题，
// 所以用源码断言钉住（逻辑本身的行为测试在 switch-flow.test.ts / vault.test.ts）。
const mobileRoot = path.resolve(__dirname, "../../..");
const read = (relativePath: string) => readFileSync(path.join(mobileRoot, relativePath), "utf8");

const client = read("src/shared/api/client.ts");
const authStore = read("src/store/auth-store.ts");
const settings = read("app/(shell)/settings.tsx");
const layout = read("app/(shell)/_layout.tsx");
const login = read("app/(auth)/login.tsx");
const service = read("src/modules/account-switch/account-switch-service.ts");
const flow = read("src/modules/account-switch/switch-flow.ts");
const addView = read("src/modules/account-switch/AddAccountView.tsx");
const i18nSource = read("src/shared/i18n/i18n.ts");

test("请求层：切换期间暂停请求，旧账号的 401 / 刷新不能登出或覆盖新账号", () => {
  assert.match(client, /await waitForAccountSwitchIdle\(\)/);
  // 请求盖会话代号，读完令牌后再确认一次没有被切换打断
  assert.match(client, /_sessionGeneration/);
  assert.match(client, /isAccountSwitchInProgress\(\) \|\| isStaleSessionGeneration\(sessionStamp\)/);
  // 401 处理最前面：旧代号 / 切换中的 401 直接作废，不刷新、不登出
  const staleBranch = client.indexOf("isStaleSessionGeneration(original?._sessionGeneration)");
  const refreshBranch = client.indexOf("isRefreshing = true");
  assert.ok(staleBranch > 0 && staleBranch < refreshBranch);
  // 刷新结果写入前核对没有被切换打断；被打断不算登录失效
  assert.match(client, /isStaleSessionGeneration\(refreshGeneration\)/);
  assert.match(client, /code === "ACCOUNT_SESSION_CHANGED"[\s\S]{0,200}return Promise\.reject\(refreshErr\)/);
  assert.match(client, /export async function waitForTokenRefreshIdle/);
  // 刷新结果只看会话代号：激活前槽位仍属于旧账号，切换流程会等本次刷新收尾后回写最新令牌
  const refreshGuard = client.indexOf("isStaleSessionGeneration(refreshGeneration)");
  assert.ok(refreshGuard > 0);
  assert.doesNotMatch(client.slice(refreshGuard - 80, refreshGuard), /isAccountSwitchInProgress/);
  // 200 + 「未登录」信封也要认旧会话：旧账号的迟到响应不能把新账号登出
  assert.match(client, /const stamp = \(response\.config[\s\S]{0,160}isStaleSessionGeneration\(stamp\)/);
});

test("auth-store：激活账号会话先清旧账号的内存态与收银条码记录，再写新令牌", () => {
  const start = authStore.indexOf("async activateAccountSession(");
  assert.ok(start > 0);
  const body = authStore.slice(start, authStore.indexOf("}));", start));
  const order = [
    "clearSensitiveQueryCache(queryClient)",
    "useCartStore.getState().reset()",
    "usePickerStore.getState().clearPicker()",
    "STORE_SELECTION_STORAGE_KEY",
    "clearCashierBarcodePrintSession()",
    "SecureStorage.setToken(accessToken)",
    "SecureStorage.setRefreshToken(refreshToken)",
    "SecureStorage.setUser(user)",
    'setAuthSessionMarker("account")',
  ].map((needle) => {
    const index = body.indexOf(needle);
    assert.ok(index >= 0, `缺少 ${needle}`);
    return index;
  });
  assert.deepEqual(order, [...order].sort((a, b) => a - b));
  // 写到一半失败：整体清场回登录页，不留半新半旧的混合身份
  assert.match(body, /catch \(error\)[\s\S]{0,200}clearLocalSession\(\)[\s\S]{0,60}throw error/);
  // 菜单不在闸门内拉（会被自己的闸门挡住）
  assert.doesNotMatch(body, /fetchMenu\(\)/);
});

test("流水线：推送注销在关闸门之前，目标账号的请求走裸请求", () => {
  const notify = flow.indexOf("await deps.notifyLeaving()");
  const begin = flow.indexOf("deps.gate.begin()");
  assert.ok(notify > 0 && begin > notify);
  assert.doesNotMatch(read("src/modules/account-switch/raw-auth.ts"), /from "@\/shared\/api\/client"|from "@\/modules\/auth\/api"[\s\S]{0,0}apiClient/);
  assert.match(service, /refreshWithToken:[\s\S]{0,80}rawRefresh/);
  assert.match(service, /fetchUserWithToken: rawFetchCurrentUser/);
  // 添加账号拒绝 iOS 审核用户名（审核走本地离线认证，不是真实账号）
  assert.match(service, /isIosReviewUsername\(username\)/);
});

test("添加账号：「保存到切换列表」默认不勾选", () => {
  assert.match(addView, /const \[save, setSave\] = useState\(false\)/);
});

test("设置页：只有个人账号会话有切换入口，退出登录走多账号流程", () => {
  assert.match(settings, /const accountSwitchEnabled = isAuthenticated && Boolean\(user\) && sessionKind === "account"/);
  assert.match(settings, /onPressSwitch=\{accountSwitchEnabled \? openAccountSwitcher : undefined\}/);
  assert.match(settings, /view=add-account|"add-account"/);
  assert.match(settings, /if \(accountSwitchEnabled\) \{[\s\S]{0,200}await signOutCurrent\(\)/);
});

test("外壳布局：多账号弹层宿主在底部导航栏之后渲染（盖住导航栏）", () => {
  const tabBar = layout.indexOf("<PrimaryTabBar");
  const host = layout.indexOf("<AccountSwitchHost />");
  assert.ok(tabBar > 0 && host > tabBar);
});

test("登录页「清除本机登录缓存」同时清空多账号保险库", () => {
  assert.match(login, /await clearAccountVault\(\);[\s\S]{0,80}clearLocalSession\(\)/);
});

test("多账号界面不使用原生 Modal，班中提示不写具体原因", () => {
  for (const file of ["AccountSwitcherSheet", "ReloginSheet", "SwitchingOverlay", "AddAccountView", "AccountSwitchHost"]) {
    assert.doesNotMatch(read(`src/modules/account-switch/${file}.tsx`), /<Modal|from "react-native-paper"[\s\S]{0,80}\bModal\b/);
  }
  const zh = JSON.parse(read("src/locales/zh/screens/accountSwitch.json")).blocked;
  const en = JSON.parse(read("src/locales/en/screens/accountSwitch.json")).blocked;
  assert.equal(zh.onShiftTitle, "班中不能切换账号");
  assert.equal(zh.onShiftMessage, "请先下班后再切换。");
  assert.equal(zh.busyMessage, "请先完成当前操作。");
  assert.ok(en.onShiftMessage.length < 60 && en.busyMessage.length < 60);
});

test("i18n：accountSwitch 命名空间已注册", () => {
  assert.match(i18nSource, /accountSwitch: accountSwitchZh/);
  assert.match(i18nSource, /accountSwitch: accountSwitchEn/);
  assert.match(i18nSource, /"workbench", "accountSwitch"\]/);
});
