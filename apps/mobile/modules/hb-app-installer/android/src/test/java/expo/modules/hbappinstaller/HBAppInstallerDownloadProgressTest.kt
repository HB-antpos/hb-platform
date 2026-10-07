package expo.modules.hbappinstaller

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class HBAppInstallerDownloadProgressTest {
  private var now = 0L
  private val reports = mutableListOf<Pair<Long, Long>>()

  @Test fun `reports start, throttles by percent and interval, and always reports completion once`() {
    val reporter = reporter(totalBytes = 1_000L)

    reporter.report(0L)
    now = 100L
    reporter.report(500L) // 间隔不足 200ms，丢弃
    now = 250L
    reporter.report(500L)
    now = 500L
    reporter.report(505L) // 百分比未前进，丢弃
    now = 510L
    reporter.report(1_000L) // 写满不受间隔限制
    now = 900L
    reporter.report(1_000L) // 已报过 100%，不重复

    assertEquals(listOf(0L to 1_000L, 500L to 1_000L, 1_000L to 1_000L), reports)
  }

  @Test fun `clamps out-of-range byte counts to the verified total`() {
    val reporter = reporter(totalBytes = 10L)

    reporter.report(-5L)
    now = 1_000L
    reporter.report(50L)

    assertEquals(listOf(0L to 10L, 10L to 10L), reports)
  }

  @Test fun `listener failures never escape into the download`() {
    var calls = 0
    val reporter = ApkDownloadProgressReporter(
      totalBytes = 10L,
      listener = ApkDownloadProgressListener { _, _, _ ->
        calls += 1
        throw IllegalArgumentException("Unsupported event: onDownloadProgress.")
      },
      elapsedMillis = { now },
    )

    reporter.report(0L)
    now = 1_000L
    reporter.report(10L)

    assertEquals(2, calls)
  }

  @Test fun `with a speed meter it heartbeats every second after warm-up even without percent progress`() {
    val events = mutableListOf<Triple<Long, Long, Long?>>()
    val reporter = ApkDownloadProgressReporter(
      totalBytes = 100L * 1024L * 1024L,
      listener = ApkDownloadProgressListener { written, total, speed -> events += Triple(now, written, speed) },
      elapsedMillis = { now },
      speedMeter = ApkDownloadSpeedMeter(warmupMillis = 10_000L, windowMillis = 5_000L),
    )

    // 50KB/s 慢网：每 100ms 一块 5KB，整个过程百分比都到不了 1%。
    var written = 0L
    while (now <= 13_000L) {
      reporter.report(written)
      written += 5L * 1024L
      now += 100L
    }

    assertEquals("开头报 0，预热期内百分比不前进就不再报", 0L, events.first().second)
    assertEquals(null, events.first().third)
    val heartbeats = events.drop(1)
    assertEquals("预热结束后每秒一次心跳：10、11、12、13 秒", listOf(10_000L, 11_000L, 12_000L, 13_000L), heartbeats.map { it.first })
    heartbeats.forEach { (_, _, speed) ->
      assertTrue("心跳带实测速率约 50KB/s，实际 $speed", requireNotNull(speed) in 45L * 1024L..55L * 1024L)
    }
  }

  @Test fun `missing listener or empty total is a no-op`() {
    ApkDownloadProgressReporter(10L, null, { now }).report(10L)
    reporter(totalBytes = 0L).report(0L)

    assertEquals(emptyList<Pair<Long, Long>>(), reports)
  }

  private fun reporter(totalBytes: Long) = ApkDownloadProgressReporter(
    totalBytes = totalBytes,
    listener = ApkDownloadProgressListener { written, total, _ -> reports += written to total },
    elapsedMillis = { now },
  )
}
