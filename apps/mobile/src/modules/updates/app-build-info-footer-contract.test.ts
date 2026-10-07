import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import loginEn from "../../locales/en/screens/login.json";
import loginZh from "../../locales/zh/screens/login.json";
import settingsEn from "../../locales/en/screens/settings.json";
import settingsZh from "../../locales/zh/screens/settings.json";
import { buildAppUpdateInfoRows, resolveAppUpdateSourceKey, type AppUpdateChannelKind } from "./app-update-info";

const appRoot = path.resolve(__dirname, "../../..");
const loginSource = readFileSync(path.join(appRoot, "app/(auth)/login.tsx"), "utf8");
const footerSource = readFileSync(path.join(appRoot, "src/modules/updates/AppBuildInfoFooter.tsx"), "utf8");

const CHANNEL_KINDS: AppUpdateChannelKind[] = ["production", "preview", "development", "custom", "none"];

test("登录页在隐私说明之后渲染版本条", () => {
  assert.match(loginSource, /import \{ AppBuildInfoFooter \} from "@\/modules\/updates\/AppBuildInfoFooter";/);
  const footerStart = loginSource.indexOf("<View style={styles.privacyFooter}>");
  const policyButton = loginSource.indexOf('t("privacy.openPolicy")', footerStart);
  const buildInfo = loginSource.indexOf("<AppBuildInfoFooter />", footerStart);
  assert.ok(footerStart >= 0 && policyButton > footerStart, "缺少隐私说明区");
  assert.ok(buildInfo > policyButton, "版本条应在隐私政策入口之后");
});

test("版本条读取版本信息有异常保护，详情复用设置页的行与文案", () => {
  assert.match(footerSource, /try \{\s*return getCurrentAppUpdateInfo\(\);\s*\} catch/);
  assert.match(footerSource, /buildAppUpdateInfoRows\(info\)/);
  assert.match(footerSource, /<BusinessSheet/);
  // 长渠道名与更新 ID 允许长按复制，便于发给管理员。
  assert.match(footerSource, /<Text selectable/);
});

test("五种渠道在中英文案里都有标签，详情行用到的设置页文案都存在", () => {
  for (const [lang, login] of [["zh", loginZh], ["en", loginEn]] as const) {
    const channel = (login as { buildInfo?: { channel?: Record<string, string> } }).buildInfo?.channel ?? {};
    for (const kind of CHANNEL_KINDS) {
      assert.ok(channel[kind]?.trim(), `${lang} 缺少渠道标签 buildInfo.channel.${kind}`);
    }
    const buildInfo = (login as { buildInfo?: Record<string, unknown> }).buildInfo ?? {};
    for (const key of ["version", "detailsTitle", "detailsSubtitle", "accessibilityLabel"]) {
      assert.equal(typeof buildInfo[key], "string", `${lang} 缺少 buildInfo.${key}`);
    }
  }

  const rows = buildAppUpdateInfoRows({
    appVersion: null,
    appBuildVersion: null,
    runtimeVersion: null,
    channel: null,
    updateId: null,
    isEmbeddedLaunch: false,
  });
  const usedKeys = new Set<string>([
    "updates.unknown",
    resolveAppUpdateSourceKey({ updateId: "x", isEmbeddedLaunch: false }),
    resolveAppUpdateSourceKey({ updateId: null, isEmbeddedLaunch: true }),
    resolveAppUpdateSourceKey({ updateId: null, isEmbeddedLaunch: false }),
  ]);
  for (const row of rows) {
    usedKeys.add(row.labelKey);
    if (row.valueKey) usedKeys.add(row.valueKey);
  }
  for (const [lang, settings] of [["zh", settingsZh], ["en", settingsEn]] as const) {
    for (const key of usedKeys) {
      const value = key.split(".").reduce<unknown>(
        (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
        settings,
      );
      assert.equal(typeof value, "string", `${lang} settings 缺少 ${key}`);
    }
  }
});
