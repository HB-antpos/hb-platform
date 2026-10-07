package expo.modules.hbappinstaller

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class HBAppInstallerDownloadSpeedMeterTest {
  private var now = 0L
  private var written = 0L
  private var throttled = 0L

  private fun meter() = ApkDownloadSpeedMeter(throttledMillis = { throttled })

  /** 以每 100ms 一块的节奏喂数据；sleepPerSecond 模拟限速器每秒补睡的时长。 */
  private fun feed(meter: ApkDownloadSpeedMeter, bytesPerSecond: Long, durationMillis: Long, sleepPerSecond: Long = 0L): Long? {
    var last: Long? = null
    val end = now + durationMillis
    while (now < end) {
      last = meter.sample(now, written)
      written += bytesPerSecond / 10
      throttled += sleepPerSecond / 10
      now += 100L
    }
    return last
  }

  private fun assertAbout(expected: Long, actual: Long?) {
    val value = requireNotNull(actual) { "预热结束后应给出速率" }
    assertTrue("期望约 $expected，实际 $value", value in expected * 9 / 10..expected * 11 / 10)
  }

  @Test fun `stays silent during the ten second warm-up`() {
    val meter = meter()
    assertNull("前 10 秒往往还没到满速，不给速率", feed(meter, bytesPerSecond = 300_000L, durationMillis = 9_900L))
    assertAbout(300_000L, feed(meter, bytesPerSecond = 300_000L, durationMillis = 200L))
  }

  @Test fun `measures a slow link below the warning threshold`() {
    val meter = meter()
    assertAbout(50L * 1024L, feed(meter, bytesPerSecond = 50L * 1024L, durationMillis = 12_000L))
  }

  @Test fun `excludes throttle sleep so a throttled fast link is not reported as slow`() {
    val meter = meter()
    // 实际写入 100KB/s，但其中每秒有 500ms 是限速补睡：链路真实速率约 200KB/s，高于 128KB/s 门槛。
    assertAbout(200L * 1024L, feed(meter, bytesPerSecond = 100L * 1024L, durationMillis = 12_000L, sleepPerSecond = 500L))
  }

  @Test fun `follows the recent window when the network degrades`() {
    val meter = meter()
    feed(meter, bytesPerSecond = 1_000_000L, durationMillis = 15_000L)
    assertAbout(40L * 1024L, feed(meter, bytesPerSecond = 40L * 1024L, durationMillis = 6_000L))
  }

  @Test fun `a stalled transfer reads as zero`() {
    val meter = meter()
    feed(meter, bytesPerSecond = 500_000L, durationMillis = 11_000L)
    feed(meter, bytesPerSecond = 0L, durationMillis = 6_000L)
    assertEquals(0L, meter.sample(now, written))
  }
}
