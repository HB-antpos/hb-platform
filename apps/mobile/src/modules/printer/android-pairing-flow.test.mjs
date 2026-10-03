import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import test from "node:test";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const moduleSource = readFileSync(
  path.resolve(
    testDirectory,
    "../../../android/app/src/main/java/com/hbweb/expo/HbPrinterModule.kt"
  ),
  "utf8"
);
const connectionsSource = readFileSync(
  path.resolve(
    testDirectory,
    "../../../android/app/src/main/java/com/hbweb/expo/HbPrinterConnections.kt"
  ),
  "utf8"
);

function connectSource() {
  const start = moduleSource.indexOf("fun connectWithTransport(address: String, transport: String?, promise: Promise)");
  const end = moduleSource.indexOf("fun disconnect(promise: Promise)", start);
  assert.ok(start >= 0 && end > start, "缺少 connectWithTransport");
  return moduleSource.slice(start, end);
}

test("Android 经典蓝牙未配对拒绝 RFCOMM 连接，BLE 不要求配对", () => {
  // 旧 JS 仍可能调用 pair；保留系统配对广播处理。
  assert.match(moduleSource, /fun pair\(address: String, promise: Promise\)/);
  assert.match(moduleSource, /BluetoothDevice\.ACTION_BOND_STATE_CHANGED/);
  assert.match(moduleSource, /device\.createBond\(\)/);
  assert.match(moduleSource, /PRINTER_PAIRING_TIMEOUT/);

  const source = connectSource();
  assert.match(source, /!useBle && device\.bondState != BluetoothDevice\.BOND_BONDED/);
  assert.match(source, /PRINTER_PAIRING_REQUIRED/);
  assert.ok(
    source.indexOf("PRINTER_PAIRING_REQUIRED") < source.indexOf("beginConnectionAttempt()"),
    "配对检查必须早于断开原会话"
  );
});

test("Android 扫描结果同时暴露真实蓝牙传输类型和系统设备类别", () => {
  assert.match(moduleSource, /BluetoothDevice\.DEVICE_TYPE_CLASSIC\s*->\s*"classic"/);
  assert.match(moduleSource, /BluetoothDevice\.DEVICE_TYPE_LE\s*->\s*"ble"/);
  assert.match(moduleSource, /BluetoothDevice\.DEVICE_TYPE_DUAL\s*->\s*"dual"/);
  assert.match(moduleSource, /else\s*->\s*"unknown"/);
  assert.match(moduleSource, /device\.bluetoothClass\?\.deviceClass/);
  assert.match(moduleSource, /map\.putString\("transport", printer\.transport\)/);
  assert.match(moduleSource, /map\.putInt\("deviceClass", printer\.deviceClass\)/);

  const scanStart = moduleSource.indexOf("fun scanPrinters(durationMs: Int, promise: Promise)");
  const connectStart = moduleSource.indexOf("fun connect(address: String, promise: Promise)", scanStart);
  assert.ok(scanStart >= 0 && connectStart > scanStart);
  const scanSource = moduleSource.slice(scanStart, connectStart);
  assert.equal((scanSource.match(/transport\s*=\s*bluetoothTransport\(device\)/g) ?? []).length, 2);
  assert.equal((scanSource.match(/deviceClass\s*=\s*device\.bluetoothClass\?\.deviceClass/g) ?? []).length, 2);
});

test("Android 按 JS 传入的类型选择 BLE GATT 或 RFCOMM，旧 connect 回退到系统缓存类型", () => {
  const legacyStart = moduleSource.indexOf("fun connect(address: String, promise: Promise)");
  const legacySource = moduleSource.slice(legacyStart, moduleSource.indexOf("fun connectWithTransport", legacyStart));
  assert.match(legacySource, /connectWithTransport\(address, null, promise\)/);

  const source = connectSource();
  assert.match(source, /"ble" -> true/);
  assert.match(source, /"classic", "dual" -> false/);
  assert.match(source, /else -> device\.type == BluetoothDevice\.DEVICE_TYPE_LE/);
  assert.match(source, /BlePrinterConnection\.open\(appContext, device, BLE_CONNECT_TIMEOUT_MS\)/);
  assert.match(source, /device\.createRfcommSocketToServiceRecord\(printerUuid\)/);
  assert.doesNotMatch(moduleSource, /PRINTER_BLE_UNSUPPORTED/, "新原生包不再拒绝 BLE");

  const pairStart = moduleSource.indexOf("fun pair(address: String, promise: Promise)");
  const pairSource = moduleSource.slice(pairStart, moduleSource.indexOf("fun addListener(eventName: String)", pairStart));
  assert.ok(
    pairSource.indexOf("DEVICE_TYPE_LE") < pairSource.indexOf("device.createBond()"),
    "BLE 地址必须在 createBond 前直接放行"
  );
});

test("Android BLE 通道：LE 传输建连、协商 MTU、优先有应答写入并逐包等待回调", () => {
  assert.match(connectionsSource, /connectGatt\(context, false, callback, BluetoothDevice\.TRANSPORT_LE\)/);
  assert.match(connectionsSource, /requestMtu\(TARGET_ATT_MTU\)/);
  assert.match(connectionsSource, /PROPERTY_WRITE != 0[\s\S]{0,120}WRITE_TYPE_DEFAULT[\s\S]{0,120}WRITE_TYPE_NO_RESPONSE/);
  assert.match(connectionsSource, /latch\.await\(WRITE_ACK_TIMEOUT_MS, TimeUnit\.MILLISECONDS\)/);
  assert.match(connectionsSource, /mtu - ATT_HEADER_BYTES/);
  // 写超时/断线的报错须命中 JS 的断线识别，触发清理旧会话且不重印。
  assert.match(connectionsSource, /Bluetooth printer connection lost/);
  for (const ignored of ["1800", "1801", "180A", "FE59"]) {
    assert.match(connectionsSource, new RegExp(`uuid16\\("${ignored}"\\)`));
  }
});
