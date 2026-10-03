import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const mobileRoot = path.resolve(__dirname, "../../..");

function read(relativePath: string) {
  return readFileSync(path.join(mobileRoot, relativePath), "utf8");
}

test("设置首页采用设备、偏好、支持三组高密度信息架构", () => {
  const source = read("app/(shell)/settings.tsx");

  assert.match(source, /HB_COLORS/);
  assert.match(source, /HB_SPACING/);
  assert.match(source, /HB_RADIUS/);
  assert.match(source, /t\("groups\.devices"\)/);
  assert.match(source, /t\("groups\.preferences"\)/);
  assert.match(source, /t\("groups\.support"\)/);
  assert.match(source, /<StatusPill/);
  assert.match(source, /appPackageVersion/);
  assert.match(source, /deviceStoreDisplayName/);
  assert.match(source, /savedPrinter/);
  assert.match(source, /savedReceiptPrinter/);
  assert.doesNotMatch(source, /TC26-FV-01|Zebra ZQ320/);
});

test("诊断、设备与双打印机详情入口可操作且共享原生打印忙碌锁", () => {
  const source = read("app/(shell)/settings.tsx");
  const settingsStart = source.indexOf("export default function Settings");
  const renderStart = source.indexOf("  return (", settingsStart);
  const renderSource = source.slice(renderStart);

  assert.match(source, /testID="settings-diagnostics"/);
  assert.match(source, /setDiagnosticsVisible\(true\)/);
  assert.match(source, /visible=\{diagnosticsVisible\}/);
  assert.match(source, /testID="settings-device-details"/);
  assert.match(source, /visible=\{deviceSettingsVisible\}/);
  assert.match(source, /testID="settings-printer-details"/);
  assert.match(source, /visible=\{printerSettingsVisible\}/);
  assert.match(source, /printerNativeBusy\s*=\s*printerBusy\s*\|\|\s*receiptPrinterBusy/);
  assert.match(source, /onPress=\{handleScanPrinters\}[\s\S]{0,240}disabled=\{printerNativeBusy \|\| !hasSelectedTransport\}/);
  assert.match(source, /onPress=\{handleConnectSavedPrinter\}[\s\S]{0,260}disabled=\{printerNativeBusy\}/);
  assert.match(source, /onPress=\{handleScanReceiptPrinters\}[\s\S]{0,220}disabled=\{printerNativeBusy\}/);
  assert.match(source, /onPress=\{handleTestReceiptPrinter\}[\s\S]{0,260}disabled=\{printerNativeBusy \|\| !savedReceiptPrinter\}/);

  for (const handler of [
    "handleCheckUpdates",
    "openApiHostSettings",
    "openDeviceActivation",
    "handleRefreshDevice",
    "handleDeviceUnbind",
    "handleScanPrinters",
    "handleConnectPrinter",
    "handleTestPrinter",
    "handleClearPrinter",
    "handleScanReceiptPrinters",
    "handleSaveReceiptPrinter",
    "handleConnectReceiptPrinter",
    "handleTestReceiptPrinter",
    "handleClearReceiptPrinter",
    "handleLogout",
  ]) {
    assert.match(renderSource, new RegExp(handler), `${handler} 必须仍有可点击入口`);
  }
  assert.match(source, /const handleConnectReceiptPrinter\s*=\s*\(device: PrinterDevice\)/);
  assert.match(source, /onSelect=\{handleConnectReceiptPrinter\}/);
  assert.match(source, /handleConnectReceiptPrinter[\s\S]{0,600}showSystemPairingRequiredAlert\(t, device\)/);
});

test("详情弹窗使用原生可访问模态并管理进入与返回焦点", () => {
  const source = read("app/(shell)/settings.tsx");

  assert.match(source, /Modal as NativeModal/);
  assert.match(source, /<NativeModal/);
  assert.match(source, /accessibilityViewIsModal/);
  assert.match(source, /onRequestClose=\{onDismiss\}/);
  assert.match(source, /AccessibilityInfo\.setAccessibilityFocus\(headingHandle\)/);
  assert.match(source, /AccessibilityInfo\.setAccessibilityFocus\(triggerHandle\)/);
  assert.match(source, /accessibilityLabel=\{dismissLabel\}/);
  assert.doesNotMatch(source, /\n\s+Modal,\n|\n\s+Portal,\n/);
  assert.match(source, /<Pressable\s+ref=\{actionRef\}[\s\S]{0,260}accessibilityRole="button"/);
  assert.match(source, /styles\.compactRowPressed/);
});

test("中英文设置文案同时提供新分组和诊断入口", () => {
  const zh = JSON.parse(read("src/locales/zh/screens/settings.json"));
  const en = JSON.parse(read("src/locales/en/screens/settings.json"));

  for (const locale of [zh, en]) {
    assert.equal(typeof locale.groups.devices, "string");
    assert.equal(typeof locale.groups.preferences, "string");
    assert.equal(typeof locale.groups.support, "string");
    assert.equal(typeof locale.overview.diagnostics, "string");
    assert.equal(typeof locale.overview.aboutDiagnostics, "string");
    assert.equal(typeof locale.diagnostics.title, "string");
  }
});

test("打印机列表明确区分配对状态并复用已配对优先排序", () => {
  const settings = read("app/(shell)/settings.tsx");
  const setupSheet = read("src/components/printer/LabelPrinterSetupSheet.tsx");
  const details = read("src/components/printer/PrinterDeviceDetails.tsx");
  const zh = JSON.parse(read("src/locales/zh/screens/settings.json"));
  const en = JSON.parse(read("src/locales/en/screens/settings.json"));

  assert.match(settings, /orderPrinterDevices/);
  assert.match(settings, /<PrinterDeviceDetails device=\{printer\}/);
  assert.match(setupSheet, /filterPrinterDevices/);
  assert.match(setupSheet, /<PrinterDeviceDetails device=\{device\}/);
  // BLE 显示“可直接连接”，经典蓝牙按配对状态显示，未配对时提示去系统设置配对并高亮。
  assert.match(details, /transport === "ble"\s*\?\s*t\("printer\.bleDirectConnect"\)/);
  assert.match(details, /device\.bonded \? t\("printer\.bonded"\) : needsSystemPairing \? t\("printer\.systemPairingRequired"\)/);
  assert.match(details, /needsSystemPairing\s*&&\s*styles\.unbonded/);

  assert.equal(zh.printer.bonded, "已配对");
  assert.equal(zh.printer.unbonded, "未配对");
  assert.equal(en.printer.bonded, "Paired");
  assert.equal(en.printer.unbonded, "Not paired");
  assert.equal(zh.printer.bleDirectConnect, "无需配对，可直接连接");
  assert.match(zh.printer.systemPairingRequired, /系统蓝牙设置/);
  assert.match(en.printer.systemPairingRequired, /system Bluetooth settings/);
});

test("未配对经典蓝牙引导去系统设置配对，BLE 直接连接", () => {
  const settings = read("app/(shell)/settings.tsx");
  const setupSheet = read("src/components/printer/LabelPrinterSetupSheet.tsx");
  const alert = read("src/components/printer/system-pairing-alert.ts");
  const zh = JSON.parse(read("src/locales/zh/screens/settings.json"));
  const en = JSON.parse(read("src/locales/en/screens/settings.json"));

  for (const source of [settings, setupSheet]) {
    assert.match(source, /if \(requiresSystemPairing\(device, Platform\.OS\)\) \{\s*showSystemPairingRequiredAlert\(t, device\);\s*return;/);
    assert.doesNotMatch(source, /dialogs\.printerPairingAction/, "App 内不再代为发起配对");
  }
  assert.match(alert, /dialogs\.printerPairingTitle/);
  assert.match(alert, /dialogs\.printerPairingMessage/);
  assert.match(alert, /dialogs\.printerPairingAction[\s\S]{0,80}openBluetoothSettings\(\)/);

  assert.equal(zh.dialogs.printerPairingTitle, "请先在系统中配对");
  assert.match(zh.dialogs.printerPairingMessage, /系统蓝牙设置[\s\S]*BLE[\s\S]*直接连接/);
  assert.equal(zh.dialogs.printerPairingAction, "打开蓝牙设置");
  assert.equal(en.dialogs.printerPairingTitle, "Pair in system settings first");
  assert.match(en.dialogs.printerPairingMessage, /system Bluetooth settings[\s\S]*BLE[\s\S]*connect directly/i);
  assert.equal(en.dialogs.printerPairingAction, "Open Bluetooth settings");
});

test("安卓标签打印机按蓝牙类型筛选，仅旧原生包阻止选择 BLE 设备", () => {
  const settings = read("app/(shell)/settings.tsx");
  const setupSheet = read("src/components/printer/LabelPrinterSetupSheet.tsx");
  const filters = read("src/components/printer/PrinterTransportFilterControls.tsx");
  const details = read("src/components/printer/PrinterDeviceDetails.tsx");

  for (const source of [settings, setupSheet]) {
    assert.match(source, /DEFAULT_PRINTER_TRANSPORT_FILTERS/);
    assert.match(source, /filterPrinterDevices\(/);
    assert.match(source, /isUnsupportedPrinterTransport\(device, Platform\.OS, isBlePrintingSupported\(\)\)/);
    assert.match(source, /<PrinterTransportFilterControls/);
    assert.match(source, /<PrinterDeviceDetails/);
    assert.match(source, /printer\.emptyTransportFiltered/);
  }

  assert.match(settings, /useEffect\(\(\) => \{[\s\S]{0,180}if \(printerSettingsVisible\)[\s\S]{0,180}setTransportFilters\(\{ \.\.\.DEFAULT_PRINTER_TRANSPORT_FILTERS \}\);[\s\S]{0,80}\}, \[printerSettingsVisible\]\)/);
  assert.match(setupSheet, /useEffect\(\(\) => \{[\s\S]{0,120}if \(visible\)[\s\S]{0,180}setTransportFilters\(\{ \.\.\.DEFAULT_PRINTER_TRANSPORT_FILTERS \}\);[\s\S]{0,80}\}, \[visible\]\)/);
  assert.match(filters, /Platform\.OS !== "android"/);
  assert.match(filters, /printer\.showClassic/);
  assert.match(filters, /printer\.showBle/);
  assert.match(filters, /printer\.selectTransport/);
  assert.match(details, /getPrinterDeviceIcon/);
  assert.match(details, /printer\.bleUnsupported/);
  for (const source of [settings, setupSheet]) {
    assert.match(source, /scanCompleted && hasSelectedTransport|printerScanCompleted && hasSelectedTransport/);
    assert.match(source, /Platform\.OS === "android"[\s\S]{0,180}printer\.emptyTransportFiltered[\s\S]{0,180}printer\.emptyFiltered/);
  }
});

test("绑定设备会话可在设置中管理离线商品数据", () => {
  const source = read("app/(shell)/settings.tsx");
  const panel = read("src/components/product-maintenance/OfflineCatalogManagementPanel.tsx");

  // 入口与面板都必须挂在离线资格门禁之后，普通账号登录看不到任何离线 UI。
  assert.match(source, /isOfflineProductQueryEligible\(\{/);
  assert.match(source, /\{offlineEligible \? \([\s\S]*?testID="settings-offline-data"/);
  assert.match(source, /\{offlineEligible \? \([\s\S]*?testID="settings-offline-data-details"/);
  assert.match(source, /<OfflineCatalogManagementPanel/);

  // 离线数据仅由员工手动下载/更新，面板保留取消与切换分店入口。
  assert.match(panel, /refreshCatalog\(/);
  assert.match(panel, /cancelRefresh\(/);
  assert.match(panel, /await selectStore\(store\)/);
  assert.doesNotMatch(panel, /setAutoRefreshEnabled\(|<Switch/);
  // 分店列表必须内联渲染：设置页详情是原生 Modal，Paper Portal 弹出的选择器会被盖住。
  // 只断言真实引用（import 与 JSX），否则解释这一点的注释本身会让断言失败。
  assert.doesNotMatch(panel, /^\s*import[\s\S]*?StorePickerModal/m, "面板不得 import StorePickerModal");
  assert.doesNotMatch(panel, /<StorePickerModal/, "面板不得渲染 StorePickerModal");

  const zh = JSON.parse(read("src/locales/zh/screens/settings.json"));
  const en = JSON.parse(read("src/locales/en/screens/settings.json"));
  for (const locale of [zh, en]) {
    for (const key of ["title", "switchStore", "updateNow", "cancelUpdate", "manualRefreshHint", "summaryReady"]) {
      assert.equal(typeof locale.offlineData[key], "string", `offlineData.${key} 缺失`);
    }
  }
});

test("设置页手动检查复用受控 OTA 唯一发布通道", () => {
  const settings = read("app/(shell)/settings.tsx");
  const layout = read("app/_layout.tsx");
  const boundary = read("src/modules/updates/MobileOtaUpdateBoundary.tsx");
  const hook = read("src/modules/updates/use-mobile-ota-update.ts");

  assert.match(settings, /useMobileOtaManualCheck/);
  assert.match(settings, /await checkMobileOtaUpdate\(\)/);
  assert.doesNotMatch(settings, /checkAndDownloadAppUpdate/);
  assert.match(layout, /onManualCheck=\{mobileOtaUpdate\.checkManually\}/);
  assert.match(boundary, /MobileOtaManualCheckContext\.Provider/);
  assert.match(hook, /optionalPromptTargetRef\.current = null;[\s\S]{0,500}await runCheckRef\.current\(\)/);
});
