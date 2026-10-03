package expo.modules.hbprinter

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothSocket
import android.bluetooth.BluetoothStatusCodes
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanResult
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.location.LocationManager
import android.os.Build
import expo.modules.kotlin.Promise
import expo.modules.kotlin.exception.CodedException
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import java.nio.charset.Charset
import java.util.LinkedHashMap
import java.util.Locale
import java.util.UUID
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

private const val BLE_PREFIX = "ble:"
private const val SPP_PREFIX = "spp:"
private const val MAX_REMEMBERED_OPERATION_IDS = 512
private const val BLE_CHUNK_SIZE = 20
private const val ACL_DISCONNECT_SETTLE_MS = 250L
private val SPP_UUID: UUID =
  UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")

private class PrinterException(
  code: String,
  message: String,
  cause: Throwable? = null,
) : CodedException(code, message, cause)

private enum class TransportKind(val tokenPrefix: String) {
  BLE(BLE_PREFIX),
  SPP(SPP_PREFIX),
}

private enum class OperationKind(val wireValue: String) {
  PRINT("print"),
  DRAWER("drawer");

  companion object {
    fun fromWire(value: String): OperationKind? =
      entries.firstOrNull { it.wireValue == value }
  }
}

private data class PrinterToken(
  val transport: TransportKind,
  val address: String,
) {
  val value: String = transport.tokenPrefix + address
}

private data class DiscoveredPrinter(
  val token: PrinterToken,
  val name: String,
  val rssi: Int?,
  val isXprinter: Boolean,
) {
  fun payload(): Map<String, Any?> = mapOf(
    "id" to token.value,
    "name" to name,
    "rssi" to rssi,
    "isXprinter" to isXprinter,
  )
}

private data class PendingOperation(
  val id: String,
  val kind: OperationKind,
  val promise: Promise,
  val transport: TransportKind,
  val timeout: ScheduledFuture<*>,
)

/**
 * Android 打印桥只暴露统一 HbPrinter 合同。BLE GATT 与已配对 SPP 是两条显式通道；
 * 连接成功后通道即锁定，任何 operationId 都不会在另一条通道上重放。
 */
class HbPrinterModule : Module() {
  private val stateExecutor: ExecutorService = Executors.newSingleThreadExecutor()
  private val ioExecutor: ExecutorService = Executors.newCachedThreadPool()
  private val scheduler: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor()
  private val destroyed = AtomicBoolean(false)

  private var scanPromise: Promise? = null
  private var scanIncludeAll = true
  private var scanTimeout: ScheduledFuture<*>? = null
  private var scanReceiverRegistered = false
  private var linkReceiverRegistered = false
  private var pendingSppLinkLoss: ScheduledFuture<*>? = null
  private var pendingSppLinkLossAddress: String? = null
  private var pendingSppLinkLossSocket: BluetoothSocket? = null
  private val discoveredDevices = linkedMapOf<String, DiscoveredPrinter>()

  private var connectPromise: Promise? = null
  private var connectTimeout: ScheduledFuture<*>? = null
  private var connectingToken: PrinterToken? = null
  private var connectionState = "disconnected"

  private var connectedTransport: TransportKind? = null
  private var connectedToken: PrinterToken? = null
  private var bluetoothGatt: BluetoothGatt? = null
  private var bleWriteCharacteristic: BluetoothGattCharacteristic? = null
  private var bleWriteType: Int? = null
  private var sppSocket: BluetoothSocket? = null
  private var connectingSppSocket: BluetoothSocket? = null

  private var pendingOperation: PendingOperation? = null
  private val pendingBleChunks = ArrayDeque<ByteArray>()
  private val operationTransportById = object :
    LinkedHashMap<String, TransportKind>(MAX_REMEMBERED_OPERATION_IDS + 1, 0.75f, false) {
    override fun removeEldestEntry(
      eldest: MutableMap.MutableEntry<String, TransportKind>?,
    ): Boolean = size > MAX_REMEMBERED_OPERATION_IDS
  }

  private val bluetoothAdapter: BluetoothAdapter?
    get() {
      val context = appContext.reactContext?.applicationContext ?: return null
      return (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
    }

  private val bleScanCallback = object : ScanCallback() {
    @SuppressLint("MissingPermission")
    override fun onScanResult(callbackType: Int, result: ScanResult) {
      postState {
        if (scanPromise == null) return@postState
        val device = result.device ?: return@postState
        addDiscoveredDevice(
          device = device,
          transport = TransportKind.BLE,
          rssi = result.rssi,
          advertisedName = result.scanRecord?.deviceName,
        )
      }
    }

    override fun onBatchScanResults(results: MutableList<ScanResult>) {
      results.forEach { onScanResult(0, it) }
    }

    override fun onScanFailed(errorCode: Int) {
      postState {
        failScan(
          "PRINTER_BLE_SCAN_FAILED",
          "BLE 扫描失败，系统错误码：$errorCode。",
        )
      }
    }
  }

  private val classicScanReceiver = object : BroadcastReceiver() {
    @SuppressLint("MissingPermission")
    override fun onReceive(context: Context?, intent: Intent?) {
      postState {
        if (scanPromise == null) return@postState
        when (intent?.action) {
          BluetoothDevice.ACTION_FOUND -> {
            val device = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
              intent.getParcelableExtra(
                BluetoothDevice.EXTRA_DEVICE,
                BluetoothDevice::class.java,
              )
            } else {
              @Suppress("DEPRECATION")
              intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
            } ?: return@postState
            val rssi = intent.getShortExtra(BluetoothDevice.EXTRA_RSSI, Short.MIN_VALUE)
              .takeUnless { it == Short.MIN_VALUE }
              ?.toInt()
            addDiscoveredDevice(device, TransportKind.SPP, rssi, null)
          }

          BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
            // BLE 仍按调用方时限继续；统一由 scanTimeout 收口两条扫描通道。
          }
        }
      }
    }
  }

  /**
   * SPP 的 BluetoothSocket.isConnected 只反映本地状态，打印机关机/走出范围后仍是 true；
   * 不监听 ACL 断开，状态会一直停在 ready，下一张小票就写进死连接并变成 unknown。
   * 这里把链路断开和蓝牙关闭同步成 disconnected，下一次打印前的 connect() 才会真正重连。
   */
  private val linkStateReceiver = object : BroadcastReceiver() {
    override fun onReceive(context: Context?, intent: Intent?) {
      when (intent?.action) {
        BluetoothDevice.ACTION_ACL_DISCONNECTED -> {
          val address = aclAddress(intent) ?: return
          postState { scheduleSppLinkLoss(address) }
        }

        BluetoothDevice.ACTION_ACL_CONNECTED -> {
          val address = aclAddress(intent) ?: return
          postState { cancelPendingSppLinkLoss(address) }
        }

        BluetoothAdapter.ACTION_STATE_CHANGED -> {
          val state = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, BluetoothAdapter.ERROR)
          if (state == BluetoothAdapter.STATE_TURNING_OFF || state == BluetoothAdapter.STATE_OFF) {
            postState { handleBluetoothTurnedOff() }
          }
        }
      }
    }
  }

  private val gattCallback = object : BluetoothGattCallback() {
    override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
      postState {
        val expected = bluetoothGatt === gatt
        if (!expected) {
          gatt.close()
          return@postState
        }
        if (status == BluetoothGatt.GATT_SUCCESS && newState == BluetoothProfile.STATE_CONNECTED) {
          if (!gatt.discoverServices()) {
            failConnect(
              "PRINTER_SERVICE_DISCOVERY_FAILED",
              "无法启动 BLE 服务发现。",
            )
          }
          return@postState
        }
        if (newState == BluetoothProfile.STATE_DISCONNECTED || status != BluetoothGatt.GATT_SUCCESS) {
          val wasConnecting = connectPromise != null
          if (wasConnecting) {
            failConnect(
              "PRINTER_CONNECT_FAILED",
              "BLE 打印机连接失败，GATT 状态：$status。",
            )
          } else {
            finishPendingOperation(
              state = "unknown",
              message = "BLE 打印机在操作期间断开，无法确认结果。",
            )
            clearConnectedTransport()
            connectionState = "disconnected"
            emitStatus("disconnected")
          }
        }
      }
    }

    override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
      postState {
        if (bluetoothGatt !== gatt || connectPromise == null) return@postState
        if (status != BluetoothGatt.GATT_SUCCESS) {
          failConnect(
            "PRINTER_SERVICE_DISCOVERY_FAILED",
            "BLE 服务发现失败，GATT 状态：$status。",
          )
          return@postState
        }
        val characteristic = findWritableCharacteristic(gatt.services)
        if (characteristic == null) {
          failConnect(
            "PRINTER_NO_WRITABLE_CHARACTERISTIC",
            "打印机没有公开可写 BLE characteristic。",
          )
          return@postState
        }
        bleWriteCharacteristic = characteristic
        bleWriteType = if (
          characteristic.properties and BluetoothGattCharacteristic.PROPERTY_WRITE != 0
        ) {
          BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
        } else {
          BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
        }
        connectedTransport = TransportKind.BLE
        connectedToken = connectingToken
        connectingToken = null
        connectionState = "ready"
        connectTimeout?.cancel(false)
        connectTimeout = null
        val promise = connectPromise
        connectPromise = null
        emitStatus("ready")
        promise?.resolve(statusPayload())
      }
    }

    @Suppress("DEPRECATION")
    override fun onCharacteristicWrite(
      gatt: BluetoothGatt,
      characteristic: BluetoothGattCharacteristic,
      status: Int,
    ) {
      postState {
        val operation = pendingOperation ?: return@postState
        if (
          bluetoothGatt !== gatt ||
          bleWriteCharacteristic?.uuid != characteristic.uuid ||
          operation.transport != TransportKind.BLE
        ) {
          return@postState
        }
        if (status != BluetoothGatt.GATT_SUCCESS) {
          finishPendingOperation(
            state = "unknown",
            message = "BLE 写入回调失败，无法确认打印或开箱结果。",
          )
          return@postState
        }
        sendNextBleChunk()
      }
    }
  }

  override fun definition() = ModuleDefinition {
    Name("HbPrinter")
    Events("printerStatus", "printerOperation", "printerError")

    OnDestroy {
      if (!destroyed.compareAndSet(false, true)) return@OnDestroy
      try {
        stateExecutor.execute {
          cancelScan(
            "PRINTER_MODULE_DESTROYED",
            "打印模块已卸载，扫描未完成。",
          )
          rejectConnect(
            "PRINTER_MODULE_DESTROYED",
            "打印模块已卸载，连接未完成。",
          )
          finishPendingOperation(
            state = "unknown",
            message = "打印模块已卸载，无法确认打印或开箱结果。",
          )
          clearConnectedTransport()
          unregisterLinkStateReceiver()
          scheduler.shutdownNow()
          ioExecutor.shutdownNow()
          stateExecutor.shutdown()
        }
      } catch (_: RejectedExecutionException) {
      }
    }

    AsyncFunction("getStatus") { promise: Promise ->
      postState(promise) { promise.resolve(statusPayload()) }
    }

    AsyncFunction("scan") { durationMs: Int, includeAll: Boolean, promise: Promise ->
      postState(promise) { startScan(durationMs, includeAll, promise) }
    }

    AsyncFunction("connect") { peripheralId: String, timeoutMs: Int, promise: Promise ->
      postState(promise) { startConnect(peripheralId, timeoutMs, promise) }
    }

    AsyncFunction("disconnect") { promise: Promise ->
      postState(promise) {
        disconnectInternal("requested")
        promise.resolve(statusPayload())
      }
    }

    AsyncFunction("write") {
        operationId: String,
        bytes: List<Int>,
        timeoutMs: Int,
        kind: String,
        promise: Promise,
      ->
      postState(promise) {
        val operationKind = OperationKind.fromWire(kind)
        if (operationKind == null) {
          promise.reject(
            PrinterException("PRINTER_INVALID_OPERATION", "未知打印机操作类型。"),
          )
          return@postState
        }
        val data = toByteArray(bytes, promise) ?: return@postState
        startWrite(operationId, data, timeoutMs, operationKind, promise)
      }
    }

    AsyncFunction("printText") {
        operationId: String,
        text: String,
        encoding: String,
        appendLineFeed: Boolean,
        cutAfterPrint: Boolean,
        timeoutMs: Int,
        promise: Promise,
      ->
      postState(promise) {
        val encoded = try {
          encode(text, encoding)
        } catch (error: PrinterException) {
          promise.reject(error)
          return@postState
        }
        val suffix = buildList<Byte> {
          if (appendLineFeed) add(0x0A.toByte())
          if (cutAfterPrint) addAll(listOf(0x1D, 0x56, 0x00).map(Int::toByte))
        }.toByteArray()
        startWrite(
          operationId,
          encoded + suffix,
          timeoutMs,
          OperationKind.PRINT,
          promise,
        )
      }
    }

    AsyncFunction("openCashDrawer") {
        operationId: String,
        pin: Int,
        onTime: Int,
        offTime: Int,
        timeoutMs: Int,
        promise: Promise,
      ->
      postState(promise) {
        if (pin != 0 && pin != 1) {
          promise.reject(
            PrinterException(
              "PRINTER_DRAWER_PIN_INVALID",
              "钱箱针脚只能是 0 或 1。",
            ),
          )
          return@postState
        }
        val command = byteArrayOf(
          0x1B,
          0x70,
          pin.toByte(),
          onTime.coerceIn(1, 255).toByte(),
          offTime.coerceIn(1, 255).toByte(),
        )
        // 钱箱脉冲没有物理开箱 ACK；无确认时只返回 unknown，禁止自动重放。
        startWrite(
          operationId,
          command,
          timeoutMs,
          OperationKind.DRAWER,
          promise,
        )
      }
    }
  }

  @SuppressLint("MissingPermission")
  private fun startScan(durationMs: Int, includeAll: Boolean, promise: Promise) {
    val adapter = requireBluetoothReady(promise) ?: return
    if (!isScanLocationServiceReady()) {
      // Android 10/11 定位总开关关闭时 BLE 扫描不报错、只返回空结果，这里提前明确拒绝。
      promise.reject(
        PrinterException(
          "PRINTER_BLUETOOTH_LOCATION_OFF",
          "定位服务已关闭；Android 10/11 需开启定位才能扫描蓝牙设备。",
        ),
      )
      return
    }
    if (scanPromise != null) {
      promise.reject(
        PrinterException("PRINTER_SCAN_IN_PROGRESS", "蓝牙打印机扫描正在进行。"),
      )
      return
    }
    discoveredDevices.clear()
    scanPromise = promise
    scanIncludeAll = includeAll

    adapter.bondedDevices.orEmpty().forEach { device ->
      if (
        device.type == BluetoothDevice.DEVICE_TYPE_CLASSIC ||
        device.type == BluetoothDevice.DEVICE_TYPE_DUAL ||
        device.type == BluetoothDevice.DEVICE_TYPE_UNKNOWN
      ) {
        addDiscoveredDevice(device, TransportKind.SPP, null, null)
      }
    }

    val context = appContext.reactContext?.applicationContext
    if (context == null) {
      failScan("PRINTER_CONTEXT_UNAVAILABLE", "Android 应用上下文不可用。")
      return
    }
    try {
      val filter = IntentFilter().apply {
        addAction(BluetoothDevice.ACTION_FOUND)
        addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
      }
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        context.registerReceiver(
          classicScanReceiver,
          filter,
          Context.RECEIVER_NOT_EXPORTED,
        )
      } else {
        @Suppress("DEPRECATION")
        context.registerReceiver(classicScanReceiver, filter)
      }
      scanReceiverRegistered = true
      adapter.bluetoothLeScanner?.startScan(bleScanCallback)
      if (adapter.isDiscovering) adapter.cancelDiscovery()
      adapter.startDiscovery()
    } catch (error: SecurityException) {
      failScan(
        "PRINTER_BLUETOOTH_PERMISSION_REQUIRED",
        "缺少 Android 蓝牙扫描权限。",
        error,
      )
      return
    } catch (error: Exception) {
      failScan("PRINTER_SCAN_FAILED", "无法启动蓝牙打印机扫描。", error)
      return
    }

    val boundedDuration = durationMs.coerceIn(1_500, 30_000)
    scanTimeout = scheduler.schedule(
      { postState { finishScan() } },
      boundedDuration.toLong(),
      TimeUnit.MILLISECONDS,
    )
  }

  @SuppressLint("MissingPermission")
  private fun addDiscoveredDevice(
    device: BluetoothDevice,
    transport: TransportKind,
    rssi: Int?,
    advertisedName: String?,
  ) {
    val address = device.address?.uppercase(Locale.US) ?: return
    if (!BluetoothAdapter.checkBluetoothAddress(address)) return
    val name = sequenceOf(advertisedName, runCatching { device.name }.getOrNull())
      .mapNotNull { it?.trim()?.takeIf(String::isNotEmpty) }
      .firstOrNull()
      ?: "Bluetooth Printer"
    val token = PrinterToken(transport, address)
    discoveredDevices[token.value] = DiscoveredPrinter(
      token = token,
      name = name,
      rssi = rssi,
      isXprinter = isXprinter(name),
    )
  }

  @SuppressLint("MissingPermission")
  private fun finishScan() {
    val promise = scanPromise ?: return
    stopScanTransport()
    scanPromise = null
    val devices = discoveredDevices.values
      .asSequence()
      .filter { scanIncludeAll || it.isXprinter }
      .sortedWith(compareBy<DiscoveredPrinter> { it.name }.thenBy { it.token.value })
      .map(DiscoveredPrinter::payload)
      .toList()
    promise.resolve(devices)
  }

  private fun failScan(code: String, message: String, cause: Throwable? = null) {
    val promise = scanPromise ?: return
    stopScanTransport()
    scanPromise = null
    sendEvent("printerError", mapOf("code" to code, "message" to message))
    promise.reject(PrinterException(code, message, cause))
  }

  private fun cancelScan(code: String, message: String) {
    failScan(code, message)
  }

  @SuppressLint("MissingPermission")
  private fun stopScanTransport() {
    scanTimeout?.cancel(false)
    scanTimeout = null
    val adapter = bluetoothAdapter
    runCatching { adapter?.bluetoothLeScanner?.stopScan(bleScanCallback) }
    runCatching { if (adapter?.isDiscovering == true) adapter.cancelDiscovery() }
    val context = appContext.reactContext?.applicationContext
    if (scanReceiverRegistered && context != null) {
      runCatching { context.unregisterReceiver(classicScanReceiver) }
    }
    scanReceiverRegistered = false
  }

  @SuppressLint("MissingPermission")
  private fun startConnect(peripheralId: String, timeoutMs: Int, promise: Promise) {
    val adapter = requireBluetoothReady(promise) ?: return
    if (connectPromise != null) {
      promise.reject(
        PrinterException(
          "PRINTER_CONNECT_IN_PROGRESS",
          "蓝牙打印机连接正在进行。",
        ),
      )
      return
    }
    val token = parseToken(peripheralId)
    if (token == null) {
      promise.reject(
        PrinterException(
          "PRINTER_INVALID_ID",
          "Android 打印机标识必须是 ble:<address> 或 spp:<address>。",
        ),
      )
      return
    }
    if (connectionState == "ready" && connectedToken == token) {
      promise.resolve(statusPayload())
      return
    }

    disconnectInternal("switch-printer")
    val device = try {
      adapter.getRemoteDevice(token.address)
    } catch (error: IllegalArgumentException) {
      promise.reject(
        PrinterException("PRINTER_INVALID_ID", "Android 打印机地址无效。", error),
      )
      return
    }

    if (token.transport == TransportKind.SPP && device.bondState != BluetoothDevice.BOND_BONDED) {
      promise.reject(
        PrinterException(
          "PRINTER_SPP_PAIRING_REQUIRED",
          "SPP 打印机必须先在 Android 系统设置中完成配对。",
        ),
      )
      return
    }

    ensureLinkStateReceiverRegistered()
    connectPromise = promise
    connectingToken = token
    connectionState = "connecting"
    emitStatus("connecting")
    val boundedTimeout = timeoutMs.coerceIn(3_000, 30_000)
    connectTimeout = scheduler.schedule(
      {
        postState {
          failConnect(
            "PRINTER_CONNECT_TIMEOUT",
            "连接蓝牙打印机超时。",
          )
        }
      },
      boundedTimeout.toLong(),
      TimeUnit.MILLISECONDS,
    )

    if (adapter.isDiscovering) adapter.cancelDiscovery()
    when (token.transport) {
      TransportKind.BLE -> {
        val context = appContext.reactContext?.applicationContext
        if (context == null) {
          failConnect("PRINTER_CONTEXT_UNAVAILABLE", "Android 应用上下文不可用。")
          return
        }
        bluetoothGatt = device.connectGatt(
          context,
          false,
          gattCallback,
          BluetoothDevice.TRANSPORT_LE,
        )
        if (bluetoothGatt == null) {
          failConnect("PRINTER_CONNECT_FAILED", "无法创建 BLE GATT 连接。")
        }
      }

      TransportKind.SPP -> startSppConnect(device, token)
    }
  }

  @SuppressLint("MissingPermission")
  private fun startSppConnect(device: BluetoothDevice, token: PrinterToken) {
    val socket = try {
      device.createRfcommSocketToServiceRecord(SPP_UUID)
    } catch (error: Exception) {
      failConnect("PRINTER_CONNECT_FAILED", "无法创建 SPP RFCOMM 连接。", error)
      return
    }
    connectingSppSocket = socket
    ioExecutor.execute {
      try {
        socket.connect()
        postState {
          if (connectPromise == null || connectingToken != token || connectingSppSocket !== socket) {
            runCatching { socket.close() }
            return@postState
          }
          connectingSppSocket = null
          sppSocket = socket
          connectedTransport = TransportKind.SPP
          connectedToken = token
          connectingToken = null
          connectionState = "ready"
          connectTimeout?.cancel(false)
          connectTimeout = null
          val promise = connectPromise
          connectPromise = null
          emitStatus("ready")
          promise?.resolve(statusPayload())
        }
      } catch (error: Exception) {
        postState {
          if (connectingSppSocket === socket && connectPromise != null) {
            failConnect(
              "PRINTER_CONNECT_FAILED",
              "无法连接已配对的 SPP 打印机。",
              error,
            )
          } else {
            runCatching { socket.close() }
          }
        }
      }
    }
  }

  private fun ensureLinkStateReceiverRegistered() {
    if (linkReceiverRegistered) return
    val context = appContext.reactContext?.applicationContext ?: return
    val filter = IntentFilter().apply {
      addAction(BluetoothDevice.ACTION_ACL_DISCONNECTED)
      addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
      addAction(BluetoothAdapter.ACTION_STATE_CHANGED)
    }
    // 两个都是系统受保护广播，NOT_EXPORTED 仍可收到，且不接受其他 App 伪造。
    runCatching {
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        context.registerReceiver(linkStateReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
      } else {
        @Suppress("DEPRECATION")
        context.registerReceiver(linkStateReceiver, filter)
      }
      linkReceiverRegistered = true
    }
  }

  private fun unregisterLinkStateReceiver() {
    if (!linkReceiverRegistered) return
    val context = appContext.reactContext?.applicationContext
    if (context != null) {
      runCatching { context.unregisterReceiver(linkStateReceiver) }
    }
    linkReceiverRegistered = false
  }

  private fun aclAddress(intent: Intent): String? {
    val device = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
    } else {
      @Suppress("DEPRECATION")
      intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
    }
    return device?.address?.uppercase(Locale.US)
  }

  /**
   * 只处理已就绪的 SPP 连接：BLE 由 GATT 回调权威判定断开，避免双模打印机另一条
   * ACL 的断开误伤当前 BLE 会话；连接中的 SPP 由 socket.connect 自身失败收口。
   * 与移动端一致先等 250ms：期间同设备 ACL_CONNECTED 到达就取消，并在到期时核对 socket
   * 身份，避免重连刚完成时迟到的旧断开广播把新连接清掉。
   */
  private fun scheduleSppLinkLoss(address: String) {
    if (!isReadySppConnection(address)) return
    val socket = sppSocket ?: return
    pendingSppLinkLoss?.cancel(false)
    pendingSppLinkLossAddress = address
    pendingSppLinkLossSocket = socket
    pendingSppLinkLoss = try {
      scheduler.schedule(
        { postState { handleSppLinkLost(address, socket) } },
        ACL_DISCONNECT_SETTLE_MS,
        TimeUnit.MILLISECONDS,
      )
    } catch (_: RejectedExecutionException) {
      null
    }
  }

  private fun cancelPendingSppLinkLoss(address: String) {
    if (pendingSppLinkLossAddress != address) return
    pendingSppLinkLoss?.cancel(false)
    clearPendingSppLinkLoss()
  }

  private fun clearPendingSppLinkLoss() {
    pendingSppLinkLoss = null
    pendingSppLinkLossAddress = null
    pendingSppLinkLossSocket = null
  }

  private fun isReadySppConnection(address: String): Boolean =
    connectedTransport == TransportKind.SPP &&
      connectionState == "ready" &&
      connectedToken?.address == address

  private fun handleSppLinkLost(address: String, socket: BluetoothSocket) {
    // 已被 ACL_CONNECTED 取消或被更新的断开替换时，pending socket 不再是这一次的 socket。
    if (pendingSppLinkLossSocket !== socket) return
    clearPendingSppLinkLoss()
    if (!isReadySppConnection(address) || sppSocket !== socket) return
    finishPendingOperation(
      state = "unknown",
      message = "SPP 打印机在操作期间断开，无法确认打印或开箱结果。",
    )
    clearConnectedTransport()
    connectionState = "disconnected"
    emitStatus("spp-link-lost")
  }

  private fun handleBluetoothTurnedOff() {
    if (connectPromise != null) {
      failConnect(
        "PRINTER_BLUETOOTH_POWERED_OFF",
        "蓝牙已关闭，打印机连接未完成。",
      )
      return
    }
    if (connectedToken == null && connectionState != "ready") return
    finishPendingOperation(
      state = "unknown",
      message = "蓝牙已关闭，无法确认打印或开箱结果。",
    )
    clearConnectedTransport()
    connectionState = "disconnected"
    emitStatus("bluetooth-off")
  }

  private fun failConnect(code: String, message: String, cause: Throwable? = null) {
    if (connectPromise == null) return
    connectionState = "failed"
    emitStatus("connect-failed")
    rejectConnect(code, message, cause)
    clearConnectedTransport()
  }

  private fun rejectConnect(code: String, message: String, cause: Throwable? = null) {
    val promise = connectPromise ?: return
    connectPromise = null
    connectTimeout?.cancel(false)
    connectTimeout = null
    connectingToken = null
    runCatching { connectingSppSocket?.close() }
    connectingSppSocket = null
    sendEvent("printerError", mapOf("code" to code, "message" to message))
    promise.reject(PrinterException(code, message, cause))
  }

  private fun disconnectInternal(reason: String) {
    rejectConnect(
      "PRINTER_CONNECT_CANCELLED",
      "蓝牙打印机连接已取消。",
    )
    finishPendingOperation(
      state = "unknown",
      message = "打印机连接已取消或断开，无法确认打印或开箱结果。",
    )
    clearConnectedTransport()
    connectionState = "disconnected"
    emitStatus(reason)
  }

  private fun clearConnectedTransport() {
    val gatt = bluetoothGatt
    bluetoothGatt = null
    bleWriteCharacteristic = null
    bleWriteType = null
    runCatching { gatt?.disconnect() }
    runCatching { gatt?.close() }
    runCatching { sppSocket?.close() }
    sppSocket = null
    runCatching { connectingSppSocket?.close() }
    connectingSppSocket = null
    connectedTransport = null
    connectedToken = null
    pendingBleChunks.clear()
  }

  private fun startWrite(
    operationId: String,
    data: ByteArray,
    timeoutMs: Int,
    kind: OperationKind,
    promise: Promise,
  ) {
    val normalizedId = operationId.trim()
    if (normalizedId.isEmpty()) {
      promise.reject(
        PrinterException(
          "PRINTER_OPERATION_ID_REQUIRED",
          "打印操作必须带有可审计的 operationId。",
        ),
      )
      return
    }
    if (pendingOperation != null) {
      promise.reject(
        PrinterException(
          "PRINTER_OPERATION_IN_PROGRESS",
          "已有打印机操作进行中。",
        ),
      )
      return
    }
    if (operationTransportById.containsKey(normalizedId)) {
      promise.reject(
        PrinterException(
          "PRINTER_OPERATION_ALREADY_USED",
          "该 operationId 已执行过；为避免重复打印或开箱，原生层拒绝再次发送。",
        ),
      )
      return
    }
    val transport = connectedTransport
    if (connectionState != "ready" || connectedToken == null || transport == null) {
      promise.reject(
        PrinterException("PRINTER_NOT_CONNECTED", "未连接可写的蓝牙打印机。"),
      )
      return
    }

    operationTransportById[normalizedId] = transport
    if (data.isEmpty()) {
      promise.resolve(
        resultPayload(normalizedId, "failed", "空打印命令被拒绝。"),
      )
      return
    }

    val boundedTimeout = timeoutMs.coerceIn(3_000, 120_000)
    lateinit var timeout: ScheduledFuture<*>
    timeout = scheduler.schedule(
      {
        postState {
          val operation = pendingOperation
          if (operation?.id != normalizedId) return@postState
          // 超时即停止后续 chunk，并断开当前通道；任何字节都不会被自动重发。
          finishPendingOperation(
            state = "unknown",
            message = "打印机写入超时，无法确认打印或开箱结果。",
          )
          clearConnectedTransport()
          connectionState = "disconnected"
          emitStatus("operation-timeout")
        }
      },
      boundedTimeout.toLong(),
      TimeUnit.MILLISECONDS,
    )
    pendingOperation = PendingOperation(
      id = normalizedId,
      kind = kind,
      promise = promise,
      transport = transport,
      timeout = timeout,
    )

    when (transport) {
      TransportKind.BLE -> startBleWrite(data)
      TransportKind.SPP -> startSppWrite(data, normalizedId)
    }
  }

  private fun startBleWrite(data: ByteArray) {
    pendingBleChunks.clear()
    data.asList()
      .chunked(BLE_CHUNK_SIZE)
      .mapTo(pendingBleChunks) { chunk -> chunk.toByteArray() }
    sendNextBleChunk()
  }

  @SuppressLint("MissingPermission")
  private fun sendNextBleChunk() {
    val operation = pendingOperation ?: return
    val gatt = bluetoothGatt
    val characteristic = bleWriteCharacteristic
    val writeType = bleWriteType
    if (
      operation.transport != TransportKind.BLE ||
      connectedTransport != TransportKind.BLE ||
      gatt == null ||
      characteristic == null ||
      writeType == null
    ) {
      finishPendingOperation(
        state = "unknown",
        message = "BLE 连接在写入期间不可用，无法确认结果。",
      )
      return
    }
    if (pendingBleChunks.isEmpty()) {
      if (writeType == BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT) {
        finishPendingOperation(
          state = "completed",
          message = "BLE 命令已收到 GATT 写入确认。",
        )
      } else {
        finishPendingOperation(
          state = "unknown",
          message = "BLE 无响应写入已排队，但打印机没有返回确认。",
        )
      }
      return
    }

    if (writeType == BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE) {
      while (pendingBleChunks.isNotEmpty() && pendingOperation != null) {
        val chunk = pendingBleChunks.removeFirst()
        if (!enqueueGattWrite(gatt, characteristic, chunk, writeType)) {
          finishPendingOperation(
            state = "unknown",
            message = "BLE 无响应写入未被系统完整接受，无法确认结果。",
          )
          return
        }
      }
      if (pendingOperation != null) sendNextBleChunk()
      return
    }

    val chunk = pendingBleChunks.removeFirst()
    if (!enqueueGattWrite(gatt, characteristic, chunk, writeType)) {
      finishPendingOperation(
        state = "unknown",
        message = "BLE 写入未被系统接受，无法确认结果。",
      )
    }
  }

  @SuppressLint("MissingPermission")
  private fun enqueueGattWrite(
    gatt: BluetoothGatt,
    characteristic: BluetoothGattCharacteristic,
    bytes: ByteArray,
    writeType: Int,
  ): Boolean {
    return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      gatt.writeCharacteristic(characteristic, bytes, writeType) ==
        BluetoothStatusCodes.SUCCESS
    } else {
      @Suppress("DEPRECATION")
      characteristic.writeType = writeType
      @Suppress("DEPRECATION")
      characteristic.value = bytes
      @Suppress("DEPRECATION")
      gatt.writeCharacteristic(characteristic)
    }
  }

  private fun startSppWrite(data: ByteArray, operationId: String) {
    val socket = sppSocket
    if (socket == null || !socket.isConnected) {
      finishPendingOperation(
        state = "unknown",
        message = "SPP 连接在写入前断开，无法确认结果。",
      )
      return
    }
    ioExecutor.execute {
      try {
        val output = socket.outputStream
        output.write(data)
        output.flush()
        postState {
          val operation = pendingOperation
          if (
            operation?.id != operationId ||
            operation.transport != TransportKind.SPP ||
            connectedTransport != TransportKind.SPP
          ) {
            return@postState
          }
          // RFCOMM flush 仅表示字节交给系统，ESC/POS 打印机没有业务 ACK。
          finishPendingOperation(
            state = "unknown",
            message = "SPP 命令已写入 RFCOMM，但打印机没有返回确认。",
          )
        }
      } catch (error: Exception) {
        postState {
          if (pendingOperation?.id == operationId) {
            finishPendingOperation(
              state = "unknown",
              message = "SPP 写入中断，无法确认打印或开箱结果。",
            )
            clearConnectedTransport()
            connectionState = "disconnected"
            emitStatus("spp-write-disconnected")
          }
        }
      }
    }
  }

  private fun finishPendingOperation(state: String, message: String?) {
    val operation = pendingOperation ?: return
    pendingOperation = null
    operation.timeout.cancel(false)
    pendingBleChunks.clear()
    val result = resultPayload(operation.id, state, message)
    operation.promise.resolve(result)
    sendEvent(
      "printerOperation",
      result + mapOf("kind" to operation.kind.wireValue),
    )
  }

  private fun findWritableCharacteristic(
    services: List<BluetoothGattService>?,
  ): BluetoothGattCharacteristic? {
    val characteristics = services.orEmpty().flatMap { it.characteristics.orEmpty() }
    return characteristics.firstOrNull {
      it.properties and BluetoothGattCharacteristic.PROPERTY_WRITE != 0
    } ?: characteristics.firstOrNull {
      it.properties and BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE != 0
    }
  }

  private fun parseToken(value: String): PrinterToken? {
    val normalized = value.trim()
    val transport = when {
      normalized.startsWith(BLE_PREFIX, ignoreCase = true) -> TransportKind.BLE
      normalized.startsWith(SPP_PREFIX, ignoreCase = true) -> TransportKind.SPP
      else -> return null
    }
    val address = normalized.substringAfter(':').uppercase(Locale.US)
    if (!BluetoothAdapter.checkBluetoothAddress(address)) return null
    return PrinterToken(transport, address)
  }

  private fun toByteArray(bytes: List<Int>, promise: Promise): ByteArray? {
    if (bytes.any { it !in 0..255 }) {
      promise.reject(
        PrinterException(
          "PRINTER_INVALID_BYTES",
          "打印字节必须位于 0 到 255。",
        ),
      )
      return null
    }
    return ByteArray(bytes.size) { bytes[it].toByte() }
  }

  private fun encode(text: String, encoding: String): ByteArray {
    val charset = when (encoding.lowercase(Locale.US)) {
      "utf8", "utf-8" -> Charsets.UTF_8
      "gb18030" -> Charset.forName("GB18030")
      "gbk", "gb2312" -> Charset.forName("GBK")
      else -> throw PrinterException(
        "PRINTER_ENCODING_UNSUPPORTED",
        "不支持的打印文本编码：$encoding。",
      )
    }
    return try {
      text.toByteArray(charset)
    } catch (error: Exception) {
      throw PrinterException(
        "PRINTER_ENCODING_FAILED",
        "打印文本编码失败。",
        error,
      )
    }
  }

  private fun isXprinter(name: String): Boolean {
    val normalized = name.trim().lowercase(Locale.US)
    return normalized == "printer001" ||
      normalized.contains("xprinter") ||
      normalized.contains("x-printer") ||
      normalized.contains("芯烨")
  }

  private fun requireBluetoothReady(promise: Promise): BluetoothAdapter? {
    val context = appContext.reactContext?.applicationContext
    val adapter = bluetoothAdapter
    if (context == null || adapter == null) {
      promise.reject(
        PrinterException(
          "PRINTER_BLUETOOTH_UNSUPPORTED",
          "此 Android 设备不支持蓝牙。",
        ),
      )
      return null
    }
    val missing = requiredPermissions().filter {
      context.checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED
    }
    if (missing.isNotEmpty()) {
      promise.reject(
        PrinterException(
          "PRINTER_BLUETOOTH_PERMISSION_REQUIRED",
          "缺少 Android 蓝牙权限：${missing.joinToString()}。",
        ),
      )
      return null
    }
    val enabled = try {
      adapter.isEnabled
    } catch (error: SecurityException) {
      promise.reject(
        PrinterException(
          "PRINTER_BLUETOOTH_PERMISSION_REQUIRED",
          "无法读取 Android 蓝牙状态。",
          error,
        ),
      )
      return null
    }
    if (!enabled) {
      promise.reject(
        PrinterException(
          "PRINTER_BLUETOOTH_POWERED_OFF",
          "蓝牙已关闭；请开启蓝牙后重试。",
        ),
      )
      return null
    }
    return adapter
  }

  /**
   * 仅 API 30 及以下需要定位服务；API 31+ 的 BLUETOOTH_SCAN 声明了 neverForLocation，不受定位开关影响。
   * 读不到 LocationManager 时放行，避免误拦截。
   */
  private fun isScanLocationServiceReady(): Boolean {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) return true
    val context = appContext.reactContext?.applicationContext ?: return true
    val locationManager =
      context.getSystemService(Context.LOCATION_SERVICE) as? LocationManager ?: return true
    return runCatching { locationManager.isLocationEnabled }.getOrDefault(true)
  }

  private fun requiredPermissions(): List<String> =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
      listOf(
        Manifest.permission.BLUETOOTH_SCAN,
        Manifest.permission.BLUETOOTH_CONNECT,
      )
    } else {
      listOf(Manifest.permission.ACCESS_FINE_LOCATION)
    }

  private fun statusPayload(): Map<String, Any?> {
    val adapter = bluetoothAdapter
    val context = appContext.reactContext?.applicationContext
    val hasPermission = context != null && requiredPermissions().all {
      context.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED
    }
    val enabled = if (adapter != null && hasPermission) {
      runCatching { adapter.isEnabled }.getOrDefault(false)
    } else {
      false
    }
    val writeMode = when {
      connectedTransport != TransportKind.BLE -> null
      bleWriteType == BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT -> "withResponse"
      bleWriteType == BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE -> "withoutResponse"
      else -> null
    }
    return mapOf(
      "supported" to (adapter != null),
      "enabled" to enabled,
      "connection" to connectionState,
      "peripheralId" to (connectedToken ?: connectingToken)?.value,
      "writeMode" to writeMode,
    )
  }

  private fun resultPayload(
    operationId: String,
    state: String,
    message: String?,
  ): Map<String, Any?> = mapOf(
    "operationId" to operationId,
    "state" to state,
    "message" to message,
  )

  private fun emitStatus(reason: String) {
    sendEvent("printerStatus", statusPayload() + mapOf("reason" to reason))
  }

  private fun postState(promise: Promise? = null, block: () -> Unit) {
    if (destroyed.get()) {
      promise?.reject(
        PrinterException("PRINTER_MODULE_DESTROYED", "打印模块已卸载。"),
      )
      return
    }
    try {
      stateExecutor.execute {
        if (destroyed.get()) {
          promise?.reject(
            PrinterException("PRINTER_MODULE_DESTROYED", "打印模块已卸载。"),
          )
        } else {
          block()
        }
      }
    } catch (error: RejectedExecutionException) {
      promise?.reject(
        PrinterException("PRINTER_MODULE_DESTROYED", "打印模块已卸载。", error),
      )
    }
  }
}
