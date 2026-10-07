package expo.modules.hbappinstaller

import java.io.File
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class HBAppInstallerDownloaderTest {
  private val root = Files.createTempDirectory("hb-apk-download-test").toFile()
  private val destination = File(root, "hb-pos-handheld-test.apk")

  @After
  fun tearDown() {
    root.deleteRecursively()
  }

  @Test
  fun `downloads an exact-size APK through one trusted HTTPS response`() {
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.ok(byteArrayOf(1, 2, 3, 4)),
      )
    }

    val result = downloader(network).download(request(expectedSizeBytes = 4))

    assertEquals(destination.toURI().toString(), result.fileUri)
    assertEquals(4L, result.sizeBytes)
    assertEquals("https://updates.example.test/build.apk", result.finalUrl)
    assertEquals(listOf<Byte>(1, 2, 3, 4), destination.readBytes().toList())
    assertFalse(destination.canExecute())
    assertNoPartialFiles()
    val connection = network.opened.single()
    assertFalse(connection.instanceFollowRedirects)
    assertEquals("GET", connection.requestMethod)
    assertEquals(1_234, connection.connectTimeout)
    assertEquals(5_678, connection.readTimeout)
    assertEquals(
      mapOf(
        "Accept" to "application/vnd.android.package-archive",
        "Accept-Encoding" to "identity",
      ),
      connection.requestHeaders,
    )
  }

  @Test
  fun `reports throttled progress while streaming and completion exactly once`() {
    var now = 0L
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.ok(ByteArray(400) { it.toByte() }, maximumReadChunk = 100),
      )
    }
    val reports = mutableListOf<Pair<Long, Long>>()
    val downloader = HBAppInstallerDownloader(
      connectionFactory = network::open,
      elapsedMillis = { now.also { now += 150L } },
    )

    downloader.download(
      request(expectedSizeBytes = 400),
      onProgress = ApkDownloadProgressListener { written, total -> reports += written to total },
    )

    // 每次上报时钟走 150ms：100、300 字节两次距上一帧不足 200ms 被节流，写满必报。
    assertEquals(listOf(0L to 400L, 200L to 400L, 400L to 400L), reports)
    assertEquals(400L, destination.length())
  }

  @Test
  fun `rejects an oversized Content-Length before opening the body and cleans files`() {
    destination.writeText("stale")
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.ok(
          body = byteArrayOf(1, 2, 3, 4, 5),
          contentLength = "5",
        ),
      )
    }

    assertThrows(InstallerException::class.java) {
      downloader(network).download(request(expectedSizeBytes = 4))
    }

    assertFalse(network.opened.single().inputOpened)
    assertCleaned()
  }

  @Test
  fun `stops a chunked oversized body mid-stream and cleans files`() {
    val response = FakeResponse.ok(
      body = ByteArray(10) { it.toByte() },
      contentLength = null,
      maximumReadChunk = 3,
    )
    val network = FakeNetwork().apply {
      respond("https://updates.example.test/build.apk", response)
    }

    assertThrows(InstallerException::class.java) {
      downloader(network).download(request(expectedSizeBytes = 4))
    }

    val stream = network.opened.single().openedStream
    assertEquals(6, stream?.bytesRead)
    assertTrue((stream?.bytesRead ?: Int.MAX_VALUE) < response.body.size)
    assertTrue(network.opened.single().disconnected)
    assertCleaned()
  }

  @Test
  fun `rejects a completed body whose size differs from signed metadata`() {
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.ok(byteArrayOf(1, 2, 3), contentLength = null),
      )
    }

    assertThrows(InstallerException::class.java) {
      downloader(network).download(request(expectedSizeBytes = 4))
    }

    assertCleaned()
  }

  @Test
  fun `follows a trusted same-origin relative redirect`() {
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/releases/current",
        FakeResponse.redirect("../artifacts/build.apk"),
      )
      respond(
        "https://updates.example.test/artifacts/build.apk",
        FakeResponse.ok(byteArrayOf(1, 2, 3, 4)),
      )
    }

    val result = downloader(network).download(
      request(
        url = "https://updates.example.test/releases/current",
        expectedSizeBytes = 4,
      ),
    )

    assertEquals("https://updates.example.test/artifacts/build.apk", result.finalUrl)
    assertEquals(2, network.opened.size)
    assertTrue(network.opened.all { !it.instanceFollowRedirects })
  }

  @Test
  fun `allows a redirect to another explicitly trusted origin without sensitive headers`() {
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.redirect("https://cdn.example.test:443/releases/build.apk"),
      )
      respond(
        "https://cdn.example.test:443/releases/build.apk",
        FakeResponse.ok(byteArrayOf(1, 2, 3, 4)),
      )
    }

    val result = downloader(network).download(
      request(
        expectedSizeBytes = 4,
        trustedOrigins = setOf(
          "https://updates.example.test",
          "https://cdn.example.test",
        ),
      ),
    )

    assertEquals("https://cdn.example.test:443/releases/build.apk", result.finalUrl)
    assertTrue(
      network.opened.all { connection ->
        connection.requestHeaders.keys.none {
          it.equals("Authorization", ignoreCase = true) ||
            it.equals("Cookie", ignoreCase = true) ||
            it.equals("Proxy-Authorization", ignoreCase = true)
        }
      },
    )
  }

  @Test
  fun `rejects missing or untrusted redirects and HTTPS downgrade`() {
    for (location in listOf<String?>(
      null,
      "https://attacker.example/fake.apk",
      "https://updates.example.test:444/build.apk",
      "http://updates.example.test/build.apk",
    )) {
      val network = FakeNetwork().apply {
        respond(
          "https://updates.example.test/build.apk",
          FakeResponse(
            statusCode = HttpURLConnection.HTTP_MOVED_TEMP,
            location = location,
          ),
        )
      }

      assertThrows(InstallerException::class.java) {
        downloader(network).download(request(expectedSizeBytes = 4))
      }
      assertCleaned()
    }
  }

  @Test
  fun `cleans partial and target files when the download is cancelled`() {
    destination.writeText("stale")
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.ok(byteArrayOf(1, 2, 3, 4)),
      )
    }

    Thread.currentThread().interrupt()
    try {
      assertThrows(InstallerException::class.java) {
        downloader(network).download(request(expectedSizeBytes = 4))
      }
    } finally {
      Thread.interrupted()
    }

    assertCleaned()
  }

  @Test
  fun `rejects redirect loops and more than five redirects`() {
    val loop = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse.redirect("/next.apk"),
      )
      respond(
        "https://updates.example.test/next.apk",
        FakeResponse.redirect("/build.apk"),
      )
    }
    assertThrows(InstallerException::class.java) {
      downloader(loop).download(request(expectedSizeBytes = 4))
    }
    assertEquals(2, loop.opened.size)
    assertCleaned()

    val overLimit = FakeNetwork().apply {
      for (index in 0..5) {
        respond(
          "https://updates.example.test/$index.apk",
          FakeResponse.redirect("/${index + 1}.apk"),
        )
      }
    }
    assertThrows(InstallerException::class.java) {
      downloader(overLimit).download(
        request(
          url = "https://updates.example.test/0.apk",
          expectedSizeBytes = 4,
        ),
      )
    }
    assertEquals(6, overLimit.opened.size)
    assertCleaned()
  }

  @Test
  fun `rejects HTTP errors and always removes stale target and partial files`() {
    destination.writeText("stale")
    val network = FakeNetwork().apply {
      respond(
        "https://updates.example.test/build.apk",
        FakeResponse(statusCode = 503),
      )
    }

    assertThrows(InstallerException::class.java) {
      downloader(network).download(request(expectedSizeBytes = 4))
    }

    assertTrue(network.opened.single().disconnected)
    assertCleaned()
  }

  @Test
  fun `rejects invalid expected sizes and initial HTTP before opening a connection`() {
    for (invalidSize in listOf(0L, -1L, APK_DOWNLOAD_MAX_SIZE_BYTES + 1L)) {
      val network = FakeNetwork()
      assertThrows(InstallerException::class.java) {
        downloader(network).download(request(expectedSizeBytes = invalidSize))
      }
      assertTrue(network.opened.isEmpty())
      assertCleaned()
    }

    val network = FakeNetwork()
    assertThrows(InstallerException::class.java) {
      downloader(network).download(
        request(
          url = "http://updates.example.test/build.apk",
          expectedSizeBytes = 4,
        ),
      )
    }
    assertTrue(network.opened.isEmpty())
    assertCleaned()
  }

  private fun downloader(network: FakeNetwork) = HBAppInstallerDownloader(
    connectionFactory = network::open,
    maximumRedirects = 5,
    connectTimeoutMillis = 1_234,
    readTimeoutMillis = 5_678,
  )

  private fun request(
    url: String = "https://updates.example.test/build.apk",
    expectedSizeBytes: Long,
    trustedOrigins: Set<String> = setOf("https://updates.example.test"),
  ) = ApkDownloadRequest(
    sourceUrl = url,
    destinationFile = destination,
    expectedSizeBytes = expectedSizeBytes,
    trustedOrigins = trustedOrigins,
  )

  private fun assertCleaned() {
    assertFalse(destination.exists())
    assertNoPartialFiles()
  }

  private fun assertNoPartialFiles() {
    assertTrue(
      root.listFiles().orEmpty().none {
        it.name.contains(".part")
      },
    )
  }
}

private class FakeNetwork {
  private val responses = mutableMapOf<String, FakeResponse>()
  val opened = mutableListOf<FakeHttpURLConnection>()

  fun respond(url: String, response: FakeResponse) {
    responses[url] = response
  }

  fun open(url: URL): HttpURLConnection {
    val response = responses[url.toString()]
      ?: error("没有为 ${url} 配置本地响应")
    return FakeHttpURLConnection(url, response).also(opened::add)
  }
}

private data class FakeResponse(
  val statusCode: Int,
  val body: ByteArray = byteArrayOf(),
  val contentLength: String? = null,
  val location: String? = null,
  val maximumReadChunk: Int = Int.MAX_VALUE,
) {
  companion object {
    fun ok(
      body: ByteArray,
      contentLength: String? = body.size.toString(),
      maximumReadChunk: Int = Int.MAX_VALUE,
    ) = FakeResponse(
      statusCode = HttpURLConnection.HTTP_OK,
      body = body,
      contentLength = contentLength,
      maximumReadChunk = maximumReadChunk,
    )

    fun redirect(location: String) = FakeResponse(
      statusCode = HttpURLConnection.HTTP_MOVED_TEMP,
      location = location,
    )
  }
}

private class FakeHttpURLConnection(
  url: URL,
  private val response: FakeResponse,
) : HttpURLConnection(url) {
  val requestHeaders = linkedMapOf<String, String>()
  var disconnected = false
  var inputOpened = false
  var openedStream: TrackingInputStream? = null

  override fun connect() = Unit

  override fun disconnect() {
    disconnected = true
  }

  override fun usingProxy(): Boolean = false

  override fun getResponseCode(): Int = response.statusCode

  override fun getHeaderField(name: String?): String? = when {
    name.equals("Content-Length", ignoreCase = true) -> response.contentLength
    name.equals("Location", ignoreCase = true) -> response.location
    else -> null
  }

  override fun setRequestProperty(key: String?, value: String?) {
    if (key != null && value != null) requestHeaders[key] = value
  }

  override fun getInputStream(): InputStream {
    inputOpened = true
    return TrackingInputStream(response.body, response.maximumReadChunk).also {
      openedStream = it
    }
  }
}

private class TrackingInputStream(
  private val bytes: ByteArray,
  private val maximumReadChunk: Int,
) : InputStream() {
  var bytesRead: Int = 0
    private set

  override fun read(): Int {
    if (bytesRead >= bytes.size) return -1
    return bytes[bytesRead++].toInt() and 0xFF
  }

  override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
    if (bytesRead >= bytes.size) return -1
    val count = minOf(length, maximumReadChunk, bytes.size - bytesRead)
    bytes.copyInto(buffer, offset, bytesRead, bytesRead + count)
    bytesRead += count
    return count
  }
}
