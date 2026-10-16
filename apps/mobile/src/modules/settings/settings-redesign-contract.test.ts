import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const mobileRoot = path.resolve(__dirname, "../../..");
const read = (relativePath: string) => readFileSync(path.join(mobileRoot, relativePath), "utf8");

const source = read("app/(shell)/settings.tsx");
// 渲染里「更多设置」视图在前、首页视图在后（isMoreView ? 更多 : 首页）。
const moreStart = source.indexOf('testID="settings-more-overview"');
const mainStart = source.indexOf('testID="settings-overview"');
const moreView = source.slice(moreStart, mainStart);
const mainView = source.slice(mainStart, source.indexOf("<DeviceActivationDialog"));

test("「更多设置」是同一设置路由的 ?view=more 视图，返回走路由栈", () => {
  assert.ok(moreStart > 0 && mainStart > moreStart);
  assert.match(source, /useLocalSearchParams<\{ view\?: string \| string\[\] \}>\(\)/);
  assert.match(source, /=== "more"/);
  assert.match(source, /pathname: "\/\(shell\)\/settings", params: \{ view: "more" \}/);
  assert.match(source, /router\.canGoBack\(\)[\s\S]{0,80}router\.back\(\)/);
  assert.match(moreView, /testID="settings-more-back"/);
});

test("首屏名片：个人码二维码直接在名片里，点开仍是原来的弹层", () => {
  assert.match(mainView, /<IdentityCard[\s\S]{0,200}testID="settings-identity"/);
  assert.match(mainView, /<CashierBarcodeCard[\s\S]{0,400}variant="tile"/);
  // 设备授权会话没有个人码：只在有个人账号的会话里渲染，也不发请求。
  assert.match(source, /const showPersonalCode = isAuthenticated && Boolean\(user\) && sessionKind !== "device"/);
  assert.match(source, /usePersonalCodeValue\([\s\S]{0,120}showPersonalCode/);

  const card = read("src/modules/employee-profile/CashierBarcodeCard.tsx");
  assert.match(card, /variant === "tile"/);
  assert.match(card, /testID="settings-personal-code"/);
  // 二维码方块与原来的紧凑卡同尺寸（104px），弹层、刷新、打印逻辑共用。
  assert.match(card, /<QRCode value=\{barcodeValue\} size=\{104\}/);
  assert.match(card, /usePersonalCodeValue\(userIdentity: string, enabled = true\)/);
});

test("低频项收进「更多设置」，首页不再出现", () => {
  for (const marker of [
    'router.push("/privacy")',
    "openApiHostSettings",
    "handleCheckUpdates",
    "change-password?mode=voluntary",
    'testID="settings-diagnostics"',
    'testID="settings-app-update"',
  ]) {
    assert.ok(moreView.includes(marker), `「更多设置」里必须有 ${marker}`);
    assert.ok(!mainView.includes(marker), `首页不应再有 ${marker}`);
  }
  // 修改密码仍只有个人账号会话能看到。
  assert.match(moreView, /isAuthenticated && user && sessionKind === "account"[\s\S]{0,600}change-password/);
  // 首页入口：一行「更多设置」。
  assert.match(mainView, /onPress=\{openMoreSettings\}[\s\S]{0,80}testID="settings-more"/);
});

test("首页删除了重复信息：头部诊断按钮、设备汇总卡、版本页脚、常驻隐私说明", () => {
  assert.equal(source.match(/testID="settings-diagnostics"/g)?.length, 1, "诊断入口只剩「关于与诊断」一处");
  assert.doesNotMatch(source, /diagnosticsHeaderTriggerRef/);
  assert.doesNotMatch(mainView, /connectionSummary/);
  assert.doesNotMatch(source, /buildFooter/);
  assert.doesNotMatch(mainView, /privacy\.employeeDescription|privacy\.deviceDescription/);
  // 数据权利说明在「更多设置」里默认折叠。
  assert.match(source, /const \[dataRightsExpanded, setDataRightsExpanded\] = useState\(false\)/);
  assert.match(moreView, /dataRightsExpanded \? \(/);
});

test("设备与打印：分区标题右侧是汇总胶囊，行尾是状态点；离线数据仍受资格门禁", () => {
  assert.match(mainView, /<SummaryPill/);
  assert.match(mainView, /countAttentionItems|attentionCount/);
  assert.match(source, /countAttentionItems\(\{[\s\S]{0,260}receiptPrinterError: receiptPrinterStatus === "error"/);
  assert.match(mainView, /<StatusDotLabel/);
  assert.match(mainView, /\{offlineEligible \? \([\s\S]{0,900}testID="settings-offline-data"/);
  // 三个弹层入口都还在，并保留触发元素引用用于恢复焦点。
  for (const ref of ["deviceTriggerRef", "labelPrinterTriggerRef", "receiptPrinterTriggerRef"]) {
    assert.match(mainView, new RegExp(`actionRef=\\{${ref}\\}`));
  }
});

test("偏好：语言、悬浮按钮开关、按住时长滑轨；退出登录仍在首页底部", () => {
  assert.match(mainView, /testID="settings-group-preferences-security"/);
  assert.match(mainView, /handleLanguageChange\(value as AppLanguage\)/);
  assert.match(mainView, /testID="settings-floating-language"/);
  assert.match(mainView, /floatingLanguageEnabled \? \(\s*<HoldTimeSlider/);
  assert.match(mainView, /onPress=\{handleLogout\}/);
});

test("新增文案中英文一一对应，且分组改名后悬浮按钮的提示不再指向旧名字", () => {
  const zh = JSON.parse(read("src/locales/zh/screens/settings.json"));
  const en = JSON.parse(read("src/locales/en/screens/settings.json"));
  const keys: [string, string][] = [
    ["more", "title"],
    ["more", "summary"],
    ["more", "back"],
    ["more", "connectionGroup"],
    ["more", "privacyGroup"],
    ["more", "dataRights"],
    ["more", "dataRightsExpand"],
    ["more", "dataRightsCollapse"],
    ["overview", "connectionAllGood"],
    ["overview", "connectionAttention"],
    ["overview", "currentDeviceSubtitle"],
    ["account", "viewProfile"],
    ["account", "personalCodeLine"],
    ["account", "notLoggedIn"],
    ["account", "deviceModeShort"],
  ];
  for (const [group, key] of keys) {
    assert.equal(typeof zh[group][key], "string", `zh ${group}.${key}`);
    assert.equal(typeof en[group][key], "string", `en ${group}.${key}`);
  }
  assert.match(zh.overview.connectionAttention, /\{\{count\}\}/);
  assert.match(en.overview.connectionAttention, /\{\{count\}\}/);
  assert.match(zh.account.personalCodeLine, /\{\{code\}\}/);
  assert.equal(zh.groups.preferences, "偏好");
  assert.equal(en.groups.preferences, "Preferences");

  const zhCommon = JSON.parse(read("src/locales/zh/common.json"));
  const enCommon = JSON.parse(read("src/locales/en/common.json"));
  assert.match(zhCommon.floatingLanguage.hiddenToast, /设置 › 偏好」/);
  assert.match(enCommon.floatingLanguage.hiddenToast, /Settings › Preferences$/);
});
