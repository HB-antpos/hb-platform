package com.hbweb.expo

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothSocket
import android.bluetooth.BluetoothStatusCodes
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.min

/** 打印机写入通道：经典蓝牙走 RFCOMM 串口，BLE 走 GATT 可写特征值；模块只按身份管理当前连接。 */
internal interface PrinterConnection {
  val isConnected: Boolean
  fun write(data: ByteArray)
  fun close()
}

internal class RfcommPrinterConnection(private val socket: BluetoothSocket) : PrinterConnection {
  override val isConnected: Boolean
    get() = socket.isConnected

  override fun write(data: ByteArray) {
    val outputStream = socket.outputStream
    outputStream.write(data)
    outputStream.flush()
  }

  override fun close() {
    try {
      socket.close()
    } catch (_: Exception) {
    }
  }
}

/**
 * BLE 打印通道。打开过程（连接 → 协商 MTU → 发现服务 → 选可写特征值）在调用线程上阻塞等待，
 * 与 RFCOMM connect() 的线程模型一致；写入按 MTU 分包，每包等系统回调后再发下一包。
 */
@SuppressLint("MissingPermission")
internal class BlePrinterConnection private constructor(
  private val onConnectionLost: (BlePrinterConnection) -> Unit,
  // 链路诊断：只上报 GATT 回调的状态码，调用方的实现不会抛异常，也不影响连接流程。
  private val onDiag: (String, Map<String, Any?>) -> Unit,
) : PrinterConnection {
  private val handler = Handler(Looper.getMainLooper())
  private val ready = CountDownLatch(1)
  private val serviceDiscoveryRequested = AtomicBoolean(false)
  private val closed = AtomicBoolean(false)
  private val lostNotified = AtomicBoolean(false)
  private val writeLock = Any()

  @Volatile private var gatt: BluetoothGatt? = null
  @Volatile private var connected = false
  @Volatile private var setupError: String? = null
  @Volatile private var mtu = DEFAULT_ATT_MTU
  @Volatile private var characteristic: BluetoothGattCharacteristic? = null
  @Volatile private var writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
  @Volatile private var pendingWrite: CountDownLatch? = null
  @Volatile private var pendingWriteStatus = BluetoothGatt.GATT_FAILURE

  override val isConnected: Boolean
    get() = connected && !closed.get()

  private val callback = object : BluetoothGattCallback() {
    override fun onConnectionStateChange(target: BluetoothGatt, status: Int, newState: Int) {
      if (closed.get()) {
        return
      }
      // status 才是 BLE 失败的真实原因码（如 133 = GATT_ERROR）；连接超时则根本没有这条回调。
      onDiag("ble.gatt.state", mapOf("status" to status, "newState" to newState))
      if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
        if (ready.count == 0L) {
          return
        }
        // 先争取大 MTU 减少分包；部分打印机不回 onMtuChanged，到时限后直接发现服务。
        if (!target.requestMtu(TARGET_ATT_MTU)) {
          requestServiceDiscovery(target)
        } else {
          handler.postDelayed({ requestServiceDiscovery(target) }, MTU_FALLBACK_MS)
        }
        return
      }

      connected = false
      pendingWrite?.countDown()
      if (ready.count > 0L) {
        failSetup("Bluetooth printer BLE connection failed (status=$status).")
        return
      }
      // 打印机断电或走出范围：释放 GATT 并通知模块清理，禁止旧会话继续接收写入。
      releaseGatt(target)
      if (lostNotified.compareAndSet(false, true)) {
        onConnectionLost(this@BlePrinterConnection)
      }
    }

    override fun onMtuChanged(target: BluetoothGatt, value: Int, status: Int) {
      onDiag("ble.mtu", mapOf("status" to status, "mtu" to value))
      if (status == BluetoothGatt.GATT_SUCCESS && value > DEFAULT_ATT_MTU) {
        mtu = value
      }
      requestServiceDiscovery(target)
    }

    override fun onServicesDiscovered(target: BluetoothGatt, status: Int) {
      if (closed.get() || ready.count == 0L) {
        return
      }
      onDiag("ble.services", mapOf("status" to status, "count" to target.services?.size))
      if (status != BluetoothGatt.GATT_SUCCESS) {
        failSetup("Bluetooth printer BLE service discovery failed (status=$status).")
        return
      }
      val selected = selectWritableCharacteristic(target.services.orEmpty())
      if (selected == null) {
        failSetup("Bluetooth printer did not expose a writable BLE characteristic.")
        return
      }
      characteristic = selected
      // 与 iOS 保持一致：支持有应答写入就优先使用，靠逐包确认防止打印机缓冲溢出丢数据。
      writeType = if (selected.properties and BluetoothGattCharacteristic.PROPERTY_WRITE != 0) {
        BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
      } else {
        BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
      }
      connected = true
      ready.countDown()
    }

    override fun onCharacteristicWrite(
      target: BluetoothGatt,
      written: BluetoothGattCharacteristic,
      status: Int,
    ) {
      pendingWriteStatus = status
      pendingWrite?.countDown()
    }
  }

  override fun write(data: ByteArray) {
    synchronized(writeLock) {
      val activeGatt = gatt
      val activeCharacteristic = characteristic
      if (activeGatt == null || activeCharacteristic == null || !isConnected) {
        throw IllegalStateException("No Bluetooth printer is connected.")
      }
      // ATT 头占 3 字节；上限 512 是 GATT 单个特征值长度上限。
      val chunkSize = (mtu - ATT_HEADER_BYTES).coerceIn(MIN_CHUNK_BYTES, MAX_CHUNK_BYTES)
      var offset = 0
      while (offset < data.size) {
        val end = min(offset + chunkSize, data.size)
        writeChunk(activeGatt, activeCharacteristic, data.copyOfRange(offset, end))
        offset = end
      }
    }
  }

  override fun close() {
    if (!closed.compareAndSet(false, true)) {
      return
    }
    connected = false
    pendingWrite?.countDown()
    ready.countDown()
    gatt?.let { target ->
      try {
        target.disconnect()
      } catch (_: Exception) {
      }
      releaseGatt(target)
    }
  }

  private fun start(context: Context, device: BluetoothDevice) {
    // 指定 TRANSPORT_LE，避免双模芯片被系统选成 BR/EDR 后找不到 GATT 服务。
    gatt = device.connectGatt(context, false, callback, BluetoothDevice.TRANSPORT_LE)
      ?: throw IllegalStateException("Android could not start the BLE printer connection.")
  }

  private fun requestServiceDiscovery(target: BluetoothGatt) {
    if (closed.get() || !serviceDiscoveryRequested.compareAndSet(false, true)) {
      return
    }
    if (!target.discoverServices()) {
      failSetup("Android could not start BLE printer service discovery.")
    }
  }

  private fun failSetup(message: String) {
    if (setupError == null) {
      setupError = message
    }
    ready.countDown()
  }

  private fun writeChunk(target: BluetoothGatt, writable: BluetoothGattCharacteristic, chunk: ByteArray) {
    val latch = CountDownLatch(1)
    pendingWriteStatus = BluetoothGatt.GATT_FAILURE
    pendingWrite = latch
    try {
      var attempts = 0
      // 系统 GATT 队列偶发忙碌时 writeCharacteristic 直接返回失败，短暂退避后重试同一包。
      while (!startWrite(target, writable, chunk)) {
        attempts += 1
        if (attempts >= WRITE_START_ATTEMPTS || !isConnected) {
          throw IllegalStateException("Bluetooth printer connection lost: BLE write could not start.")
        }
        SystemClock.sleep(WRITE_RETRY_DELAY_MS)
      }
      if (!latch.await(WRITE_ACK_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
        throw IllegalStateException("Bluetooth printer connection lost: BLE write timed out.")
      }
      if (!isConnected) {
        throw IllegalStateException("Bluetooth printer connection lost while printing.")
      }
      if (pendingWriteStatus != BluetoothGatt.GATT_SUCCESS) {
        throw IllegalStateException("Bluetooth printer BLE write failed (status=$pendingWriteStatus).")
      }
    } finally {
      pendingWrite = null
    }
  }

  private fun startWrite(target: BluetoothGatt, writable: BluetoothGattCharacteristic, chunk: ByteArray): Boolean {
    return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      target.writeCharacteristic(writable, chunk, writeType) == BluetoothStatusCodes.SUCCESS
    } else {
      @Suppress("DEPRECATION")
      writable.writeType = writeType
      @Suppress("DEPRECATION")
      writable.setValue(chunk)
      @Suppress("DEPRECATION")
      target.writeCharacteristic(writable)
    }
  }

  private fun releaseGatt(target: BluetoothGatt) {
    try {
      target.close()
    } catch (_: Exception) {
    }
    if (gatt === target) {
      gatt = null
    }
  }

  companion object {
    private const val DEFAULT_ATT_MTU = 23
    private const val TARGET_ATT_MTU = 247
    private const val ATT_HEADER_BYTES = 3
    private const val MIN_CHUNK_BYTES = 20
    private const val MAX_CHUNK_BYTES = 512
    private const val MTU_FALLBACK_MS = 1_500L
    private const val WRITE_START_ATTEMPTS = 20
    private const val WRITE_RETRY_DELAY_MS = 15L
    private const val WRITE_ACK_TIMEOUT_MS = 5_000L

    private fun uuid16(value: String): UUID = UUID.fromString("0000$value-0000-1000-8000-00805F9B34FB")

    // GAP/GATT/设备信息/电池/Nordic DFU 中的可写特征值不是打印数据口，写进去会改名或进固件升级。
    private val IGNORED_SERVICE_UUIDS = setOf(
      uuid16("1800"),
      uuid16("1801"),
      uuid16("180A"),
      uuid16("180F"),
      uuid16("FE59"),
    )

    // 常见 BLE 打印机透传口优先；都不匹配时退回首个可写特征值（与 iOS 行为一致）。
    private val PREFERRED_CHARACTERISTIC_UUIDS = listOf(
      UUID.fromString("49535343-8841-43F4-A8D4-ECBE34729BB3"),
      UUID.fromString("BEF8D6C9-9C21-4C9E-B632-BD58C1009F9F"),
      uuid16("FF02"),
      uuid16("2AF1"),
    )

    private fun BluetoothGattCharacteristic.isWritable(): Boolean =
      properties and (
        BluetoothGattCharacteristic.PROPERTY_WRITE or BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE
      ) != 0

    internal fun selectWritableCharacteristic(services: List<BluetoothGattService>): BluetoothGattCharacteristic? {
      val candidates = services
        .filterNot { it.uuid in IGNORED_SERVICE_UUIDS }
        .flatMap { service -> service.characteristics.orEmpty().filter { it.isWritable() } }
      return PREFERRED_CHARACTERISTIC_UUIDS.firstNotNullOfOrNull { preferred ->
        candidates.firstOrNull { it.uuid == preferred }
      } ?: candidates.firstOrNull()
    }

    /** 阻塞直到可写特征值就绪；失败或超时都会释放 GATT，调用方无需再清理。 */
    fun open(
      context: Context,
      device: BluetoothDevice,
      timeoutMs: Long,
      onConnectionLost: (BlePrinterConnection) -> Unit,
      onDiag: (String, Map<String, Any?>) -> Unit = { _, _ -> },
    ): BlePrinterConnection {
      val connection = BlePrinterConnection(onConnectionLost, onDiag)
      try {
        connection.start(context, device)
        if (!connection.ready.await(timeoutMs, TimeUnit.MILLISECONDS)) {
          throw IllegalStateException("Bluetooth printer BLE connection timed out.")
        }
        connection.setupError?.let { throw IllegalStateException(it) }
        if (!connection.isConnected) {
          throw IllegalStateException("Bluetooth printer BLE connection was cancelled.")
        }
        return connection
      } catch (error: Exception) {
        connection.close()
        throw error
      }
    }
  }
}
