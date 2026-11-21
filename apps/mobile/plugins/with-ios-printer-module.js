const fs = require("fs");
const path = require("path");
const {
  IOSConfig,
  createRunOncePlugin,
  withDangerousMod,
  withInfoPlist,
  withXcodeProject,
} = require("@expo/config-plugins");

// Swift 模块较大，单独存模板，避免 JS 反引号转义导致生成文件和源码漂移。
const MODULE_SWIFT = fs.readFileSync(path.join(__dirname, "ios", "HbPrinterModule.swift"), "utf8");

const MODULE_EXPORTS = `#import <React/RCTBridgeModule.h>
#import <React/RCTEventEmitter.h>

@interface RCT_EXTERN_MODULE(HbPrinterModule, RCTEventEmitter)

RCT_EXTERN_METHOD(getStatus:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(scanPrinters:(nonnull NSNumber *)durationMs
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(connect:(NSString *)address
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(disconnect:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(print:(NSString *)command
                  encoding:(NSString *)encoding
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(printProductLabel:(NSDictionary *)payload
                  printType:(NSString *)printType
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(printDiscountLabel:(NSDictionary *)payload
                  printType:(NSString *)printType
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(printClearanceLabel:(NSDictionary *)payload
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(printBigDiscountLabel:(NSDictionary *)payload
                  printType:(NSString *)printType
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(printWarehouseProductLabel:(NSDictionary *)payload
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(printWarehouseLocationLabel:(NSDictionary *)payload
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

+ (BOOL)requiresMainQueueSetup
{
  return NO;
}

@end
`;

// 与 app.json 的 NSBluetoothAlwaysUsageDescription、react-native-ble-plx 插件的 bluetoothAlwaysPermission 保持一致：
// 两个插件都会写这个键，文案相同才不会因执行顺序不同而互相覆盖成旧文案。
const BLUETOOTH_USAGE = "Used to scan and connect Bluetooth receipt printers, label printers and barcode scanners";
const BRIDGE_IMPORTS = [
  "#import <React/RCTBridgeModule.h>",
  "#import <React/RCTEventEmitter.h>",
];

function addSourceFile(project, projectName, fileName) {
  const relativePath = `${projectName}/${fileName}`;
  if (project.hasFile(relativePath)) {
    return;
  }

  IOSConfig.XcodeUtils.addBuildSourceFileToGroup({
    project,
    groupName: projectName,
    filepath: relativePath,
  });
}

function getIosProjectName(config) {
  // EAS clean prebuild 的 AppDelegate 尚未生成，需用 Expo 配置里的 name 兜底。
  return IOSConfig.XcodeUtils.getHackyProjectName(
    config.modRequest.platformProjectRoot,
    config
  );
}

function withIosPrinterModule(config) {
  config = withInfoPlist(config, (config) => {
    config.modResults.NSBluetoothAlwaysUsageDescription = BLUETOOTH_USAGE;
    config.modResults.NSBluetoothPeripheralUsageDescription = BLUETOOTH_USAGE;
    return config;
  });

  config = withDangerousMod(config, [
    "ios",
    async (config) => {
      const projectRoot = config.modRequest.platformProjectRoot;
      const projectName = getIosProjectName(config);
      const appSourceRoot = path.join(projectRoot, projectName);
      const bridgingHeaderPath = path.join(appSourceRoot, `${projectName}-Bridging-Header.h`);
      fs.mkdirSync(appSourceRoot, { recursive: true });

      // iOS 原生目录是生成物，插件在 prebuild/EAS 时重建 BLE 打印桥源码。
      fs.writeFileSync(path.join(appSourceRoot, "HbPrinterModule.swift"), MODULE_SWIFT);
      fs.writeFileSync(path.join(appSourceRoot, "HbPrinterModule.m"), MODULE_EXPORTS);
      const bridgingHeader = fs.existsSync(bridgingHeaderPath)
        ? fs.readFileSync(bridgingHeaderPath, "utf8")
        : "";
      const missingBridgeImports = BRIDGE_IMPORTS.filter((item) => !bridgingHeader.includes(item));
      if (missingBridgeImports.length > 0) {
        fs.writeFileSync(
          bridgingHeaderPath,
          `${bridgingHeader.trimEnd()}\n${missingBridgeImports.join("\n")}\n`
        );
      }
      return config;
    },
  ]);

  return withXcodeProject(config, (config) => {
    const projectName = getIosProjectName(config);
    addSourceFile(config.modResults, projectName, "HbPrinterModule.swift");
    addSourceFile(config.modResults, projectName, "HbPrinterModule.m");
    return config;
  });
}

module.exports = createRunOncePlugin(withIosPrinterModule, "with-ios-printer-module", "1.0.0");
