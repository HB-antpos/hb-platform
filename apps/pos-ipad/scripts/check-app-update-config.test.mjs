import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import test from "node:test";

const expoCli = createRequire(import.meta.url).resolve("expo/bin/cli");

test("resolved Expo config 永久关闭启动自动检查，development/test 禁止 OTA 自动策略检查", () => {
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "development-simulator",
  });
  assert.equal(config.updates?.checkAutomatically, "NEVER");
  assert.deepEqual(config.updates?.requestHeaders, {
    "expo-channel-name": "pos-ipad-production",
  });
  assert.equal(config.extra?.hbpos?.automaticOtaChecks, false);
  assert.equal(config.extra?.hbpos?.buildProfile, "development-simulator");
  assert.equal(config.extra?.eas, undefined);
});

test("production EAS build 使用 iPad 专用 channel，不复用 mobile production", async () => {
  const eas = JSON.parse(
    await readFile(new URL("../eas.json", import.meta.url), "utf8"),
  );
  assert.equal(eas.cli.version, "21.3.0");
  assert.equal(eas.build.production.channel, "pos-ipad-production");
  assert.notEqual(eas.build.production.channel, "production");
});

test("production 缺 EAS projectId/updates.url 时 fail-fast，不允许静默构建无效 OTA", () => {
  const result = runConfig({ EAS_BUILD_PROFILE: "production" });
  assert.notEqual(result.status, 0);
});

test("EAS production profile 不得被公开环境变量降级绕过 fail-fast", () => {
  const result = runConfig({
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_BUILD_PROFILE: "development",
  });
  assert.notEqual(result.status, 0);
});

test("production 只接受显式 HTTPS updates URL 与 EAS projectId 注入", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const updatesUrl = `https://u.expo.dev/${projectId}`;
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: updatesUrl,
    EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: "0.2.0",
  });
  assert.equal(config.updates?.url, updatesUrl);
  assert.equal(config.updates?.checkAutomatically, "NEVER");
  assert.equal(config.runtimeVersion, "0.2.0");
  assert.equal(config.extra?.eas?.projectId, projectId);
  assert.equal(config.extra?.hbpos?.automaticOtaChecks, true);
});

test("显式 runtimeVersion 必须与当前 appVersion 一致", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const result = runConfig({
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
    EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: "0.1.0",
  });

  assert.notEqual(result.status, 0);
});

test("production updates URL 必须精确绑定同一 EAS project，拒绝任意 HTTPS 主机或其他 projectId", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const otherProjectId = "223e4567-e89b-42d3-a456-426614174000";
  assert.notEqual(
    runConfig({
      EAS_BUILD_PROFILE: "production",
      EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
      EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://updates.example/${projectId}`,
    }).status,
    0,
  );
  assert.notEqual(
    runConfig({
      EAS_BUILD_PROFILE: "production",
      EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
      EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${otherProjectId}`,
    }).status,
    0,
  );
});

test("publish 注入的 runtimeVersion 仅接受当前 appVersion，非法值 fail-fast", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const commonEnvironment = {
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
  };
  assert.notEqual(
    runConfig({
      ...commonEnvironment,
      EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: `r/${"a".repeat(118)}`,
    }).status,
    0,
  );
  assert.notEqual(
    runConfig({
      ...commonEnvironment,
      EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: `r/${"a".repeat(119)}`,
    }).status,
    0,
  );
  assert.notEqual(
    runConfig({
      ...commonEnvironment,
      EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: "1.2.3\npoison",
    }).status,
    0,
  );
});

test("仅配置 Sentry DSN 时注入原生上传插件", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const environment = {
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
    SENTRY_ORG: " example-org ",
    SENTRY_PROJECT: " example-project ",
    SENTRY_URL: " https://sentry.example.test/ ",
  };
  for (const dsn of [undefined, "  ", " https://public@sentry.example.test/1 "]) {
    const config = resolveConfig({
      ...environment,
      ...(dsn === undefined ? {} : { EXPO_PUBLIC_HBPOS_SENTRY_DSN: dsn }),
    });
    const plugin = config.plugins.find(
      (entry) => Array.isArray(entry) && entry[0] === "@sentry/react-native/expo",
    );
    assert.deepEqual(plugin, dsn?.trim() ? [
      "@sentry/react-native/expo",
      {
        organization: "example-org",
        project: "example-project",
        url: "https://sentry.example.test/",
      },
    ] : undefined);
  }
});

function resolveConfig(environment) {
  const result = runConfig(environment);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout);
}

function runConfig(environment) {
  const cleanEnvironment = { ...process.env };
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_SENTRY_DSN;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_UPDATES_URL;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_BUILD_PROFILE;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_RUNTIME_VERSION;
  return spawnSync(
    process.execPath,
    [
      expoCli,
      "config",
      "--type",
      "public",
      "--json",
    ],
    {
      cwd: new URL("..", import.meta.url),
      env: { ...cleanEnvironment, ...environment },
      encoding: "utf8",
    },
  );
}
