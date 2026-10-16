import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

const root = join(__dirname, "..", "..", "..");
const read = (relative: string) => readFileSync(join(root, relative), "utf8");

const moduleFiles = [
  "src/modules/floating-language/FloatingLanguageButton.tsx",
  "src/modules/floating-language/QuickJumpPanel.tsx",
  "src/modules/floating-language/geometry.ts",
  "src/modules/floating-language/preference.ts",
  "src/modules/floating-language/quick-jump.ts",
  "src/modules/floating-language/recent-routes.ts",
  "src/modules/floating-language/language.ts",
];

test("壳布局：悬浮按钮挂在装 Stack 的容器里（底部导航栏之上），不挂在根布局", () => {
  const shell = read("app/(shell)/_layout.tsx");
  const stackAt = shell.indexOf("<Stack");
  const buttonAt = shell.indexOf("<FloatingLanguageButton");
  const tabBarAt = shell.indexOf("<PrimaryTabBar");
  assert.ok(stackAt > 0 && buttonAt > stackAt && tabBarAt > buttonAt);
  // Stack 与悬浮按钮在同一个容器里：两者之间不能先闭合容器。
  assert.doesNotMatch(shell.slice(stackAt, buttonAt), /<\/View>/);
  assert.doesNotMatch(read("app/_layout.tsx"), /FloatingLanguageButton/);
});

test("不引入需要重建原生包的依赖，也不用原生 Modal / BusinessSheet", () => {
  for (const file of moduleFiles) {
    const source = read(file);
    assert.doesNotMatch(source, /react-native-gesture-handler|react-native-reanimated|expo-haptics/, file);
    // 只查实际用法（注释里会提到「原生 Modal」）。
    assert.doesNotMatch(source, /<(Modal|BusinessSheet|Portal)\b|components\/ui\/BusinessSheet/, file);
  }
});

test("语言切换沿用设置页口径：iOS 审核会话只改内存", () => {
  const source = read("src/modules/floating-language/language.ts");
  assert.match(source, /isIosReviewSessionActive\(\)/);
  assert.match(source, /i18n\.changeLanguage\(next\)/);
  assert.match(source, /setAppLanguage\(next\)/);
});

test("审核态、全屏作业页不显示；入口复用导航权限上下文", () => {
  const source = read("src/modules/floating-language/FloatingLanguageButton.tsx");
  assert.match(source, /sessionKind === "iosReview" \|\| iosReviewOfflineGuardActive/);
  assert.match(source, /shouldHidePrimaryTabBar\(pathname\)/);
  assert.match(source, /useAppNavigationAccess\(\)/);
});

test("设置页：偏好分组里有开关，读写同一个本机偏好", () => {
  const settings = read("app/(shell)/settings.tsx");
  assert.match(settings, /testID="settings-floating-language"/);
  assert.match(settings, /value=\{floatingLanguageEnabled\}/);
  assert.match(settings, /onValueChange=\{setFloatingLanguageEnabled\}/);
  const preferencesAt = settings.indexOf('testID="settings-group-preferences-security"');
  const switchAt = settings.indexOf('testID="settings-floating-language"');
  const supportAt = settings.indexOf('testID="settings-group-app-support"');
  assert.ok(preferencesAt > 0 && switchAt > preferencesAt && supportAt > switchAt);
});

test("按住时长可调：按钮的计时与进度环都读设置值；设置页用滑轨（不加原生滑块依赖）且只在开启时显示", () => {
  const button = read("src/modules/floating-language/FloatingLanguageButton.tsx");
  assert.match(button, /latest\.current\.longPressMs/);
  assert.doesNotMatch(button, /FLOATING_LONG_PRESS_MS/);
  const settings = read("app/(shell)/settings.tsx");
  assert.match(settings, /<HoldTimeSlider/);
  assert.match(settings, /testID="settings-floating-language-hold"/);
  assert.match(settings, /onCommit=\{setFloatingLongPressMs\}/);
  const holdAt = settings.indexOf("<HoldTimeSlider");
  assert.match(settings.slice(Math.max(0, holdAt - 120), holdAt), /floatingLanguageEnabled \? \(/);
  // 滑块是纯 JS：不引入原生滑块依赖，保证可以走 OTA。
  const slider = read("src/modules/floating-language/HoldTimeSlider.tsx");
  assert.doesNotMatch(slider, /@react-native-community\/slider|react-native-gesture-handler|react-native-reanimated/);
  assert.match(slider, /accessibilityRole="adjustable"/);
  assert.doesNotMatch(read("package.json"), /@react-native-community\/slider/);
});
