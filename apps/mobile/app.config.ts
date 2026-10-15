import appJson from "./app.json";

const baseExpoConfig = appJson.expo;
const baseUpdatesConfig = baseExpoConfig.updates as typeof baseExpoConfig.updates & {
  checkAutomatically?: string;
};
const nativeRuntimeVersion =
  process.env.EXPO_PUBLIC_RUNTIME_VERSION?.trim() || baseExpoConfig.runtimeVersion || baseExpoConfig.version;
const nativeAppBuildProfile =
  process.env.EXPO_PUBLIC_APP_BUILD_PROFILE?.trim() || process.env.EAS_BUILD_PROFILE?.trim() || "production";
const nativeInstallerFlag = process.env.EXPO_PUBLIC_NATIVE_APK_INSTALLER_ENABLED?.trim().toLowerCase();
const nativeAppInstallerEnabled = nativeInstallerFlag !== "0" && nativeInstallerFlag !== "false";
const nativeAppInstallerTrustedOrigins = Array.from(new Set([
  "https://hotbargain.vip",
  "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com",
  ...(process.env.EXPO_PUBLIC_NATIVE_APK_TRUSTED_ORIGINS ?? "")
    .split(",")
    .map((value: string) => value.trim())
    .filter(Boolean),
]));
// 仅供 development 手工验证中央更新策略；production 固定使用代码中的可信地址。
const nativeIosUpdateCenterUrl = process.env.EXPO_PUBLIC_IOS_UPDATE_CENTER_URL?.trim() || "";
// Android 推送（FCM）凭据：EAS 的文件型环境变量 GOOGLE_SERVICES_JSON 会给出文件路径。
// 有值才写入 android.googleServicesFile；没有（本地开发、OTA 发布、没配置的构建）就不写，保持原行为，
// 这类构建里 Android 推送令牌取不到，JS 侧静默跳过。密钥文件本身绝不入库（见 .gitignore）。
const googleServicesFile = process.env.GOOGLE_SERVICES_JSON?.trim() || "";

export default {
  expo: {
    ...baseExpoConfig,
    runtimeVersion: nativeRuntimeVersion,
    android: {
      ...baseExpoConfig.android,
      ...(googleServicesFile ? { googleServicesFile } : {}),
      // 无构建 profile 时也使用 Android 独立运行时；显式 OTA 参数仍可选择目标运行时。
      runtimeVersion: process.env.EXPO_PUBLIC_RUNTIME_VERSION?.trim()
        || baseExpoConfig.android.runtimeVersion
        || nativeRuntimeVersion,
    },
    updates: {
      ...baseUpdatesConfig,
      // 原生层只负责承载更新，自动检查统一交给 JS，方便测试包按 profile 禁用。
      checkAutomatically: "NEVER",
    },
    extra: {
      ...baseExpoConfig.extra,
      // 固化当前安装包的 EAS profile，自更新时按同一轨道检查 APK。
      nativeAppBuildProfile,
      // 旧 runtime 的 OTA 包关闭原生安装器，只用 Linking 提醒下载，避免加载新 native module。
      nativeAppInstallerEnabled,
      // 原生下载器只跟随明确受信的 HTTPS API/COS origin；各环境可追加但不能覆盖生产基线。
      nativeAppInstallerTrustedOrigins,
      nativeIosUpdateCenterUrl,
      logCenter: {
        endpoint: process.env.EXPO_PUBLIC_LOG_CENTER_ENDPOINT?.trim() || "",
        // 客户端包不得携带长期凭据；无服务端签发的短期凭据时日志中心保持停用。
        environment:
          process.env.EXPO_PUBLIC_LOG_CENTER_ENVIRONMENT?.trim()
          || process.env.APP_ENV?.trim()
          || process.env.NODE_ENV?.trim()
          || "development",
        projectCode: process.env.EXPO_PUBLIC_LOG_CENTER_PROJECT?.trim() || "HbwebExpo",
        serviceName: process.env.EXPO_PUBLIC_LOG_CENTER_SERVICE?.trim() || "HbwebExpoApp",
        sourceType: "Mobile",
      },
    },
  },
};
