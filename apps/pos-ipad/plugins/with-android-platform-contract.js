const {
  AndroidConfig,
  createRunOncePlugin,
  withAndroidManifest,
  withAndroidStyles,
} = require("@expo/config-plugins");

const POST_NOTIFICATIONS_PERMISSION =
  "android.permission.POST_NOTIFICATIONS";
const CAMERA_FEATURE = "android.hardware.camera";
const SPLASH_SCREEN_BEHAVIOR = "android:windowSplashScreenBehavior";

function replaceManifestEntry(entries, name, attributes) {
  const retained = (entries ?? []).filter(
    (entry) => entry?.$?.["android:name"] !== name,
  );
  retained.push({
    $: {
      "android:name": name,
      ...attributes,
    },
  });
  return retained;
}

/**
 * 固化 Android 30+ 的生成契约，避免 Expo prebuild 产物触发 lint 或误报硬件必需。
 */
function withAndroidPlatformContract(config) {
  config = withAndroidManifest(config, (nextConfig) => {
    const manifest = nextConfig.modResults.manifest;
    manifest["uses-permission"] = replaceManifestEntry(
      manifest["uses-permission"],
      POST_NOTIFICATIONS_PERMISSION,
      {},
    );
    manifest["uses-feature"] = replaceManifestEntry(
      manifest["uses-feature"],
      CAMERA_FEATURE,
      { "android:required": "false" },
    );
    return nextConfig;
  });

  config = withAndroidStyles(config, (nextConfig) => {
    nextConfig.modResults = AndroidConfig.Styles.assignStylesValue(
      nextConfig.modResults,
      {
        parent: { name: "Theme.App.SplashScreen" },
        name: SPLASH_SCREEN_BEHAVIOR,
        value: "icon_preferred",
        targetApi: "33",
        add: true,
      },
    );
    return nextConfig;
  });

  return config;
}

module.exports = createRunOncePlugin(
  withAndroidPlatformContract,
  "with-android-platform-contract",
  "0.1.0",
);
