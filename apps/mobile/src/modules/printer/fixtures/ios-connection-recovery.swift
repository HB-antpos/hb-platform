import CoreFoundation
import Foundation

typealias RCTPromiseResolveBlock = (Any?) -> Void
typealias RCTPromiseRejectBlock = (String?, String?, Error?) -> Void

enum CBManagerState {
  case unknown
  case resetting
  case unsupported
  case unauthorized
  case poweredOff
  case poweredOn
}

enum CBPeripheralState {
  case disconnected
  case connected
}

enum CBCharacteristicWriteType {
  case withResponse
  case withoutResponse
}

struct CBCharacteristicProperties: OptionSet {
  let rawValue: Int

  static let write = CBCharacteristicProperties(rawValue: 1 << 0)
  static let writeWithoutResponse = CBCharacteristicProperties(rawValue: 1 << 1)
}

final class CBCharacteristic {
  let properties: CBCharacteristicProperties

  init(properties: CBCharacteristicProperties) {
    self.properties = properties
  }
}

final class CBPeripheral {
  let identifier: UUID
  weak var delegate: AnyObject?
  var state: CBPeripheralState = .connected
  var canSendWriteWithoutResponse = true
  var maximumWriteLength = 20
  private(set) var writes: [(Data, CBCharacteristic, CBCharacteristicWriteType)] = []

  init(identifier: UUID = UUID()) {
    self.identifier = identifier
  }

  func maximumWriteValueLength(for type: CBCharacteristicWriteType) -> Int {
    maximumWriteLength
  }

  func writeValue(_ data: Data, for characteristic: CBCharacteristic, type: CBCharacteristicWriteType) {
    writes.append((data, characteristic, type))
  }
}

final class CBCentralManager {
  var state: CBManagerState = .poweredOn
  var retrievedPeripherals: [UUID: CBPeripheral] = [:]
  private(set) var cancelledPeripherals: [CBPeripheral] = []
  private(set) var connectedPeripherals: [CBPeripheral] = []

  func retrievePeripherals(withIdentifiers identifiers: [UUID]) -> [CBPeripheral] {
    identifiers.compactMap { retrievedPeripherals[$0] }
  }

  func connect(_ peripheral: CBPeripheral, options: [String: Any]?) {
    connectedPeripherals.append(peripheral)
  }

  func cancelPeripheralConnection(_ peripheral: CBPeripheral) {
    // CoreBluetooth cancellation is asynchronous. Keep the peripheral connected so the
    // harness can deliver the stale ACK before the terminal disconnect callback.
    cancelledPeripherals.append(peripheral)
  }
}

final class HarnessModule {
  private let bluetoothQueue = DispatchQueue(label: "ios-connection-recovery.harness")
  private var centralManager = CBCentralManager()
  private var discoveredPeripherals: [String: CBPeripheral] = [:]
  private var connectedPeripheral: CBPeripheral?
  private var writeCharacteristic: CBCharacteristic?
  private var connectedAddress: String?
  private var connectResolve: RCTPromiseResolveBlock?
  private var connectReject: RCTPromiseRejectBlock?
  private var connectTimeoutWorkItem: DispatchWorkItem?
  private var connectionGeneration = 0
  private var activeConnectGeneration: Int?
  private var retiringPeripheralIDs: Set<ObjectIdentifier> = []
  private var pendingCharacteristicServiceCount = 0
  private var printResolve: RCTPromiseResolveBlock?
  private var printReject: RCTPromiseRejectBlock?
  private struct PendingWriteChunk {
    let data: Data
    let type: CBCharacteristicWriteType
  }

  private var pendingWriteChunks: [PendingWriteChunk] = []
  private var pendingWriteCharacteristic: CBCharacteristic?
  private var awaitingWriteResponse = false
  private var pendingWriteStartedAt: Date?
  private var pendingWritePerfSummary = ""
  private var printTimeoutWorkItem: DispatchWorkItem?
  private var hasStatusListeners = false
  private var writeMode = "fast"
  private var writeAckIntervalBytes = 2048
  private static let statusEvent = "HbPrinterStatusChanged"

  private func sendEvent(withName name: String, body: Any?) {}

// __PRODUCTION_METHODS__
}

struct HarnessFailure: Error, CustomStringConvertible {
  let description: String
}

func expect(_ condition: @autoclosure () -> Bool, _ message: String) throws {
  if !condition() {
    throw HarnessFailure(description: message)
  }
}

func pumpMainQueue(until condition: @escaping () -> Bool, timeout: TimeInterval = 1) {
  let deadline = Date().addingTimeInterval(timeout)
  while !condition() && Date() < deadline {
    _ = RunLoop.main.run(mode: .default, before: Date().addingTimeInterval(0.01))
  }
}

extension HarnessModule {
  private func attach(_ peripheral: CBPeripheral, characteristic: CBCharacteristic) {
    connectedPeripheral = peripheral
    connectedAddress = peripheral.identifier.uuidString
    writeCharacteristic = characteristic
    discoveredPeripherals[peripheral.identifier.uuidString] = peripheral
    centralManager.retrievedPeripherals[peripheral.identifier] = peripheral
  }

  func verifyTimeoutRetiresSessionAndIgnoresLateAck() throws {
    let oldPeripheral = CBPeripheral()
    let oldCharacteristic = CBCharacteristic(properties: .write)
    attach(oldPeripheral, characteristic: oldCharacteristic)

    var firstRejects: [(String?, String?)] = []
    writePrinterCommand(
      String(repeating: "A", count: 45),
      encoding: "UTF-8",
      resolver: { _ in },
      rejecter: { code, message, _ in firstRejects.append((code, message)) }
    )
    try expect(oldPeripheral.writes.count == 1, "withResponse 打印 A 应仅发送首个分片并等待 ACK")

    printTimeoutWorkItem?.perform()
    pumpMainQueue(until: { !firstRejects.isEmpty })
    try expect(firstRejects.first?.0 == "PRINT_TIMEOUT", "打印 A 应保留原始 PRINT_TIMEOUT")
    try expect(connectedPeripheral == nil && writeCharacteristic == nil, "超时后必须立即清除旧会话")
    try expect(
      centralManager.cancelledPeripherals.last === oldPeripheral,
      "超时后必须取消旧 peripheral"
    )
    try expect(
      retiringPeripheralIDs.contains(ObjectIdentifier(oldPeripheral)),
      "异步取消完成前必须隔离旧 peripheral"
    )

    var retryRejects: [(String?, String?)] = []
    connect(
      oldPeripheral.identifier.uuidString,
      resolver: { _ in },
      rejecter: { code, message, _ in retryRejects.append((code, message)) }
    )
    pumpMainQueue(until: { !retryRejects.isEmpty })
    try expect(retryRejects.first?.0 == "CONNECT_ERROR", "重试不得复用仍在退役的旧 peripheral")
    try expect(
      !centralManager.connectedPeripherals.contains(where: { $0 === oldPeripheral }),
      "退役实例不得再次交给 CoreBluetooth connect"
    )
    try expect(oldPeripheral.writes.count == 1, "重试不得在旧 peripheral 上自动重放打印 A")

    let newPeripheral = CBPeripheral()
    let newCharacteristic = CBCharacteristic(properties: .write)
    attach(newPeripheral, characteristic: newCharacteristic)
    var secondResolved = false
    writePrinterCommand(
      String(repeating: "B", count: 45),
      encoding: "UTF-8",
      resolver: { _ in secondResolved = true },
      rejecter: { code, message, _ in
        fatalError("打印 B 不应失败: \(code ?? "") \(message ?? "")")
      }
    )
    try expect(newPeripheral.writes.count == 1, "打印 B 应等待自己的首个 ACK")

    peripheral(oldPeripheral, didWriteValueFor: oldCharacteristic, error: nil)
    try expect(newPeripheral.writes.count == 1, "打印 A 的迟到 ACK 不得推进打印 B")
    try expect(!secondResolved, "打印 A 的迟到 ACK 不得结算打印 B")

    peripheral(newPeripheral, didWriteValueFor: newCharacteristic, error: nil)
    peripheral(newPeripheral, didWriteValueFor: newCharacteristic, error: nil)
    peripheral(newPeripheral, didWriteValueFor: newCharacteristic, error: nil)
    pumpMainQueue(until: { secondResolved })
    try expect(secondResolved, "打印 B 应由自己的 ACK 完成")
  }

  func verifyWriteErrorRetiresSession() throws {
    let peripheralUnderTest = CBPeripheral()
    let characteristic = CBCharacteristic(properties: .write)
    attach(peripheralUnderTest, characteristic: characteristic)
    var rejects: [(String?, String?)] = []
    writePrinterCommand(
      String(repeating: "E", count: 25),
      encoding: "UTF-8",
      resolver: { _ in },
      rejecter: { code, message, _ in rejects.append((code, message)) }
    )

    let writeError = NSError(domain: "Harness", code: 17, userInfo: [
      NSLocalizedDescriptionKey: "write failed",
    ])
    self.peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: writeError)
    pumpMainQueue(until: { !rejects.isEmpty })
    try expect(rejects.first?.0 == "PRINT_ERROR", "写回调错误应保留 PRINT_ERROR")
    try expect(connectedPeripheral == nil && writeCharacteristic == nil, "写回调错误后必须清除旧会话")
    try expect(
      retiringPeripheralIDs.contains(ObjectIdentifier(peripheralUnderTest)),
      "写回调错误后必须隔离旧 peripheral"
    )
  }

  func verifyReadinessWithoutPrintKeepsHealthySession() throws {
    let healthyPeripheral = CBPeripheral()
    let characteristic = CBCharacteristic(properties: .writeWithoutResponse)
    attach(healthyPeripheral, characteristic: characteristic)

    peripheralIsReady(toSendWriteWithoutResponse: healthyPeripheral)
    try expect(connectedPeripheral === healthyPeripheral, "无在途打印时 readiness 不得关闭健康会话")
    try expect(writeCharacteristic === characteristic, "无在途打印时 readiness 不得清除写特征")
    try expect(centralManager.cancelledPeripherals.isEmpty, "无在途打印时不得请求断开")
  }

  func verifyEncodingFailureKeepsHealthySession() throws {
    let healthyPeripheral = CBPeripheral()
    let characteristic = CBCharacteristic(properties: .write)
    attach(healthyPeripheral, characteristic: characteristic)
    var rejects: [(String?, String?)] = []

    writePrinterCommand(
      "content",
      encoding: "NOT-A-REAL-ENCODING",
      resolver: { _ in },
      rejecter: { code, message, _ in rejects.append((code, message)) }
    )
    pumpMainQueue(until: { !rejects.isEmpty })
    try expect(rejects.first?.0 == "PRINT_ERROR", "内容编码失败应以 PRINT_ERROR 返回")
    try expect(connectedPeripheral === healthyPeripheral, "内容编码失败不得清连接")
    try expect(writeCharacteristic === characteristic, "内容编码失败不得清写特征")
    try expect(centralManager.cancelledPeripherals.isEmpty, "内容编码失败不得请求断开")
  }

  private func writeTypes(_ peripheral: CBPeripheral) -> [CBCharacteristicWriteType] {
    peripheral.writes.map { $0.2 }
  }

  func verifyFastModeStreamsWithoutResponseAndWaitsAtBarriers() throws {
    let peripheralUnderTest = CBPeripheral()
    let characteristic = CBCharacteristic(properties: [.write, .writeWithoutResponse])
    attach(peripheralUnderTest, characteristic: characteristic)
    // 每包 20 字节、每 40 字节一个栅栏：100 字节 = 5 包，第 2、4 包和最后一包要应答。
    writeAckIntervalBytes = 40
    var resolved = false
    writePrinterCommand(
      String(repeating: "F", count: 100),
      encoding: "UTF-8",
      resolver: { _ in resolved = true },
      rejecter: { code, message, _ in fatalError("fast 打印不应失败: \(code ?? "") \(message ?? "")") }
    )
    try expect(writeTypes(peripheralUnderTest) == [.withoutResponse, .withResponse], "fast 应无应答连发到第一个栅栏后停下")

    peripheralIsReady(toSendWriteWithoutResponse: peripheralUnderTest)
    try expect(peripheralUnderTest.writes.count == 2, "栅栏应答前 peripheralIsReady 不得推进下一包")
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    try expect(peripheralUnderTest.writes.count == 4, "栅栏应答后应继续发到下一个栅栏")
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    try expect(
      writeTypes(peripheralUnderTest) == [.withoutResponse, .withResponse, .withoutResponse, .withResponse, .withResponse],
      "最后一包必须是有应答写入"
    )
    pumpMainQueue(until: { resolved }, timeout: 0.2)
    try expect(!resolved, "最后一包应答前不得报打印成功")
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    pumpMainQueue(until: { resolved })
    try expect(resolved, "最后一包应答后应报打印成功")
    let payload = peripheralUnderTest.writes.map { $0.0 }.reduce(Data(), +)
    try expect(payload == Data(String(repeating: "F", count: 100).utf8), "分包拼回后必须与原始数据一致且顺序不变")

    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    try expect(peripheralUnderTest.writes.count == 5, "多余应答不得触发额外写入")
  }

  func verifyFastModeHonorsLocalSendBuffer() throws {
    let peripheralUnderTest = CBPeripheral()
    let characteristic = CBCharacteristic(properties: [.write, .writeWithoutResponse])
    attach(peripheralUnderTest, characteristic: characteristic)
    writeAckIntervalBytes = 2048
    peripheralUnderTest.canSendWriteWithoutResponse = false
    var resolved = false
    writePrinterCommand(
      String(repeating: "G", count: 60),
      encoding: "UTF-8",
      resolver: { _ in resolved = true },
      rejecter: { _, _, _ in fatalError("缓冲等待不应失败") }
    )
    try expect(peripheralUnderTest.writes.isEmpty, "本机发送缓冲满时不得写入无应答包")
    peripheralUnderTest.canSendWriteWithoutResponse = true
    peripheralIsReady(toSendWriteWithoutResponse: peripheralUnderTest)
    try expect(writeTypes(peripheralUnderTest) == [.withoutResponse, .withoutResponse, .withResponse], "缓冲恢复后继续发送，末包要应答")
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    pumpMainQueue(until: { resolved })
    try expect(resolved, "末包应答后应报打印成功")
  }

  func verifyReliableModeKeepsLegacyWithResponseWrites() throws {
    let peripheralUnderTest = CBPeripheral()
    let characteristic = CBCharacteristic(properties: [.write, .writeWithoutResponse])
    attach(peripheralUnderTest, characteristic: characteristic)
    var configured = false
    configureWrite(["mode": "reliable", "ackIntervalBytes": 1], resolver: { _ in configured = true }, rejecter: { _, _, _ in })
    pumpMainQueue(until: { configured })
    try expect(writeMode == "reliable", "configureWrite 应切到 reliable")
    try expect(writeAckIntervalBytes == 256, "栅栏间隔应夹到下限 256")

    var resolved = false
    writePrinterCommand(
      String(repeating: "R", count: 45),
      encoding: "UTF-8",
      resolver: { _ in resolved = true },
      rejecter: { _, _, _ in fatalError("reliable 打印不应失败") }
    )
    try expect(writeTypes(peripheralUnderTest) == [.withResponse], "reliable 应逐包有应答写入")
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    peripheral(peripheralUnderTest, didWriteValueFor: characteristic, error: nil)
    pumpMainQueue(until: { resolved })
    try expect(writeTypes(peripheralUnderTest) == [.withResponse, .withResponse, .withResponse], "reliable 全部有应答")
    try expect(resolved, "reliable 末包应答后应报成功")

    var ignored = false
    configureWrite(["mode": "turbo", "ackIntervalBytes": 999_999], resolver: { _ in ignored = true }, rejecter: { _, _, _ in })
    pumpMainQueue(until: { ignored })
    try expect(writeMode == "reliable", "未知模式应忽略并保留当前值")
    try expect(writeAckIntervalBytes == 16384, "栅栏间隔应夹到上限 16384")
  }

  func verifyWithoutResponseOnlyCharacteristicStillStreams() throws {
    let peripheralUnderTest = CBPeripheral()
    let characteristic = CBCharacteristic(properties: .writeWithoutResponse)
    attach(peripheralUnderTest, characteristic: characteristic)
    var resolved = false
    writePrinterCommand(
      String(repeating: "W", count: 50),
      encoding: "UTF-8",
      resolver: { _ in resolved = true },
      rejecter: { _, _, _ in fatalError("无应答特征值打印不应失败") }
    )
    pumpMainQueue(until: { resolved })
    try expect(writeTypes(peripheralUnderTest) == [.withoutResponse, .withoutResponse, .withoutResponse], "只支持无应答的特征值保持全部无应答")
    try expect(resolved, "只支持无应答的特征值发完即成功（与旧行为一致）")
  }
}

do {
  try HarnessModule().verifyTimeoutRetiresSessionAndIgnoresLateAck()
  print("PASS timeout retires old session and stale ACK cannot advance retry")
  try HarnessModule().verifyWriteErrorRetiresSession()
  print("PASS didWrite error retires old session")
  try HarnessModule().verifyReadinessWithoutPrintKeepsHealthySession()
  print("PASS readiness without active print keeps healthy session")
  try HarnessModule().verifyEncodingFailureKeepsHealthySession()
  print("PASS encoding failure keeps healthy session")
  try HarnessModule().verifyFastModeStreamsWithoutResponseAndWaitsAtBarriers()
  print("PASS fast mode streams without response and waits at barriers")
  try HarnessModule().verifyFastModeHonorsLocalSendBuffer()
  print("PASS fast mode honors local send buffer")
  try HarnessModule().verifyReliableModeKeepsLegacyWithResponseWrites()
  print("PASS reliable mode keeps legacy with-response writes")
  try HarnessModule().verifyWithoutResponseOnlyCharacteristicStillStreams()
  print("PASS without-response-only characteristic still streams")
} catch {
  fputs("FAIL \(error)\n", stderr)
  exit(1)
}
