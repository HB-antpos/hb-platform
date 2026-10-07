package expo.modules.hbappinstaller

import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.IDN
import java.net.URI
import java.net.URL
import java.security.MessageDigest
import java.util.Locale

private const val APK_MIME_TYPE = "application/vnd.android.package-archive"
private const val MAXIMUM_REDIRECTS = 5
private const val DOWNLOAD_BUFFER_SIZE = 64 * 1024

internal data class ApkDownloadRequest(
  val sourceUrl: String,
  val destinationFile: File,
  val destinationFileUri: String,
  val expectedSizeBytes: Long,
  val expectedSha256Hex: String,
  val trustedOrigins: Set<String>,
  /** 后台下载占用带宽的比例（0~1，不含端点）；null 表示不限速（如强制更新）。 */
  val bandwidthShare: Double? = null,
)

internal data class ApkDownloadResult(
  val fileUri: String,
  val sizeBytes: Long,
  val sha256Hex: String,
  val finalUrl: String,
)

/** 只接受受信 HTTPS origin；每一跳重定向都重新校验，且永不自动携带认证 header。 */
internal class HBAppInstallerDownloader(
  private val connectionFactory: (URL) -> HttpURLConnection = { it.openConnection() as HttpURLConnection },
  private val elapsedMillis: () -> Long = ::monotonicMillis,
  private val sleeper: (Long) -> Unit = ::sleepForDownloadThrottle,
) {
  fun download(
    request: ApkDownloadRequest,
    onProgress: ApkDownloadProgressListener? = null,
  ): ApkDownloadResult {
    validateSize(request.expectedSizeBytes)
    val expectedHash = normalizedSha256(request.expectedSha256Hex, "APP_DOWNLOAD_METADATA_INVALID")
    val trusted = request.trustedOrigins.mapTo(linkedSetOf(), ::parseTrustedOrigin)
    if (trusted.isEmpty()) throw rejectedUrl("可信下载 origin 为空。")
    val destination = request.destinationFile.canonicalFile
    val parent = destination.parentFile ?: throw failure("APK 下载目标目录无效。")
    if (!parent.isDirectory) throw failure("APK 下载目标目录不存在。")
    val partial = File(parent, "${destination.name}.part")
    var completed = false
    try {
      deleteOrThrow(partial)
      partial.createNewFile()
      partial.setExecutable(false, false)
      val rateLimiter = normalizedBandwidthShare(request.bandwidthShare)?.let {
        ApkDownloadRateLimiter(it, elapsedMillis, sleeper)
      }
      // 有人监听进度才测速；测速扣掉限速补睡，后台限速不会被当成网络慢。
      val speedMeter = onProgress?.let {
        ApkDownloadSpeedMeter(throttledMillis = { rateLimiter?.throttledMillis ?: 0L })
      }
      val progress = ApkDownloadProgressReporter(
        request.expectedSizeBytes,
        onProgress,
        elapsedMillis,
        speedMeter = speedMeter,
      )
      val response = streamResponse(request.sourceUrl, trusted, request.expectedSizeBytes, partial, progress, rateLimiter)
      val actualHash = response.sha256Hex
      if (!MessageDigest.isEqual(actualHash.hexBytes(), expectedHash.hexBytes())) {
        throw InstallerException("APP_DOWNLOAD_SHA256_MISMATCH", "APK 下载内容与已验证 SHA-256 不一致。")
      }
      if (partial.length() != request.expectedSizeBytes) throw sizeMismatch()
      // 仅在完整性校验完成后替换目标，避免残缺文件成为可安装 APK。
      deleteOrThrow(destination)
      if (!partial.renameTo(destination)) throw failure("无法原子提交 APK 临时文件。")
      destination.setExecutable(false, false)
      completed = true
      return ApkDownloadResult(request.destinationFileUri, response.sizeBytes, actualHash, response.finalUrl)
    } finally {
      if (!completed) partial.delete()
    }
  }

  private fun streamResponse(
    sourceUrl: String,
    trustedOrigins: Set<TrustedOrigin>,
    expectedSize: Long,
    partial: File,
    progress: ApkDownloadProgressReporter,
    rateLimiter: ApkDownloadRateLimiter?,
  ): StreamResult {
    var current = parseTrustedUrl(sourceUrl, trustedOrigins)
    val visited = linkedSetOf<String>()
    var redirects = 0
    while (true) {
      if (!visited.add(current.loopKey)) throw redirectRejected("APK 下载重定向形成循环。")
      val connection = connectionFactory(current.uri.toURL()).apply {
        instanceFollowRedirects = false
        requestMethod = "GET"
        connectTimeout = 15_000
        readTimeout = 30_000
        useCaches = false
        setRequestProperty("Accept", APK_MIME_TYPE)
        setRequestProperty("Accept-Encoding", "identity")
      }
      try {
        val status = connection.responseCode
        if (status in redirectStatuses) {
          if (redirects >= MAXIMUM_REDIRECTS) throw redirectRejected("APK 下载重定向次数超过限制。")
          val location = connection.getHeaderField("Location")?.trim()?.takeIf { it.isNotEmpty() }
            ?: throw redirectRejected("APK 下载重定向缺少 Location。")
          current = try {
            parseTrustedUrl(current.uri.resolve(URI(location)).normalize().toASCIIString(), trustedOrigins)
          } catch (error: InstallerException) {
            throw error
          } catch (error: Exception) {
            throw redirectRejected("APK 下载重定向地址无效。", error)
          }
          redirects += 1
          continue
        }
        if (status !in 200..299) throw InstallerException("APP_DOWNLOAD_HTTP_ERROR", "APK 下载服务器返回 HTTP $status。")
        validateContentType(connection.getHeaderField("Content-Type"))
        validateContentLength(connection.getHeaderField("Content-Length"), expectedSize)
        return streamExact(connection, partial, expectedSize, current.uri.toASCIIString(), progress, rateLimiter)
      } finally {
        connection.disconnect()
      }
    }
  }

  private fun streamExact(
    connection: HttpURLConnection,
    partial: File,
    expectedSize: Long,
    finalUrl: String,
    progress: ApkDownloadProgressReporter,
    rateLimiter: ApkDownloadRateLimiter?,
  ): StreamResult {
    val digest = MessageDigest.getInstance("SHA-256")
    val buffer = ByteArray(DOWNLOAD_BUFFER_SIZE)
    var total = 0L
    connection.inputStream.use { input ->
      FileOutputStream(partial, false).use { output ->
        // 响应头校验通过、开始写盘时先报 0，JS 据此从转圈切换为确定进度。
        progress.report(0L)
        while (true) {
          if (Thread.currentThread().isInterrupted) {
            throw InstallerException("APP_DOWNLOAD_CANCELLED", "APK 下载已取消。")
          }
          val count = input.read(buffer)
          if (count < 0) break
          if (count == 0) continue
          val next = total + count
          if (next > expectedSize || next > APK_DOWNLOAD_MAX_SIZE_BYTES) {
            throw InstallerException("APP_DOWNLOAD_SIZE_LIMIT_EXCEEDED", "APK 下载响应超过已验证大小限制。")
          }
          output.write(buffer, 0, count)
          digest.update(buffer, 0, count)
          total = next
          progress.report(total)
          // 后台下载按探测带宽的一定比例限速，避免占满网络影响正常使用。
          rateLimiter?.onBytesTransferred(count)
        }
        if (total != expectedSize) throw sizeMismatch()
        // fsync 后才允许 rename，掉电不会把只写到 page cache 的文件当成完成包。
        output.fd.sync()
      }
    }
    return StreamResult(total, digest.digest().hex(), finalUrl)
  }
}

private data class StreamResult(val sizeBytes: Long, val sha256Hex: String, val finalUrl: String)

private fun sleepForDownloadThrottle(millis: Long) {
  try {
    Thread.sleep(millis)
  } catch (error: InterruptedException) {
    // 取消下载会中断线程：恢复中断标记并按取消处理，与读循环的取消语义一致。
    Thread.currentThread().interrupt()
    throw InstallerException("APP_DOWNLOAD_CANCELLED", "APK 下载已取消。", error)
  }
}
private data class TrustedOrigin(val host: String, val port: Int)
private data class TrustedUrl(val uri: URI, val loopKey: String)
private val redirectStatuses = setOf(301, 302, 303, 307, 308)

private fun validateSize(value: Long) {
  if (value !in 1..APK_DOWNLOAD_MAX_SIZE_BYTES) {
    throw InstallerException("APP_DOWNLOAD_METADATA_INVALID", "已验证 APK 文件大小无效。")
  }
}

private fun validateContentLength(value: String?, expected: Long) {
  if (value == null) return
  val size = value.trim().toLongOrNull() ?: throw failure("APK Content-Length 无效。")
  if (size != expected) throw sizeMismatch()
  validateSize(size)
}

private fun validateContentType(value: String?) {
  val mime = value?.substringBefore(';')?.trim()?.lowercase(Locale.US)
  if (mime != APK_MIME_TYPE) throw InstallerException("APP_DOWNLOAD_MIME_REJECTED", "APK 下载响应类型无效。")
}

private fun parseTrustedOrigin(value: String): TrustedOrigin {
  val uri = parseUri(value, "可信下载 origin 无效。")
  if (
    !uri.scheme.equals("https", true) || uri.userInfo != null || uri.host.isNullOrEmpty() ||
    (uri.rawPath.isNotEmpty() && uri.rawPath != "/") || uri.rawQuery != null || uri.rawFragment != null
  ) throw rejectedUrl("可信下载 origin 无效。")
  return TrustedOrigin(normalizeHost(requireNotNull(uri.host)), effectivePort(uri.port))
}

private fun parseTrustedUrl(value: String, trusted: Set<TrustedOrigin>): TrustedUrl {
  val uri = parseUri(value, "APK 下载 URL 无效。").normalize()
  if (
    !uri.isAbsolute || uri.isOpaque || !uri.scheme.equals("https", true) || uri.userInfo != null ||
    uri.host.isNullOrEmpty() || uri.rawFragment != null
  ) throw rejectedUrl("APK 下载只允许 HTTPS URL。")
  val origin = TrustedOrigin(normalizeHost(requireNotNull(uri.host)), effectivePort(uri.port))
  if (origin !in trusted) throw rejectedUrl("APK 下载 URL 不在可信 origin 列表中。")
  val path = uri.rawPath.takeUnless { it.isNullOrEmpty() } ?: "/"
  return TrustedUrl(uri, "https://${origin.host}:${origin.port}$path${uri.rawQuery?.let { "?$it" }.orEmpty()}")
}

private fun parseUri(value: String, message: String): URI = try {
  URI(value)
} catch (error: Exception) {
  throw rejectedUrl(message, error)
}

private fun normalizeHost(host: String): String = try {
  if (':' in host) host.lowercase(Locale.US) else IDN.toASCII(host, IDN.USE_STD3_ASCII_RULES).lowercase(Locale.US)
} catch (error: Exception) {
  throw rejectedUrl("APK 下载 host 无效。", error)
}

private fun effectivePort(port: Int): Int = when {
  port == -1 || port == 443 -> 443
  port in 1..65535 -> port
  else -> throw rejectedUrl("APK 下载端口无效。")
}

private fun ByteArray.hex() = joinToString("") { "%02X".format(Locale.US, it.toInt() and 0xff) }
internal fun String.hexBytes() = ByteArray(length / 2) { substring(it * 2, it * 2 + 2).toInt(16).toByte() }
private fun deleteOrThrow(file: File) { if (file.exists() && !file.delete()) throw failure("无法清理 APK 临时文件。") }
private fun sizeMismatch() = InstallerException("APP_DOWNLOAD_SIZE_MISMATCH", "APK 下载大小与已验证元数据不一致。")
private fun rejectedUrl(message: String, cause: Throwable? = null) = InstallerException("APP_DOWNLOAD_URL_REJECTED", message, cause)
private fun redirectRejected(message: String, cause: Throwable? = null) = InstallerException("APP_DOWNLOAD_REDIRECT_REJECTED", message, cause)
private fun failure(message: String, cause: Throwable? = null) = InstallerException("APP_DOWNLOAD_FAILED", message, cause)
