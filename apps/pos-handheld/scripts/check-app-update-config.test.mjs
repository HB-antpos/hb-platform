import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import test from "node:test";

const expoCli = createRequire(import.meta.url).resolve("expo/bin/cli");

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

test("resolved Expo config 与客户端共享冻结的 iOS bundle identity", async () => {
  const identity = JSON.parse(
    await readFile(
      new URL(
        "../src/core/contracts/pos-handheld-ios-identity.json",
        import.meta.url,
      ),
      "utf8",
    ),
  );
  const config = resolveConfig({ EAS_BUILD_PROFILE: "development" });

  assert.deepEqual(Object.keys(identity), ["bundleIdentifier"]);
  assert.equal(identity.bundleIdentifier, "com.hbweb.poshandheld");
  assert.equal(config.ios?.bundleIdentifier, identity.bundleIdentifier);
});

test("resolved Expo config 永久关闭启动自动检查，development/test 禁止 OTA 自动策略检查", () => {
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "development-simulator",
  });
  assert.equal(config.updates?.checkAutomatically, "NEVER");
  assert.deepEqual(config.updates?.requestHeaders, {
    "expo-channel-name": "pos-handheld-production",
  });
  assert.equal(config.extra?.hbpos?.automaticOtaChecks, false);
  assert.equal(config.extra?.hbpos?.buildProfile, "development-simulator");
  assert.deepEqual(config.extra?.hbpos?.trustedApkOrigins, [
    "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
  ]);
  assert.equal(config.extra?.eas, undefined);
});

test("APK trusted origins 只来自签名构建配置，覆盖 COS effective origin 且严格拒绝非 origin", () => {
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "development",
    EXPO_PUBLIC_HBPOS_TRUSTED_API_ORIGINS: "https://api-only.example.test",
    EXPO_PUBLIC_HBPOS_TRUSTED_APK_ORIGINS:
      "https://cos.example.test,https://cdn.example.test",
  });
  assert.deepEqual(config.extra?.hbpos?.trustedApkOrigins, [
    "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
    "https://cos.example.test",
    "https://cdn.example.test",
  ]);
  assert.equal(
    config.extra?.hbpos?.trustedApkOrigins.includes(
      "https://api-only.example.test",
    ),
    false,
  );

  for (const invalid of [
    "http://cos.example.test",
    "https://user:secret@cos.example.test",
    "https://cos.example.test/path",
  ]) {
    assert.notEqual(
      runConfig({
        EAS_BUILD_PROFILE: "development",
        EXPO_PUBLIC_HBPOS_TRUSTED_APK_ORIGINS: invalid,
      }).status,
      0,
    );
  }
});

test("production EAS build 使用 Handheld 专用 channel，不复用 mobile production", async () => {
  const eas = JSON.parse(
    await readFile(new URL("../eas.json", import.meta.url), "utf8"),
  );
  assert.equal(eas.cli.version, ">= 21.3.0");
  assert.equal(eas.cli.appVersionSource, "remote");
  assert.equal(eas.cli.requireCommit, true);
  assert.equal(eas.build.production.channel, "pos-handheld-production");
  assert.notEqual(eas.build.production.channel, "production");
  assert.equal(eas.build.production.environment, "production");
  assert.equal(eas.build.production.android.buildType, "apk");
  assert.equal(eas.submit.production.ios.ascAppId, "6802182045");
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

test("android-internal 尊重显式 production profile，并启用生产 OTA fail-closed", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "android-internal",
    EXPO_PUBLIC_HBPOS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
  });
  assert.equal(config.extra?.hbpos?.buildProfile, "production");
  assert.equal(config.extra?.hbpos?.automaticOtaChecks, true);
});

test("preview 即使 EAS 配置完整也不启用 production OTA 策略门禁", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "preview",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
  });

  assert.equal(config.extra?.hbpos?.automaticOtaChecks, false);
  assert.equal(config.updates?.checkAutomatically, "NEVER");
});

test("resolved Expo config 全局禁用 Android 应用备份", () => {
  const config = resolveConfig({ EAS_BUILD_PROFILE: "development" });

  assert.equal(config.android?.allowBackup, false);
});

test("本地 HTTP API 只进入 development；preview/production/android-internal 全部要求 HTTPS", () => {
  const development = resolveConfig({ EAS_BUILD_PROFILE: "development" });
  assert.equal(
    development.extra?.hbpos?.trustedApiOrigins.includes(
      "http://192.168.31.246:5003",
    ),
    true,
  );
  assert.equal(
    development.extra?.hbpos?.trustedApiOrigins.includes(
      "http://192.168.31.246:5159",
    ),
    true,
  );

  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  for (const profile of ["preview", "production", "android-internal"]) {
    const config = resolveConfig({
      EAS_BUILD_PROFILE: profile,
      EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
      EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
    });
    assert.equal(
      config.extra?.hbpos?.trustedApiOrigins.includes(
        "http://192.168.31.246:5159",
      ),
      false,
    );
    assert.equal(
      config.extra?.hbpos?.trustedApiOrigins.every((origin) =>
        origin.startsWith("https://"),
      ),
      true,
    );
    assert.notEqual(
      runConfig({
        EAS_BUILD_PROFILE: profile,
        EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
        EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
        EXPO_PUBLIC_HBPOS_TRUSTED_API_ORIGINS:
          "http://192.168.31.247:5159",
      }).status,
      0,
    );
  }
});

test("env example 声明签名构建 APK origin 白名单", async () => {
  const example = await readFile(
    new URL("../.env.example", import.meta.url),
    "utf8",
  );
  assert.match(example, /^EXPO_PUBLIC_HBPOS_TRUSTED_APK_ORIGINS=https:\/\//mu);
});

test("production 只接受显式 HTTPS updates URL 与 EAS projectId 注入", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const updatesUrl = `https://u.expo.dev/${projectId}`;
  const config = resolveConfig({
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: updatesUrl,
    EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: "0.1.0",
  });
  assert.equal(config.updates?.url, updatesUrl);
  assert.equal(config.updates?.checkAutomatically, "NEVER");
  assert.equal(config.runtimeVersion, "0.1.0");
  assert.equal(config.extra?.eas?.projectId, projectId);
  assert.equal(config.extra?.hbpos?.automaticOtaChecks, true);
});

test("显式 runtimeVersion 必须与当前 appVersion 一致", () => {
  const projectId = "123e4567-e89b-42d3-a456-426614174000";
  const result = runConfig({
    EAS_BUILD_PROFILE: "production",
    EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID: projectId,
    EXPO_PUBLIC_HBPOS_UPDATES_URL: `https://u.expo.dev/${projectId}`,
    EXPO_PUBLIC_HBPOS_RUNTIME_VERSION: "0.2.0",
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

function resolveConfig(environment) {
  const result = runConfig(environment);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout);
}

function runConfig(environment) {
  const cleanEnvironment = { ...process.env };
  // 配置测试必须只使用用例显式注入的变量，避免开发机 .env.local 掩盖 fail-fast 场景。
  cleanEnvironment.EXPO_NO_DOTENV = "1";
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_SENTRY_DSN;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_UPDATES_URL;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_BUILD_PROFILE;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_RUNTIME_VERSION;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_API_URL;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_TRUSTED_API_ORIGINS;
  delete cleanEnvironment.EXPO_PUBLIC_HBPOS_TRUSTED_APK_ORIGINS;
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
