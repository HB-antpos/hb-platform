package expo.modules.hbappinstaller

import kotlin.math.max

/** 前几秒往往还没到满速（TCP 慢启动等），探测 10 秒再定目标速率，免得把限速压得过低。 */
internal const val RATE_LIMIT_PROBE_MILLIS = 10_000L
internal const val RATE_LIMIT_MIN_BYTES_PER_SECOND = 64L * 1024L
private const val RATE_LIMIT_WINDOW_MILLIS = 1_000L

/** 只接受 (0, 1) 之间的带宽占比；其余（含旧 JS 不传）一律视为不限速。 */
internal fun normalizedBandwidthShare(value: Double?): Double? =
  value?.takeIf { it.isFinite() && it > 0.0 && it < 1.0 }

/**
 * 后台下载 APK 时不占满网络：先不限速探测几秒实际下载速率，之后把速率压到探测值的 [bandwidthShare]，
 * 给登录、查询等正常使用留出带宽。
 *
 * 原理：读得慢 → 系统接收缓冲填满 → TCP 流控让服务器放慢发送，所以在读循环里按目标速率补睡即可真实降速。
 * 强制更新（用户被拦着等）由 JS 不传占比，不会走到这里。
 */
internal class ApkDownloadRateLimiter(
  private val bandwidthShare: Double,
  private val elapsedMillis: () -> Long,
  private val sleeper: (Long) -> Unit,
  private val probeMillis: Long = RATE_LIMIT_PROBE_MILLIS,
  private val minBytesPerSecond: Long = RATE_LIMIT_MIN_BYTES_PER_SECOND,
) {
  private var startedAt = -1L
  private var probeBytes = 0L
  private var windowStartedAt = 0L
  private var windowBytes = 0L

  /** 探测结束后确定的目标速率；探测期间为 null（不限速）。 */
  var limitBytesPerSecond: Long? = null
    private set

  /** 累计限速补睡时长；测速时扣除，后台限速不会被误判为网络差。 */
  var throttledMillis = 0L
    private set

  /** 每写入一块后调用；需要降速时在当前线程补睡。 */
  fun onBytesTransferred(count: Int) {
    if (count <= 0) return
    val now = elapsedMillis()
    if (startedAt < 0) startedAt = now
    val limit = limitBytesPerSecond
    if (limit == null) {
      probeBytes += count
      val elapsed = now - startedAt
      if (elapsed >= probeMillis) {
        val measured = probeBytes * 1_000L / max(elapsed, 1L)
        limitBytesPerSecond = max(minBytesPerSecond, (measured * bandwidthShare).toLong())
        windowStartedAt = now
        windowBytes = 0L
      }
      return
    }
    windowBytes += count
    // 这些字节按目标速率至少要花 requiredMillis；实际用时不足就补睡差额，平均速率即为目标速率。
    val requiredMillis = windowBytes * 1_000L / limit
    val actualMillis = now - windowStartedAt
    if (requiredMillis > actualMillis) {
      val pause = requiredMillis - actualMillis
      throttledMillis += pause
      sleeper(pause)
    }
    // 窗口按秒滚动：断续的网络不会攒出一大笔「欠账」后再连续不睡地猛冲。
    if (max(requiredMillis, actualMillis) >= RATE_LIMIT_WINDOW_MILLIS) {
      windowStartedAt = elapsedMillis()
      windowBytes = 0L
    }
  }
}
