const {
  createRunOncePlugin,
  withAndroidManifest,
  withInfoPlist,
} = require("@expo/config-plugins");

// 与 app.config.ts 及 react-native-ble-plx 插件的蓝牙用途说明保持同一句（打印机、钱箱与 BLE 扫码枪共用）。
const BLUETOOTH_USAGE = "HB POS 使用蓝牙连接门店小票打印机、钱箱和蓝牙扫码枪。";
// 与手持端同一套安卓蓝牙权限矩阵：Android 11 及以下走旧蓝牙 + 定位，Android 12+ 走 SCAN/CONNECT。
const ANDROID_PERMISSION_MATRIX = [
  {
    name: "android.permission.BLUETOOTH",
    attributes: { "android:maxSdkVersion": "30" },
  },
  {
    name: "android.permission.BLUETOOTH_ADMIN",
    attributes: { "android:maxSdkVersion": "30" },
  },
  {
    name: "android.permission.ACCESS_FINE_LOCATION",
    attributes: { "android:maxSdkVersion": "30" },
  },
  {
    name: "android.permission.BLUETOOTH_SCAN",
    attributes: { "android:usesPermissionFlags": "neverForLocation" },
  },
  {
    name: "android.permission.BLUETOOTH_CONNECT",
    attributes: {},
  },
];

/**
 * 本地 Expo Module 由 autolinking 编译进 iOS 与 Android；插件只声明系统权限。
 * 后台 BLE 默认关闭，避免收银前台应用在没有明确产品需求时常驻扫描；
 * Android 不声明广播、后台定位或前台服务。
 */
function withHbPrinter(config, { backgroundBle = false } = {}) {
  config = withInfoPlist(config, (nextConfig) => {
    nextConfig.modResults.NSBluetoothAlwaysUsageDescription = BLUETOOTH_USAGE;
    if (backgroundBle) {
      const modes = new Set(nextConfig.modResults.UIBackgroundModes ?? []);
      modes.add("bluetooth-central");
      nextConfig.modResults.UIBackgroundModes = [...modes];
    }
    return nextConfig;
  });

  config = withAndroidManifest(config, (nextConfig) => {
    const manifest = nextConfig.modResults.manifest;
    const permissions = manifest["uses-permission"] ?? [];
    for (const permission of ANDROID_PERMISSION_MATRIX) {
      // 先去掉同名声明再写入，保证最终清单里每个蓝牙权限只有这一份属性。
      const retained = permissions.filter(
        (entry) => entry?.$?.["android:name"] !== permission.name,
      );
      retained.push({
        $: {
          "android:name": permission.name,
          ...permission.attributes,
        },
      });
      permissions.splice(0, permissions.length, ...retained);
    }
    manifest["uses-permission"] = permissions;
    return nextConfig;
  });

  return config;
}

module.exports = createRunOncePlugin(withHbPrinter, "with-hb-printer", "0.2.0");
