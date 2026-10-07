package com.hbweb.expo

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import com.facebook.react.bridge.Arguments
import com.facebook.react.bridge.WritableArray
import java.util.ArrayDeque

/**
 * 标签打印机蓝牙链路的原生诊断缓冲。
 *
 * 只做被动记录：系统的 ACL / 配对 / 蓝牙开关广播、每次连接尝试的结果与耗时、BLE GATT 回调状态码。
 * 绝不发起任何额外的蓝牙操作（不做 SDP 探测、不扫描），免得干扰要观察的连接本身。
 * 事件先留在这里，由 JS 在连接失败/成功时调用 drainLinkDiagnostics 取走并并入链路诊断。
 *
 * 诊断只是旁路：record 内部任何异常都吞掉，绝不能影响连接与打印。
 */
internal class PrinterLinkDiagnostics(
  private val clock: () -> Long = { System.currentTimeMillis() },
) {
  private val lock = Any()
  private val events = ArrayDeque<Map<String, Any?>>()

  fun record(type: String, fields: Map<String, Any?>) {
    try {
      val event = LinkedHashMap<String, Any?>(fields.size + 2)
      event["ev"] = type
      event["atMs"] = clock()
      for ((key, value) in fields) {
        // ev / atMs 是保留键，字段里同名的丢弃。
        if (key != "ev" && key != "atMs") {
          event[key] = value
        }
      }
      synchronized(lock) {
        events.addLast(event)
        // JS 长时间不取（旧 JS、App 在后台）时只保留最近的，缓冲不会无限增长。
        while (events.size > MAX_EVENTS) {
          events.removeFirst()
        }
      }
    } catch (_: Throwable) {
    }
  }

  fun record(type: String, vararg fields: Pair<String, Any?>) {
    record(type, fields.toMap())
  }

  /** 取走并清空缓冲，按发生顺序返回。 */
  fun drain(): List<Map<String, Any?>> = synchronized(lock) {
    val copy = ArrayList(events)
    events.clear()
    copy
  }

  companion object {
    const val MAX_EVENTS = 120
  }
}

/** 事件值只会是 String / Boolean / 数字 / null，JS 侧按标量处理。 */
internal fun List<Map<String, Any?>>.toWritableArray(): WritableArray {
  val array = Arguments.createArray()
  for (event in this) {
    val map = Arguments.createMap()
    for ((key, value) in event) {
      when (value) {
        null -> map.putNull(key)
        is String -> map.putString(key, value)
        is Boolean -> map.putBoolean(key, value)
        is Int -> map.putInt(key, value)
        // 毫秒时间戳超出 Int，统一走 Double：JS number 对 2^53 以内的整数是精确的。
        is Long -> map.putDouble(key, value.toDouble())
        is Double -> map.putDouble(key, value)
        is Float -> map.putDouble(key, value.toDouble())
        else -> map.putString(key, value.toString())
      }
    }
    array.pushMap(map)
  }
  return array
}

internal fun bondStateName(state: Int): String = when (state) {
  BluetoothDevice.BOND_NONE -> "none"
  BluetoothDevice.BOND_BONDING -> "bonding"
  BluetoothDevice.BOND_BONDED -> "bonded"
  else -> "unknown($state)"
}

internal fun adapterStateName(state: Int): String = when (state) {
  BluetoothAdapter.STATE_OFF -> "off"
  BluetoothAdapter.STATE_TURNING_ON -> "turning_on"
  BluetoothAdapter.STATE_ON -> "on"
  BluetoothAdapter.STATE_TURNING_OFF -> "turning_off"
  else -> "unknown($state)"
}

internal fun deviceTypeName(type: Int): String = when (type) {
  BluetoothDevice.DEVICE_TYPE_CLASSIC -> "classic"
  BluetoothDevice.DEVICE_TYPE_LE -> "ble"
  BluetoothDevice.DEVICE_TYPE_DUAL -> "dual"
  else -> "unknown($type)"
}

/** 异常链压成一行：类名: 消息 <- 原因类名: 原因消息，最多三层、每层截到 160 字符。 */
internal fun describeThrowable(error: Throwable): String {
  val parts = ArrayList<String>(3)
  var current: Throwable? = error
  while (current != null && parts.size < 3) {
    parts.add("${current.javaClass.simpleName}: ${current.message.orEmpty()}".take(160))
    val cause = current.cause
    current = if (cause === current) null else cause
  }
  return parts.joinToString(" <- ")
}

/**
 * 系统是否仍认为与该设备存在 ACL 链路。BluetoothDevice.isConnected 是隐藏 API，
 * 部分系统版本不可用，反射失败一律返回 null（未知），不能当成“已断开”。
 */
@SuppressLint("DiscouragedPrivateApi", "PrivateApi")
internal fun aclConnectedOrNull(device: BluetoothDevice): Boolean? = try {
  BluetoothDevice::class.java.getMethod("isConnected").invoke(device) as? Boolean
} catch (_: Throwable) {
  null
}
