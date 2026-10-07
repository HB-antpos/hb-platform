package expo.modules.hbappinstaller

import org.junit.Assert.assertEquals
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
      listener = ApkDownloadProgressListener { _, _ ->
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

  @Test fun `missing listener or empty total is a no-op`() {
    ApkDownloadProgressReporter(10L, null, { now }).report(10L)
    reporter(totalBytes = 0L).report(0L)

    assertEquals(emptyList<Pair<Long, Long>>(), reports)
  }

  private fun reporter(totalBytes: Long) = ApkDownloadProgressReporter(
    totalBytes = totalBytes,
    listener = ApkDownloadProgressListener { written, total -> reports += written to total },
    elapsedMillis = { now },
  )
}
