package expo.modules.hbappinstaller

import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.IDN
import java.net.URI
import java.net.URL
import java.util.Locale

internal const val APK_DOWNLOAD_MAX_SIZE_BYTES = 512L * 1024L * 1024L

private const val APK_MIME_TYPE = "application/vnd.android.package-archive"
private const val DEFAULT_MAXIMUM_REDIRECTS = 5
private const val DEFAULT_CONNECT_TIMEOUT_MILLIS = 15_000
private const val DEFAULT_READ_TIMEOUT_MILLIS = 30_000
private const val DOWNLOAD_BUFFER_SIZE = 64 * 1024

internal data class ApkDownloadRequest(
  val sourceUrl: String,
  val destinationFile: File,
  val expectedSizeBytes: Long,
  val trustedOrigins: Set<String>,
  val destinationFileUri: String = destinationFile.toURI().toString(),
)

internal data class ApkDownloadResult(
  val fileUri: String,
  val sizeBytes: Long,
  val finalUrl: String,
)

/**
 * APK 下载必须在 native 层逐块写入，才能在响应超过签名大小时立刻停止。
 * 连接不会自动跟随跳转；每一跳都重新执行 HTTPS origin 白名单校验。
 */
internal class HBAppInstallerDownloader(
  private val connectionFactory: (URL) -> HttpURLConnection = ::openHttpConnection,
  private val maximumRedirects: Int = DEFAULT_MAXIMUM_REDIRECTS,
  private val connectTimeoutMillis: Int = DEFAULT_CONNECT_TIMEOUT_MILLIS,
  private val readTimeoutMillis: Int = DEFAULT_READ_TIMEOUT_MILLIS,
  private val elapsedMillis: () -> Long = ::monotonicMillis,
) {
  init {
    require(maximumRedirects >= 0)
    require(connectTimeoutMillis > 0)
    require(readTimeoutMillis > 0)
  }

  fun download(
    request: ApkDownloadRequest,
    onProgress: ApkDownloadProgressListener? = null,
  ): ApkDownloadResult {
    var partialFile: File? = null
    var completed = false
    var destination = request.destinationFile

    try {
      destination = destination.canonicalFile
      validateExpectedSize(request.expectedSizeBytes)
      val trustedOrigins = request.trustedOrigins.mapTo(linkedSetOf(), ::parseTrustedOrigin)
      if (trustedOrigins.isEmpty()) throw rejectedUrl("可信下载 origin 为空。")

      val parent = destination.parentFile
        ?: throw downloadFailure("APK 下载目标目录无效。")
      if (!parent.isDirectory) throw downloadFailure("APK 下载目标目录不存在。")
      deleteOrThrow(destination)

      partialFile = File.createTempFile("${destination.name}.", ".part", parent).apply {
        // 临时文件在完成校验与同目录 rename 前始终不可执行。
        setExecutable(false, false)
      }

      val response = downloadToPartialFile(
        sourceUrl = request.sourceUrl,
        trustedOrigins = trustedOrigins,
        expectedSizeBytes = request.expectedSizeBytes,
        partialFile = partialFile,
        progress = ApkDownloadProgressReporter(
          totalBytes = request.expectedSizeBytes,
          listener = onProgress,
          elapsedMillis = elapsedMillis,
        ),
      )
      if (partialFile.length() != request.expectedSizeBytes) {
        throw sizeMismatch()
      }
      deleteOrThrow(destination)
      if (!partialFile.renameTo(destination)) {
        throw downloadFailure("无法完成 APK 临时文件重命名。")
      }
      destination.setExecutable(false, false)
      completed = true
      return ApkDownloadResult(
        fileUri = request.destinationFileUri,
        sizeBytes = response.sizeBytes,
        finalUrl = response.finalUrl,
      )
    } catch (error: InstallerException) {
      throw error
    } catch (error: Exception) {
      throw InstallerException(
        "APP_DOWNLOAD_FAILED",
        "APK 下载失败。",
        error,
      )
    } finally {
      if (!completed) {
        partialFile?.delete()
        destination.delete()
      }
    }
  }

  private fun downloadToPartialFile(
    sourceUrl: String,
    trustedOrigins: Set<TrustedOrigin>,
    expectedSizeBytes: Long,
    partialFile: File,
    progress: ApkDownloadProgressReporter,
  ): StreamedResponse {
    var current = parseTrustedUrl(sourceUrl, trustedOrigins)
    val visited = linkedSetOf<String>()
    var redirects = 0

    while (true) {
      if (!visited.add(current.loopKey)) {
        throw redirectRejected("APK 下载重定向形成循环。")
      }
      val connection = connectionFactory(current.uri.toURL()).apply {
        instanceFollowRedirects = false
        requestMethod = "GET"
        connectTimeout = connectTimeoutMillis
        readTimeout = readTimeoutMillis
        useCaches = false
        doInput = true
        doOutput = false
        // 不接收也不转发 Authorization、Cookie 或其他敏感 header。
        setRequestProperty("Accept", APK_MIME_TYPE)
        setRequestProperty("Accept-Encoding", "identity")
      }

      try {
        val statusCode = connection.responseCode
        if (statusCode in REDIRECT_STATUS_CODES) {
          if (redirects >= maximumRedirects) {
            throw redirectRejected("APK 下载重定向次数超过限制。")
          }
          val location = connection.getHeaderField("Location")
            ?.trim()
            ?.takeIf(String::isNotEmpty)
            ?: throw redirectRejected("APK 下载重定向缺少 Location。")
          val resolved = try {
            current.uri.resolve(URI(location)).normalize()
          } catch (error: Exception) {
            throw redirectRejected("APK 下载重定向地址无效。", error)
          }
          current = parseTrustedUrl(resolved.toASCIIString(), trustedOrigins)
          redirects += 1
          continue
        }
        if (statusCode !in 200..299) {
          throw InstallerException(
            "APP_DOWNLOAD_HTTP_ERROR",
            "APK 下载服务器返回 HTTP $statusCode。",
          )
        }

        validateContentLength(
          connection.getHeaderField("Content-Length"),
          expectedSizeBytes,
        )
        val sizeBytes = streamExactSize(
          connection = connection,
          destination = partialFile,
          expectedSizeBytes = expectedSizeBytes,
          progress = progress,
        )
        return StreamedResponse(
          sizeBytes = sizeBytes,
          finalUrl = current.uri.toASCIIString(),
        )
      } finally {
        connection.disconnect()
      }
    }
  }

  private fun streamExactSize(
    connection: HttpURLConnection,
    destination: File,
    expectedSizeBytes: Long,
    progress: ApkDownloadProgressReporter,
  ): Long {
    var totalBytes = 0L
    val buffer = ByteArray(DOWNLOAD_BUFFER_SIZE)
    connection.inputStream.use { input ->
      FileOutputStream(destination, false).use { output ->
        // 响应头校验通过、开始写盘时先报 0，JS 据此从转圈切换为确定进度。
        progress.report(0L)
        while (true) {
          if (Thread.currentThread().isInterrupted) {
            throw InstallerException(
              "APP_DOWNLOAD_CANCELLED",
              "APK 下载已取消。",
            )
          }
          val count = input.read(buffer)
          if (count < 0) break
          if (count == 0) continue
          val nextTotal = totalBytes + count
          if (
            nextTotal > expectedSizeBytes ||
            nextTotal > APK_DOWNLOAD_MAX_SIZE_BYTES
          ) {
            throw InstallerException(
              "APP_DOWNLOAD_SIZE_LIMIT_EXCEEDED",
              "APK 下载响应超过已验证大小限制。",
            )
          }
          output.write(buffer, 0, count)
          totalBytes = nextTotal
          progress.report(totalBytes)
        }
        if (totalBytes != expectedSizeBytes) throw sizeMismatch()
        output.fd.sync()
      }
    }
    return totalBytes
  }
}

private data class StreamedResponse(
  val sizeBytes: Long,
  val finalUrl: String,
)

private data class TrustedOrigin(
  val host: String,
  val port: Int,
)

private data class TrustedUrl(
  val uri: URI,
  val loopKey: String,
)

private val REDIRECT_STATUS_CODES = setOf(
  HttpURLConnection.HTTP_MOVED_PERM,
  HttpURLConnection.HTTP_MOVED_TEMP,
  HttpURLConnection.HTTP_SEE_OTHER,
  307,
  308,
)

private fun openHttpConnection(url: URL): HttpURLConnection =
  url.openConnection() as? HttpURLConnection
    ?: throw downloadFailure("APK 下载连接类型无效。")

private fun validateExpectedSize(expectedSizeBytes: Long) {
  if (expectedSizeBytes !in 1..APK_DOWNLOAD_MAX_SIZE_BYTES) {
    throw InstallerException(
      "APP_DOWNLOAD_METADATA_INVALID",
      "已验证 APK 文件大小无效。",
    )
  }
}

private fun validateContentLength(value: String?, expectedSizeBytes: Long) {
  if (value == null) return
  val contentLength = value.trim().toLongOrNull()
    ?: throw downloadFailure("APK Content-Length 无效。")
  if (
    contentLength < 0 ||
    contentLength > expectedSizeBytes ||
    contentLength > APK_DOWNLOAD_MAX_SIZE_BYTES
  ) {
    throw InstallerException(
      "APP_DOWNLOAD_SIZE_LIMIT_EXCEEDED",
      "APK Content-Length 超过已验证大小限制。",
    )
  }
  if (contentLength != expectedSizeBytes) throw sizeMismatch()
}

private fun parseTrustedOrigin(value: String): TrustedOrigin {
  val uri = parseUri(value, "可信下载 origin 无效。")
  if (
    !uri.scheme.equals("https", ignoreCase = true) ||
    uri.userInfo != null ||
    uri.host.isNullOrEmpty() ||
    (uri.rawPath.isNotEmpty() && uri.rawPath != "/") ||
    uri.rawQuery != null ||
    uri.rawFragment != null
  ) {
    throw rejectedUrl("可信下载 origin 无效。")
  }
  return TrustedOrigin(
    host = normalizeHost(requireNotNull(uri.host)),
    port = effectiveHttpsPort(uri.port),
  )
}

private fun parseTrustedUrl(
  value: String,
  trustedOrigins: Set<TrustedOrigin>,
): TrustedUrl {
  val uri = parseUri(value, "APK 下载 URL 无效。").normalize()
  if (
    !uri.isAbsolute ||
    uri.isOpaque ||
    !uri.scheme.equals("https", ignoreCase = true) ||
    uri.userInfo != null ||
    uri.host.isNullOrEmpty() ||
    uri.rawFragment != null
  ) {
    throw rejectedUrl("APK 下载只允许 HTTPS URL。")
  }
  val origin = TrustedOrigin(
    host = normalizeHost(requireNotNull(uri.host)),
    port = effectiveHttpsPort(uri.port),
  )
  if (origin !in trustedOrigins) {
    throw rejectedUrl("APK 下载 URL 不在可信 origin 列表中。")
  }
  val path = uri.rawPath.takeUnless(String?::isNullOrEmpty) ?: "/"
  val query = uri.rawQuery?.let { "?$it" }.orEmpty()
  return TrustedUrl(
    uri = uri,
    loopKey = "https://${origin.host}:${origin.port}$path$query",
  )
}

private fun parseUri(value: String, message: String): URI = try {
  URI(value)
} catch (error: Exception) {
  throw rejectedUrl(message, error)
}

private fun normalizeHost(host: String): String = try {
  if (':' in host) {
    host.lowercase(Locale.US)
  } else {
    IDN.toASCII(host, IDN.USE_STD3_ASCII_RULES).lowercase(Locale.US)
  }
} catch (error: Exception) {
  throw rejectedUrl("APK 下载 host 无效。", error)
}

private fun effectiveHttpsPort(port: Int): Int {
  if (port == -1 || port == 443) return 443
  if (port !in 1..65_535) throw rejectedUrl("APK 下载端口无效。")
  return port
}

private fun deleteOrThrow(file: File) {
  if (file.exists() && !file.delete()) {
    throw downloadFailure("无法清理旧 APK 下载文件。")
  }
}

private fun sizeMismatch() = InstallerException(
  "APP_DOWNLOAD_SIZE_MISMATCH",
  "APK 下载大小与已验证元数据不一致。",
)

private fun rejectedUrl(message: String, cause: Throwable? = null) = InstallerException(
  "APP_DOWNLOAD_URL_REJECTED",
  message,
  cause,
)

private fun redirectRejected(message: String, cause: Throwable? = null) = InstallerException(
  "APP_DOWNLOAD_REDIRECT_REJECTED",
  message,
  cause,
)

private fun downloadFailure(message: String, cause: Throwable? = null) = InstallerException(
  "APP_DOWNLOAD_FAILED",
  message,
  cause,
)
