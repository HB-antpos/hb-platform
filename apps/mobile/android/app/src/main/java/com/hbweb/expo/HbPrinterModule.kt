package com.hbweb.expo

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import com.facebook.react.bridge.Arguments
import com.facebook.react.bridge.Promise
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod
import com.facebook.react.bridge.ReadableMap
import com.facebook.react.modules.core.DeviceEventManagerModule
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.MultiFormatWriter
import java.nio.charset.Charset
import java.text.SimpleDateFormat
import java.util.Date
import java.util.EnumMap
import java.util.Locale
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.roundToInt

class HbPrinterModule(
  reactContext: ReactApplicationContext
) : ReactContextBaseJavaModule(reactContext) {
  private val appContext = reactContext.applicationContext
  private val bluetoothAdapter: BluetoothAdapter? by lazy {
    val manager = appContext.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
    manager?.adapter
  }
  private val handler = Handler(Looper.getMainLooper())
  private val connectionLock = Any()
  private val pairingLock = Any()
  private val printerUuid: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
  private val labelWidth = 570
  private val labelHeight = 400
  private val warehouseLabelHeight = 208

  // 当前唯一打印连接：经典蓝牙为 RFCOMM，BLE 为 GATT；清理时按对象身份比对，防止过期回调误清新连接。
  @Volatile
  private var connection: PrinterConnection? = null

  @Volatile
  private var connectedAddress: String? = null

  private var connectionGeneration = 0L
  private var statusReceiverRegistered = false
  @Volatile
  private var listenerCount = 0
  private var pendingAclDisconnect: Runnable? = null
  private var pendingAclDisconnectAddress: String? = null
  private var pendingPairingAddress: String? = null
  private var pendingPairingPromise: Promise? = null
  private var pendingPairingTimeout: Runnable? = null

  private val statusReceiver = object : BroadcastReceiver() {
    @SuppressLint("MissingPermission")
    override fun onReceive(context: Context?, intent: Intent?) {
      when (intent?.action) {
        BluetoothDevice.ACTION_ACL_DISCONNECTED -> {
          val device = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
          } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
          }
          val disconnectedAddress = device?.address
          // BLE 断线由 GATT 回调负责，这里只处理 RFCOMM，避免两条路径重复清理。
          val activeSocket = synchronized(connectionLock) {
            if (disconnectedAddress == connectedAddress) connection as? RfcommPrinterConnection else null
          }
          if (activeSocket != null && disconnectedAddress != null) {
            pendingAclDisconnect?.let(handler::removeCallbacks)
            val task = Runnable {
              pendingAclDisconnect = null
              pendingAclDisconnectAddress = null
              // 延后一轮等候同设备 ACL_CONNECTED；仍校验 socket 身份，避免过期广播清新连接。
              if (clearConnection(activeSocket)) {
                emitStatusChanged()
              }
            }
            pendingAclDisconnect = task
            pendingAclDisconnectAddress = disconnectedAddress
            handler.postDelayed(task, ACL_DISCONNECT_SETTLE_MS)
          }
        }
        BluetoothDevice.ACTION_ACL_CONNECTED -> {
          val device = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
          } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
          }
          if (device?.address == pendingAclDisconnectAddress) {
            pendingAclDisconnect?.let(handler::removeCallbacks)
            pendingAclDisconnect = null
            pendingAclDisconnectAddress = null
          }
        }
        BluetoothDevice.ACTION_BOND_STATE_CHANGED -> {
          val device = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
          } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
          }
          val address = device?.address
          if (address != null) {
            val bondState = intent.getIntExtra(BluetoothDevice.EXTRA_BOND_STATE, BluetoothDevice.ERROR)
            val previousBondState = intent.getIntExtra(
              BluetoothDevice.EXTRA_PREVIOUS_BOND_STATE,
              BluetoothDevice.ERROR,
            )
            when {
              bondState == BluetoothDevice.BOND_BONDED -> completePendingPairing(address)
              bondState == BluetoothDevice.BOND_NONE && previousBondState == BluetoothDevice.BOND_BONDING -> {
                failPendingPairing(
                  "PRINTER_PAIRING_REJECTED",
                  "Bluetooth printer pairing was cancelled, rejected, or failed.",
                  expectedAddress = address,
                )
              }
            }
          }
        }
        BluetoothAdapter.ACTION_STATE_CHANGED -> {
          val state = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, BluetoothAdapter.ERROR)
          if (state == BluetoothAdapter.STATE_OFF || state == BluetoothAdapter.STATE_TURNING_OFF) {
            failPendingPairing(
              "BLUETOOTH_DISABLED",
              "Bluetooth was turned off while pairing the printer.",
            )
            invalidateConnectionAttempt()
            clearConnection()
          }
          // STATE_ON 也必须通知 JS，才能在用户重新打开蓝牙后立即触发重连判断。
          emitStatusChanged()
        }
      }
    }
  }

  override fun getName(): String = "HbPrinterModule"

  override fun initialize() {
    super.initialize()
    registerStatusReceiver()
  }

  override fun invalidate() {
    failPendingPairing(
      "PRINTER_PAIRING_CANCELLED",
      "Bluetooth printer pairing was cancelled because the app is closing.",
    )
    unregisterStatusReceiver()
    invalidateConnectionAttempt()
    clearConnection()
    super.invalidate()
  }

  @SuppressLint("MissingPermission")
  @ReactMethod
  fun pair(address: String, promise: Promise) {
    val adapter = bluetoothAdapter
    if (adapter == null) {
      promise.reject("BLUETOOTH_UNSUPPORTED", "Bluetooth is not supported on this device.")
      return
    }

    if (!adapter.isEnabled) {
      promise.reject("BLUETOOTH_DISABLED", "Bluetooth is turned off.")
      return
    }

    val device = try {
      adapter.getRemoteDevice(address)
    } catch (error: IllegalArgumentException) {
      promise.reject("PRINTER_INVALID_ADDRESS", "The Bluetooth printer address is invalid.", error)
      return
    }

    // BLE 打印走 GATT 直连，无需系统绑定；旧 JS 仍可能调用 pair，直接放行。
    if (device.type == BluetoothDevice.DEVICE_TYPE_LE) {
      promise.resolve(true)
      return
    }

    if (device.bondState == BluetoothDevice.BOND_BONDED) {
      promise.resolve(true)
      return
    }

    val timeoutTask = Runnable {
      failPendingPairing(
        "PRINTER_PAIRING_TIMEOUT",
        "Timed out while waiting for the Android pairing confirmation.",
        expectedAddress = address,
      )
    }
    val accepted = synchronized(pairingLock) {
      if (pendingPairingPromise != null) {
        false
      } else {
        pendingPairingAddress = address
        pendingPairingPromise = promise
        pendingPairingTimeout = timeoutTask
        true
      }
    }
    if (!accepted) {
      promise.reject("PRINTER_PAIRING_IN_PROGRESS", "Another Bluetooth printer pairing is already in progress.")
      return
    }

    handler.postDelayed(timeoutTask, PAIRING_TIMEOUT_MS)
    try {
      // 已由系统开始配对时只等待广播；BOND_NONE 才主动打开 Android 配对流程。
      if (device.bondState != BluetoothDevice.BOND_BONDING && !device.createBond()) {
        failPendingPairing(
          "PRINTER_PAIRING_START_FAILED",
          "Android could not start Bluetooth printer pairing.",
          expectedAddress = address,
        )
      }
    } catch (error: Exception) {
      failPendingPairing(
        "PRINTER_PAIRING_START_FAILED",
        error.message ?: "Android could not start Bluetooth printer pairing.",
        error,
        expectedAddress = address,
      )
    }
  }

  @ReactMethod
  fun addListener(eventName: String) {
    if (eventName == STATUS_EVENT) {
      listenerCount += 1
    }
  }

  @ReactMethod
  fun removeListeners(count: Int) {
    listenerCount = (listenerCount - count).coerceAtLeast(0)
  }

  @ReactMethod
  fun getStatus(promise: Promise) {
    try {
      val adapter = bluetoothAdapter
      val map = Arguments.createMap()
      map.putBoolean("supported", adapter != null)
      map.putBoolean("enabled", adapter?.isEnabled == true)
      val current = synchronized(connectionLock) { connection to connectedAddress }
      map.putBoolean("connected", current.first?.isConnected == true)
      map.putString("address", current.second)
      promise.resolve(map)
    } catch (error: Exception) {
      promise.reject("STATUS_ERROR", error.message, error)
    }
  }

  @SuppressLint("MissingPermission")
  @ReactMethod
  fun scanPrinters(durationMs: Int, promise: Promise) {
    val adapter = bluetoothAdapter
    if (adapter == null) {
      promise.reject("BLUETOOTH_UNSUPPORTED", "Bluetooth is not supported on this device.")
      return
    }

    if (!adapter.isEnabled) {
      promise.reject("BLUETOOTH_DISABLED", "Bluetooth is turned off.")
      return
    }

    val devices = ConcurrentHashMap<String, WritablePrinterDevice>()
    adapter.bondedDevices?.forEach { device ->
      devices[device.address] = WritablePrinterDevice(
        name = device.name,
        address = device.address,
        bonded = true,
        connected = device.address == connectedAddress && connection?.isConnected == true,
        transport = bluetoothTransport(device),
        deviceClass = device.bluetoothClass?.deviceClass,
      )
    }

    val resolved = AtomicBoolean(false)
    var receiver: BroadcastReceiver? = null

    fun finishScan() {
      if (!resolved.compareAndSet(false, true)) {
        return
      }

      try {
        if (adapter.isDiscovering) {
          adapter.cancelDiscovery()
        }
      } catch (_: Exception) {
      }

      receiver?.let {
        try {
          appContext.unregisterReceiver(it)
        } catch (_: Exception) {
        }
      }

      val array = Arguments.createArray()
      devices.values.sortedBy { it.name ?: it.address }.forEach { printer ->
        val map = Arguments.createMap()
        map.putString("name", printer.name)
        map.putString("address", printer.address)
        map.putBoolean("bonded", printer.bonded)
        map.putBoolean("connected", printer.connected)
        map.putString("transport", printer.transport)
        if (printer.deviceClass == null) {
          map.putNull("deviceClass")
        } else {
          map.putInt("deviceClass", printer.deviceClass)
        }
        array.pushMap(map)
      }
      promise.resolve(array)
    }

    receiver = object : BroadcastReceiver() {
      override fun onReceive(context: Context?, intent: Intent?) {
        when (intent?.action) {
          BluetoothDevice.ACTION_FOUND -> {
            val device = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
              intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE, BluetoothDevice::class.java)
            } else {
              @Suppress("DEPRECATION")
              intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
            }

            if (device?.address != null) {
              devices[device.address] = WritablePrinterDevice(
                name = device.name,
                address = device.address,
                bonded = device.bondState == BluetoothDevice.BOND_BONDED,
                connected = device.address == connectedAddress && connection?.isConnected == true,
                transport = bluetoothTransport(device),
                deviceClass = device.bluetoothClass?.deviceClass,
              )
            }
          }
          BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> finishScan()
        }
      }
    }

    val filter = IntentFilter().apply {
      addAction(BluetoothDevice.ACTION_FOUND)
      addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
    }

    try {
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        // 蓝牙发现广播来自系统蓝牙组件；Android 13+ 需允许特权系统发送方。
        appContext.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
      } else {
        @Suppress("DEPRECATION")
        appContext.registerReceiver(receiver, filter)
      }

      if (adapter.isDiscovering) {
        adapter.cancelDiscovery()
      }
      adapter.startDiscovery()
      handler.postDelayed({ finishScan() }, durationMs.coerceAtLeast(1500).toLong())
    } catch (error: Exception) {
      try {
        appContext.unregisterReceiver(receiver)
      } catch (_: Exception) {
      }
      promise.reject("SCAN_ERROR", error.message, error)
    }
  }

  @ReactMethod
  fun connect(address: String, promise: Promise) {
    // 旧 JS 不传类型：按系统缓存的设备类型选择通道。
    connectWithTransport(address, null, promise)
  }

  /** JS 传入扫描/保存时记录的传输类型；重启后系统可能已忘记未绑定 BLE 地址的类型，不能只靠 device.type。 */
  @SuppressLint("MissingPermission")
  @ReactMethod
  fun connectWithTransport(address: String, transport: String?, promise: Promise) {
    val adapter = bluetoothAdapter
    if (adapter == null) {
      promise.reject("BLUETOOTH_UNSUPPORTED", "Bluetooth is not supported on this device.")
      return
    }

    if (!adapter.isEnabled) {
      promise.reject("BLUETOOTH_DISABLED", "Bluetooth is turned off.")
      return
    }

    val device = try {
      adapter.getRemoteDevice(address)
    } catch (error: IllegalArgumentException) {
      promise.reject("PRINTER_INVALID_ADDRESS", "The Bluetooth printer address is invalid.", error)
      return
    }

    val useBle = when (transport) {
      "ble" -> true
      "classic", "dual" -> false
      else -> device.type == BluetoothDevice.DEVICE_TYPE_LE
    }

    // RFCOMM connect() 会隐式触发配对并一直阻塞到 socket 超时；经典蓝牙必须先在系统蓝牙设置中配对。
    // BLE 走 GATT 直连，不要求绑定。
    if (!useBle && device.bondState != BluetoothDevice.BOND_BONDED) {
      promise.reject(
        "PRINTER_PAIRING_REQUIRED",
        "Pair the Bluetooth printer before starting the RFCOMM connection.",
      )
      return
    }

    Thread {
      var nextConnection: PrinterConnection? = null
      try {
        val attemptGeneration = beginConnectionAttempt()
        if (adapter.isDiscovering) {
          adapter.cancelDiscovery()
        }

        nextConnection = if (useBle) {
          BlePrinterConnection.open(appContext, device, BLE_CONNECT_TIMEOUT_MS) { lost ->
            if (clearConnection(lost)) {
              emitStatusChanged()
            }
          }
        } else {
          val socket = device.createRfcommSocketToServiceRecord(printerUuid)
          try {
            socket.connect()
          } catch (error: Exception) {
            closeQuietly(socket)
            throw error
          }
          RfcommPrinterConnection(socket)
        }
        val installed = synchronized(connectionLock) {
          if (connectionGeneration != attemptGeneration || adapter.isEnabled != true) {
            false
          } else {
            connection = nextConnection
            connectedAddress = address
            true
          }
        }
        if (!installed) {
          throw IllegalStateException("Bluetooth printer connection was cancelled.")
        }
        nextConnection = null
        emitStatusChanged()
        promise.resolve(true)
      } catch (error: Exception) {
        // 连接未写入共享状态时必须单独关闭，避免 RFCOMM/GATT 资源泄漏。
        nextConnection?.close()
        promise.reject("CONNECT_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun disconnect(promise: Promise) {
    try {
      invalidateConnectionAttempt()
      clearConnection()
      emitStatusChanged()
      promise.resolve(true)
    } catch (error: Exception) {
      promise.reject("DISCONNECT_ERROR", error.message, error)
    }
  }

  @ReactMethod
  fun print(command: String, encoding: String?, promise: Promise) {
    Thread {
      try {
        writePrinterCommand(command, encoding ?: "GB18030")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun printProductLabel(payload: ReadableMap, printType: String?, promise: Promise) {
    Thread {
      try {
        val startedAt = SystemClock.elapsedRealtime()
        val command = buildProductLabelCommand(payload, printType?.trim().orEmpty())
        val builtAt = SystemClock.elapsedRealtime()
        writePrinterCommand(command, "GB18030")
        val sentAt = SystemClock.elapsedRealtime()
        Log.i("HbPrinterPerf", "productLabel buildMs=${builtAt - startedAt} writeMs=${sentAt - builtAt} totalMs=${sentAt - startedAt}")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_PRODUCT_LABEL_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun printDiscountLabel(payload: ReadableMap, printType: String?, promise: Promise) {
    Thread {
      try {
        val startedAt = SystemClock.elapsedRealtime()
        val command = buildDiscountLabelCommand(payload, printType?.trim().orEmpty())
        val builtAt = SystemClock.elapsedRealtime()
        writePrinterCommand(command, "GB18030")
        val sentAt = SystemClock.elapsedRealtime()
        Log.i("HbPrinterPerf", "discountLabel buildMs=${builtAt - startedAt} writeMs=${sentAt - builtAt} totalMs=${sentAt - startedAt}")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_DISCOUNT_LABEL_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun printClearanceLabel(payload: ReadableMap, promise: Promise) {
    Thread {
      try {
        val command = buildClearanceLabelCommand(payload)
        writePrinterCommand(command, "GB18030")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_CLEARANCE_LABEL_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun printBigDiscountLabel(payload: ReadableMap, printType: String?, promise: Promise) {
    Thread {
      try {
        val command = buildBigDiscountLabelCommand(payload, printType?.trim().orEmpty())
        writePrinterCommand(command, "GB18030")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_BIG_DISCOUNT_LABEL_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun printWarehouseProductLabel(payload: ReadableMap, promise: Promise) {
    Thread {
      try {
        val command = buildWarehouseProductLabelCommand(payload)
        writePrinterCommand(command, "GB18030")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_WAREHOUSE_PRODUCT_LABEL_ERROR", error.message, error)
      }
    }.start()
  }

  @ReactMethod
  fun printWarehouseLocationLabel(payload: ReadableMap, promise: Promise) {
    Thread {
      try {
        val command = buildWarehouseLocationLabelCommand(payload)
        writePrinterCommand(command, "GB18030")
        promise.resolve(true)
      } catch (error: Exception) {
        promise.reject("PRINT_WAREHOUSE_LOCATION_LABEL_ERROR", error.message, error)
      }
    }.start()
  }

  private fun writePrinterCommand(command: String, encoding: String) {
    val activeConnection = synchronized(connectionLock) { connection }
    if (activeConnection == null || !activeConnection.isConnected) {
      throw IllegalStateException("No Bluetooth printer is connected.")
    }

    val charset = Charset.forName(encoding)
    try {
      activeConnection.write(command.toByteArray(charset))
    } catch (error: Exception) {
      // 数据是否已被打印机接收不可判定：只失效连接并保留原始异常，禁止自动重放。
      if (clearConnection(activeConnection)) {
        emitStatusChanged()
      }
      throw error
    }
  }

  private fun buildProductLabelCommand(payload: ReadableMap, printType: String = ""): String {
    val isSmall = printType.equals("small", ignoreCase = true)
    val w = if (isSmall) 472 else labelWidth
    val h = if (isSmall) 320 else labelHeight
    val productName = payload.getNullableString("productName")
    val itemNumber = payload.getNullableString("itemNumber")
    val supplierName = formatSupplierAbbreviation(payload.getNullableString("supplierName"))
    val barcode = payload.getNullableString("barcode")
    val retailPrice = payload.getNullableDouble("retailPrice")
    val discountRate = payload.getNullableDouble("discountRate") ?: 0.0
    val grade = payload.getNullableString("grade").trim().uppercase(Locale.US).firstOrNull()?.toString()

    val price = formatPriceParts(retailPrice)
    val priceIntegerBitmap = textToBitmap(price.integer, fontSizeToPixels(40f), true, "sans-serif-black")
    val priceDotBitmap = textToBitmap(".", fontSizeToPixels(20f), true, "sans-serif-black")
    val priceDecimalBitmap = textToBitmap(price.decimal, fontSizeToPixels(20f), true, "sans-serif-black")
    val priceCurrencyBitmap = textToBitmap("$", fontSizeToPixels(20f), false, "sans-serif-black")
    val itemBitmap = textToBitmap(itemNumber, fontSizeToPixels(8f), true, "sans-serif-black")
    val supplierBitmap = textToBitmap(supplierName, fontSizeToPixels(8f), true, "sans-serif-light", true, 2)
    val dateBitmap = textToBitmap(todayString(), fontSizeToPixels(8f), true, "sans-serif-black", true, 2)
    val nameMaxWidth = max(
      1,
      w - priceDecimalBitmap.width - priceDotBitmap.width - priceIntegerBitmap.width - priceCurrencyBitmap.width,
    )
    val nameBitmap = longTextToBitmap(productName, fontSizeToPixels(10f), false, "Arial", 2, nameMaxWidth)
    val discountBitmap = if (discountRate > 0) {
      textToBitmap("${(discountRate * 100).roundToInt().toString().padStart(2, '0')}%OFF", fontSizeToPixels(8f), true, "sans-serif-black", true, 2)
    } else {
      null
    }
    val gradeBitmap = grade?.let {
      textToBitmap(it, fontSizeToPixels(8f), true, "sans-serif-black", true, 4)
    }

    val startY = 30
    val startX = w - priceDecimalBitmap.width
    val commands = mutableListOf(
      "! 0 200 200 $h 1",
      "PAGE-WIDTH $w",
      bitmapCommand(5, 5, nameBitmap),
      bitmapCommand(5, 120, itemBitmap),
      bitmapCommand(5 + itemBitmap.width + 10, 118, supplierBitmap),
    )

    if (barcode.isNotBlank()) {
      val barcodeType = if (isValidEan13(barcode)) "EAN13" else "128"
      commands += "BARCODE-TEXT 7 0 5"
      commands += "BARCODE $barcodeType 1 2 30 5 145 $barcode"
    }

    if (discountBitmap != null) {
      commands += bitmapCommand(w - discountBitmap.width - dateBitmap.width - 20, 175, discountBitmap)
    }

    if (gradeBitmap != null) {
      commands += bitmapCommand(300, 175, gradeBitmap)
    }

    commands += bitmapCommand(startX, startY, priceDecimalBitmap)
    commands += bitmapCommand(startX - priceDotBitmap.width, startY + priceIntegerBitmap.height - 10, priceDotBitmap)
    commands += bitmapCommand(startX - priceDotBitmap.width - priceIntegerBitmap.width, startY, priceIntegerBitmap)
    commands += bitmapCommand(
      startX - priceDotBitmap.width - priceIntegerBitmap.width - priceCurrencyBitmap.width,
      startY,
      priceCurrencyBitmap,
    )
    commands += bitmapCommand(w - dateBitmap.width, 175, dateBitmap)
    commands += "PRINT"

    return commands.joinToString("\r\n", postfix = "\r\n")
  }

  // 只在普通商品标签里优先走合法 EAN13，其余情况回退到 CODE128。
  private fun isValidEan13(barcode: String): Boolean {
    if (barcode.length != 13 || barcode.any { !it.isDigit() }) {
      return false
    }

    val expectedCheckDigit = barcode
      .take(12)
      .mapIndexed { index, char ->
        val digit = char.digitToInt()
        if (index % 2 == 0) digit else digit * 3
      }
      .sum()
      .let { (10 - (it % 10)) % 10 }

    return barcode.last().digitToInt() == expectedCheckDigit
  }

  private fun buildDiscountLabelCommand(payload: ReadableMap, printType: String = ""): String {
    val isSmall = printType.equals("small", ignoreCase = true)
    val w = if (isSmall) 472 else labelWidth
    val h = if (isSmall) 320 else labelHeight
    val productName = payload.getNullableString("productName")
    val itemNumber = payload.getNullableString("itemNumber")
    val barcode = payload.getNullableString("barcode").ifBlank { itemNumber }
    val retailPrice = payload.getNullableDouble("retailPrice") ?: 0.0
    val discountRate = payload.getNullableDouble("discountRate") ?: 0.0
    val discountValue = discountRate * 100.0
    // 先按分舍入，避免 12.34 × 75% 的浮点尾差在两端显示成不同价格。
    val nowPrice = kotlin.math.round((retailPrice * (1.0 - discountRate)) * 100.0 + 1e-8) / 100.0
    val showOriginalPrice = retailPrice.isFinite() && nowPrice.isFinite() && retailPrice > nowPrice && nowPrice >= 0

    // 按实际位图宽度缩小文字，金额始终完整保留，不截断高位或小数。
    fun fittedText(value: String, size: Float, maxWidth: Int, maxHeight: Int = 64, inverse: Boolean = false, padding: Int = 0): Bitmap {
      var fittedSize = size
      var bitmap = textToBitmap(value, fontSizeToPixels(fittedSize), true, "sans-serif-black", inverse, padding)
      while ((bitmap.width > maxWidth || bitmap.height > maxHeight) && fittedSize > 1f) {
        fittedSize -= 0.5f
        bitmap = textToBitmap(value, fontSizeToPixels(fittedSize), true, "sans-serif-black", inverse, padding)
      }
      return bitmap
    }

    val columnGap = 10
    val infoX = 84
    val infoWidth = if (isSmall) 100 else 124
    val wasX = infoX + infoWidth + columnGap
    val wasWidth = if (isSmall) 76 else 96
    val nowX = if (showOriginalPrice) wasX + wasWidth + columnGap else wasX
    val nowWidth = w - 12 - nowX
    val nowPadding = 6
    val nowGroupGap = 6
    val nowLabelBitmap = fittedText("NOW", 6f, nowWidth, inverse = true)
    val nowPriceBitmap = fittedText("$${formatMoney(nowPrice)}", 16f, nowWidth - nowLabelBitmap.width - nowGroupGap - nowPadding * 2, 52, inverse = true)
    val nowHeight = max(nowLabelBitmap.height, nowPriceBitmap.height) + nowPadding * 2
    val nowBitmap = Bitmap.createBitmap(nowWidth, nowHeight, Bitmap.Config.ARGB_8888)
    Canvas(nowBitmap).apply {
      drawColor(Color.BLACK)
      drawBitmap(nowLabelBitmap, nowPadding.toFloat(), ((nowHeight - nowLabelBitmap.height) / 2).toFloat(), null)
      drawBitmap(nowPriceBitmap, (nowWidth - nowPadding - nowPriceBitmap.width).toFloat(), ((nowHeight - nowPriceBitmap.height) / 2).toFloat(), null)
    }
    val wasLabelBitmap = fittedText("WAS", 6f, wasWidth)
    val wasPriceBitmap = fittedText("$${formatMoney(retailPrice)}", 8f, wasWidth, 30)
    val dateBitmap = fittedText(todayString(), 6f, infoWidth, 24, inverse = true, padding = 2)
    // 超长货号明确显示省略号；二维码继续编码完整条码/货号。
    val itemDisplay = if (itemNumber.length > 24) itemNumber.take(21) + "..." else itemNumber
    val itemBitmap = itemDisplay.takeIf { it.isNotBlank() }?.let { fittedText(it, 7f, infoWidth, 28) }
    val discountBitmap = fittedText(discountValue.roundToInt().toString().padStart(2, '0'), 44f, w / 2, 108)
    val offBitmap = fittedText("OFF", 16f, 110)
    val percentBitmap = fittedText("%", 20f, 70)
    val startY = 20
    val headerGap = 8 // EG 每行按 8 点补齐，留出字节尾部空白，避免相邻位图覆盖。
    val startX = w - 12 - discountBitmap.width - headerGap - max(percentBitmap.width, percentBitmap.width / 2 + offBitmap.width)
    val qrBitmap = barcode.takeIf { it.isNotBlank() }?.let { createQrCodeBitmap(it, 64) }
    // 两种纸宽都遵守现有 204 点有效打印区，底部保留 10 点。
    val infoBandBottom = 194
    val qrX = 10
    val qrY = infoBandBottom - (qrBitmap?.height ?: 64)
    val dateY = infoBandBottom - dateBitmap.height
    val itemY = dateY - (itemBitmap?.height ?: 0) - 6
    val wasPriceY = infoBandBottom - wasPriceBitmap.height
    val nameMaxWidth = max(1, startX - 15)
    val namePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
      color = Color.BLACK
      textSize = fontSizeToPixels(10f)
      typeface = Typeface.create("Arial", Typeface.NORMAL)
    }
    val nameLines = wrapText(cpclText(productName), namePaint, nameMaxWidth, 2).toMutableList()
    // 英文优先在词间换行；只有单词本身太长时才沿用逐字换行。
    if (nameLines.size == 2 && !nameLines[0].endsWith(" ") && !nameLines[1].startsWith(" ")) {
      val split = nameLines[0].lastIndexOf(' ')
      if (split > 0) {
        nameLines[1] = nameLines[0].substring(split).trim() + nameLines[1]
        nameLines[0] = nameLines[0].substring(0, split)
      }
    }
    val nameLineHeight = ceil(namePaint.fontMetrics.descent - namePaint.fontMetrics.ascent).toInt()
    val nameBitmap = Bitmap.createBitmap(nameMaxWidth, nameLineHeight * nameLines.size, Bitmap.Config.ARGB_8888)
    Canvas(nameBitmap).apply {
      drawColor(Color.WHITE)
      nameLines.forEachIndexed { index, value ->
        var display = value.trim()
        if (namePaint.measureText(display) > nameMaxWidth) {
          while (display.isNotEmpty() && namePaint.measureText(display + "...") > nameMaxWidth) display = display.dropLast(1)
          display += "..."
        }
        drawText(display, 0f, index * nameLineHeight - namePaint.fontMetrics.ascent, namePaint)
      }
    }

    val commands = mutableListOf(
      "! 0 200 200 $h 1",
      "PAGE-WIDTH $w",
      bitmapCommand(5, 5, nameBitmap),
      bitmapCommand(startX, startY, discountBitmap),
      bitmapCommand(startX + discountBitmap.width + headerGap, startY, percentBitmap),
      bitmapCommand(
        startX + discountBitmap.width + headerGap + percentBitmap.width / 2,
        startY + discountBitmap.height - offBitmap.height,
        offBitmap,
      ),
    )

    if (itemBitmap != null) {
      commands += bitmapCommand(infoX, itemY, itemBitmap)
    }

    if (qrBitmap != null) {
      commands += bitmapCommand(qrX, qrY, qrBitmap)
    }

    commands += bitmapCommand(infoX, dateY, dateBitmap)
    if (showOriginalPrice) {
      commands += bitmapCommand(wasX, wasPriceY - wasLabelBitmap.height - 4, wasLabelBitmap)
      commands += bitmapCommand(wasX, wasPriceY, wasPriceBitmap)
      val strikeY = wasPriceY + wasPriceBitmap.height / 2
      commands += "LINE $wasX $strikeY ${wasX + wasPriceBitmap.width - 1} $strikeY 2"
    }
    commands += bitmapCommand(nowX, infoBandBottom - nowBitmap.height, nowBitmap)
    commands += "PRINT"

    return commands.joinToString("\r\n", postfix = "\r\n")
  }

  private fun buildClearanceLabelCommand(payload: ReadableMap): String {
    val w = 614
    val h = 205
    val productName = payload.getNullableString("productName")
    val itemNumber = payload.getNullableString("itemNumber")
    val supplierName = formatSupplierAbbreviation(payload.getNullableString("supplierName"))
    val barcode = payload.getNullableString("clearanceBarcode").ifBlank {
      payload.getNullableString("barcode")
    }
    val retailPrice = payload.getNullableDouble("retailPrice") ?: 0.0
    val discountRate = payload.getNullableDouble("discountRate") ?: 0.0
    val clearancePrice = payload.getNullableDouble("clearancePrice") ?: (retailPrice * (1.0 - discountRate))

    val clearanceLabelBitmap = textToBitmap("CLEARANCE", fontSizeToPixels(30f), true, "sans-serif-black", true, 2)
    val dateBitmap = textToBitmap(todayString(), fontSizeToPixels(7f), true, "sans-serif-black", true, 2)
    val itemBitmap = textToBitmap(itemNumber, fontSizeToPixels(7f), true, "sans-serif-black")
    val price = formatPriceParts(clearancePrice)
    val priceIntegerBitmap = textToBitmap(price.integer, fontSizeToPixels(40f), true, "sans-serif-black")
    val priceDotBitmap = textToBitmap(".", fontSizeToPixels(20f), true, "sans-serif-black")
    val priceDecimalBitmap = textToBitmap(price.decimal, fontSizeToPixels(20f), true, "sans-serif-black")
    val priceCurrencyBitmap = textToBitmap("$", fontSizeToPixels(20f), false, "sans-serif-black")
    val priceTotalWidth = priceCurrencyBitmap.width + priceIntegerBitmap.width + priceDotBitmap.width + priceDecimalBitmap.width
    val qrBitmap = barcode.takeIf { it.isNotBlank() }?.let { createQrCodeBitmap(it, 64) }
    val qrVisualWidth = qrBitmap?.width ?: 0
    val infoAreaLeft = qrVisualWidth + 15
    val nameMaxWidth = max(1, w - priceTotalWidth - infoAreaLeft - 15)
    val nameBitmap = longTextToBitmap(productName, fontSizeToPixels(8f), false, "Arial", 2, nameMaxWidth)

    val nameY = 21
    val priceStartY = nameY + 8
    val priceStartX = w - priceDecimalBitmap.width - 68
    val clearanceLabelY = h - clearanceLabelBitmap.height - 5
    val clearanceLabelX = max(0, (w - clearanceLabelBitmap.width) / 2)
    val qrY = clearanceLabelY - (qrBitmap?.height ?: 64) - 5
    val infoY = qrY + 5
    val dateY = infoY + max(itemBitmap.height, dateBitmap.height) + 3

    val commands = mutableListOf(
      "! 0 200 200 $h 1",
      "PAGE-WIDTH $w",
      bitmapCommand(5, nameY, nameBitmap),
      bitmapCommand(priceStartX, priceStartY, priceDecimalBitmap),
      bitmapCommand(priceStartX - priceDotBitmap.width, priceStartY + priceIntegerBitmap.height - 10, priceDotBitmap),
      bitmapCommand(priceStartX - priceDotBitmap.width - priceIntegerBitmap.width, priceStartY, priceIntegerBitmap),
      bitmapCommand(
        priceStartX - priceDotBitmap.width - priceIntegerBitmap.width - priceCurrencyBitmap.width,
        priceStartY,
        priceCurrencyBitmap,
      ),
      bitmapCommand(clearanceLabelX, clearanceLabelY, clearanceLabelBitmap),
      bitmapCommand(infoAreaLeft, infoY, itemBitmap),
      bitmapCommand(infoAreaLeft, dateY, dateBitmap),
    )

    if (qrBitmap != null) {
      commands += bitmapCommand(5, qrY, qrBitmap)
    }
    commands += "PRINT"

    return commands.joinToString("\r\n", postfix = "\r\n")
  }

  private fun buildBigDiscountLabelCommand(payload: ReadableMap, printType: String): String {
    val productName = payload.getNullableString("productName")
    val barcode = payload.getNullableString("barcode")
    val retailPrice = payload.getNullableDouble("retailPrice") ?: 0.0
    val discountRate = payload.getNullableDouble("discountRate") ?: 0.0
    val paperWidth = 480
    val afterDiscount = retailPrice * (1.0 - discountRate)
    val price = formatPriceParts(afterDiscount)
    val saveAmount = retailPrice * discountRate

    val commands = mutableListOf(
      "! 0 200 200 1200 1",
      "PAGE-WIDTH $paperWidth",
    )

    commands += buildBigDiscountHeaderCommands(discountRate, printType, paperWidth)

    val currencyBitmap = textToBitmap("$", fontSizeToPixels(20f), false, "sans-serif-black")
    val wasCurrencyBitmap = textToBitmap("$", fontSizeToPixels(8f), true, "sans-serif-black")
    val saveCurrencyBitmap = textToBitmap("$", fontSizeToPixels(8f), false, "sans-serif-black")
    val eaBitmap = textToBitmap("ea", fontSizeToPixels(8f), false, "sans-serif-light", true, 2)
    val wasBitmap = textToBitmap("WAS ", fontSizeToPixels(10f), true, "sans-serif-light")
    val saveBitmap = textToBitmap("SAVE", fontSizeToPixels(16f), true, "sans-serif-light", true, 2)
    val intBitmap = textToBitmap(price.integer, fontSizeToPixels(60f), true, "sans-serif-black")
    val decimalBitmap = textToBitmap(price.decimal, fontSizeToPixels(24f), true, "sans-serif-black")
    val dotBitmap = textToBitmap(".", fontSizeToPixels(20f), true, "sans-serif-black")
    val rrpBitmap = textToBitmap(formatMoney(retailPrice), fontSizeToPixels(10f), true, "sans-serif-condensed")
    val saveAmountBitmap = textToBitmap(formatMoney(saveAmount), fontSizeToPixels(16f), true, "sans-serif-condensed")
    val nameBitmap = longTextToBitmap(productName, fontSizeToPixels(10f), false, "Arial", 4, paperWidth)
    val dashLineBitmap = createDashLineBitmap(450, 2)
    val dateBitmap = textToBitmap(todayString(), fontSizeToPixels(8f), false, "Arial", true, 2)

    val startY = 220
    var startX = (paperWidth - currencyBitmap.width - intBitmap.width) / 2
    if (price.decimal.toIntOrNull() != 0) {
      startX = (paperWidth - currencyBitmap.width - intBitmap.width - dotBitmap.width - decimalBitmap.width) / 2
      commands += bitmapCommand(startX + currencyBitmap.width + intBitmap.width, startY + (intBitmap.height * 0.9).toInt(), dotBitmap)
      commands += bitmapCommand(startX + currencyBitmap.width + intBitmap.width + dotBitmap.width, startY, decimalBitmap)
    }

    commands += bitmapCommand(startX, startY, currencyBitmap)
    commands += bitmapCommand(startX + currencyBitmap.width, startY, intBitmap)
    commands += bitmapCommand(startX + currencyBitmap.width + intBitmap.width + 30, startY + (intBitmap.height * 0.9).toInt(), eaBitmap)

    val rrpStartY = startY + intBitmap.height + 20
    commands += bitmapCommand(5, rrpStartY, wasBitmap)
    commands += bitmapCommand(5 + wasBitmap.width, rrpStartY, wasCurrencyBitmap)
    commands += bitmapCommand(5 + wasBitmap.width + wasCurrencyBitmap.width, rrpStartY, rrpBitmap)

    if (discountRate > 0) {
      commands += "LINE 5 $rrpStartY ${5 + wasBitmap.width + rrpBitmap.width} ${rrpStartY + wasBitmap.height} 2"
      commands += "LINE 5 ${rrpStartY + wasBitmap.height} ${5 + wasBitmap.width + rrpBitmap.width} $rrpStartY 2"
      val saveStartX = 30 + wasBitmap.width + wasCurrencyBitmap.width + rrpBitmap.width
      commands += bitmapCommand(saveStartX, rrpStartY, saveBitmap)
      commands += bitmapCommand(saveStartX + saveBitmap.width + 5, rrpStartY, saveCurrencyBitmap)
      commands += bitmapCommand(saveStartX + saveBitmap.width + 5 + saveCurrencyBitmap.width + 5, rrpStartY, saveAmountBitmap)
    }

    commands += bitmapCommand(5, 550 - nameBitmap.height - 5, nameBitmap)
    commands += bitmapCommand(15, 550, dashLineBitmap)

    if (barcode.isNotBlank()) {
      commands += "BARCODE 128 1 2 30 15 560 ${cpclText(barcode)}"
    }

    commands += bitmapCommand(paperWidth - dateBitmap.width - 10, 630 - dateBitmap.height, dateBitmap)
    commands += "PRINT"

    return commands.joinToString("\r\n", postfix = "\r\n")
  }

  private fun buildBigDiscountHeaderCommands(discountRate: Double, printType: String, paperWidth: Int): List<String> {
    val discount = discountRate * 100.0
    if (printType.isNotBlank()) {
      val titleBitmap = textToBitmap(printType, fontSizeToPixels(25f), true, "sans-serif-black")
      return listOf(bitmapCommand(paperWidth / 2 - titleBitmap.width / 2, 80, titleBitmap))
    }

    if (discount <= 10.0 || discount > 100.0) {
      val specialBitmap = textToBitmap("Special", fontSizeToPixels(40f), true, "sans-serif-black")
      return listOf(bitmapCommand(paperWidth / 2 - specialBitmap.width / 2, 40, specialBitmap))
    }

    if (abs(discount - 50.0) < 0.01) {
      val halfBitmap = textToBitmap("1/2", fontSizeToPixels(40f), true, "sans-serif-black")
      val priceBitmap = textToBitmap("PRICE", fontSizeToPixels(25f), true, "sans-serif-black")
      return listOf(
        bitmapCommand(130, 20, halfBitmap),
        bitmapCommand(120, 20 + halfBitmap.height, priceBitmap),
      )
    }

    val discountBitmap = textToBitmap(discount.roundToInt().toString(), fontSizeToPixels(40f), true, "sans-serif-black")
    val percentBitmap = textToBitmap("%", fontSizeToPixels(24f), true, "sans-serif-condensed")
    val offBitmap = textToBitmap("OFF", fontSizeToPixels(20f), true, "sans-serif-black")
    val startX = (paperWidth - discountBitmap.width - percentBitmap.width) / 2
    return listOf(
      bitmapCommand(startX, 20, discountBitmap),
      bitmapCommand(startX + discountBitmap.width, 20, percentBitmap),
      bitmapCommand((paperWidth - offBitmap.width) / 2, 20 + discountBitmap.height + 20, offBitmap),
    )
  }

  private fun buildWarehouseProductLabelCommand(payload: ReadableMap): String {
    val w = labelWidth
    val h = warehouseLabelHeight
    val productName = payload.getNullableString("productName")
    val itemNumber = payload.getNullableString("itemNumber")
    val barcode = payload.getNullableString("barcode")
    val middlePackageQuantity = payload.getNullableDouble("middlePackageQuantity")
    val purchasePrice = payload.getNullableDouble("purchasePrice")
    val retailPrice = payload.getNullableDouble("retailPrice")
    val locationCode = payload.getNullableString("locationCode")
    val domesticPrice = payload.getNullableDouble("domesticPrice")
    val oemPrice = payload.getNullableDouble("oemPrice")
    val importPrice = payload.getNullableDouble("importPrice")
    val displayPrice = retailPrice ?: domesticPrice ?: oemPrice ?: importPrice
    val costPrice = purchasePrice ?: importPrice ?: domesticPrice ?: oemPrice
    // 仓库商品标签只在实际中包数大于 1 时显示 INNER。
    val innerText = middlePackageQuantity?.takeIf { it > 1 }?.let {
      "INNER ${formatOptionalQuantity(it)}"
    }
    val priceDetails = listOf(
      "COST ${formatOptionalMoney(costPrice)}",
      "RRP ${formatOptionalMoney(displayPrice)}",
    )
    val contentWidth = w - 40

    val titleBitmap = textToBitmap("WAREHOUSE PRODUCT", fontSizeToPixels(8f), true, "sans-serif-black", true, 2)
    val nameBitmap = longTextToBitmap(productName, fontSizeToPixels(8f), true, "Arial", 1, max(180, contentWidth))
    val itemBitmap = textToBitmap("ITEM ${cpclText(itemNumber.ifBlank { "--" })}", fontSizeToPixels(7f), true, "sans-serif-black")
    val innerBitmap = innerText?.let {
      textToBitmap(it, fontSizeToPixels(7f), true, "sans-serif-black")
    }
    val costBitmap = textToBitmap(priceDetails[0], fontSizeToPixels(7f), true, "sans-serif-black")
    val rrpBitmap = textToBitmap(priceDetails[1], fontSizeToPixels(7f), true, "sans-serif-black")
    val locationBitmap = textToBitmap(
      "LOC ${cpclText(locationCode.ifBlank { "UNASSIGNED" })}",
      fontSizeToPixels(8f),
      true,
      "sans-serif-black",
      true,
      2,
    )
    val dateBitmap = textToBitmap(todayString(), fontSizeToPixels(7f), true, "sans-serif-black", true, 2)
    fun centerX(bitmap: Bitmap) = max(0, (w - bitmap.width) / 2)

    val commands = mutableListOf(
      "! 0 200 200 $h 1",
      "PAGE-WIDTH $w",
      bitmapCommand(centerX(titleBitmap), 12, titleBitmap),
      bitmapCommand(centerX(nameBitmap), 38, nameBitmap),
      bitmapCommand(centerX(itemBitmap), 66, itemBitmap),
      bitmapCommand(20, 92, locationBitmap),
      bitmapCommand(w - dateBitmap.width - 20, 92, dateBitmap),
    )

    if (barcode.isNotBlank()) {
      commands += "BARCODE-TEXT 7 0 5"
      commands += "BARCODE 128 1 1 38 24 146 ${cpclText(barcode)}"
      if (innerBitmap != null) {
        commands += bitmapCommand(380, 132, innerBitmap)
      }
      commands += bitmapCommand(380, 156, costBitmap)
      commands += bitmapCommand(380, 180, rrpBitmap)
    } else {
      if (innerBitmap != null) {
        commands += bitmapCommand(centerX(innerBitmap), 132, innerBitmap)
      }
      commands += bitmapCommand(centerX(costBitmap), 156, costBitmap)
      commands += bitmapCommand(centerX(rrpBitmap), 180, rrpBitmap)
    }

    commands += "PRINT"
    return commands.joinToString("\r\n", postfix = "\r\n")
  }

  private fun buildWarehouseLocationLabelCommand(payload: ReadableMap): String {
    val w = labelWidth
    val h = warehouseLabelHeight
    val locationCode = payload.getNullableString("locationCode")
    val locationBarcode = payload.getNullableString("locationBarcode")
    val locationGuid = payload.getNullableString("locationGuid")

    // 显示代码优先级：locationCode > locationBarcode > locationGuid；全空时打印占位符。
    val displayCode = locationCode.ifBlank { locationBarcode.ifBlank { locationGuid } }
    val printableCode = displayCode.ifBlank { "--" }
    // 条码优先级：locationBarcode，其次显示代码；全空则不出条码。
    val barcode = locationBarcode.ifBlank { displayCode }

    // 上方 2/3：完整货位代码以黑字白底、sans-serif-black、加粗位图显示。
    // 安全区域宽 540、高 120；目标字号 30（约 90 dots），逐级缩小取最大可容纳字号，不换行、不截断。
    val maxCodeWidth = 540
    val maxCodeHeight = 120
    var codeBitmap = textToBitmap(printableCode, fontSizeToPixels(1f), true, "sans-serif-black")
    for (fontSize in 30 downTo 1) {
      val candidate = textToBitmap(printableCode, fontSizeToPixels(fontSize.toFloat()), true, "sans-serif-black")
      if (candidate.width <= maxCodeWidth && candidate.height <= maxCodeHeight) {
        codeBitmap = candidate
        break
      }
    }
    // 极端长代码即使最小字号仍放不下时，按比例缩放完整位图，避免裁切或越界。
    if (codeBitmap.width > maxCodeWidth || codeBitmap.height > maxCodeHeight) {
      val scale = minOf(
        maxCodeWidth.toFloat() / codeBitmap.width,
        maxCodeHeight.toFloat() / codeBitmap.height,
      )
      codeBitmap = Bitmap.createScaledBitmap(
        codeBitmap,
        max(1, (codeBitmap.width * scale).toInt()),
        max(1, (codeBitmap.height * scale).toInt()),
        true,
      )
    }
    // 位图水平居中，并在上方 2/3 区域内垂直居中。
    val codeX = max(0, (w - codeBitmap.width) / 2)
    val codeY = (h * 2 / 3 - codeBitmap.height) / 2

    val commands = mutableListOf(
      "! 0 200 200 $h 1",
      "PAGE-WIDTH $w",
      bitmapCommand(codeX, codeY, codeBitmap),
    )

    // 下方 1/3：CENTER 使 Code128 条码水平居中；空条码不输出 BARCODE。
    if (barcode.isNotBlank()) {
      // BARCODE-TEXT 会跨标签保持，必须显式关闭，避免继承上一张商品标签的可读数字。
      commands += "BARCODE-TEXT OFF"
      commands += "CENTER"
      commands += "BARCODE 128 1 1 44 0 151 ${cpclText(barcode)}"
    }

    commands += "PRINT"
    return commands.joinToString("\r\n", postfix = "\r\n")
  }

  private fun ReadableMap.getNullableString(key: String): String {
    return if (hasKey(key) && !isNull(key)) getString(key)?.trim().orEmpty() else ""
  }

  private fun ReadableMap.getNullableDouble(key: String): Double? {
    return if (hasKey(key) && !isNull(key)) getDouble(key) else null
  }

  private fun formatPriceParts(value: Double?): PriceParts {
    val safeValue = value ?: 0.0
    val cents = (safeValue * 100).roundToInt()
    val integer = cents / 100
    val decimal = (cents % 100).toString().padStart(2, '0')
    return PriceParts(integer.toString(), decimal)
  }

  private fun todayString(pattern: String = "yyyy/MM/dd"): String {
    return SimpleDateFormat(pattern, Locale.US).format(Date())
  }

  private fun formatMoney(value: Double): String {
    return String.format(Locale.US, "%.2f", value)
  }

  private fun formatOptionalMoney(value: Double?): String {
    return if (value == null) "--" else formatMoney(value)
  }

  private fun formatOptionalQuantity(value: Double?): String {
    if (value == null) {
      return "--"
    }
    val rounded = value.roundToInt()
    return if (abs(value - rounded) < 0.01) rounded.toString() else String.format(Locale.US, "%.2f", value)
  }

  private fun cpclText(value: String): String {
    return value.replace(Regex("[\\r\\n]+"), " ").trim()
  }

  private fun fontSizeToPixels(fontSize: Float): Float {
    return fontSize * 3f
  }

  private fun textToBitmap(
    text: String,
    fontSize: Float,
    isBold: Boolean,
    fontFamily: String,
    isInverse: Boolean = false,
    padding: Int = 0,
    strikeThrough: Boolean = false,
  ): Bitmap {
    val safeText = text.ifBlank { " " }
    val paint = Paint().apply {
      isAntiAlias = true
      color = if (isInverse) Color.WHITE else Color.BLACK
      textSize = fontSize
      textAlign = Paint.Align.LEFT
      typeface = Typeface.create(fontFamily, if (isBold) Typeface.BOLD else Typeface.NORMAL)
      if (strikeThrough) {
        flags = flags or Paint.STRIKE_THRU_TEXT_FLAG
      }
    }
    val bounds = android.graphics.Rect()
    paint.getTextBounds(safeText, 0, safeText.length, bounds)
    val measuredWidth = paint.measureText(safeText)
    val width = max(1, ceil(max(measuredWidth, bounds.width().toFloat())).toInt() + padding * 2)
    val height = max(1, bounds.height() + padding * 2)
    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    val canvas = Canvas(bitmap)
    canvas.drawColor(if (isInverse) Color.BLACK else Color.WHITE)
    canvas.drawText(safeText, padding.toFloat(), (height - bounds.bottom - padding).toFloat(), paint)
    return bitmap
  }

  private fun longTextToBitmap(
    text: String,
    fontSize: Float,
    isBold: Boolean,
    fontFamily: String,
    maxLines: Int,
    maxWidth: Int,
  ): Bitmap {
    val safeText = text.ifBlank { " " }
    val paint = Paint().apply {
      isAntiAlias = true
      color = Color.BLACK
      textSize = fontSize
      textAlign = Paint.Align.LEFT
      typeface = Typeface.create(fontFamily, if (isBold) Typeface.BOLD else Typeface.NORMAL)
    }
    val lines = wrapText(safeText, paint, maxWidth, maxLines)
    val metrics = paint.fontMetrics
    val lineHeight = ceil(metrics.descent - metrics.ascent).toInt()
    val width = max(1, minOf(maxWidth, ceil(lines.maxOf { paint.measureText(it) }).toInt()))
    val height = max(1, lineHeight * lines.size)
    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    val canvas = Canvas(bitmap)
    canvas.drawColor(Color.WHITE)
    lines.forEachIndexed { index, line ->
      canvas.drawText(line, 0f, index * lineHeight - metrics.ascent, paint)
    }
    return bitmap
  }

  private fun wrapText(text: String, paint: Paint, maxWidth: Int, maxLines: Int): List<String> {
    val lines = mutableListOf<String>()
    var current = StringBuilder()
    text.forEach { char ->
      val next = current.toString() + char
      if (current.isNotEmpty() && paint.measureText(next) > maxWidth && lines.size < maxLines - 1) {
        lines += current.toString()
        current = StringBuilder(char.toString())
      } else {
        current.append(char)
      }
    }
    if (current.isNotEmpty() || lines.isEmpty()) {
      lines += current.toString()
    }
    return lines.take(maxLines)
  }

  private fun processCapitalization(value: String): String {
    return value
      .lowercase(Locale.US)
      .split(Regex("\\s+"))
      .filter { it.isNotBlank() }
      .joinToString(" ") { word -> word.replaceFirstChar { it.titlecase(Locale.US) } }
  }

  private fun formatSupplierAbbreviation(value: String): String {
    val words = value
      .lowercase(Locale.US)
      .split(Regex("\\s+"))
      .filter { it.isNotBlank() }
      .map { word -> word.replaceFirstChar { it.titlecase(Locale.US) } }

    if (words.isEmpty()) {
      return ""
    }

    if (words.size == 1) {
      return words.first().take(3).uppercase(Locale.US)
    }

    return words
      .take(4)
      .map { it.first().uppercaseChar() }
      .joinToString(".")
  }

  private fun bitmapCommand(x: Int, y: Int, bitmap: Bitmap): String {
    val widthBytes = (bitmap.width + 7) / 8
    return "EG $widthBytes ${bitmap.height} $x $y ${bitmapToHex(bitmap, widthBytes)}"
  }

  private fun createQrCodeBitmap(value: String, size: Int): Bitmap {
    val hints = EnumMap<EncodeHintType, Any>(EncodeHintType::class.java).apply {
      put(EncodeHintType.MARGIN, 0)
      put(EncodeHintType.CHARACTER_SET, "UTF-8")
    }
    val matrix = MultiFormatWriter().encode(
      value,
      BarcodeFormat.QR_CODE,
      size,
      size,
      hints,
    )
    val bitmap = Bitmap.createBitmap(matrix.width, matrix.height, Bitmap.Config.ARGB_8888)
    for (y in 0 until matrix.height) {
      for (x in 0 until matrix.width) {
        bitmap.setPixel(x, y, if (matrix.get(x, y)) Color.BLACK else Color.WHITE)
      }
    }
    return bitmap
  }

  private fun createDashLineBitmap(width: Int, height: Int): Bitmap {
    val bitmap = Bitmap.createBitmap(max(1, width), max(1, height), Bitmap.Config.ARGB_8888)
    val canvas = Canvas(bitmap)
    canvas.drawColor(Color.WHITE)
    val paint = Paint().apply {
      color = Color.BLACK
      strokeWidth = height.toFloat()
    }
    var x = 0f
    while (x < width) {
      canvas.drawLine(x, height / 2f, minOf(x + 10f, width.toFloat()), height / 2f, paint)
      x += 18f
    }
    return bitmap
  }

  private fun bitmapToHex(bitmap: Bitmap, widthBytes: Int): String {
    val hex = StringBuilder(widthBytes * bitmap.height * 2)
    for (y in 0 until bitmap.height) {
      for (byteIndex in 0 until widthBytes) {
        var value = 0
        for (bit in 0 until 8) {
          val x = byteIndex * 8 + bit
          if (x < bitmap.width && isBlack(bitmap.getPixel(x, y))) {
            value = value or (1 shl (7 - bit))
          }
        }
        hex.append(value.toString(16).padStart(2, '0').uppercase(Locale.US))
      }
    }
    return hex.toString()
  }

  private fun isBlack(pixel: Int): Boolean {
    val alpha = Color.alpha(pixel)
    if (alpha == 0) {
      return false
    }
    val luminance = (Color.red(pixel) * 299 + Color.green(pixel) * 587 + Color.blue(pixel) * 114) / 1000
    return luminance < 200
  }

  private fun beginConnectionAttempt(): Long {
    val previousConnection: PrinterConnection?
    val generation: Long
    synchronized(connectionLock) {
      previousConnection = connection
      connection = null
      connectedAddress = null
      connectionGeneration += 1
      generation = connectionGeneration
    }
    previousConnection?.close()
    return generation
  }

  private fun invalidateConnectionAttempt() {
    synchronized(connectionLock) {
      connectionGeneration += 1
    }
  }

  private fun clearConnection(expectedConnection: PrinterConnection? = null): Boolean {
    val connectionToClose: PrinterConnection?
    synchronized(connectionLock) {
      if (expectedConnection != null && connection !== expectedConnection) {
        return false
      }
      connectionToClose = connection
      if (connectionToClose == null && connectedAddress == null) {
        return false
      }
      connection = null
      connectedAddress = null
      connectionGeneration += 1
    }
    connectionToClose?.close()
    return true
  }

  private fun closeQuietly(target: BluetoothSocket?) {
    try {
      target?.close()
    } catch (_: Exception) {
    }
  }

  @SuppressLint("MissingPermission")
  private fun bluetoothTransport(device: BluetoothDevice): String = when (device.type) {
    BluetoothDevice.DEVICE_TYPE_CLASSIC -> "classic"
    BluetoothDevice.DEVICE_TYPE_LE -> "ble"
    BluetoothDevice.DEVICE_TYPE_DUAL -> "dual"
    else -> "unknown"
  }

  private fun completePendingPairing(address: String) {
    val pending = synchronized(pairingLock) {
      if (pendingPairingAddress?.equals(address, ignoreCase = true) != true) {
        null
      } else {
        takePendingPairingLocked()
      }
    } ?: return
    pending.timeout?.let(handler::removeCallbacks)
    pending.promise.resolve(true)
  }

  private fun failPendingPairing(
    code: String,
    message: String,
    error: Throwable? = null,
    expectedAddress: String? = null,
  ) {
    val pending = synchronized(pairingLock) {
      if (
        expectedAddress != null &&
        pendingPairingAddress?.equals(expectedAddress, ignoreCase = true) != true
      ) {
        null
      } else {
        takePendingPairingLocked()
      }
    } ?: return
    pending.timeout?.let(handler::removeCallbacks)
    if (error == null) {
      pending.promise.reject(code, message)
    } else {
      pending.promise.reject(code, message, error)
    }
  }

  private fun takePendingPairingLocked(): PendingPairing? {
    val promise = pendingPairingPromise ?: return null
    val pending = PendingPairing(promise, pendingPairingTimeout)
    pendingPairingAddress = null
    pendingPairingPromise = null
    pendingPairingTimeout = null
    return pending
  }

  @SuppressLint("MissingPermission")
  private fun registerStatusReceiver() {
    if (statusReceiverRegistered) {
      return
    }
    val filter = IntentFilter().apply {
      addAction(BluetoothDevice.ACTION_ACL_DISCONNECTED)
      addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
      addAction(BluetoothDevice.ACTION_BOND_STATE_CHANGED)
      addAction(BluetoothAdapter.ACTION_STATE_CHANGED)
    }
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      // 蓝牙状态广播由特权系统组件发送，NOT_EXPORTED 会漏收这类广播。
      appContext.registerReceiver(statusReceiver, filter, Context.RECEIVER_EXPORTED)
    } else {
      @Suppress("DEPRECATION")
      appContext.registerReceiver(statusReceiver, filter)
    }
    statusReceiverRegistered = true
  }

  private fun unregisterStatusReceiver() {
    if (!statusReceiverRegistered) {
      return
    }
    try {
      appContext.unregisterReceiver(statusReceiver)
    } catch (_: IllegalArgumentException) {
    } finally {
      pendingAclDisconnect?.let(handler::removeCallbacks)
      pendingAclDisconnect = null
      pendingAclDisconnectAddress = null
      statusReceiverRegistered = false
    }
  }

  private fun emitStatusChanged() {
    if (listenerCount <= 0 || !reactApplicationContext.hasActiveReactInstance()) {
      return
    }
    reactApplicationContext
      .getJSModule(DeviceEventManagerModule.RCTDeviceEventEmitter::class.java)
      .emit(STATUS_EVENT, Arguments.createMap())
  }

  companion object {
    private const val STATUS_EVENT = "HbPrinterStatusChanged"
    private const val ACL_DISCONNECT_SETTLE_MS = 250L
    private const val PAIRING_TIMEOUT_MS = 45_000L
    private const val BLE_CONNECT_TIMEOUT_MS = 15_000L
  }

  data class PendingPairing(
    val promise: Promise,
    val timeout: Runnable?,
  )

  data class WritablePrinterDevice(
    val name: String?,
    val address: String,
    val bonded: Boolean,
    val connected: Boolean,
    val transport: String,
    val deviceClass: Int?,
  )

  data class PriceParts(
    val integer: String,
    val decimal: String,
  )
}
