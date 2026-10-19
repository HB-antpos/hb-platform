import CoreBluetooth
import CoreImage
import Foundation
// RN 0.81 预编译核心（React-Core-prebuilt）下，桥接头里的 #import <React/RCTEventEmitter.h>
// 不能让 Swift 看到 RCTEventEmitter，EAS 构建会报 cannot find type；必须显式导入 React 模块。
import React
import UIKit

@objc(HbPrinterModule)
class HbPrinterModule: RCTEventEmitter, CBCentralManagerDelegate, CBPeripheralDelegate {
  private struct PriceParts {
    let integer: String
    let decimal: String
  }

  private struct PrinterBitmap {
    let width: Int
    let height: Int
    let hex: String
    let inkMinY: Int
    let inkMaxY: Int

    var widthBytes: Int {
      (width + 7) / 8
    }

    var inkHeight: Int {
      max(1, inkMaxY - inkMinY + 1)
    }
  }

  private let bluetoothQueue = DispatchQueue(label: "com.hbweb.expo.printer.ble")
  private var centralManager: CBCentralManager!
  private var discoveredPrinters: [String: [String: Any]] = [:]
  private var discoveredPeripherals: [String: CBPeripheral] = [:]
  private var scanResolve: RCTPromiseResolveBlock?
  private var scanReject: RCTPromiseRejectBlock?
  private var scanFinishWorkItem: DispatchWorkItem?
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
  private var pendingWriteChunks: [Data] = []
  private var pendingWriteType: CBCharacteristicWriteType?
  private var pendingWriteCharacteristic: CBCharacteristic?
  private var printTimeoutWorkItem: DispatchWorkItem?
  private var hasStatusListeners = false

  override init() {
    super.init()
    centralManager = CBCentralManager(delegate: self, queue: bluetoothQueue)
  }

  override func supportedEvents() -> [String]! {
    [Self.statusEvent]
  }

  override func startObserving() {
    hasStatusListeners = true
  }

  override func stopObserving() {
    hasStatusListeners = false
  }

  @objc(getStatus:rejecter:)
  func getStatus(_ resolve: @escaping RCTPromiseResolveBlock, rejecter reject: @escaping RCTPromiseRejectBlock) {
    bluetoothQueue.async {
      let state = self.centralManager.state
      let status = NSMutableDictionary()
      status["supported"] = state != .unsupported
      status["enabled"] = state == .poweredOn
      status["connected"] = self.connectedPeripheral?.state == .connected && self.writeCharacteristic != nil
      status["address"] = self.connectedAddress ?? NSNull()
      self.resolve(resolve, status)
    }
  }

  @objc(scanPrinters:resolver:rejecter:)
  func scanPrinters(
    _ durationMs: NSNumber,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      guard self.ensureBluetoothReady(reject) else {
        return
      }

      guard self.scanResolve == nil else {
        self.reject(reject, "SCAN_IN_PROGRESS", "Bluetooth printer scan is already running.")
        return
      }

      // iOS BLE 没有 Android MAC 地址，这里用 peripheral UUID 作为后续 connect(address) 的稳定地址。
      self.discoveredPrinters.removeAll()
      self.discoveredPeripherals.removeAll()
      self.scanResolve = resolve
      self.scanReject = reject
      if self.centralManager.isScanning {
        self.centralManager.stopScan()
      }
      self.centralManager.scanForPeripherals(withServices: nil, options: [
        CBCentralManagerScanOptionAllowDuplicatesKey: false,
      ])

      let delayMs = max(durationMs.intValue, 1500)
      let workItem = DispatchWorkItem { [weak self] in
        self?.finishScan()
      }
      self.scanFinishWorkItem = workItem
      self.bluetoothQueue.asyncAfter(deadline: .now() + .milliseconds(delayMs), execute: workItem)
    }
  }

  @objc(connect:resolver:rejecter:)
  func connect(
    _ address: String,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      guard self.ensureBluetoothReady(reject) else {
        return
      }

      guard self.connectResolve == nil else {
        self.reject(reject, "CONNECT_IN_PROGRESS", "Bluetooth printer connection is already running.")
        return
      }

      guard let uuid = UUID(uuidString: address) else {
        self.reject(reject, "INVALID_ADDRESS", "iOS Bluetooth printer address must be a peripheral UUID.")
        return
      }

      guard let peripheral = self.discoveredPeripherals[address]
        ?? self.centralManager.retrievePeripherals(withIdentifiers: [uuid]).first else {
        self.reject(reject, "CONNECT_ERROR", "Bluetooth printer was not found. Please scan again before connecting.")
        return
      }

      // 手动选择或自动恢复可能再次连接同一台健康打印机；直接成功，避免无意义取消后短暂报错。
      if peripheral === self.connectedPeripheral,
         self.connectedAddress == address,
         peripheral.state == .connected,
         self.writeCharacteristic != nil {
        self.resolve(resolve, true)
        return
      }

      // 同一 CBPeripheral 在取消完成前可能继续回送上一轮委托；等终止回调后再允许复用。
      guard !self.retiringPeripheralIDs.contains(ObjectIdentifier(peripheral)) else {
        self.reject(reject, "CONNECT_ERROR", "Previous Bluetooth printer connection is still disconnecting. Please retry.")
        return
      }

      self.disconnectInternal()
      guard !self.retiringPeripheralIDs.contains(ObjectIdentifier(peripheral)) else {
        self.emitStatusChanged()
        self.reject(reject, "CONNECT_ERROR", "Previous Bluetooth printer connection is still disconnecting. Please retry.")
        return
      }
      self.connectionGeneration += 1
      let attemptGeneration = self.connectionGeneration
      self.activeConnectGeneration = attemptGeneration
      self.connectResolve = resolve
      self.connectReject = reject
      peripheral.delegate = self
      self.connectedPeripheral = peripheral
      self.connectedAddress = address
      self.centralManager.connect(peripheral, options: nil)

      // 连接和服务发现都依赖外设回调，超时后清理状态，避免 Promise 悬挂。
      let workItem = DispatchWorkItem { [weak self] in
        guard let self else {
          return
        }
        self.failPendingConnect(
          "CONNECT_TIMEOUT",
          "Bluetooth printer connection timed out.",
          expectedPeripheral: peripheral,
          expectedGeneration: attemptGeneration
        )
      }
      self.connectTimeoutWorkItem = workItem
      self.bluetoothQueue.asyncAfter(deadline: .now() + .seconds(12), execute: workItem)
    }
  }

  @objc(disconnect:rejecter:)
  func disconnect(_ resolve: @escaping RCTPromiseResolveBlock, rejecter reject: @escaping RCTPromiseRejectBlock) {
    bluetoothQueue.async {
      self.disconnectInternal()
      self.emitStatusChanged()
      self.resolve(resolve, true)
    }
  }

  @objc(print:encoding:resolver:rejecter:)
  func print(
    _ command: String,
    encoding: String?,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      self.writePrinterCommand(command, encoding: encoding, resolver: resolve, rejecter: reject)
    }
  }

  @objc(printProductLabel:printType:resolver:rejecter:)
  func printProductLabel(
    _ payload: NSDictionary,
    printType: String?,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      let command = self.buildProductLabelCommand(payload, printType: printType)
      self.writePrinterCommand(command, encoding: "GB18030", resolver: resolve, rejecter: reject)
    }
  }

  @objc(printDiscountLabel:printType:resolver:rejecter:)
  func printDiscountLabel(
    _ payload: NSDictionary,
    printType: String?,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      let command = self.buildDiscountLabelCommand(payload, printType: printType)
      self.writePrinterCommand(command, encoding: "GB18030", resolver: resolve, rejecter: reject)
    }
  }

  @objc(printClearanceLabel:resolver:rejecter:)
  func printClearanceLabel(
    _ payload: NSDictionary,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    rejectUnsupportedLabelPrint(reject)
  }

  @objc(printBigDiscountLabel:printType:resolver:rejecter:)
  func printBigDiscountLabel(
    _ payload: NSDictionary,
    printType: String?,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      let command = self.buildBigDiscountLabelCommand(payload, printType: printType)
      self.writePrinterCommand(command, encoding: "GB18030", resolver: resolve, rejecter: reject)
    }
  }

  @objc(printWarehouseProductLabel:resolver:rejecter:)
  func printWarehouseProductLabel(
    _ payload: NSDictionary,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      let command = self.buildWarehouseProductLabelCommand(payload)
      self.writePrinterCommand(command, encoding: "GB18030", resolver: resolve, rejecter: reject)
    }
  }

  @objc(printWarehouseLocationLabel:resolver:rejecter:)
  func printWarehouseLocationLabel(
    _ payload: NSDictionary,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    bluetoothQueue.async {
      let command = self.buildWarehouseLocationLabelCommand(payload)
      self.writePrinterCommand(command, encoding: "GB18030", resolver: resolve, rejecter: reject)
    }
  }

  func centralManagerDidUpdateState(_ central: CBCentralManager) {
    if central.state != .poweredOn {
      failPendingScan("BLUETOOTH_DISABLED", "Bluetooth is turned off.")
      if connectResolve != nil {
        failPendingConnect("BLUETOOTH_DISABLED", "Bluetooth is turned off.")
      } else {
        disconnectInternal()
      }
      // 无线电关闭后 CoreBluetooth 不一定再补发每个 peripheral 的终止回调；恢复时允许重新取回实例。
      retiringPeripheralIDs.removeAll()
    }
    // poweredOn 同样通知 JS，让系统蓝牙恢复后可以立刻重新读取状态并重连。
    emitStatusChanged()
  }

  func centralManager(
    _ central: CBCentralManager,
    didDiscover peripheral: CBPeripheral,
    advertisementData: [String: Any],
    rssi RSSI: NSNumber
  ) {
    let address = peripheral.identifier.uuidString
    discoveredPeripherals[address] = peripheral
    let name = peripheral.name
      ?? advertisementData[CBAdvertisementDataLocalNameKey] as? String
      ?? "Bluetooth Printer"
    discoveredPrinters[address] = [
      "name": name,
      "address": address,
      "bonded": false,
      "connected": address == connectedAddress && peripheral.state == .connected,
    ]
  }

  func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
    guard peripheral === connectedPeripheral, connectResolve != nil else {
      return
    }
    peripheral.discoverServices(nil)
  }

  func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
    let peripheralID = ObjectIdentifier(peripheral)
    let wasCurrent = peripheral === connectedPeripheral
    if wasCurrent {
      failPendingConnect("CONNECT_ERROR", error?.localizedDescription ?? "Failed to connect Bluetooth printer.")
    }
    // didFailToConnect 是该次取消/连接的终止回调，到这里才允许同一实例开始下一轮连接。
    let finishedRetiring = retiringPeripheralIDs.remove(peripheralID) != nil
    if wasCurrent || finishedRetiring {
      emitStatusChanged()
    }
  }

  func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
    // 同时核对当前 peripheral 实例，降低旧连接回调误清后续连接的风险。
    let peripheralID = ObjectIdentifier(peripheral)
    let wasCurrent = peripheral === connectedPeripheral
    if wasCurrent {
      if connectResolve != nil || connectReject != nil {
        // 发现服务期间断开也必须立即结算连接 Promise，不能让超时门禁留下永久 pending。
        failPendingConnect(
          "CONNECT_ERROR",
          error?.localizedDescription ?? "Bluetooth printer disconnected while connecting."
        )
      } else if printResolve != nil {
        failPendingPrint("PRINT_ERROR", error?.localizedDescription ?? "Bluetooth printer disconnected while printing.")
      }
      writeCharacteristic = nil
      connectedPeripheral = nil
      connectedAddress = nil
      emitStatusChanged()
    }
    // 所有蓝牙委托都在 bluetoothQueue 串行执行，以终止回调作为允许复用该实例的边界。
    let finishedRetiring = retiringPeripheralIDs.remove(peripheralID) != nil
    if !wasCurrent && finishedRetiring {
      emitStatusChanged()
    }
  }

  func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
    guard peripheral === connectedPeripheral, connectResolve != nil else {
      return
    }
    if let error {
      failPendingConnect("CONNECT_ERROR", error.localizedDescription)
      return
    }

    let services = peripheral.services ?? []
    if services.isEmpty {
      failPendingConnect("CONNECT_ERROR", "Bluetooth printer did not expose writable services.")
      return
    }

    pendingCharacteristicServiceCount = services.count
    services.forEach { service in
      peripheral.discoverCharacteristics(nil, for: service)
    }
  }

  func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
    guard peripheral === connectedPeripheral, service.peripheral === connectedPeripheral, connectResolve != nil else {
      return
    }
    defer {
      pendingCharacteristicServiceCount = max(0, pendingCharacteristicServiceCount - 1)
      if writeCharacteristic == nil && pendingCharacteristicServiceCount == 0 && connectResolve != nil {
        failPendingConnect("CONNECT_ERROR", "Bluetooth printer did not expose a writable characteristic.")
      }
    }

    if let error {
      failPendingConnect("CONNECT_ERROR", error.localizedDescription)
      return
    }

    guard let characteristic = service.characteristics?.first(where: { item in
      item.properties.contains(.write) || item.properties.contains(.writeWithoutResponse)
    }) else {
      return
    }

    writeCharacteristic = characteristic
    connectTimeoutWorkItem?.cancel()
    connectTimeoutWorkItem = nil
    let resolve = connectResolve
    connectResolve = nil
    connectReject = nil
    activeConnectGeneration = nil
    emitStatusChanged()
    self.resolve(resolve, true)
  }

  func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
    guard peripheral === connectedPeripheral, characteristic === pendingWriteCharacteristic else {
      return
    }
    if let error {
      failPendingWrite("PRINT_ERROR", error.localizedDescription)
      return
    }

    flushPendingWrites()
  }

  func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral) {
    guard peripheral === connectedPeripheral else {
      return
    }
    flushPendingWrites()
  }

  private func writePrinterCommand(
    _ command: String,
    encoding: String?,
    resolver resolve: @escaping RCTPromiseResolveBlock,
    rejecter reject: @escaping RCTPromiseRejectBlock
  ) {
    guard let peripheral = connectedPeripheral,
          peripheral.state == .connected,
          let characteristic = writeCharacteristic else {
      self.reject(reject, "PRINT_ERROR", "No Bluetooth printer is connected.")
      return
    }

    guard printResolve == nil else {
      self.reject(reject, "PRINT_IN_PROGRESS", "Bluetooth printer is already printing.")
      return
    }

    do {
      let data = try encodeCommand(command, encoding: encoding)
      if data.isEmpty {
        self.resolve(resolve, true)
        return
      }

      let writeType: CBCharacteristicWriteType = characteristic.properties.contains(.write) ? .withResponse : .withoutResponse
      let chunkSize = max(20, peripheral.maximumWriteValueLength(for: writeType))
      pendingWriteChunks = stride(from: 0, to: data.count, by: chunkSize).map { offset in
        data.subdata(in: offset..<min(offset + chunkSize, data.count))
      }
      pendingWriteType = writeType
      pendingWriteCharacteristic = characteristic
      printResolve = resolve
      printReject = reject

      // 位图标签的 EG 命令明显更大，按数据量放宽超时，避免慢速 BLE withResponse 误报失败。
      let timeoutSeconds = printTimeoutSeconds(forByteCount: data.count)
      // BLE 写入有 MTU 和发送窗口上限；必须等系统回调后再继续，避免假成功。
      let workItem = DispatchWorkItem { [weak self] in
        guard let self else {
          return
        }
        self.failPendingWrite("PRINT_TIMEOUT", "Bluetooth printer write timed out.")
      }
      printTimeoutWorkItem = workItem
      bluetoothQueue.asyncAfter(deadline: .now() + .seconds(timeoutSeconds), execute: workItem)
      flushPendingWrites()
    } catch {
      self.reject(reject, "PRINT_ERROR", error.localizedDescription)
    }
  }

  private func printTimeoutSeconds(forByteCount byteCount: Int) -> Int {
    max(15, min(120, 10 + Int(ceil(Double(byteCount) / 1024.0))))
  }

  private func ensureBluetoothReady(_ reject: @escaping RCTPromiseRejectBlock) -> Bool {
    switch centralManager.state {
    case .poweredOn:
      return true
    case .unsupported:
      self.reject(reject, "BLUETOOTH_UNSUPPORTED", "Bluetooth is not supported on this device.")
    case .unauthorized:
      self.reject(reject, "BLUETOOTH_UNAUTHORIZED", "Bluetooth permission was not granted.")
    case .poweredOff:
      self.reject(reject, "BLUETOOTH_DISABLED", "Bluetooth is turned off.")
    default:
      self.reject(reject, "BLUETOOTH_NOT_READY", "Bluetooth is not ready yet. Please try again.")
    }
    return false
  }

  private func finishScan() {
    guard let resolve = scanResolve else {
      return
    }

    centralManager.stopScan()
    scanFinishWorkItem?.cancel()
    scanFinishWorkItem = nil
    scanResolve = nil
    scanReject = nil
    let printers = discoveredPrinters.values.sorted {
      String(describing: $0["name"] ?? "") < String(describing: $1["name"] ?? "")
    }
    self.resolve(resolve, printers)
  }

  private func failPendingScan(_ code: String, _ message: String) {
    guard let reject = scanReject else {
      return
    }

    centralManager.stopScan()
    scanFinishWorkItem?.cancel()
    scanFinishWorkItem = nil
    scanResolve = nil
    scanReject = nil
    self.reject(reject, code, message)
  }

  private func failPendingConnect(
    _ code: String,
    _ message: String,
    expectedPeripheral: CBPeripheral? = nil,
    expectedGeneration: Int? = nil
  ) {
    if let expectedPeripheral, expectedPeripheral !== connectedPeripheral {
      return
    }
    if let expectedGeneration, expectedGeneration != activeConnectGeneration {
      return
    }
    guard connectResolve != nil || connectReject != nil else {
      return
    }
    disconnectInternal(pendingConnectCode: code, pendingConnectMessage: message)
  }

  private func flushPendingWrites() {
    guard let peripheral = connectedPeripheral,
          peripheral.state == .connected,
          let characteristic = pendingWriteCharacteristic,
          let writeType = pendingWriteType else {
      failPendingWrite("PRINT_ERROR", "No Bluetooth printer is connected.")
      return
    }

    while !pendingWriteChunks.isEmpty {
      if writeType == .withoutResponse && !peripheral.canSendWriteWithoutResponse {
        return
      }

      let chunk = pendingWriteChunks.removeFirst()
      peripheral.writeValue(chunk, for: characteristic, type: writeType)
      if writeType == .withResponse {
        return
      }
    }

    finishPendingPrint()
  }

  private func finishPendingPrint() {
    let resolve = printResolve
    printTimeoutWorkItem?.cancel()
    printTimeoutWorkItem = nil
    printResolve = nil
    printReject = nil
    pendingWriteChunks.removeAll()
    pendingWriteType = nil
    pendingWriteCharacteristic = nil
    self.resolve(resolve, true)
  }

  private func failPendingWrite(_ code: String, _ message: String) {
    guard printResolve != nil || printReject != nil else {
      return
    }
    // 写失败后旧 ACK 仍可能到达；先结算本次打印并退出会话，禁止旧回调推进下一张标签。
    failPendingPrint(code, message)
    disconnectInternal()
    emitStatusChanged()
  }

  private func failPendingPrint(_ code: String, _ message: String) {
    guard printResolve != nil || printReject != nil else {
      return
    }

    let reject = printReject
    printTimeoutWorkItem?.cancel()
    printTimeoutWorkItem = nil
    printResolve = nil
    printReject = nil
    pendingWriteChunks.removeAll()
    pendingWriteType = nil
    pendingWriteCharacteristic = nil
    self.reject(reject, code, message)
  }

  private func disconnectInternal(
    pendingConnectCode: String = "CONNECT_CANCELLED",
    pendingConnectMessage: String = "Bluetooth printer connection was cancelled."
  ) {
    let pendingConnectReject = connectReject
    connectTimeoutWorkItem?.cancel()
    connectTimeoutWorkItem = nil
    connectResolve = nil
    connectReject = nil
    connectionGeneration += 1
    activeConnectGeneration = nil
    pendingCharacteristicServiceCount = 0
    if printResolve != nil {
      failPendingPrint("PRINT_ERROR", "Bluetooth printer disconnected while printing.")
    }
    if let peripheral = connectedPeripheral, peripheral.state != .disconnected {
      // cancelPeripheralConnection 异步完成；终止回调前隔离该实例，避免旧回调污染快速重连。
      retiringPeripheralIDs.insert(ObjectIdentifier(peripheral))
      centralManager.cancelPeripheralConnection(peripheral)
    }
    writeCharacteristic = nil
    connectedPeripheral = nil
    connectedAddress = nil
    self.reject(pendingConnectReject, pendingConnectCode, pendingConnectMessage)
  }

  private func emitStatusChanged() {
    let state = centralManager.state
    let status: [String: Any] = [
      "supported": state != .unsupported,
      "enabled": state == .poweredOn,
      "connected": connectedPeripheral?.state == .connected && writeCharacteristic != nil,
      "address": connectedAddress ?? NSNull(),
    ]
    // RN 事件必须回到主线程；无订阅者时不调用 sendEvent，避免 RCTEventEmitter 警告。
    DispatchQueue.main.async { [weak self] in
      guard let self, self.hasStatusListeners else {
        return
      }
      self.sendEvent(withName: Self.statusEvent, body: status)
    }
  }

  private static let statusEvent = "HbPrinterStatusChanged"

  private func buildProductLabelCommand(_ payload: NSDictionary, printType: String?) -> String {
    let isSmall = printType?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "small"
    let width = isSmall ? 472 : 570
    let height = isSmall ? 320 : 400
    let productName = dictString(payload, "productName")
    let itemNumber = dictString(payload, "itemNumber")
    let supplierName = formatSupplierAbbreviation(dictString(payload, "supplierName"))
    let barcode = cpclText(dictString(payload, "barcode"), maxLength: 64)
    let retailPrice = dictDouble(payload, "retailPrice")
    let discountRate = dictDouble(payload, "discountRate") ?? 0
    let grade = formatGrade(dictString(payload, "grade"))
    let price = formatPriceParts(retailPrice)

    // 普通商品标签对齐 Android：所有文字先渲染成单色位图，再写入 CPCL EG 命令。
    let priceIntegerBitmap = textToBitmap(price.integer, fontSize: fontSizeToPixels(40), isBold: true, fontFamily: "sans-serif-black")
    let priceDotBitmap = textToBitmap(".", fontSize: fontSizeToPixels(20), isBold: false, fontFamily: "sans-serif-black")
    let priceDecimalBitmap = textToBitmap(price.decimal, fontSize: fontSizeToPixels(20), isBold: false, fontFamily: "sans-serif-black")
    let priceCurrencyBitmap = textToBitmap("$", fontSize: fontSizeToPixels(20), isBold: false, fontFamily: "sans-serif-black")
    let itemBitmap = textToBitmap(itemNumber, fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black")
    let supplierBitmap = textToBitmap(supplierName, fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-light", isInverse: true, padding: 2)
    let dateBitmap = textToBitmap(todayString(), fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black", isInverse: true, padding: 2)
    let priceCurrencyX = width - priceDecimalBitmap.width - priceDotBitmap.width - priceIntegerBitmap.width - priceCurrencyBitmap.width
    // 商品名从 x=5 开始绘制，右侧预留价格块和 10px 间距，避免长名称压住价格。
    let nameMaxWidth = max(1, priceCurrencyX - 10)
    let nameBitmap = longTextToBitmap(productName, fontSize: fontSizeToPixels(10), isBold: false, fontFamily: "Arial", maxLines: 2, maxWidth: nameMaxWidth)
    let discountBitmap: PrinterBitmap?
    if discountRate > 0 {
      discountBitmap = textToBitmap(discountLabel(discountRate), fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black", isInverse: true, padding: 2)
    } else {
      discountBitmap = nil
    }

    let gradeBitmap: PrinterBitmap?
    if grade.isEmpty {
      gradeBitmap = nil
    } else {
      gradeBitmap = textToBitmap(grade, fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black", isInverse: true, padding: 4)
    }

    let startY = 30
    // iOS 位图高度包含 UIKit 行高；普通标签小数点按真实墨迹底部和整数对齐。
    let priceDotY = startY + priceIntegerBitmap.inkMaxY - priceDotBitmap.inkMaxY
    let startX = width - priceDecimalBitmap.width
    var commands = [
      "! 0 200 200 \(height) 1",
      "PAGE-WIDTH \(width)",
      bitmapCommand(5, 5, nameBitmap),
      bitmapCommand(5, 120, itemBitmap),
      bitmapCommand(5 + itemBitmap.width + 10, 118, supplierBitmap),
    ]

    if !barcode.isEmpty {
      let barcodeType = isValidEan13(barcode) ? "EAN13" : "128"
      commands.append("BARCODE-TEXT 7 0 5")
      // EAN13 的首位数字印在条码左侧外面：x=5 会被挤出标签左边界裁掉，必须留出位置（与 TS / Kotlin 一致）。
      let barcodeX = barcodeType == "EAN13" ? 30 : 5
      commands.append("BARCODE \(barcodeType) 1 2 30 \(barcodeX) 145 \(barcode)")
    }

    if let discountBitmap {
      commands.append(bitmapCommand(width - discountBitmap.width - dateBitmap.width - 20, 175, discountBitmap))
    }

    if let gradeBitmap {
      commands.append(bitmapCommand(300, 175, gradeBitmap))
    }

    commands.append(bitmapCommand(startX, startY, priceDecimalBitmap))
    commands.append(bitmapCommand(startX - priceDotBitmap.width, priceDotY, priceDotBitmap))
    commands.append(bitmapCommand(startX - priceDotBitmap.width - priceIntegerBitmap.width, startY, priceIntegerBitmap))
    commands.append(bitmapCommand(
      priceCurrencyX,
      startY,
      priceCurrencyBitmap
    ))
    commands.append(bitmapCommand(width - dateBitmap.width, 175, dateBitmap))
    commands.append("PRINT")
    return commands.joined(separator: "\r\n") + "\r\n"
  }

  private func buildDiscountLabelCommand(_ payload: NSDictionary, printType: String?) -> String {
    let isSmall = printType?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "small"
    let width = isSmall ? 472 : 570
    let height = isSmall ? 320 : 400
    let productName = dictString(payload, "productName")
    let itemNumber = dictString(payload, "itemNumber")
    let barcodeValue = dictString(payload, "barcode")
    let barcode = barcodeValue.isEmpty ? itemNumber : barcodeValue
    let retailPrice = dictDouble(payload, "retailPrice") ?? 0
    let discountRate = dictDouble(payload, "discountRate") ?? 0
    let discountValue = discountRate * 100
    // 先按分舍入，避免 12.34 × 75% 的浮点尾差在 iOS 上被格式化成 9.25。
    let nowPrice = (retailPrice * (1 - discountRate) * 100 + 1e-8).rounded() / 100
    let showOriginalPrice = retailPrice.isFinite && nowPrice.isFinite && retailPrice > nowPrice && nowPrice >= 0

    // 此模板按真实墨迹裁掉 UIKit 行高留白，再测宽缩字；不截断金额。
    func fittedText(_ value: String, size: CGFloat, maxWidth: Int, maxHeight: Int = 64, inverse: Bool = false, padding: Int = 0) -> (bitmap: PrinterBitmap, fontSize: CGFloat, inkTop: Int) {
      var fittedSize = size
      while true {
        let raw = textToBitmap(value, fontSize: fontSizeToPixels(fittedSize), isBold: true, fontFamily: "sans-serif-black", isInverse: inverse, padding: padding)
        let bitmap = inverse ? raw : PrinterBitmap(
          width: raw.width,
          height: raw.inkHeight,
          hex: String(raw.hex.dropFirst(raw.inkMinY * raw.widthBytes * 2).prefix(raw.inkHeight * raw.widthBytes * 2)),
          inkMinY: 0,
          inkMaxY: raw.inkHeight - 1
        )
        if (bitmap.width <= maxWidth && bitmap.height <= maxHeight) || fittedSize <= 1 {
          return (bitmap, fittedSize, inverse ? 0 : raw.inkMinY)
        }
        fittedSize -= 0.5
      }
    }

    let columnGap = 10
    let infoX = 84
    let infoWidth = isSmall ? 100 : 124
    let wasX = infoX + infoWidth + columnGap
    let wasWidth = isSmall ? 76 : 96
    let nowX = showOriginalPrice ? wasX + wasWidth + columnGap : wasX
    let nowWidth = width - 12 - nowX
    let nowPadding = 6
    let nowGroupGap = 6
    let nowLabel = fittedText("NOW", size: 6, maxWidth: nowWidth)
    let nowPriceText = "$\(formatMoney(nowPrice))"
    let nowPriceTextBitmap = fittedText(nowPriceText, size: 16, maxWidth: nowWidth - nowLabel.bitmap.width - nowGroupGap - nowPadding * 2, maxHeight: 52)
    let nowHeight = max(nowLabel.bitmap.height, nowPriceTextBitmap.bitmap.height) + nowPadding * 2
    let nowBitmap = renderPrinterBitmap(width: nowWidth, height: nowHeight, isInverse: true) {
      ("NOW" as NSString).draw(
        at: CGPoint(x: nowPadding, y: (nowHeight - nowLabel.bitmap.height) / 2 - nowLabel.inkTop),
        withAttributes: [.font: printerFont(fontFamily: "sans-serif-black", fontSize: fontSizeToPixels(nowLabel.fontSize), isBold: true), .foregroundColor: UIColor.white]
      )
      (nowPriceText as NSString).draw(
        at: CGPoint(x: nowWidth - nowPadding - nowPriceTextBitmap.bitmap.width, y: (nowHeight - nowPriceTextBitmap.bitmap.height) / 2 - nowPriceTextBitmap.inkTop),
        withAttributes: [.font: printerFont(fontFamily: "sans-serif-black", fontSize: fontSizeToPixels(nowPriceTextBitmap.fontSize), isBold: true), .foregroundColor: UIColor.white]
      )
    }
    let wasLabelBitmap = fittedText("WAS", size: 6, maxWidth: wasWidth).bitmap
    let wasPriceBitmap = fittedText("$\(formatMoney(retailPrice))", size: 8, maxWidth: wasWidth, maxHeight: 30).bitmap
    let dateBitmap = fittedText(todayString(), size: 6, maxWidth: infoWidth, maxHeight: 24, inverse: true, padding: 2).bitmap
    // 超长货号明确显示省略号；二维码继续编码完整条码/货号。
    let itemDisplay = itemNumber.count > 24 ? String(itemNumber.prefix(21)) + "..." : itemNumber
    let itemBitmap = itemDisplay.isEmpty ? nil : fittedText(itemDisplay, size: 7, maxWidth: infoWidth, maxHeight: 28).bitmap
    let discountBitmap = fittedText(String(format: "%02d", Int(discountValue.rounded())), size: 44, maxWidth: width / 2, maxHeight: 108).bitmap
    let offBitmap = fittedText("OFF", size: 16, maxWidth: 110).bitmap
    let percentBitmap = fittedText("%", size: 20, maxWidth: 70).bitmap
    let startY = 20
    let headerGap = 8 // EG 每行按 8 点补齐，留出字节尾部空白，避免相邻位图覆盖。
    let startX = width - 12 - discountBitmap.width - headerGap - max(percentBitmap.width, percentBitmap.width / 2 + offBitmap.width)
    let qrBitmap = barcode.isEmpty ? nil : createQrCodeBitmap(barcode, size: 64)
    // 两种纸宽都遵守现有 204 点有效打印区，底部保留 10 点。
    let infoBandBottom = 194
    let qrX = 10
    let qrY = infoBandBottom - (qrBitmap?.height ?? 64)
    let dateY = infoBandBottom - dateBitmap.height
    let itemY = dateY - (itemBitmap?.height ?? 0) - 6
    let wasPriceY = infoBandBottom - wasPriceBitmap.height
    let discountOffY = startY + discountBitmap.height - offBitmap.height
    let nameMaxWidth = max(1, startX - 15)
    let nameFont = printerFont(fontFamily: "Arial", fontSize: fontSizeToPixels(10), isBold: false)
    var nameLines = wrapText(cpclText(productName), font: nameFont, maxWidth: nameMaxWidth, maxLines: 2)
    // 英文优先在词间换行；只有单词本身太长时才沿用逐字换行。
    if nameLines.count == 2 && !nameLines[0].hasSuffix(" ") && !nameLines[1].hasPrefix(" "),
       let split = nameLines[0].lastIndex(of: " "), split != nameLines[0].startIndex {
      nameLines[1] = String(nameLines[0][split...]).trimmingCharacters(in: .whitespaces) + nameLines[1]
      nameLines[0] = String(nameLines[0][..<split])
    }
    let nameLineHeight = Int(ceil(nameFont.lineHeight))
    let nameBitmap = renderPrinterBitmap(width: nameMaxWidth, height: nameLineHeight * nameLines.count, isInverse: false) {
      for (index, value) in nameLines.enumerated() {
        var display = value.trimmingCharacters(in: .whitespaces)
        if measureText(display, font: nameFont) > CGFloat(nameMaxWidth) {
          while !display.isEmpty && measureText(display + "...", font: nameFont) > CGFloat(nameMaxWidth) { display.removeLast() }
          display += "..."
        }
        (display as NSString).draw(at: CGPoint(x: 0, y: index * nameLineHeight), withAttributes: [.font: nameFont, .foregroundColor: UIColor.black])
      }
    }

    var commands = [
      "! 0 200 200 \(height) 1",
      "PAGE-WIDTH \(width)",
      bitmapCommand(5, 5, nameBitmap),
      bitmapCommand(startX, startY, discountBitmap),
      bitmapCommand(startX + discountBitmap.width + headerGap, startY, percentBitmap),
      bitmapCommand(
        startX + discountBitmap.width + headerGap + percentBitmap.width / 2,
        discountOffY,
        offBitmap
      ),
    ]

    if let itemBitmap {
      commands.append(bitmapCommand(infoX, itemY, itemBitmap))
    }

    if let qrBitmap {
      commands.append(bitmapCommand(qrX, qrY, qrBitmap))
    }

    commands.append(bitmapCommand(infoX, dateY, dateBitmap))
    if showOriginalPrice {
      commands.append(bitmapCommand(wasX, wasPriceY - wasLabelBitmap.height - 4, wasLabelBitmap))
      commands.append(bitmapCommand(wasX, wasPriceY, wasPriceBitmap))
      let strikeY = wasPriceY + wasPriceBitmap.height / 2
      commands.append("LINE \(wasX) \(strikeY) \(wasX + wasPriceBitmap.width - 1) \(strikeY) 2")
    }
    commands.append(bitmapCommand(nowX, infoBandBottom - nowBitmap.height, nowBitmap))
    commands.append("PRINT")
    return commands.joined(separator: "\r\n") + "\r\n"
  }

  private func buildBigDiscountLabelCommand(_ payload: NSDictionary, printType: String?) -> String {
    let productName = dictString(payload, "productName")
    let barcode = dictString(payload, "barcode")
    let retailPrice = dictDouble(payload, "retailPrice") ?? 0
    let discountRate = dictDouble(payload, "discountRate") ?? 0
    let paperWidth = 480
    let afterDiscount = retailPrice * (1 - discountRate)
    let price = formatPriceParts(afterDiscount)
    let saveAmount = retailPrice * discountRate

    var commands = [
      "! 0 200 200 1200 1",
      "PAGE-WIDTH \(paperWidth)",
    ]

    commands.append(contentsOf: buildBigDiscountHeaderCommands(discountRate: discountRate, printType: printType, paperWidth: paperWidth))

    let currencyBitmap = textToBitmap("$", fontSize: fontSizeToPixels(20), isBold: false, fontFamily: "sans-serif-black")
    let wasCurrencyBitmap = textToBitmap("$", fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black")
    let saveCurrencyBitmap = textToBitmap("$", fontSize: fontSizeToPixels(8), isBold: false, fontFamily: "sans-serif-black")
    let eaBitmap = textToBitmap("ea", fontSize: fontSizeToPixels(8), isBold: false, fontFamily: "sans-serif-light")
    let wasBitmap = textToBitmap("WAS ", fontSize: fontSizeToPixels(10), isBold: true, fontFamily: "sans-serif-light")
    let saveBitmap = textToBitmap("SAVE", fontSize: fontSizeToPixels(16), isBold: true, fontFamily: "sans-serif-light", isInverse: true, padding: 2)
    let intBitmap = textToBitmap(price.integer, fontSize: fontSizeToPixels(60), isBold: true, fontFamily: "sans-serif-black")
    let decimalBitmap = textToBitmap(price.decimal, fontSize: fontSizeToPixels(30), isBold: false, fontFamily: "sans-serif-black")
    let dotBitmap = textToBitmap(".", fontSize: fontSizeToPixels(36), isBold: false, fontFamily: "sans-serif-black")
    let rrpBitmap = textToBitmap(formatMoney(retailPrice), fontSize: fontSizeToPixels(10), isBold: true, fontFamily: "sans-serif-condensed")
    let saveAmountBitmap = textToBitmap(formatMoney(saveAmount), fontSize: fontSizeToPixels(16), isBold: true, fontFamily: "sans-serif-condensed")
    let nameBitmap = longTextToBitmap(productName, fontSize: fontSizeToPixels(10), isBold: false, fontFamily: "Arial", maxLines: 4, maxWidth: paperWidth)
    let dashLineBitmap = createDashLineBitmap(width: 450, height: 2)
    let dateBitmap = textToBitmap(todayString(), fontSize: fontSizeToPixels(8), isBold: false, fontFamily: "Arial", isInverse: true, padding: 2)

    let startY = 220
    var startX = (paperWidth - currencyBitmap.width - intBitmap.width) / 2
    var eaX = startX + currencyBitmap.width + intBitmap.width + 30
    if (Int(price.decimal) ?? 0) != 0 {
      startX = (paperWidth - currencyBitmap.width - intBitmap.width - dotBitmap.width - decimalBitmap.width) / 2
      eaX = startX + currencyBitmap.width + intBitmap.width + dotBitmap.width + decimalBitmap.width + 12
      // 大折扣当前价的小数点按真实墨迹底部对齐个位数字，避免 UIKit 行高把点压低。
      let priceDotY = startY + intBitmap.inkMaxY - dotBitmap.inkMaxY
      commands.append(bitmapCommand(startX + currencyBitmap.width + intBitmap.width, priceDotY, dotBitmap))
      commands.append(bitmapCommand(startX + currencyBitmap.width + intBitmap.width + dotBitmap.width, startY, decimalBitmap))
    }

    commands.append(bitmapCommand(startX, startY, currencyBitmap))
    commands.append(bitmapCommand(startX + currencyBitmap.width, startY, intBitmap))
    commands.append(bitmapCommand(eaX, startY + Int(Double(intBitmap.height) * 0.9), eaBitmap))

    let rrpStartY = startY + intBitmap.height + 20
    commands.append(bitmapCommand(5, rrpStartY, wasBitmap))
    commands.append(bitmapCommand(5 + wasBitmap.width, rrpStartY, wasCurrencyBitmap))
    commands.append(bitmapCommand(5 + wasBitmap.width + wasCurrencyBitmap.width, rrpStartY, rrpBitmap))

    if discountRate > 0 {
      commands.append("LINE 5 \(rrpStartY) \(5 + wasBitmap.width + rrpBitmap.width) \(rrpStartY + wasBitmap.height) 2")
      commands.append("LINE 5 \(rrpStartY + wasBitmap.height) \(5 + wasBitmap.width + rrpBitmap.width) \(rrpStartY) 2")
      let saveStartX = 30 + wasBitmap.width + wasCurrencyBitmap.width + rrpBitmap.width
      commands.append(bitmapCommand(saveStartX, rrpStartY, saveBitmap))
      commands.append(bitmapCommand(saveStartX + saveBitmap.width + 5, rrpStartY, saveCurrencyBitmap))
      commands.append(bitmapCommand(saveStartX + saveBitmap.width + 5 + saveCurrencyBitmap.width + 5, rrpStartY, saveAmountBitmap))
    }

    commands.append(bitmapCommand(5, 550 - nameBitmap.height - 5, nameBitmap))
    commands.append(bitmapCommand(15, 550, dashLineBitmap))

    if !barcode.isEmpty {
      commands.append("BARCODE 128 1 2 30 15 560 \(cpclText(barcode))")
    }

    commands.append(bitmapCommand(paperWidth - dateBitmap.width - 10, 630 - dateBitmap.height, dateBitmap))
    commands.append("PRINT")
    return commands.joined(separator: "\r\n") + "\r\n"
  }

  private func buildBigDiscountHeaderCommands(discountRate: Double, printType: String?, paperWidth: Int) -> [String] {
    let discount = discountRate * 100
    let title = printType?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    if !title.isEmpty {
      let titleBitmap = textToBitmap(title, fontSize: fontSizeToPixels(25), isBold: true, fontFamily: "sans-serif-black")
      return [bitmapCommand(paperWidth / 2 - titleBitmap.width / 2, 80, titleBitmap)]
    }

    if discount <= 10 || discount > 100 {
      let specialBitmap = textToBitmap("Special", fontSize: fontSizeToPixels(40), isBold: true, fontFamily: "sans-serif-black")
      return [bitmapCommand(paperWidth / 2 - specialBitmap.width / 2, 40, specialBitmap)]
    }

    if abs(discount - 50) < 0.01 {
      let halfBitmap = textToBitmap("1/2", fontSize: fontSizeToPixels(40), isBold: true, fontFamily: "sans-serif-black")
      let priceBitmap = textToBitmap("PRICE", fontSize: fontSizeToPixels(25), isBold: true, fontFamily: "sans-serif-black")
      return [
        bitmapCommand(130, 20, halfBitmap),
        bitmapCommand(120, 20 + halfBitmap.height, priceBitmap),
      ]
    }

    let discountBitmap = textToBitmap(String(Int(discount.rounded())), fontSize: fontSizeToPixels(40), isBold: true, fontFamily: "sans-serif-black")
    let percentBitmap = textToBitmap("%", fontSize: fontSizeToPixels(24), isBold: true, fontFamily: "sans-serif-condensed")
    let offBitmap = textToBitmap("OFF", fontSize: fontSizeToPixels(20), isBold: true, fontFamily: "sans-serif-black")
    let startX = (paperWidth - discountBitmap.width - percentBitmap.width) / 2
    return [
      bitmapCommand(startX, 20, discountBitmap),
      bitmapCommand(startX + discountBitmap.width, 20, percentBitmap),
      bitmapCommand((paperWidth - offBitmap.width) / 2, 20 + discountBitmap.height + 20 - offBitmap.inkHeight, offBitmap),
    ]
  }

  private func buildWarehouseProductLabelCommand(_ payload: NSDictionary) -> String {
    let width = 570
    let height = 208
    let productName = dictString(payload, "productName")
    let itemNumber = dictString(payload, "itemNumber")
    let barcode = dictString(payload, "barcode")
    let middlePackageQuantity = dictDouble(payload, "middlePackageQuantity")
    let purchasePrice = dictDouble(payload, "purchasePrice")
    let retailPrice = dictDouble(payload, "retailPrice")
    let locationCode = dictString(payload, "locationCode")
    let domesticPrice = dictDouble(payload, "domesticPrice")
    let oemPrice = dictDouble(payload, "oemPrice")
    let importPrice = dictDouble(payload, "importPrice")
    let displayPrice = retailPrice ?? domesticPrice ?? oemPrice ?? importPrice
    let costPrice = purchasePrice ?? importPrice ?? domesticPrice ?? oemPrice
    // 仓库商品标签只在实际中包数大于 1 时显示 INNER。
    let innerText: String? = (middlePackageQuantity ?? 0) > 1
      ? "INNER \(formatOptionalQuantity(middlePackageQuantity))"
      : nil
    let priceDetails = [
      "COST \(formatOptionalMoney(costPrice))",
      "RRP \(formatOptionalMoney(displayPrice))",
    ]
    let contentWidth = width - 40

    // 仓库商品标签按 Android 的信息密度布局：标题/商品/货号居中，货位和价格区固定。
    let titleBitmap = textToBitmap("WAREHOUSE PRODUCT", fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black", isInverse: true, padding: 2)
    let nameBitmap = longTextToBitmap(productName, fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "Arial", maxLines: 1, maxWidth: max(180, contentWidth))
    let itemText = itemNumber.isEmpty ? "--" : itemNumber
    let itemBitmap = textToBitmap("ITEM \(cpclText(itemText))", fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black")
    let innerBitmap = innerText.map { textToBitmap($0, fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black") }
    let costBitmap = textToBitmap(priceDetails[0], fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black")
    let rrpBitmap = textToBitmap(priceDetails[1], fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black")
    let locationText = locationCode.isEmpty ? "UNASSIGNED" : locationCode
    let locationBitmap = textToBitmap("LOC \(cpclText(locationText))", fontSize: fontSizeToPixels(8), isBold: true, fontFamily: "sans-serif-black", isInverse: true, padding: 2)
    let dateBitmap = textToBitmap(todayString(), fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black", isInverse: true, padding: 2)
    func centerX(_ bitmap: PrinterBitmap) -> Int { max(0, (width - bitmap.width) / 2) }

    var commands = [
      "! 0 200 200 \(height) 1",
      "PAGE-WIDTH \(width)",
      bitmapCommand(centerX(titleBitmap), 12, titleBitmap),
      bitmapCommand(centerX(nameBitmap), 38, nameBitmap),
      bitmapCommand(centerX(itemBitmap), 66, itemBitmap),
      bitmapCommand(20, 92, locationBitmap),
      bitmapCommand(width - dateBitmap.width - 20, 92, dateBitmap),
    ]

    if !barcode.isEmpty {
      commands.append("BARCODE-TEXT 7 0 5")
      commands.append("BARCODE 128 1 1 38 24 146 \(cpclText(barcode))")
      if let innerBitmap {
        commands.append(bitmapCommand(380, 132, innerBitmap))
      }
      commands.append(bitmapCommand(380, 156, costBitmap))
      commands.append(bitmapCommand(380, 180, rrpBitmap))
    } else {
      if let innerBitmap {
        commands.append(bitmapCommand(centerX(innerBitmap), 132, innerBitmap))
      }
      commands.append(bitmapCommand(centerX(costBitmap), 156, costBitmap))
      commands.append(bitmapCommand(centerX(rrpBitmap), 180, rrpBitmap))
    }

    commands.append("PRINT")
    return commands.joined(separator: "\r\n") + "\r\n"
  }

  private func buildWarehouseLocationLabelCommand(_ payload: NSDictionary) -> String {
    let width = 570
    let height = 208
    let locationCode = warehouseLocationText(payload, "locationCode")
    let locationBarcode = warehouseLocationText(payload, "locationBarcode")
    let locationGuid = warehouseLocationText(payload, "locationGuid")
    // 显示代码优先级：locationCode > locationBarcode > locationGuid。
    let displayCode = locationCode.isEmpty ? (locationBarcode.isEmpty ? locationGuid : locationBarcode) : locationCode
    let printableCode = displayCode.isEmpty ? "--" : displayCode
    // 条码优先级：locationBarcode，否则回退显示代码。
    let barcode = locationBarcode.isEmpty ? displayCode : locationBarcode

    var commands = [
      "! 0 200 200 \(height) 1",
      "PAGE-WIDTH \(width)",
    ]

    // 上方 2/3 的安全区为宽 540、高 120；代码必须完整显示，禁止换行或截断。
    let safeWidth = 540
    let safeHeight = 120
    let upperZoneHeight = height * 2 / 3
    // 目标字号 30（fontSizeToPixels 后约 90 dots），从 30 逐级递减到 1，
    // 先按字体度量挑选能放进安全区的最大字号，避免超长代码反复渲染位图。
    var fittedFontSize = CGFloat(1)
    var foundFittingSize = false
    for fontSize in stride(from: 30, through: 1, by: -1) {
      let font = printerFont(fontFamily: "sans-serif-black", fontSize: fontSizeToPixels(CGFloat(fontSize)), isBold: true)
      let measured = (printableCode as NSString).size(withAttributes: [.font: font])
      if Int(ceil(measured.width)) <= safeWidth && Int(ceil(measured.height)) <= safeHeight {
        fittedFontSize = CGFloat(fontSize)
        foundFittingSize = true
        break
      }
    }

    // 极端长代码即使 1 号仍放不下时，继续按比例缩小完整文字，禁止截断或越界。
    if !foundFittingSize {
      let minimumFont = printerFont(fontFamily: "sans-serif-black", fontSize: fontSizeToPixels(1), isBold: true)
      let measured = (printableCode as NSString).size(withAttributes: [.font: minimumFont])
      let widthScale = CGFloat(safeWidth) / max(1, measured.width)
      let heightScale = CGFloat(safeHeight) / max(1, measured.height)
      fittedFontSize = max(0.1, min(widthScale, heightScale) * 0.98)
    }

    var codeBitmap = warehouseLocationTextToBitmap(
      printableCode,
      fontSize: fontSizeToPixels(fittedFontSize)
    )
    if codeBitmap.width > safeWidth || codeBitmap.height > safeHeight {
      let correction = min(
        CGFloat(safeWidth) / CGFloat(codeBitmap.width),
        CGFloat(safeHeight) / CGFloat(codeBitmap.height)
      )
      fittedFontSize = max(0.1, fittedFontSize * correction * 0.98)
      codeBitmap = warehouseLocationTextToBitmap(printableCode, fontSize: fontSizeToPixels(fittedFontSize))
    }
    // 代码位图水平居中，并在上方 2/3 区域内垂直居中。
    let codeX = max(0, (width - codeBitmap.width) / 2)
    let codeY = max(0, (upperZoneHeight - codeBitmap.height) / 2)
    commands.append(bitmapCommand(codeX, codeY, codeBitmap))

    // 下方 1/3：CENTER + Code128 条码居中打印；条码为空时不输出 BARCODE 行。
    if !barcode.isEmpty {
      // BARCODE-TEXT 会跨标签保持，必须显式关闭，避免继承上一张商品标签的可读数字。
      commands.append("BARCODE-TEXT OFF")
      commands.append("CENTER")
      commands.append("BARCODE 128 1 1 44 0 151 \(cpclText(barcode))")
    }

    commands.append("PRINT")
    return commands.joined(separator: "\r\n") + "\r\n"
  }

  private func warehouseLocationText(_ payload: NSDictionary, _ key: String) -> String {
    guard let value = payload[key], !(value is NSNull) else {
      return ""
    }

    let text = value as? String ?? String(describing: value)
    return text
      .replacingOccurrences(of: "[\\r\\n]+", with: " ", options: .regularExpression)
      .trimmingCharacters(in: .whitespacesAndNewlines)
  }

  private func warehouseLocationTextToBitmap(_ text: String, fontSize: CGFloat) -> PrinterBitmap {
    let safeText = text.isEmpty ? " " : text
    let font = printerFont(fontFamily: "sans-serif-black", fontSize: fontSize, isBold: true)
    let attributes: [NSAttributedString.Key: Any] = [
      .font: font,
      .foregroundColor: UIColor.black,
    ]
    let measuredSize = (safeText as NSString).size(withAttributes: attributes)
    let width = max(1, Int(ceil(measuredSize.width)))
    let height = max(1, Int(ceil(measuredSize.height)))

    return renderPrinterBitmap(width: width, height: height, isInverse: false) {
      (safeText as NSString).draw(at: .zero, withAttributes: attributes)
    }
  }

  private func dictString(_ payload: NSDictionary, _ key: String) -> String {
    guard let value = payload[key], !(value is NSNull) else {
      return ""
    }

    if let text = value as? String {
      return cpclText(text)
    }

    return cpclText(String(describing: value))
  }

  private func dictDouble(_ payload: NSDictionary, _ key: String) -> Double? {
    guard let value = payload[key], !(value is NSNull) else {
      return nil
    }

    if let number = value as? NSNumber {
      return number.doubleValue
    }

    if let text = value as? String {
      return Double(text.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    return nil
  }

  private func cpclText(_ value: String, maxLength: Int = 80) -> String {
    let normalized = value
      .replacingOccurrences(of: "[\\r\\n]+", with: " ", options: .regularExpression)
      .trimmingCharacters(in: .whitespacesAndNewlines)
    return String(normalized.prefix(maxLength))
  }

  private func fontSizeToPixels(_ fontSize: CGFloat) -> CGFloat {
    fontSize * 3
  }

  private func formatPriceParts(_ value: Double?) -> PriceParts {
    let cents = Int(((value ?? 0) * 100).rounded())
    return PriceParts(integer: String(cents / 100), decimal: String(format: "%02d", abs(cents % 100)))
  }

  private func formatMoney(_ value: Double) -> String {
    String(format: "%.2f", locale: Locale(identifier: "en_US_POSIX"), value)
  }

  private func formatOptionalMoney(_ value: Double?) -> String {
    guard let value else {
      return "--"
    }
    return formatMoney(value)
  }

  private func formatOptionalQuantity(_ value: Double?) -> String {
    guard let value else {
      return "--"
    }
    let rounded = Int(value.rounded())
    return abs(value - Double(rounded)) < 0.01 ? String(rounded) : String(format: "%.2f", locale: Locale(identifier: "en_US_POSIX"), value)
  }

  private func todayString() -> String {
    let formatter = DateFormatter()
    formatter.locale = Locale(identifier: "en_US_POSIX")
    formatter.dateFormat = "yyyy/MM/dd"
    return formatter.string(from: Date())
  }

  private func discountLabel(_ discountRate: Double) -> String {
    "\(String(format: "%02d", Int((discountRate * 100).rounded())))%OFF"
  }

  private func formatSupplierAbbreviation(_ value: String) -> String {
    let locale = Locale(identifier: "en_US")
    let words = cpclText(value)
      .lowercased(with: locale)
      .split(whereSeparator: { $0.isWhitespace })
      .map { word -> String in
        let text = String(word)
        return String(text.prefix(1)).uppercased(with: locale) + String(text.dropFirst())
      }

    if words.isEmpty {
      return ""
    }

    if words.count == 1 {
      return String(words[0].prefix(3)).uppercased(with: locale)
    }

    return words
      .prefix(4)
      .map { String($0.prefix(1)).uppercased(with: locale) }
      .joined(separator: ".")
  }

  private func formatGrade(_ value: String) -> String {
    String(cpclText(value).uppercased(with: Locale(identifier: "en_US")).prefix(1))
  }

  private func isValidEan13(_ value: String) -> Bool {
    let barcode = cpclText(value, maxLength: 64)
    guard barcode.range(of: #"^\d{13}$"#, options: .regularExpression) != nil else {
      return false
    }

    let digits = barcode.compactMap { $0.wholeNumberValue }
    let checkDigit = digits.prefix(12).enumerated().reduce(0) { sum, item in
      sum + item.element * (item.offset.isMultiple(of: 2) ? 1 : 3)
    }
    return (10 - (checkDigit % 10)) % 10 == digits[12]
  }

  private func printerFont(fontFamily: String, fontSize: CGFloat, isBold: Bool) -> UIFont {
    if fontFamily == "Arial" {
      let fontName = isBold ? "Arial-BoldMT" : "Arial"
      return UIFont(name: fontName, size: fontSize) ?? UIFont.systemFont(ofSize: fontSize, weight: isBold ? .bold : .regular)
    }

    if fontFamily.contains("black") {
      return UIFont.systemFont(ofSize: fontSize, weight: .black)
    }

    if fontFamily.contains("light") {
      return UIFont.systemFont(ofSize: fontSize, weight: isBold ? .bold : .light)
    }

    return UIFont.systemFont(ofSize: fontSize, weight: isBold ? .bold : .regular)
  }

  private func textToBitmap(
    _ text: String,
    fontSize: CGFloat,
    isBold: Bool,
    fontFamily: String,
    isInverse: Bool = false,
    padding: Int = 0
  ) -> PrinterBitmap {
    let safeText = cpclText(text).isEmpty ? " " : cpclText(text)
    let font = printerFont(fontFamily: fontFamily, fontSize: fontSize, isBold: isBold)
    let attributes: [NSAttributedString.Key: Any] = [
      .font: font,
      .foregroundColor: isInverse ? UIColor.white : UIColor.black,
    ]
    let measuredSize = (safeText as NSString).size(withAttributes: attributes)
    let width = max(1, Int(ceil(measuredSize.width)) + padding * 2)
    let height = max(1, Int(ceil(measuredSize.height)) + padding * 2)

    return renderPrinterBitmap(width: width, height: height, isInverse: isInverse) {
      (safeText as NSString).draw(
        at: CGPoint(x: CGFloat(padding), y: CGFloat(padding)),
        withAttributes: attributes
      )
    }
  }

  private func longTextToBitmap(
    _ text: String,
    fontSize: CGFloat,
    isBold: Bool,
    fontFamily: String,
    maxLines: Int,
    maxWidth: Int
  ) -> PrinterBitmap {
    let safeText = cpclText(text).isEmpty ? " " : cpclText(text)
    let font = printerFont(fontFamily: fontFamily, fontSize: fontSize, isBold: isBold)
    let attributes: [NSAttributedString.Key: Any] = [
      .font: font,
      .foregroundColor: UIColor.black,
    ]
    let lines = wrapText(safeText, font: font, maxWidth: maxWidth, maxLines: maxLines)
    let lineHeight = ceil(font.lineHeight)
    let measuredWidth = lines.map { measureText($0, font: font) }.max() ?? 1
    let width = max(1, min(maxWidth, Int(ceil(measuredWidth))))
    let height = max(1, Int(lineHeight) * lines.count)

    return renderPrinterBitmap(width: width, height: height, isInverse: false) {
      lines.enumerated().forEach { index, line in
        (line as NSString).draw(
          at: CGPoint(x: 0, y: CGFloat(index) * lineHeight),
          withAttributes: attributes
        )
      }
    }
  }

  private func wrapText(_ text: String, font: UIFont, maxWidth: Int, maxLines: Int) -> [String] {
    var lines: [String] = []
    var current = ""

    for character in text {
      let next = current + String(character)
      if !current.isEmpty && measureText(next, font: font) > CGFloat(maxWidth) && lines.count < maxLines - 1 {
        lines.append(current)
        current = String(character)
      } else {
        current = next
      }
    }

    if !current.isEmpty || lines.isEmpty {
      lines.append(current)
    }

    return Array(lines.prefix(maxLines))
  }

  private func measureText(_ text: String, font: UIFont) -> CGFloat {
    (text as NSString).size(withAttributes: [.font: font]).width
  }

  private func renderPrinterBitmap(width: Int, height: Int, isInverse: Bool, draw: () -> Void) -> PrinterBitmap {
    let size = CGSize(width: width, height: height)
    let format = UIGraphicsImageRendererFormat()
    format.scale = 1
    format.opaque = true
    let renderer = UIGraphicsImageRenderer(size: size, format: format)
    let image = renderer.image { _ in
      (isInverse ? UIColor.black : UIColor.white).setFill()
      UIRectFill(CGRect(origin: .zero, size: size))
      draw()
    }
    return convertImageToPrinterBitmap(image, width: width, height: height)
  }

  private func convertImageToPrinterBitmap(_ image: UIImage, width: Int, height: Int) -> PrinterBitmap {
    guard let cgImage = image.cgImage else {
      return PrinterBitmap(width: width, height: height, hex: "", inkMinY: 0, inkMaxY: max(0, height - 1))
    }

    var rgba = [UInt8](repeating: 255, count: width * height * 4)
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    rgba.withUnsafeMutableBytes { buffer in
      guard let context = CGContext(
        data: buffer.baseAddress,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
      ) else {
        return
      }
      context.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
    }

    // CPCL EG 使用 1bit 单色图，阈值保持和 Android isBlack 一致。
    let inkBounds = bitmapInkVerticalBounds(rgba, width: width, height: height)
    return PrinterBitmap(width: width, height: height, hex: bitmapToHex(rgba, width: width, height: height), inkMinY: inkBounds.minY, inkMaxY: inkBounds.maxY)
  }

  private func bitmapCommand(_ x: Int, _ y: Int, _ bitmap: PrinterBitmap) -> String {
    "EG \(bitmap.widthBytes) \(bitmap.height) \(x) \(y) \(bitmap.hex)"
  }

  private func createQrCodeBitmap(_ value: String, size: Int) -> PrinterBitmap {
    guard
      let data = value.data(using: .utf8),
      let filter = CIFilter(name: "CIQRCodeGenerator")
    else {
      return textToBitmap(value, fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black")
    }

    filter.setValue(data, forKey: "inputMessage")
    filter.setValue("L", forKey: "inputCorrectionLevel")

    guard let outputImage = filter.outputImage else {
      return textToBitmap(value, fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black")
    }

    let context = CIContext(options: nil)
    guard let cgImage = context.createCGImage(outputImage, from: outputImage.extent) else {
      return textToBitmap(value, fontSize: fontSizeToPixels(7), isBold: true, fontFamily: "sans-serif-black")
    }

    let targetSize = max(1, size)
    // CoreImage 的 QR 默认带 quiet zone；裁到黑色边界后再缩放，贴近 Android MARGIN=0。
    let qrImage = UIImage(cgImage: cropQrQuietZone(cgImage) ?? cgImage)
    return renderPrinterBitmap(width: targetSize, height: targetSize, isInverse: false) {
      qrImage.draw(in: CGRect(x: 0, y: 0, width: targetSize, height: targetSize))
    }
  }

  private func cropQrQuietZone(_ image: CGImage) -> CGImage? {
    let width = image.width
    let height = image.height
    guard width > 0, height > 0 else {
      return nil
    }

    var rgba = [UInt8](repeating: 255, count: width * height * 4)
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    rgba.withUnsafeMutableBytes { buffer in
      guard let context = CGContext(
        data: buffer.baseAddress,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
      ) else {
        return
      }
      context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
    }

    var minX = width
    var minY = height
    var maxX = -1
    var maxY = -1

    for y in 0..<height {
      for x in 0..<width {
        let offset = (y * width + x) * 4
        if isBlackPixel(rgba, offset: offset) {
          minX = min(minX, x)
          minY = min(minY, y)
          maxX = max(maxX, x)
          maxY = max(maxY, y)
        }
      }
    }

    guard maxX >= minX, maxY >= minY else {
      return nil
    }

    return image.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1))
  }

  private func createDashLineBitmap(width: Int, height: Int) -> PrinterBitmap {
    let safeWidth = max(1, width)
    let safeHeight = max(1, height)
    return renderPrinterBitmap(width: safeWidth, height: safeHeight, isInverse: false) {
      // 大折扣标签底部虚线按 Android 的 10px 实线、18px 步进绘制。
      let path = UIBezierPath()
      var x: CGFloat = 0
      while x < CGFloat(safeWidth) {
        path.move(to: CGPoint(x: x, y: CGFloat(safeHeight) / 2))
        path.addLine(to: CGPoint(x: min(x + 10, CGFloat(safeWidth)), y: CGFloat(safeHeight) / 2))
        x += 18
      }
      UIColor.black.setStroke()
      path.lineWidth = CGFloat(safeHeight)
      path.stroke()
    }
  }

  private func bitmapToHex(_ rgba: [UInt8], width: Int, height: Int) -> String {
    let widthBytes = (width + 7) / 8
    var hex = ""
    hex.reserveCapacity(widthBytes * height * 2)

    for y in 0..<height {
      for byteIndex in 0..<widthBytes {
        var value: UInt8 = 0
        for bit in 0..<8 {
          let x = byteIndex * 8 + bit
          let offset = (y * width + x) * 4
          if x < width && isBlackPixel(rgba, offset: offset) {
            value |= UInt8(1 << (7 - bit))
          }
        }
        hex += String(format: "%02X", value)
      }
    }

    return hex
  }

  private func bitmapInkVerticalBounds(_ rgba: [UInt8], width: Int, height: Int) -> (minY: Int, maxY: Int) {
    var minY = height
    var maxY = -1

    for y in 0..<height {
      for x in 0..<width {
        let offset = (y * width + x) * 4
        if isBlackPixel(rgba, offset: offset) {
          minY = min(minY, y)
          maxY = max(maxY, y)
        }
      }
    }

    // 空白图回退到整个位图，避免调用方对齐时拿到无效边界。
    if maxY < minY {
      return (0, max(0, height - 1))
    }

    return (minY, maxY)
  }

  private func isBlackPixel(_ rgba: [UInt8], offset: Int) -> Bool {
    guard offset + 3 < rgba.count else {
      return false
    }

    let alpha = Int(rgba[offset + 3])
    if alpha == 0 {
      return false
    }

    let luminance = (Int(rgba[offset]) * 299 + Int(rgba[offset + 1]) * 587 + Int(rgba[offset + 2]) * 114) / 1000
    return luminance < 200
  }

  private func encodeCommand(_ command: String, encoding: String?) throws -> Data {
    let normalizedEncoding = encoding?.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
    if normalizedEncoding == nil || normalizedEncoding == "UTF-8" || normalizedEncoding == "UTF8" {
      return Data(command.utf8)
    }

    if normalizedEncoding == "GB18030" || normalizedEncoding == "GBK" || normalizedEncoding == "GB2312" {
      let cfEncoding = CFStringConvertIANACharSetNameToEncoding("GB18030" as CFString)
      if cfEncoding != kCFStringEncodingInvalidId {
        let nsEncoding = CFStringConvertEncodingToNSStringEncoding(cfEncoding)
        let stringEncoding = String.Encoding(rawValue: nsEncoding)
        if let data = command.data(using: stringEncoding) {
          return data
        }
      }
    }

    throw NSError(
      domain: "HbPrinterModule",
      code: 1,
      userInfo: [NSLocalizedDescriptionKey: "Unsupported printer command encoding: \(encoding ?? "")"]
    )
  }

  private func rejectUnsupportedLabelPrint(_ reject: @escaping RCTPromiseRejectBlock) {
    // iOS 先只支持原始命令写入；Android 位图标签渲染未复刻时必须明确失败。
    reject("IOS_LABEL_PRINT_UNSUPPORTED", "iOS label bitmap printing is not supported yet.", nil)
  }

  private func resolve(_ resolve: RCTPromiseResolveBlock?, _ value: Any?) {
    DispatchQueue.main.async {
      resolve?(value)
    }
  }

  private func reject(_ reject: RCTPromiseRejectBlock?, _ code: String, _ message: String) {
    DispatchQueue.main.async {
      reject?(code, message, nil)
    }
  }
}
