package expo.modules.hbappinstaller

import kotlin.math.max

/** 刚开始下载的前 10 秒往往还没到满速（TCP 慢启动等），这段时间不给出速率，避免误报网络差。 */
internal const val SPEED_WARMUP_MILLIS = 10_000L

/** 速率取最近 5 秒的滑动窗口：不被瞬时抖动带偏，网络变差时也能较快反映出来。 */
internal const val SPEED_WINDOW_MILLIS = 5_000L
private const val SPEED_SAMPLE_INTERVAL_MILLIS = 250L

/**
 * 下载链路实测速率 = 窗口内写入字节 ÷（窗口用时 − 其间限速主动补睡的时长）。
 * 扣掉补睡后，后台限速下载不会因为「被我们自己压慢」而被误判为网络差。
 * 只在下载线程上调用，无需同步。
 */
internal class ApkDownloadSpeedMeter(
  private val throttledMillis: () -> Long = { 0L },
  private val warmupMillis: Long = SPEED_WARMUP_MILLIS,
  private val windowMillis: Long = SPEED_WINDOW_MILLIS,
) {
  private class Sample(val at: Long, val bytes: Long, val throttled: Long)

  private val samples = ArrayDeque<Sample>()
  private var startedAt = -1L

  /** 记录当前累计字节并返回链路速率（字节/秒）；预热期内返回 null。 */
  fun sample(now: Long, bytesWritten: Long): Long? {
    val throttled = throttledMillis()
    if (startedAt < 0) startedAt = now
    // 只保留一个早于窗口起点的样本作基准，更早的样本丢弃。
    while (samples.size >= 2 && now - samples[1].at >= windowMillis) samples.removeFirst()
    val base = samples.firstOrNull()
    // 读循环每块都会调用；按间隔抽样，样本数恒定在窗口/间隔量级。
    if (base == null || now - samples.last().at >= SPEED_SAMPLE_INTERVAL_MILLIS) {
      samples.addLast(Sample(now, bytesWritten, throttled))
    }
    if (base == null || now - startedAt < warmupMillis) return null
    val activeMillis = (now - base.at) - (throttled - base.throttled)
    return max(bytesWritten - base.bytes, 0L) * 1_000L / max(activeMillis, 1L)
  }
}
