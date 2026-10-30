import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createInstance } from "i18next";

const root = resolve(import.meta.dirname, "../../..");
const sources = [
  "app/(shell)/device-management.tsx",
  "src/components/device-management/DeviceCards.tsx",
  "src/components/device-management/DeviceFilterSheets.tsx",
  "src/components/device-management/VersionDistribution.tsx",
  "src/components/device-management/primitives.tsx",
  "src/modules/device-management/version-state.ts",
  "src/modules/device-management/admin-sheets.tsx",
].map((file) => readFileSync(resolve(root, file), "utf8"));

// 字面量键：t("a.b") / t("deviceManagement:a.b") / labelKey: "versionState.x" / hintKey: "versionState.x"。
const literalKeys = new Set<string>();
for (const source of sources) {
  for (const match of source.matchAll(/\bt\(\s*"([A-Za-z]+(?::[A-Za-z]+)?(?:\.[A-Za-z]+)*)"/g)) literalKeys.add(match[1]);
  for (const match of source.matchAll(/(?:labelKey|hintKey):\s*"([A-Za-z.]+)"/g)) literalKeys.add(`deviceManagement:${match[1]}`);
}
assert.ok(literalKeys.size > 60, `expected many literal keys, got ${literalKeys.size}`);

// 动态键家族（t(`...${x}`) 在源码里读不出来，逐个列出实际会取到的值）。
const dynamicKeys = [
  ...["active", "disabled", "locked", "pendingConfirmation", "unregistered"].map((key) => `statuses.${key}`),
  ...["all", "Android", "iOS", "Windows", "Mac"].map((key) => `filters.systemOptions.${key}`),
  ...["all", "Mobile", "PDA", "POS", "Admin"].map((key) => `filters.typeOptions.${key}`),
  ...["all", "pendingConfirmation", "active", "disabled", "locked", "unregistered"].map((key) => `filters.${key}`),
  ...["all", "online", "offline"].map((key) => `appUsage.onlineFilters.${key}`),
  ...["activate", "disable", "lock"].flatMap((key) => [`actions.${key}`, `messages.${key}Success`, `messages.${key}Failed`]),
  ...["justNow", "minutes", "hours", "days"].map((key) => `time.${key}`),
  ...["Available", "Consumed", "Expired", "Revoked"].map((key) => `activation.statuses.${key}`),
  ...["Initial", "Rebind"].map((key) => `activation.consumptionKinds.${key}`),
  ...["all", "latest", "behind", "ahead", "notTargeted", "unreported", "appAll", "appLatest", "appBehind"].map((key) => `versionFilter.${key}`),
].map((key) => `deviceManagement:${key}`);

// 版本徽标由 version-state.ts 返回键，这里把它能返回的键全部取出来再核对。
import { getAppVersionBadge, getRegisteredVersionBadge } from "../../modules/device-management/version-state";
const badgeKeys = new Set<string>();
for (const versionState of ["latest", "behind", "ahead", "notTargeted", "unreported"] as const) {
  const badge = getRegisteredVersionBadge({ versionState, targetAppVersion: "1.0.96" });
  if (badge) [badge.labelKey, badge.hintKey].forEach((key) => key && badgeKeys.add(`deviceManagement:${key}`));
}
for (const versionState of ["latest", "behind", "unreported"] as const) {
  const badge = getAppVersionBadge({ versionState, latestAppVersion: "1.0.11", latestAppBuildVersion: "64" });
  if (badge) [badge.labelKey, badge.hintKey].forEach((key) => key && badgeKeys.add(`deviceManagement:${key}`));
}

async function main() {
  const translator = createInstance();
  const namespaces = ["deviceManagement", "common"] as const;
  for (const language of ["zh", "en"] as const) {
    const resources: Record<string, Record<string, unknown>> = {};
    for (const namespace of namespaces) {
      const file = namespace === "common" ? `src/locales/${language}/common.json` : `src/locales/${language}/screens/deviceManagement.json`;
      resources[namespace] = JSON.parse(readFileSync(resolve(root, file), "utf8"));
    }
    await translator.init({ lng: language, fallbackLng: false, defaultNS: "deviceManagement", ns: [...namespaces], resources: { [language]: resources } });
    const all = new Set([...literalKeys, ...dynamicKeys, ...badgeKeys]);
    for (const key of all) {
      // count 触发复数解析：只写了 _one/_other 的键也要能找到。
      assert.ok(translator.exists(key, { count: 2 }), `${language}: missing text for ${key}`);
    }
    // 页面标题是最显眼的文案，单独钉死，避免被批量改写误伤。
    assert.equal(translator.t("title"), language === "zh" ? "设备管理" : "Device management");
    assert.ok(translator.t("distribution.title", { count: 1 }).includes("1"), `${language}: distribution title must interpolate count`);
  }
  console.log("device-management-i18n ok");
}

void main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
