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

const diagnosticsSource = readFileSync(
  path.resolve(
    testDirectory,
    "../../../android/app/src/main/java/com/hbweb/expo/HbPrinterLinkDiagnostics.kt"
  ),
  "utf8"
);

/** 取从某个声明开始到下一个同级 `\n  }\n` 的函数体，用于检查单个函数的结构。 */
function functionBody(source, signature) {
  const start = source.indexOf(signature);
  assert.ok(start >= 0, `缺少 ${signature}`);
  const end = source.indexOf("\n  }\n", start);
  assert.ok(end > start, `${signature} 没有结束`);
  return source.slice(start, end);
}

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
  assert.match(source, /BlePrinterConnection\.open\(\s*appContext,\s*device,\s*BLE_CONNECT_TIMEOUT_MS,/);
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

test("链路诊断：BLE 打开必须用具名参数，尾随 lambda 会绑到排在后面的 onDiag 上", () => {
  const source = connectSource();
  assert.match(source, /onConnectionLost\s*=\s*\{\s*lost\s*->/);
  assert.match(source, /onDiag\s*=\s*\{\s*type,\s*fields\s*->/);
  assert.doesNotMatch(source, /BLE_CONNECT_TIMEOUT_MS\)\s*\{\s*lost\s*->/, "不能退回尾随 lambda 写法");
  assert.match(
    connectionsSource,
    /onConnectionLost: \(BlePrinterConnection\) -> Unit,\s*onDiag: \(String, Map<String, Any\?>\) -> Unit = \{ _, _ -> \},/,
    "onDiag 必须带默认值，其它调用方不受影响"
  );
});

test("链路诊断：连接结果必须先记录再通知 JS，开始的现场必须早于断开旧会话", () => {
  const source = connectSource();
  assert.ok(source.indexOf("diagConnectBegin(") >= 0);
  assert.ok(
    source.indexOf("diagConnectBegin(") < source.indexOf("beginConnectionAttempt()"),
    "开始前的现场（旧连接是否还没清理）必须在断开旧会话之前记录"
  );
  assert.ok(
    source.indexOf('"connect.ok"') >= 0 && source.indexOf('"connect.ok"') < source.indexOf("promise.resolve(true)"),
    "JS 在 resolve 之后立刻取走缓冲，先通知再记录会漏掉这次结果"
  );
  assert.ok(
    source.indexOf("diagConnectFailure(") >= 0 &&
      source.indexOf("diagConnectFailure(") < source.indexOf('promise.reject("CONNECT_ERROR"'),
    "失败同理必须先记录再 reject"
  );
  // 提前拒绝（蓝牙关闭 / 未配对）也要留痕：配对丢失在日志里靠这个区分。
  assert.equal((source.match(/"connect\.rejected"/g) ?? []).length, 2);
});

test("链路诊断：ACL / 配对 / 蓝牙开关广播与 BLE GATT 状态码都已接入，且只记录目标地址", () => {
  assert.match(moduleSource, /diagAclEvent\("acl\.disconnected", device\)/);
  assert.match(moduleSource, /diagAclEvent\("acl\.connected", device\)/);
  assert.match(moduleSource, /"bond\.changed"/);
  assert.match(moduleSource, /"adapter\.state"/);
  assert.match(moduleSource, /"connection\.cleared"/);
  assert.match(connectionsSource, /onDiag\("ble\.gatt\.state"/);
  assert.match(connectionsSource, /onDiag\("ble\.mtu"/);
  assert.match(connectionsSource, /onDiag\("ble\.services"/);
  // 周边别的蓝牙设备的广播不能刷屏：只有最近要连接的地址或当前已连接的地址才记录。
  assert.match(moduleSource, /private fun isDiagTarget\(address: String\?\): Boolean =\s*address != null && \(address == diagAddress \|\| address == connectedAddress\)/);
  assert.match(functionBody(moduleSource, "private fun diagAclEvent"), /isDiagTarget\(address\)/);
  // 供 JS 能力探测与取走缓冲。
  assert.match(moduleSource, /@ReactMethod\s+fun drainLinkDiagnostics\(promise: Promise\)/);
  assert.match(diagnosticsSource, /const val MAX_EVENTS = 120/);
});

test("链路诊断：纯被动，不发起任何额外的蓝牙操作", () => {
  // 主动 SDP 探测会在失败设备上额外发起寻呼，可能干扰要观察的连接，所以明确不做。
  for (const forbidden of ["fetchUuidsWithSdp", "startDiscovery", "createBond", "connectGatt", "createRfcommSocket", "cancelDiscovery"]) {
    assert.doesNotMatch(diagnosticsSource, new RegExp(forbidden), `诊断核心不能调用 ${forbidden}`);
  }
  assert.doesNotMatch(moduleSource, /fetchUuidsWithSdp/);
  // 系统是否仍有 ACL 链路只能走隐藏 API 的只读反射，失败返回 null（未知），不能当成已断开。
  assert.match(diagnosticsSource, /getMethod\("isConnected"\)/);
  assert.match(diagnosticsSource, /\} catch \(_: Throwable\) \{\s*null\s*\}/);
});

test("链路诊断：所有记录入口都吞异常，drainLinkDiagnostics 永不 reject", () => {
  assert.match(functionBody(diagnosticsSource, "fun record(type: String, fields: Map<String, Any?>)"), /catch \(_: Throwable\)/);
  for (const signature of ["private fun diagConnectBegin", "private fun diagConnectFailure", "private fun diagAclEvent"]) {
    assert.match(functionBody(moduleSource, signature), /catch \(_: Throwable\)/, `${signature} 必须吞异常`);
  }
  const drain = functionBody(moduleSource, "fun drainLinkDiagnostics(promise: Promise)");
  assert.doesNotMatch(drain, /promise\.reject/, "取不到就返回空数组，诊断不能让 JS 报错");
  assert.match(drain, /Arguments\.createArray\(\)/);
});
