package expo.modules.hbappinstaller

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class HBAppInstallerDownloadRateLimiterTest {
  private var now = 0L
  private val sleeps = mutableListOf<Long>()
  private val sleeper: (Long) -> Unit = { millis ->
    sleeps += millis
    now += millis
  }

  private fun limiter(share: Double = 0.5) = ApkDownloadRateLimiter(
    bandwidthShare = share,
    elapsedMillis = { now },
    sleeper = sleeper,
    probeMillis = 3_000L,
    minBytesPerSecond = 64L * 1024L,
  )

  /** 以每 100ms 一块的节奏喂数据，模拟固定速率的网络。 */
  private fun feedAtRate(limiter: ApkDownloadRateLimiter, bytesPerSecond: Long, durationMillis: Long) {
    val chunk = (bytesPerSecond / 10).toInt()
    val end = now + durationMillis
    while (now < end) {
      limiter.onBytesTransferred(chunk)
      now += 100L
    }
  }

  @Test fun `probes unthrottled then caps throughput to the requested share of the measured rate`() {
    val limiter = limiter(share = 0.5)
    feedAtRate(limiter, bytesPerSecond = 1_000_000L, durationMillis = 3_100L)

    assertTrue("探测期不应限速", sleeps.isEmpty())
    val limit = requireNotNull(limiter.limitBytesPerSecond) { "探测 3 秒后应确定目标速率" }
    assertTrue("目标速率应约为探测值的一半，实际 $limit", limit in 450_000L..550_000L)

    // 网络很快（数据瞬间到达）时：累计补睡让平均速率回到目标值。
    val startedAt = now
    repeat(20) { limiter.onBytesTransferred(64 * 1024) }
    val elapsed = now - startedAt
    val actualRate = 20L * 64 * 1024 * 1_000 / elapsed
    assertTrue("平均速率应接近目标 $limit，实际 $actualRate", actualRate in (limit * 9 / 10)..(limit * 11 / 10))
  }

  @Test fun `probes ten seconds by default because the first seconds rarely reach full speed`() {
    val limiter = ApkDownloadRateLimiter(bandwidthShare = 0.5, elapsedMillis = { now }, sleeper = sleeper)
    feedAtRate(limiter, bytesPerSecond = 1_000_000L, durationMillis = 9_900L)
    assertNull("10 秒内仍在探测、不限速", limiter.limitBytesPerSecond)

    feedAtRate(limiter, bytesPerSecond = 1_000_000L, durationMillis = 200L)
    assertTrue("满 10 秒后确定目标速率", requireNotNull(limiter.limitBytesPerSecond) in 450_000L..550_000L)
  }

  @Test fun `accumulates throttle sleep so the speed meter can exclude it`() {
    val limiter = limiter(share = 0.5)
    feedAtRate(limiter, bytesPerSecond = 1_000_000L, durationMillis = 3_100L)
    repeat(20) { limiter.onBytesTransferred(64 * 1024) }

    assertTrue(sleeps.isNotEmpty())
    assertEquals(sleeps.sum(), limiter.throttledMillis)
  }

  @Test fun `never throttles below the floor on a very slow link`() {
    val limiter = limiter(share = 0.5)
    feedAtRate(limiter, bytesPerSecond = 20_000L, durationMillis = 3_100L)
    limiter.onBytesTransferred(2_000)

    assertEquals(64L * 1024L, limiter.limitBytesPerSecond)
  }

  @Test fun `a link already slower than the target needs no extra sleep`() {
    val limiter = limiter(share = 0.5)
    feedAtRate(limiter, bytesPerSecond = 1_000_000L, durationMillis = 3_100L)
    assertTrue(requireNotNull(limiter.limitBytesPerSecond) > 200_000L)

    // 之后网络降到 200KB/s（目标约 500KB/s），读本身就已经够慢，不应再补睡。
    // 注意：探测刚结束时若有突发大块，限速器会在同一窗口内补睡还账，这是预期行为，不在本用例范围。
    feedAtRate(limiter, bytesPerSecond = 200_000L, durationMillis = 5_000L)

    assertTrue("慢网不应额外补睡，实际补睡 $sleeps", sleeps.isEmpty())
  }

  @Test fun `only shares strictly between zero and one enable throttling`() {
    assertNull(normalizedBandwidthShare(null))
    assertNull(normalizedBandwidthShare(0.0))
    assertNull(normalizedBandwidthShare(1.0))
    assertNull(normalizedBandwidthShare(1.5))
    assertNull(normalizedBandwidthShare(-0.2))
    assertNull(normalizedBandwidthShare(Double.NaN))
    assertEquals(0.5, normalizedBandwidthShare(0.5)!!, 0.0)
  }

  @Test fun `downloader throttles only when a share is requested and still publishes the exact bytes`() {
    val root = java.nio.file.Files.createTempDirectory("hb-rate-limit-test").toFile()
    try {
      val body = ByteArray(512 * 1024) { (it % 251).toByte() }
      val bodyHash = java.security.MessageDigest.getInstance("SHA-256").digest(body)
        .joinToString("") { "%02X".format(it.toInt() and 0xff) }
      fun run(share: Double?): List<Long> {
        val destination = java.io.File(root, "hb-${share ?: "none"}.apk")
        val throttleSleeps = mutableListOf<Long>()
        var clock = 0L
        val downloader = HBAppInstallerDownloader(
          connectionFactory = { url -> RateLimitFakeConnection(url, body) },
          // 探测期每次取时间前进 2 秒（约 6 块后满 10 秒探测结束），之后只前进 1 毫秒，模拟数据瞬间到达、必须补睡。
          elapsedMillis = { clock.also { clock += if (clock < 11_000L) 2_000L else 1L } },
          sleeper = { millis -> throttleSleeps += millis },
        )
        val result = downloader.download(
          ApkDownloadRequest(
            sourceUrl = "https://api.example.test/build",
            destinationFile = destination,
            destinationFileUri = destination.toURI().toString(),
            expectedSizeBytes = body.size.toLong(),
            expectedSha256Hex = bodyHash,
            trustedOrigins = setOf("https://api.example.test"),
            bandwidthShare = share,
          ),
        )
        assertEquals(bodyHash, result.sha256Hex)
        assertEquals(body.toList(), destination.readBytes().toList())
        return throttleSleeps
      }

      assertTrue("不传占比（如强制更新）绝不限速", run(null).isEmpty())
      assertTrue("传了占比才限速", run(0.5).isNotEmpty())
    } finally {
      root.deleteRecursively()
    }
  }
}

private class RateLimitFakeConnection(url: java.net.URL, private val body: ByteArray) : java.net.HttpURLConnection(url) {
  override fun connect() = Unit
  override fun disconnect() = Unit
  override fun usingProxy() = false
  override fun getResponseCode() = HTTP_OK
  override fun getInputStream() = java.io.ByteArrayInputStream(body)
  override fun getHeaderField(name: String?) = when {
    name.equals("Content-Length", true) -> body.size.toString()
    name.equals("Content-Type", true) -> "application/vnd.android.package-archive"
    else -> null
  }
}
