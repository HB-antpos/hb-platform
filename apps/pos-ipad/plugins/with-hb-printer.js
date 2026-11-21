const { createRunOncePlugin, withInfoPlist } = require("@expo/config-plugins");

// 与 app.config.ts 及 react-native-ble-plx 插件的蓝牙用途说明保持同一句（打印机、钱箱与 BLE 扫码枪共用）。
const BLUETOOTH_USAGE = "HB POS 使用蓝牙连接门店小票打印机、钱箱和蓝牙扫码枪。";

/**
 * 本地 Expo Module 已由 autolinking 编译进 iOS；插件只声明系统权限。
 * 后台 BLE 默认关闭，避免收银前台应用在没有明确产品需求时常驻扫描。
 */
function withHbPrinter(config, { backgroundBle = false } = {}) {
  return withInfoPlist(config, (nextConfig) => {
    nextConfig.modResults.NSBluetoothAlwaysUsageDescription = BLUETOOTH_USAGE;
    if (backgroundBle) {
      const modes = new Set(nextConfig.modResults.UIBackgroundModes ?? []);
      modes.add("bluetooth-central");
      nextConfig.modResults.UIBackgroundModes = [...modes];
    }
    return nextConfig;
  });
}

module.exports = createRunOncePlugin(withHbPrinter, "with-hb-printer", "0.1.0");
