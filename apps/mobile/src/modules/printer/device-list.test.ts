import assert from "node:assert/strict";
import test from "node:test";

import { DEFAULT_PRINTER_TRANSPORT_FILTERS, filterPrinterDevices, getDisplayPrinterTransport, getPrinterDeviceIcon, isUnsupportedPrinterTransport, orderPrinterDevices, requiresSystemPairing } from "./device-list";
import type { PrinterDevice } from "./types";

test("蓝牙扫描列表将已配对设备稳定排在未配对设备之前", () => {
  const devices: PrinterDevice[] = [
    { name: "XP-P326B-75A3", address: "D0:23:81:3F:75:A3", bonded: false, connected: false },
    { name: "XP-P326B-75A3", address: "10:23:81:3F:75:A3", bonded: true, connected: true },
    { name: "XP-365B", address: "30:00:00:00:00:01", bonded: true, connected: false },
    { name: "Receipt", address: "40:00:00:00:00:01", bonded: false, connected: false },
  ];

  const ordered = orderPrinterDevices(devices);

  assert.deepEqual(
    ordered.map((device) => device.address),
    ["10:23:81:3F:75:A3", "30:00:00:00:00:01", "D0:23:81:3F:75:A3", "40:00:00:00:00:01"]
  );
  assert.deepEqual(
    devices.map((device) => device.address),
    ["D0:23:81:3F:75:A3", "10:23:81:3F:75:A3", "30:00:00:00:00:01", "40:00:00:00:00:01"],
    "排序不得修改原始扫描结果"
  );
});

const transportDevices: PrinterDevice[] = [
  { name: "XP-P326B-75A3", address: "D0:23:81:3F:75:A3", bonded: true, connected: false, transport: "ble" },
  { name: "XP-P326B-75A3", address: "10:23:81:3F:75:A3", bonded: false, connected: false, transport: "classic", deviceClass: 1664 },
  { name: "XP dual", address: "dual", bonded: false, connected: false, transport: "dual" },
  { name: "XP unknown", address: "unknown", bonded: false, connected: false, transport: "unknown" },
  { name: "XP legacy", address: "legacy", bonded: false, connected: false },
  { name: "Receipt", address: "receipt", bonded: true, connected: false, transport: "classic" },
];

test("Android 默认同时列出经典蓝牙与 BLE，已配对优先", () => {
  assert.deepEqual(DEFAULT_PRINTER_TRANSPORT_FILTERS, { showClassic: true, showBle: true });
  const result = filterPrinterDevices(transportDevices, { ...DEFAULT_PRINTER_TRANSPORT_FILTERS, xpOnly: true, platform: "android" });
  assert.deepEqual(result.map((device) => device.address), ["D0:23:81:3F:75:A3", "10:23:81:3F:75:A3", "dual", "unknown", "legacy"]);
});

test("两开关四种组合、双模仅出现一次，筛选后保持已配对优先", () => {
  for (const [showClassic, showBle, expected] of [
    [true, false, ["receipt", "10:23:81:3F:75:A3", "dual", "unknown", "legacy"]],
    [false, true, ["D0:23:81:3F:75:A3", "dual"]],
    [true, true, ["D0:23:81:3F:75:A3", "receipt", "10:23:81:3F:75:A3", "dual", "unknown", "legacy"]],
    [false, false, []],
  ] as const) {
    const result = filterPrinterDevices(transportDevices, { showClassic, showBle, platform: "android" });
    assert.deepEqual(result.map((device) => device.address), expected);
  }
  assert.equal(transportDevices[0].address, "D0:23:81:3F:75:A3", "不得原地修改扫描结果");
});

test("iOS 不受 Android 类型开关影响，XP 筛选仍生效", () => {
  const result = filterPrinterDevices(transportDevices, { showClassic: false, showBle: false, xpOnly: true, platform: "ios" });
  assert.equal(result.length, 5);
  assert.equal(result[0].transport, "ble");
  assert.equal(isUnsupportedPrinterTransport(transportDevices[0], "ios", false), false);
});

test("XP 筛选沿用忽略首尾空格和大小写的现有行为", () => {
  const devices: PrinterDevice[] = [
    { name: " xp-label ", address: "xp", bonded: false, connected: false },
    { name: "other XP", address: "other", bonded: false, connected: false },
    { address: "unnamed", bonded: false, connected: false },
  ];
  for (const platform of ["android", "ios"]) {
    assert.deepEqual(filterPrinterDevices(devices, {
      ...DEFAULT_PRINTER_TRANSPORT_FILTERS, xpOnly: true, platform,
    }).map((device) => device.address), ["xp"]);
  }
});

test("只有旧安卓原生包拒绝 BLE-only，设备图标不能决定兼容性", () => {
  assert.equal(isUnsupportedPrinterTransport(transportDevices[0], "android", false), true);
  assert.equal(isUnsupportedPrinterTransport(transportDevices[0], "android", true), false, "新原生包走 BLE GATT");
  for (const device of transportDevices.slice(1)) assert.equal(isUnsupportedPrinterTransport(device, "android", false), false);
  assert.equal(getPrinterDeviceIcon(transportDevices[1]), "printer");
  assert.equal(getPrinterDeviceIcon({ ...transportDevices[0], deviceClass: 1664 }), "printer");
  assert.equal(isUnsupportedPrinterTransport({ ...transportDevices[0], deviceClass: 1664 }, "android", false), true);
  assert.equal(getPrinterDeviceIcon({ ...transportDevices[1], deviceClass: 256 }), "bluetooth");
  for (const deviceClass of [0x0600, 0x0610, 0x0620, 0x0640]) {
    assert.equal(getPrinterDeviceIcon({ ...transportDevices[1], deviceClass }), "bluetooth", "影像设备不一定是打印机");
  }
  for (const deviceClass of [0x0680, 0x06c0]) {
    assert.equal(getPrinterDeviceIcon({ ...transportDevices[1], deviceClass }), "printer");
  }
  assert.equal(getPrinterDeviceIcon(transportDevices[4]), "bluetooth");
});

test("经典/双模/未知类型未配对须先在系统中配对，BLE 与 iOS 直接连接", () => {
  const byAddress = (address: string) => transportDevices.find((device) => device.address === address)!;
  assert.equal(requiresSystemPairing(byAddress("D0:23:81:3F:75:A3"), "android"), false, "已配对 BLE");
  assert.equal(requiresSystemPairing({ ...byAddress("D0:23:81:3F:75:A3"), bonded: false }, "android"), false, "未配对 BLE 也直接连");
  assert.equal(requiresSystemPairing(byAddress("10:23:81:3F:75:A3"), "android"), true, "未配对经典蓝牙");
  assert.equal(requiresSystemPairing(byAddress("dual"), "android"), true, "双模走经典蓝牙");
  assert.equal(requiresSystemPairing(byAddress("unknown"), "android"), true);
  assert.equal(requiresSystemPairing(byAddress("legacy"), "android"), true, "旧原生包无类型字段按经典处理");
  assert.equal(requiresSystemPairing(byAddress("receipt"), "android"), false, "已配对经典蓝牙");
  assert.equal(requiresSystemPairing(byAddress("10:23:81:3F:75:A3"), "ios"), false);
});

test("iOS 列表统一显示为 BLE，安卓按系统报告的类型显示", () => {
  assert.equal(getDisplayPrinterTransport(transportDevices[4], "ios"), "ble");
  assert.equal(getDisplayPrinterTransport(transportDevices[1], "android"), "classic");
  assert.equal(getDisplayPrinterTransport(transportDevices[4], "android"), "unknown");
});
