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
// JS 排版用的通用文字渲染器（measureTexts / renderTexts），与 Android HbPrinterTextRenderer.kt 同口径。
const TEXT_RENDERER_SWIFT = fs.readFileSync(path.join(__dirname, "ios", "HbPrinterTextRenderer.swift"), "utf8");
// 标签打印内置字体：与 Android（build.gradle 的 assets.srcDirs）共用 plugins/printer-assets/printer-fonts 同一份文件。
const PRINTER_FONT_DIR = path.join(__dirname, "printer-assets", "printer-fonts");
const PRINTER_FONT_FILES = fs
  .readdirSync(PRINTER_FONT_DIR)
  .filter((name) => name.startsWith("HbPrinter-") && name.endsWith(".ttf"))
  .sort();

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

RCT_EXTERN_METHOD(configureWrite:(NSDictionary *)options
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(print:(NSString *)command
                  encoding:(NSString *)encoding
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(writeBytes:(NSString *)base64
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(getLabelRenderInfo:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(measureTexts:(NSArray *)items
                  resolver:(RCTPromiseResolveBlock)resolve
                  rejecter:(RCTPromiseRejectBlock)reject)

RCT_EXTERN_METHOD(renderTexts:(NSArray *)items
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

function addResourceFile(project, projectName, fileName) {
  const relativePath = `${projectName}/${fileName}`;
  if (project.hasFile(relativePath)) {
    return;
  }

  // 字体作为资源打进 App 包（Copy Bundle Resources），Swift 用 Bundle.main.url(forResource:) 读取。
  IOSConfig.XcodeUtils.addResourceFileToGroup({
    project,
    groupName: projectName,
    filepath: relativePath,
    isBuildFile: true,
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
      fs.writeFileSync(path.join(appSourceRoot, "HbPrinterTextRenderer.swift"), TEXT_RENDERER_SWIFT);
      for (const fontFile of PRINTER_FONT_FILES) {
        fs.copyFileSync(path.join(PRINTER_FONT_DIR, fontFile), path.join(appSourceRoot, fontFile));
      }
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
    addSourceFile(config.modResults, projectName, "HbPrinterTextRenderer.swift");
    for (const fontFile of PRINTER_FONT_FILES) {
      addResourceFile(config.modResults, projectName, fontFile);
    }
    return config;
  });
}

module.exports = createRunOncePlugin(withIosPrinterModule, "with-ios-printer-module", "1.0.0");
