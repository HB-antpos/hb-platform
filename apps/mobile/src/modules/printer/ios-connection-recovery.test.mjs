import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(testDirectory, "../../../../..");
const productionSource = process.env.HB_IOS_PRINTER_SOURCE
  ? resolve(process.env.HB_IOS_PRINTER_SOURCE)
  : join(repositoryRoot, "apps/mobile/plugins/ios/HbPrinterModule.swift");
const fixturePath = join(testDirectory, "fixtures/ios-connection-recovery.swift");

function extractFunction(source, signature, { optional = false } = {}) {
  const start = source.indexOf(signature);
  if (start < 0) {
    if (optional) return "";
    throw new Error(`Production Swift method was not found: ${signature.trim()}`);
  }

  const bodyStart = source.indexOf("{", start);
  assert.notEqual(bodyStart, -1, `Method has no body: ${signature.trim()}`);
  let depth = 0;
  for (let index = bodyStart; index < source.length; index += 1) {
    if (source[index] === "{") depth += 1;
    if (source[index] === "}") {
      depth -= 1;
      if (depth === 0) return source.slice(start, index + 1);
    }
  }
  throw new Error(`Production Swift method has an unterminated body: ${signature.trim()}`);
}

function buildHarness(source) {
  const signatures = [
    ["  func connect("],
    ["  func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?)"],
    ["  func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral)"],
    ["  func configureWrite("],
    ["  private func writePrinterCommand("],
    // 新包把写入主体拆到 writePrinterData（writeBytes 共用）；旧源码没有时跳过。
    ["  private func writePrinterData(", { optional: true }],
    ["  private func planWriteChunks("],
    ["  private func printTimeoutSeconds(forByteCount byteCount: Int)"],
    ["  private func ensureBluetoothReady(_ reject: @escaping RCTPromiseRejectBlock)"],
    ["  private func failPendingConnect("],
    ["  private func flushPendingWrites()"],
    ["  private func finishPendingPrint()"],
    ["  private func failPendingWrite(", { optional: true }],
    ["  private func failPendingPrint("],
    ["  private func disconnectInternal("],
    ["  private func emitStatusChanged()"],
    ["  private func encodeCommand("],
    ["  private func resolve("],
    ["  private func reject("],
  ];
  const methods = signatures
    .map(([signature, options]) => extractFunction(source, signature, options))
    .filter(Boolean)
    .join("\n\n");
  const fixture = readFileSync(fixturePath, "utf8");
  assert.ok(fixture.includes("// __PRODUCTION_METHODS__"), "Swift fixture is missing its production-method marker");
  return fixture.replace("// __PRODUCTION_METHODS__", methods);
}

const swiftVersion = spawnSync("swiftc", ["--version"], { encoding: "utf8" });
const swiftUnavailable = swiftVersion.error?.code === "ENOENT";
const unsupportedPlatform = process.platform !== "darwin";
const skipReason = swiftUnavailable
  ? "swiftc is not installed"
  : unsupportedPlatform
    ? "native Swift harness requires Darwin Foundation/CoreFoundation bridging"
    : false;

test("继承 RCTEventEmitter 的生产 Swift 模块必须显式 import React", () => {
  // 回归测试 harness 自带 RCTEventEmitter 桩类，覆盖不到真实工程；RN 0.81 预编译核心下
  // 只靠桥接头会在 EAS 构建时报 cannot find type 'RCTEventEmitter' in scope（1.0.6 build 49）。
  const source = readFileSync(productionSource, "utf8");
  if (/class\s+HbPrinterModule\s*:\s*RCTEventEmitter\b/.test(source)) {
    assert.match(source, /^import React$/m, "HbPrinterModule 继承 RCTEventEmitter 时必须 import React");
  }
});

test("iOS 原生写失败会退役旧会话并隔离迟到 ACK", { skip: skipReason }, () => {
  if (swiftVersion.status !== 0) {
    assert.fail(`swiftc --version failed:\n${swiftVersion.stderr || swiftVersion.error}`);
  }

  const source = readFileSync(productionSource, "utf8");
  const harness = buildHarness(source);
  const temporaryDirectory = mkdtempSync(join(tmpdir(), "hb-ios-printer-recovery-"));
  const harnessPath = join(temporaryDirectory, "main.swift");
  const executablePath = join(temporaryDirectory, "ios-connection-recovery");
  writeFileSync(harnessPath, harness);

  try {
    const compile = spawnSync("swiftc", [harnessPath, "-o", executablePath], {
      encoding: "utf8",
      maxBuffer: 10 * 1024 * 1024,
    });
    assert.equal(
      compile.status,
      0,
      `Swift harness failed to compile against ${productionSource}:\n${compile.stdout}\n${compile.stderr}`
    );

    const run = spawnSync(executablePath, [], {
      encoding: "utf8",
      maxBuffer: 10 * 1024 * 1024,
    });
    assert.equal(
      run.status,
      0,
      `Swift regression harness failed against ${productionSource}:\n${run.stdout}\n${run.stderr}`
    );
    assert.match(run.stdout, /PASS timeout retires old session and stale ACK cannot advance retry/);
    assert.match(run.stdout, /PASS didWrite error retires old session/);
    assert.match(run.stdout, /PASS readiness without active print keeps healthy session/);
    assert.match(run.stdout, /PASS encoding failure keeps healthy session/);
    assert.match(run.stdout, /PASS fast mode streams without response and waits at barriers/);
    assert.match(run.stdout, /PASS fast mode honors local send buffer/);
    assert.match(run.stdout, /PASS reliable mode keeps legacy with-response writes/);
    assert.match(run.stdout, /PASS without-response-only characteristic still streams/);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});
