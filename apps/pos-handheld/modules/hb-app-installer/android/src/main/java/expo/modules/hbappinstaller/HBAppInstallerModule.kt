package expo.modules.hbappinstaller

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.content.pm.Signature
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import expo.modules.kotlin.records.Field
import expo.modules.kotlin.records.Record
import java.io.File
import java.security.MessageDigest
import java.util.Locale

private const val APK_MIME_TYPE = "application/vnd.android.package-archive"
private const val MAX_APK_SIZE_BYTES = APK_DOWNLOAD_MAX_SIZE_BYTES
private const val MAX_SAFE_JAVASCRIPT_INTEGER = 9_007_199_254_740_991.0
private val APK_FILE_NAME = Regex("^hb-[A-Za-z0-9._-]+\\.apk$", RegexOption.IGNORE_CASE)
private val SHA256_HEX = Regex("^[A-Fa-f0-9]{64}$")
private val ANDROID_PACKAGE_NAME =
  Regex("^[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z][A-Za-z0-9_]*)+$")

internal data class InstallVerifiedApkMetadata(
  val fileUri: String,
  val expectedSha256Hex: String,
  val expectedPackageName: String,
  val expectedVersionCode: Long,
  val expectedVersionName: String,
  val expectedSigningCertificateSha256: String,
)

internal data class DownloadApkMetadata(
  val url: String,
  val destinationFileUri: String,
  val expectedSizeBytes: Long,
  val trustedOrigins: Set<String>,
)

internal class DownloadApkRequestRecord : Record {
  @Field var url: String = ""
  @Field var destinationFileUri: String = ""
  @Field var expectedSizeBytes: Double = 0.0
  @Field var trustedOrigins: List<String> = emptyList()

  fun validated() = DownloadApkMetadata(
    url = url,
    destinationFileUri = destinationFileUri,
    expectedSizeBytes = validatedExpectedSize(expectedSizeBytes),
    trustedOrigins = trustedOrigins.toSet(),
  )
}

internal class InstallVerifiedApkRequestRecord : Record {
  @Field var fileUri: String = ""
  @Field var expectedSha256Hex: String = ""
  @Field var expectedPackageName: String = ""
  @Field var expectedVersionCode: Double = 0.0
  @Field var expectedVersionName: String = ""
  @Field var expectedSigningCertificateSha256: String = ""

  fun validated(): InstallVerifiedApkMetadata {
    if (!SHA256_HEX.matches(expectedSha256Hex)) {
      throw invalidMetadata("SHA-256")
    }
    if (
      expectedPackageName.length > 255 ||
      !ANDROID_PACKAGE_NAME.matches(expectedPackageName)
    ) {
      throw invalidMetadata("包名")
    }
    if (
      expectedVersionName.isEmpty() ||
      expectedVersionName.length > 255 ||
      expectedVersionName != expectedVersionName.trim() ||
      expectedVersionName.any(Char::isISOControl)
    ) {
      // versionName 是授权元数据的一部分；后端为空时必须拒绝，不能退化为不校验。
      throw invalidMetadata("版本名称")
    }
    return InstallVerifiedApkMetadata(
      fileUri = fileUri,
      expectedSha256Hex = expectedSha256Hex.uppercase(Locale.US),
      expectedPackageName = expectedPackageName,
      expectedVersionCode = validatedVersionCode(expectedVersionCode),
      expectedVersionName = expectedVersionName,
      expectedSigningCertificateSha256 = normalizeSigningCertificateSha256(
        expectedSigningCertificateSha256,
      ),
    )
  }
}

/**
 * 模块只把受信 HTTPS 响应下载到本应用专用目录；安装前再核对 SHA-256、包名、版本和签名。
 * 安装入口不接受远端/content URI，也不会把任意包交给系统安装器。
 */
class HBAppInstallerModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("HBAppInstaller")

    AsyncFunction("getInstallPermissionStatus") {
      // 仅查询当前包的未知来源安装开关；查询本身绝不能触发下载或安装。
      if (requireContext().packageManager.canRequestPackageInstalls()) {
        "granted"
      } else {
        "denied"
      }
    }

    AsyncFunction("openInstallPermissionSettings") {
      val context = requireContext()
      // Android 8+ 支持针对当前包打开未知来源授权页，Android 11/API 30 继续沿用此契约。
      val intent = Intent(
        Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
        Uri.parse("package:${context.packageName}"),
      ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
      if (intent.resolveActivity(context.packageManager) == null) {
        throw InstallerException(
          "APP_INSTALL_PERMISSION_SETTINGS_UNAVAILABLE",
          "Android 未提供未知应用安装授权设置页。",
        )
      }
      try {
        context.startActivity(intent)
      } catch (error: Exception) {
        throw InstallerException(
          "APP_INSTALL_PERMISSION_SETTINGS_UNAVAILABLE",
          "无法打开未知应用安装授权设置页。",
          error,
        )
      }
    }

    AsyncFunction("getDownloadDirectory") {
      val context = requireContext()
      val directory = downloadDirectory(context, persistent = false)
      ensureDirectory(directory)
      Uri.fromFile(directory.canonicalFile).toString()
    }

    AsyncFunction("downloadApk") { request: DownloadApkRequestRecord ->
      val context = requireContext()
      val metadata = request.validated()
      val directory = downloadDirectory(context, persistent = false)
      ensureDirectory(directory)
      val destination = validatedDownloadTarget(context, metadata.destinationFileUri)
      val result = HBAppInstallerDownloader().download(
        ApkDownloadRequest(
          sourceUrl = metadata.url,
          destinationFile = destination,
          destinationFileUri = metadata.destinationFileUri,
          expectedSizeBytes = metadata.expectedSizeBytes,
          trustedOrigins = metadata.trustedOrigins,
        ),
      )
      mapOf(
        "fileUri" to result.fileUri,
        "sizeBytes" to result.sizeBytes,
        "finalUrl" to result.finalUrl,
      )
    }

    AsyncFunction("removeDownloadedApk") { fileUri: String ->
      val target = validatedDownloadTarget(requireContext(), fileUri)
      if (target.exists() && !target.delete()) {
        throw InstallerException(
          "APP_DOWNLOAD_CLEANUP_FAILED",
          "无法清理 HB POS APK 下载文件。",
        )
      }
    }

    AsyncFunction("installVerifiedApk") { request: InstallVerifiedApkRequestRecord ->
      val context = requireContext()
      val metadata = request.validated()
      val archiveInfo = validateDownloadedApk(context, metadata)
      requireInstallPermission(context)

      val apk = validatedLocalApk(context, metadata.fileUri)
      val contentUri = FileProvider.getUriForFile(
        context,
        "${context.packageName}.hbappinstaller.fileprovider",
        apk,
      )
      val intent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(contentUri, APK_MIME_TYPE)
        clipData = ClipData.newRawUri("HB POS update", contentUri)
        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
      }
      if (intent.resolveActivity(context.packageManager) == null) {
        throw InstallerException(
          "APP_INSTALLER_UNAVAILABLE",
          "Android 系统安装器不可用。",
        )
      }
      context.startActivity(intent)
      mapOf(
        "launched" to true,
        "packageName" to archiveInfo.packageName,
        "versionCode" to metadata.expectedVersionCode,
      )
    }

    AsyncFunction("verifyDownloadedApk") { request: InstallVerifiedApkRequestRecord ->
      validateDownloadedApk(requireContext(), request.validated())
      Unit
    }
  }

  private fun validateDownloadedApk(
    context: Context,
    metadata: InstallVerifiedApkMetadata,
  ): PackageInfo {
      if (metadata.expectedPackageName != context.packageName) {
        throw InstallerException(
          "APP_INSTALL_PACKAGE_MISMATCH",
          "已验证包名与当前 HB POS 应用不一致。",
        )
      }
      val apk = validatedLocalApk(context, metadata.fileUri)
      validateSha256(apk, metadata.expectedSha256Hex)
      val archiveInfo = readArchiveInfo(context.packageManager, apk)
      if (
        archiveInfo.packageName != context.packageName ||
        archiveInfo.packageName != metadata.expectedPackageName
      ) {
        throw InstallerException(
          "APP_INSTALL_PACKAGE_MISMATCH",
          "APK 包名与当前应用或已验证的服务端元数据不一致。",
        )
      }
      if (archiveInfo.longVersionCode != metadata.expectedVersionCode) {
        throw InstallerException(
          "APP_INSTALL_VERSION_MISMATCH",
          "APK 版本与已验证的服务端元数据不一致。",
        )
      }
      if (archiveInfo.versionName != metadata.expectedVersionName) {
        throw InstallerException(
          "APP_INSTALL_VERSION_NAME_MISMATCH",
          "APK 版本名称与已验证的服务端元数据不一致。",
        )
      }
      val currentInfo = readInstalledInfo(context)
      if (metadata.expectedVersionCode <= currentInfo.longVersionCode) {
        throw InstallerException(
          "APP_INSTALL_VERSION_NOT_NEWER",
          "只允许安装版本号更高的 HB POS APK。",
        )
      }
      HBAppInstallerSignerPolicy.validate(
        expectedSigningCertificateSha256 = metadata.expectedSigningCertificateSha256,
        installed = readSignerEvidence(currentInfo, "当前应用"),
        archive = readSignerEvidence(archiveInfo, "APK"),
      )
      return archiveInfo
  }

  private fun requireContext(): Context =
    appContext.reactContext?.applicationContext
      ?: throw InstallerException(
        "APP_INSTALL_CONTEXT_UNAVAILABLE",
        "Android 应用上下文不可用。",
      )

  private fun requireInstallPermission(context: Context) {
    if (!context.packageManager.canRequestPackageInstalls()) {
      throw InstallerException(
        "APP_INSTALL_PERMISSION_REQUIRED",
        "系统尚未允许 HB POS 安装应用更新。",
      )
    }
  }

  private fun validatedLocalApk(context: Context, fileUri: String): File {
    val uri = try {
      Uri.parse(fileUri)
    } catch (error: Exception) {
      throw InstallerException(
        "APP_INSTALL_URI_INVALID",
        "APK 本地文件 URI 无效。",
        error,
      )
    }
    if (
      uri.scheme != "file" ||
      !uri.authority.isNullOrEmpty() ||
      uri.query != null ||
      uri.fragment != null ||
      uri.path.isNullOrEmpty()
    ) {
      throw InstallerException(
        "APP_INSTALL_URI_REJECTED",
        "安装器只接受 HB POS 专用目录中的本地 APK。",
      )
    }
    val file = File(requireNotNull(uri.path)).canonicalFile
    val allowedParents = listOf(
      downloadDirectory(context, persistent = false).canonicalFile,
      downloadDirectory(context, persistent = true).canonicalFile,
    )
    val parent = file.parentFile
      ?: throw InstallerException(
        "APP_INSTALL_PATH_REJECTED",
        "APK 不在 HB POS 专用更新目录中。",
      )
    if (parent !in allowedParents || !APK_FILE_NAME.matches(file.name)) {
      throw InstallerException(
        "APP_INSTALL_PATH_REJECTED",
        "APK 不在 HB POS 专用更新目录中。",
      )
    }
    if (!file.isFile || file.length() !in 1..MAX_APK_SIZE_BYTES) {
      throw InstallerException(
        "APP_INSTALL_FILE_INVALID",
        "APK 文件不存在、为空或超过大小限制。",
      )
    }
    return file
  }

  private fun validatedDownloadTarget(context: Context, fileUri: String): File {
    val uri = try {
      Uri.parse(fileUri)
    } catch (error: Exception) {
      throw InstallerException(
        "APP_DOWNLOAD_URI_INVALID",
        "APK 下载目标 URI 无效。",
        error,
      )
    }
    if (
      uri.scheme != "file" ||
      !uri.authority.isNullOrEmpty() ||
      uri.query != null ||
      uri.fragment != null ||
      uri.path.isNullOrEmpty()
    ) {
      throw InstallerException(
        "APP_DOWNLOAD_PATH_REJECTED",
        "APK 下载目标不在 HB POS 专用更新目录中。",
      )
    }
    val target = File(requireNotNull(uri.path)).canonicalFile
    val allowedParent = downloadDirectory(context, persistent = false).canonicalFile
    if (
      target.parentFile != allowedParent ||
      !APK_FILE_NAME.matches(target.name)
    ) {
      throw InstallerException(
        "APP_DOWNLOAD_PATH_REJECTED",
        "APK 下载目标不在 HB POS 专用更新目录中。",
      )
    }
    return target
  }

  private fun validateSha256(file: File, expectedHex: String) {
    if (!SHA256_HEX.matches(expectedHex)) {
      throw InstallerException(
        "APP_INSTALL_METADATA_INVALID",
        "已验证 APK SHA-256 无效。",
      )
    }
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().buffered().use { input ->
      val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
      while (true) {
        val count = input.read(buffer)
        if (count < 0) break
        digest.update(buffer, 0, count)
      }
    }
    val expected = expectedHex.hexDecode()
    if (!MessageDigest.isEqual(digest.digest(), expected)) {
      throw InstallerException(
        "APP_INSTALL_SHA256_MISMATCH",
        "APK SHA-256 与已验证的服务端元数据不一致。",
      )
    }
  }

  private fun readArchiveInfo(packageManager: PackageManager, file: File): PackageInfo {
    val flags = PackageManager.GET_SIGNING_CERTIFICATES.toLong()
    val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      packageManager.getPackageArchiveInfo(
        file.absolutePath,
        PackageManager.PackageInfoFlags.of(flags),
      )
    } else {
      @Suppress("DEPRECATION")
      packageManager.getPackageArchiveInfo(file.absolutePath, flags.toInt())
    }
    return info ?: throw InstallerException(
      "APP_INSTALL_ARCHIVE_INVALID",
      "本地文件不是可解析的 Android APK。",
    )
  }

  private fun readInstalledInfo(context: Context): PackageInfo {
    val flags = PackageManager.GET_SIGNING_CERTIFICATES.toLong()
    return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      context.packageManager.getPackageInfo(
        context.packageName,
        PackageManager.PackageInfoFlags.of(flags),
      )
    } else {
      @Suppress("DEPRECATION")
      context.packageManager.getPackageInfo(context.packageName, flags.toInt())
    }
  }

  private fun readSignerEvidence(info: PackageInfo, source: String): SignerEvidence = try {
    val signingInfo = info.signingInfo ?: throw signerUnreadable(source)
    val hasMultipleSigners = signingInfo.hasMultipleSigners()
    val currentSignatures = signingInfo.apkContentsSigners ?: throw signerUnreadable(source)
    val currentDigests = digestSignatures(currentSignatures, source)
    if (currentDigests.size != currentSignatures.size) {
      throw signerUnreadable(source)
    }

    val historyDigests = if (hasMultipleSigners) {
      emptySet()
    } else {
      val history = signingInfo.signingCertificateHistory ?: throw signerUnreadable(source)
      digestSignatures(history, source).also { digests ->
        if (digests.size != history.size) throw signerUnreadable(source)
      }
    }
    SignerEvidence(
      hasMultipleSigners = hasMultipleSigners,
      currentSignerDigests = currentDigests,
      signingCertificateHistory = historyDigests,
    )
  } catch (error: InstallerException) {
    throw error
  } catch (error: Exception) {
    throw InstallerException(
      "APP_INSTALL_SIGNER_UNREADABLE",
      "无法读取${source}签名证书。",
      error,
    )
  }

  private fun digestSignatures(signatures: Array<out Signature>, source: String): Set<String> =
    signatures.mapTo(linkedSetOf()) { signature ->
      val bytes = signature.toByteArray()
      if (bytes.isEmpty()) throw signerUnreadable(source)
      MessageDigest.getInstance("SHA-256")
        .digest(bytes)
        .joinToString("") { "%02X".format(Locale.US, it.toInt() and 0xFF) }
    }

  private fun signerUnreadable(source: String) = InstallerException(
    "APP_INSTALL_SIGNER_UNREADABLE",
    "无法无歧义地读取${source}签名证书。",
  )

  private fun ensureDirectory(directory: File) {
    if ((!directory.exists() && !directory.mkdirs()) || !directory.isDirectory) {
      throw InstallerException(
        "APP_INSTALL_DIRECTORY_UNAVAILABLE",
        "无法创建 HB POS APK 更新目录。",
      )
    }
  }

  private fun downloadDirectory(context: Context, persistent: Boolean): File =
    File(if (persistent) context.filesDir else context.cacheDir, "hb-app-updates")
}

private fun validatedVersionCode(value: Double): Long {
  if (
    !value.isFinite() ||
    value % 1.0 != 0.0 ||
    value <= 0.0 ||
    value > MAX_SAFE_JAVASCRIPT_INTEGER
  ) {
    throw invalidMetadata("版本号")
  }
  return value.toLong()
}

private fun validatedExpectedSize(value: Double): Long {
  if (
    !value.isFinite() ||
    value % 1.0 != 0.0 ||
    value <= 0.0 ||
    value > APK_DOWNLOAD_MAX_SIZE_BYTES.toDouble()
  ) {
    throw InstallerException(
      "APP_DOWNLOAD_METADATA_INVALID",
      "已验证 APK 文件大小无效。",
    )
  }
  return value.toLong()
}

private fun invalidMetadata(field: String) = InstallerException(
  "APP_INSTALL_METADATA_INVALID",
  "已验证 APK ${field}无效。",
)

private fun String.hexDecode(): ByteArray = ByteArray(length / 2) { index ->
  substring(index * 2, index * 2 + 2).toInt(16).toByte()
}
