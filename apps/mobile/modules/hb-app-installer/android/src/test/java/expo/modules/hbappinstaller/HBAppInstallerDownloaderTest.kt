package expo.modules.hbappinstaller

import java.io.ByteArrayInputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files
import java.security.MessageDigest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class HBAppInstallerDownloaderTest {
  private val root = Files.createTempDirectory("hb-native-installer-test").toFile()
  private val destination = File(root, "hb-production-17.apk")

  @After fun tearDown() { root.deleteRecursively() }

  @Test fun `streams only exact trusted APK bytes then atomically publishes`() {
    val body = byteArrayOf(1, 2, 3, 4)
    val network = FakeNetwork().apply {
      respond("https://api.example.test/build", FakeResponse.ok(body))
    }

    val result = downloader(network).download(request(body))

    assertEquals(destination.toURI().toString(), result.fileUri)
    assertEquals(body.size.toLong(), result.sizeBytes)
    assertEquals(hash(body), result.sha256Hex)
    assertEquals(body.toList(), destination.readBytes().toList())
    assertFalse(destination.canExecute())
    assertFalse(File(root, "${destination.name}.part").exists())
    assertFalse(network.opened.single().instanceFollowRedirects)
  }

  @Test fun `reports start and completion progress against the verified size`() {
    val body = byteArrayOf(1, 2, 3, 4)
    val network = FakeNetwork().apply {
      respond("https://api.example.test/build", FakeResponse.ok(body))
    }
    val reports = mutableListOf<Pair<Long, Long>>()

    downloader(network).download(
      request(body),
      onProgress = ApkDownloadProgressListener { written, total -> reports += written to total },
    )

    assertEquals(listOf(0L to 4L, 4L to 4L), reports)
  }

  @Test fun `rejects hash mismatch and leaves neither final nor partial APK`() {
    val body = byteArrayOf(1, 2, 3, 4)
    val network = FakeNetwork().apply { respond("https://api.example.test/build", FakeResponse.ok(body)) }

    val error = assertThrows(InstallerException::class.java) {
      downloader(network).download(request(body, expectedHash = "00".repeat(32)))
    }

    assertEquals("APP_DOWNLOAD_SHA256_MISMATCH", error.code)
    assertCleaned()
  }

  @Test fun `rejects wrong content length before opening response body`() {
    val body = byteArrayOf(1, 2, 3, 4)
    val network = FakeNetwork().apply {
      respond("https://api.example.test/build", FakeResponse.ok(body, contentLength = "5"))
    }

    val error = assertThrows(InstallerException::class.java) { downloader(network).download(request(body)) }

    assertEquals("APP_DOWNLOAD_SIZE_MISMATCH", error.code)
    assertFalse(network.opened.single().inputOpened)
    assertCleaned()
  }

  @Test fun `rejects MIME type and untrusted redirect`() {
    val body = byteArrayOf(1, 2, 3, 4)
    val invalidMime = FakeNetwork().apply {
      respond("https://api.example.test/build", FakeResponse.ok(body, contentType = "text/html"))
    }
    assertEquals(
      "APP_DOWNLOAD_MIME_REJECTED",
      assertThrows(InstallerException::class.java) { downloader(invalidMime).download(request(body)) }.code,
    )
    assertCleaned()

    val redirect = FakeNetwork().apply {
      respond("https://api.example.test/build", FakeResponse.redirect("https://attacker.example.test/file.apk"))
    }
    assertEquals(
      "APP_DOWNLOAD_URL_REJECTED",
      assertThrows(InstallerException::class.java) { downloader(redirect).download(request(body)) }.code,
    )
    assertCleaned()
  }

  @Test fun `follows at most trusted HTTPS redirects without implicit following`() {
    val body = byteArrayOf(1, 2, 3, 4)
    val network = FakeNetwork().apply {
      respond("https://api.example.test/build", FakeResponse.redirect("https://cos.example.test/file.apk"))
      respond("https://cos.example.test/file.apk", FakeResponse.ok(body))
    }

    val result = downloader(network).download(
      request(body, origins = setOf("https://api.example.test", "https://cos.example.test")),
    )

    assertEquals("https://cos.example.test/file.apk", result.finalUrl)
    assertEquals(2, network.opened.size)
    assertTrue(network.opened.all { !it.instanceFollowRedirects })
  }

  private fun downloader(network: FakeNetwork) = HBAppInstallerDownloader(network::open)
  private fun request(
    body: ByteArray,
    expectedHash: String = hash(body),
    origins: Set<String> = setOf("https://api.example.test"),
  ) = ApkDownloadRequest(
    sourceUrl = "https://api.example.test/build",
    destinationFile = destination,
    destinationFileUri = destination.toURI().toString(),
    expectedSizeBytes = body.size.toLong(),
    expectedSha256Hex = expectedHash,
    trustedOrigins = origins,
  )
  private fun assertCleaned() {
    assertFalse(destination.exists())
    assertFalse(File(root, "${destination.name}.part").exists())
  }
}

private fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes)
  .joinToString("") { "%02X".format(it.toInt() and 0xff) }

private class FakeNetwork {
  private val responses = mutableMapOf<String, FakeResponse>()
  val opened = mutableListOf<FakeConnection>()
  fun respond(url: String, response: FakeResponse) { responses[url] = response }
  fun open(url: URL): HttpURLConnection = FakeConnection(url, responses[url.toString()] ?: error("missing $url")).also(opened::add)
}

private data class FakeResponse(
  val status: Int,
  val body: ByteArray = byteArrayOf(),
  val contentLength: String? = null,
  val contentType: String? = "application/vnd.android.package-archive",
  val location: String? = null,
) {
  companion object {
    fun ok(body: ByteArray, contentLength: String? = body.size.toString(), contentType: String? = "application/vnd.android.package-archive") =
      FakeResponse(HttpURLConnection.HTTP_OK, body, contentLength, contentType)
    fun redirect(location: String) = FakeResponse(HttpURLConnection.HTTP_MOVED_TEMP, location = location)
  }
}

private class FakeConnection(url: URL, private val response: FakeResponse) : HttpURLConnection(url) {
  var inputOpened = false
  override fun connect() = Unit
  override fun disconnect() = Unit
  override fun usingProxy() = false
  override fun getResponseCode() = response.status
  override fun getInputStream(): ByteArrayInputStream { inputOpened = true; return ByteArrayInputStream(response.body) }
  override fun getHeaderField(name: String?) = when {
    name.equals("Content-Length", true) -> response.contentLength
    name.equals("Content-Type", true) -> response.contentType
    name.equals("Location", true) -> response.location
    else -> null
  }
}
