import type { ConfigContext, ExpoConfig } from "expo/config";

import posHandheldIosIdentity from "./src/core/contracts/pos-handheld-ios-identity.json";

const supportedInterfaceOrientations = [
  "UIInterfaceOrientationPortrait",
];
// Expo 配置与客户端共同读取纯 JSON 身份合同，避免构建身份与更新校验漂移。
const defaultHbposApiBaseUrl = "https://hotbargain.vip/pos-api";
const localHbposApiBaseUrl = "http://192.168.31.246:5003";
const legacyLocalHbposApiBaseUrl = "http://192.168.31.246:5159";
const defaultTrustedApkOrigin =
  "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com";
const posHandheldProductionChannel = "pos-handheld-production";
const posHandheldAppVersion = "0.1.0";

function buildOtaUpdateConfiguration(): Readonly<{
  buildProfile: string;
  automaticOtaChecks: boolean;
  updates: NonNullable<ExpoConfig["updates"]>;
  easProjectId: string | null;
  runtimeVersion: NonNullable<ExpoConfig["runtimeVersion"]>;
  requireHttpsApiOrigins: boolean;
}> {
  const easBuildProfile = process.env.EAS_BUILD_PROFILE?.trim() || null;
  const explicitBuildProfile =
    process.env.EXPO_PUBLIC_HBPOS_BUILD_PROFILE?.trim() || null;
  const buildProfile =
    explicitBuildProfile ||
    easBuildProfile ||
    "development";
  const securityProfiles = [buildProfile, easBuildProfile].filter(
    (value): value is string => value !== null,
  );
  const production = securityProfiles.some(
    (value) => value === "production" || value === "android-internal",
  );
  const preview =
    !production && securityProfiles.some((value) => value === "preview");
  const development =
    !production &&
    !preview &&
    securityProfiles.every((value) => value.startsWith("development"));
  const easProjectId =
    process.env.EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID?.trim() || null;
  const updatesUrl =
    process.env.EXPO_PUBLIC_HBPOS_UPDATES_URL?.trim() || null;
  const explicitRuntimeVersion =
    process.env.EXPO_PUBLIC_HBPOS_RUNTIME_VERSION?.trim() || null;
  if (
    production &&
    (!easProjectId || !updatesUrl)
  ) {
    throw new Error(
      "Production HB POS requires EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID and EXPO_PUBLIC_HBPOS_UPDATES_URL.",
    );
  }
  if ((easProjectId === null) !== (updatesUrl === null)) {
    throw new Error(
      "HB POS EAS projectId and updates URL must be configured together.",
    );
  }
  if (
    easProjectId &&
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
      easProjectId,
    )
  ) {
    throw new Error("HB POS EAS projectId must be a UUID.");
  }
  if (updatesUrl) {
    const parsed = new URL(updatesUrl);
    if (
      parsed.protocol !== "https:" ||
      parsed.hostname !== "u.expo.dev" ||
      parsed.port ||
      parsed.pathname !== `/${easProjectId}` ||
      parsed.search ||
      parsed.hash ||
      parsed.username ||
      parsed.password
    ) {
      throw new Error(
        "HB POS updates URL must exactly match the configured EAS project.",
      );
    }
  }
  if (
    explicitRuntimeVersion &&
    (
      explicitRuntimeVersion.length > 120 ||
      !/^[A-Za-z0-9][A-Za-z0-9._/-]*$/u.test(explicitRuntimeVersion)
    )
  ) {
    throw new Error("HB POS runtimeVersion is invalid.");
  }
  if (
    explicitRuntimeVersion &&
    explicitRuntimeVersion !== posHandheldAppVersion
  ) {
    throw new Error(
      `HB POS runtimeVersion 必须与当前 appVersion ${posHandheldAppVersion} 一致。`,
    );
  }
  const configured = easProjectId !== null && updatesUrl !== null;
  const updates: NonNullable<ExpoConfig["updates"]> = {
    // 所有 EAS 检查都由门店策略状态机显式触发，启动时绝不绕过后台策略。
    checkAutomatically: "NEVER",
    requestHeaders: {
      "expo-channel-name": posHandheldProductionChannel,
    },
    ...(updatesUrl ? { url: updatesUrl } : {}),
  };
  return Object.freeze({
    buildProfile,
    // 仅 production channel 具备后台 OTA 策略门禁，preview 由独立 channel 正常解析更新。
    automaticOtaChecks:
      configured &&
      production,
    updates,
    easProjectId,
    // OTA 发布脚本显式注入目标 runtime；普通原生构建仍按 App 版本生成。
    runtimeVersion:
      explicitRuntimeVersion ?? ({ policy: "appVersion" } as const),
    requireHttpsApiOrigins: !development,
  });
}

function buildHbposApiConfiguration(
  requireHttpsApiOrigins: boolean,
): Readonly<{
  apiBaseUrl: string;
  trustedApiOrigins: readonly string[];
  trustedApkOrigins: readonly string[];
}> {
  const apiBaseUrl =
    process.env.EXPO_PUBLIC_HBPOS_API_URL?.trim() ||
    defaultHbposApiBaseUrl;
  const candidates = [
    apiBaseUrl,
    ...(requireHttpsApiOrigins
      ? []
      : [localHbposApiBaseUrl, legacyLocalHbposApiBaseUrl]),
    ...(process.env.EXPO_PUBLIC_HBPOS_TRUSTED_API_ORIGINS ?? "")
      .split(",")
      .map((value: string) => value.trim())
      .filter(Boolean),
  ];
  const trustedApkOrigins = [
    defaultTrustedApkOrigin,
    ...(process.env.EXPO_PUBLIC_HBPOS_TRUSTED_APK_ORIGINS ?? "")
      .split(",")
      .map((value: string) => value.trim())
      .filter(Boolean),
  ].map(requiredHttpsOrigin);
  return Object.freeze({
    apiBaseUrl,
    trustedApiOrigins: Object.freeze([
      ...new Set(
        candidates.map((value) =>
          requiredApiOrigin(value, requireHttpsApiOrigins),
        ),
      ),
    ]),
    trustedApkOrigins: Object.freeze([...new Set(trustedApkOrigins)]),
  });
}

function requiredApiOrigin(value: string, requireHttps: boolean): string {
  const parsed = new URL(value);
  if (
    (requireHttps
      ? parsed.protocol !== "https:"
      : parsed.protocol !== "https:" && parsed.protocol !== "http:") ||
    parsed.username ||
    parsed.password
  ) {
    throw new Error(
      "HB POS API origins must use HTTPS outside development builds.",
    );
  }
  return parsed.origin;
}

function requiredHttpsOrigin(value: string): string {
  const parsed = new URL(value);
  if (
    parsed.protocol !== "https:" ||
    parsed.username ||
    parsed.password ||
    parsed.port ||
    parsed.pathname !== "/" ||
    parsed.search ||
    parsed.hash
  ) {
    throw new Error("HB POS trusted APK origin must be an exact HTTPS origin.");
  }
  return parsed.origin;
}

function buildLogCenterConfiguration(
  buildProfile: string,
): Readonly<{
  enabled: boolean;
  ingestUrl: string;
  writeKey: string;
  environment: string;
}> {
  return Object.freeze({
    enabled:
      process.env.EXPO_PUBLIC_HBPOS_LOG_CENTER_ENABLED?.trim().toLowerCase() ===
      "true",
    ingestUrl:
      process.env.EXPO_PUBLIC_HBPOS_LOG_CENTER_INGEST_URL?.trim() ?? "",
    // 这是受限、可撤销的仅写凭据；绝不把服务端 hash 或管理凭据放入安装包。
    writeKey:
      process.env.EXPO_PUBLIC_HBPOS_LOG_CENTER_WRITE_KEY?.trim() ?? "",
    environment:
      process.env.EXPO_PUBLIC_HBPOS_LOG_CENTER_ENVIRONMENT?.trim() ??
      buildProfile,
  });
}

export default ({ config }: ConfigContext): ExpoConfig => {
  const ota = buildOtaUpdateConfiguration();
  const hbpos = buildHbposApiConfiguration(
    ota.requireHttpsApiOrigins,
  );
  const logCenter = buildLogCenterConfiguration(ota.buildProfile);
  // 与客户端启用条件一致：未配置 DSN 时不注入原生源码地图上传任务。
  const sentryPlugins: NonNullable<ExpoConfig["plugins"]> =
    process.env.EXPO_PUBLIC_HBPOS_SENTRY_DSN?.trim()
      ? [[
          "@sentry/react-native/expo",
          {
            ...(process.env.SENTRY_ORG?.trim()
              ? { organization: process.env.SENTRY_ORG.trim() }
              : {}),
            project: process.env.SENTRY_PROJECT?.trim() || "hb-pos-handheld",
            url: process.env.SENTRY_URL?.trim() || "https://sentry.io/",
          },
        ]]
      : [];
  return ({
  ...config,
  name: "HB POS Mobile",
  slug: "hb-pos-handheld",
  owner: "pangaoqi",
  version: posHandheldAppVersion,
  icon: "./assets/icon.png",
  scheme: "hbpos-handheld",
  platforms: ["ios", "android"],
  orientation: "portrait",
  userInterfaceStyle: "light",
  newArchEnabled: true,
  runtimeVersion: ota.runtimeVersion,
  updates: ota.updates,
  ios: {
    appleTeamId: "3SV4A23SVW",
    bundleIdentifier: posHandheldIosIdentity.bundleIdentifier,
    buildNumber: "1",
    supportsTablet: false,
    requireFullScreen: true,
    infoPlist: {
      UIRequiresFullScreen: true,
      UISupportedInterfaceOrientations: supportedInterfaceOrientations,
      NSBluetoothAlwaysUsageDescription: "HB POS 使用蓝牙连接门店小票打印机。",
      NSCameraUsageDescription: "HB POS 使用相机作为条码扫描备用方式。",
      NSLocalNetworkUsageDescription: "HB POS 需要访问受支持的门店支付终端。",
    },
  },
  android: {
    package: "com.hbweb.poshandheld",
    versionCode: 1,
    // POS 不允许系统或云端恢复任何应用数据，避免凭据与业务状态跨设备复制。
    allowBackup: false,
    adaptiveIcon: {
      foregroundImage: "./assets/icon.png",
      backgroundColor: "#F4F1EA",
    },
    blockedPermissions: [
      "android.permission.RECORD_AUDIO",
      "android.permission.ACCESS_BACKGROUND_LOCATION",
    ],
  },
  plugins: [
    "expo-router",
    "expo-localization",
    ...sentryPlugins,
    [
      "expo-audio",
      {
        // POS 仅播放短提示音，绝不申请麦克风或 Android 录音权限。
        microphonePermission: false,
        recordAudioAndroid: false,
      },
    ],
    [
      "expo-camera",
      {
        cameraPermission: "允许 HB POS 使用相机扫描商品条码。",
        // 相机仅用于扫码，禁用所有麦克风与录音权限。
        microphonePermission: false,
        recordAudioAndroid: false,
      },
    ],
    [
      "expo-secure-store",
      {
        faceIDPermission: "允许 HB POS 使用 Face ID 解锁安全凭据。",
      },
    ],
    [
      "expo-sqlite",
      {
        useSQLCipher: true,
        enableFTS: true,
      },
    ],
    [
      "expo-build-properties",
      {
        ios: {
          buildReactNativeFromSource: true,
          deploymentTarget: "17.0",
        },
        android: {
          // 门店仍有 Android 10 手持机；原生模块对 Android 12/13 新 API 均有版本分支。
          minSdkVersion: 29,
        },
      },
    ],
    "./plugins/with-rn-fmt-xcode26",
    [
      "./plugins/with-hb-printer",
      {
        backgroundBle: false,
      },
    ],
    "./plugins/with-android-platform-contract",
  ],
  experiments: {
    typedRoutes: true,
  },
  extra: {
    hbpos: {
      apiBaseUrl: hbpos.apiBaseUrl,
      // 只允许签名构建中声明的 origin；持久化设置不能扩张凭据发送边界。
      trustedApiOrigins: hbpos.trustedApiOrigins,
      // 仅签名构建可声明 APK/COS origin；运行时持久化设置不能扩张此集合。
      trustedApkOrigins: hbpos.trustedApkOrigins,
      businessTimeZone:
        process.env.EXPO_PUBLIC_HBPOS_BUSINESS_TIME_ZONE?.trim() ??
        "Australia/Brisbane",
      buildProfile: ota.buildProfile,
      automaticOtaChecks: ota.automaticOtaChecks,
      logCenter,
    },
    ...(ota.easProjectId
      ? { eas: { projectId: ota.easProjectId } }
      : {}),
    payments: {
      // 这里只允许公开的终端选择；provider token/secret 始终保留在 Hbpos.Api。
      provider:
        process.env.EXPO_PUBLIC_HBPOS_CARD_PROVIDER?.trim().toLowerCase() ??
        "",
      square: {
        environment:
          process.env.EXPO_PUBLIC_HBPOS_SQUARE_ENVIRONMENT?.trim() ?? "",
        deviceId:
          process.env.EXPO_PUBLIC_HBPOS_SQUARE_DEVICE_ID?.trim() ?? "",
        locationId:
          process.env.EXPO_PUBLIC_HBPOS_SQUARE_LOCATION_ID?.trim() ?? "",
      },
      linkly: {
        environment:
          process.env.EXPO_PUBLIC_HBPOS_LINKLY_ENVIRONMENT?.trim() ?? "",
      },
      voucher: {
        enabled:
          process.env.EXPO_PUBLIC_HBPOS_VOUCHER_ENABLED?.trim()
            .toLowerCase() === "true",
      },
    },
  },
  });
};
