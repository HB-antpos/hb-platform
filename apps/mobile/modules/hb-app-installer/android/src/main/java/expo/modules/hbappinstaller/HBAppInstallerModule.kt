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
private const val MAX_SAFE_JAVASCRIPT_INTEGER = 9_007_199_254_740_991.0
private val PACKAGE_NAME = Regex("^[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z][A-Za-z0-9_]*)+$")

internal data class DownloadMetadata(
  val url: String,
  val destinationFileUri: String,
  val expectedSizeBytes: Long,
  val expectedSha256Hex: String,
  val trustedOrigins: Set<String>,
)

internal data class VerifyMetadata(
  val fileUri: String,
  val expectedSizeBytes: Long,
  val expectedSha256Hex: String,
  val expectedPackageName: String,
  val expectedVersionCode: Long,
  val expectedVersionName: String,
)

internal class DownloadApkRequestRecord : Record {
  @Field var url: String = ""
  @Field var destinationFileUri: String = ""
  @Field var expectedSizeBytes: Double = 0.0
  @Field var expectedSha256Hex: String = ""
  @Field var trustedOrigins: List<String> = emptyList()

  fun validated() = DownloadMetadata(
    url = url,
    destinationFileUri = destinationFileUri,
    expectedSizeBytes = validatedSize(expectedSizeBytes),
    expectedSha256Hex = normalizedSha256(expectedSha256Hex, "APP_DOWNLOAD_METADATA_INVALID"),
    trustedOrigins = trustedOrigins.toSet(),
  )
}

internal class VerifyApkRequestRecord : Record {
  @Field var fileUri: String = ""
  @Field var expectedSizeBytes: Double = 0.0
  @Field var expectedSha256Hex: String = ""
  @Field var expectedPackageName: String = ""
  @Field var expectedVersionCode: Double = 0.0
  @Field var expectedVersionName: String = ""

  fun validated(): VerifyMetadata {
    if (expectedPackageName.length > 255 || !PACKAGE_NAME.matches(expectedPackageName)) {
      throw InstallerException("APP_INSTALL_METADATA_INVALID", "已验证 APK 包名无效。")
    }
    if (
      expectedVersionName.isEmpty() || expectedVersionName.length > 255 ||
      expectedVersionName != expectedVersionName.trim() || expectedVersionName.any(Char::isISOControl)
    ) throw InstallerException("APP_INSTALL_METADATA_INVALID", "已验证 APK 版本名称无效。")
    return VerifyMetadata(
      fileUri = fileUri,
      expectedSizeBytes = validatedSize(expectedSizeBytes),
      expectedSha256Hex = normalizedSha256(expectedSha256Hex, "APP_INSTALL_METADATA_INVALID"),
      expectedPackageName = expectedPackageName,
      expectedVersionCode = validatedVersionCode(expectedVersionCode),
      expectedVersionName = expectedVersionName,
    )
  }
}

/**
 * Mobile APK 安装器：下载时只接受受信字节，弹窗前和启动安装器前都复验包身份。
 * 所有可安装文件仅存在于本应用私有 `hb-app-updates` 目录。
 */
class HBAppInstallerModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("HBAppInstaller")

    // 旧原生包没有该事件，JS 收不到进度时保持不确定转圈，不能伪造百分比。
    Events(DOWNLOAD_PROGRESS_EVENT)

    AsyncFunction("getInstallPermissionStatus") {
      if (isInstallPermissionGranted(requireContext())) "granted" else "denied"
    }

    AsyncFunction("openInstallPermissionSettings") {
      val context = requireContext()
      val intent = when (HBAppInstallerInstallPermissionPolicy.settingsPage(Build.VERSION.SDK_INT)) {
        InstallPermissionSettingsPage.APP_SPECIFIC -> Intent(
          Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
          Uri.parse("package:${context.packageName}"),
        )
        InstallPermissionSettingsPage.SECURITY -> Intent(Settings.ACTION_SECURITY_SETTINGS)
      }.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
      launchSystemActivity(
        "APP_INSTALL_PERMISSION_SETTINGS_UNAVAILABLE",
        "无法打开未知应用安装授权设置页。",
      ) {
        context.startActivity(intent)
      }
    }

    AsyncFunction("getDownloadDirectory") {
      val directory = downloadDirectory(requireContext(), persistent = false)
      ensureDirectory(directory)
      Uri.fromFile(directory.canonicalFile).toString()
    }

    AsyncFunction("downloadApk") { request: DownloadApkRequestRecord ->
      val context = requireContext()
      val metadata = request.validated()
      val directory = downloadDirectory(context, persistent = false)
      ensureDirectory(directory)
      val destination = validatedDownloadTarget(context, metadata.destinationFileUri)
      HBAppInstallerTargetLock.withLock(destination) {
        val result = HBAppInstallerDownloader().download(
          ApkDownloadRequest(
            sourceUrl = metadata.url,
            destinationFile = destination,
            destinationFileUri = metadata.destinationFileUri,
            expectedSizeBytes = metadata.expectedSizeBytes,
            expectedSha256Hex = metadata.expectedSha256Hex,
            trustedOrigins = metadata.trustedOrigins,
          ),
          onProgress = ApkDownloadProgressListener { bytesWritten, totalBytes ->
            // 带上 JS 传入的原始目标 URI，JS 只认自己这次下载的进度。
            sendEvent(
              DOWNLOAD_PROGRESS_EVENT,
              mapOf(
                "destinationFileUri" to metadata.destinationFileUri,
                "bytesWritten" to bytesWritten.toDouble(),
                "totalBytes" to totalBytes.toDouble(),
              ),
            )
          },
        )
        mapOf(
          "fileUri" to result.fileUri,
          "sizeBytes" to result.sizeBytes,
          "sha256Hex" to result.sha256Hex,
          "finalUrl" to result.finalUrl,
        )
      }
    }

    AsyncFunction("verifyApk") { request: VerifyApkRequestRecord ->
      val context = requireContext()
      val metadata = request.validated()
      val target = validatedLocalApk(context, metadata.fileUri)
      HBAppInstallerTargetLock.withLock(target) {
        val identity = installCoordinator(context).verifyApk(target, metadata)
        mapOf("verified" to true, "packageName" to identity.packageName, "versionCode" to identity.versionCode)
      }
    }

    AsyncFunction("removeDownloadedApk") { fileUri: String ->
      val target = validatedDownloadTarget(requireContext(), fileUri)
      HBAppInstallerTargetLock.withLock(target) {
        if (target.exists() && !target.delete()) {
          throw InstallerException("APP_DOWNLOAD_CLEANUP_FAILED", "无法清理 HB Group APK 下载文件。")
        }
        File(target.parentFile, "${target.name}.part").delete()
      }
    }

    AsyncFunction("installVerifiedApk") { request: VerifyApkRequestRecord ->
      val context = requireContext()
      val metadata = request.validated()
      val target = validatedLocalApk(context, metadata.fileUri)
      HBAppInstallerTargetLock.withLock(target) {
        val identity = installCoordinator(context).installVerifiedApk(target, metadata)
        mapOf("launched" to true, "packageName" to identity.packageName, "versionCode" to identity.versionCode)
      }
    }
  }

  private fun verifyApk(context: Context, apk: File, metadata: VerifyMetadata): VerifiedApkIdentity {
    if (metadata.expectedPackageName != context.packageName) {
      throw InstallerException("APP_INSTALL_PACKAGE_MISMATCH", "已验证包名与当前 HB Group 应用不一致。")
    }
    if (apk.length() != metadata.expectedSizeBytes) {
      throw InstallerException("APP_INSTALL_SIZE_MISMATCH", "APK 大小与已验证服务端元数据不一致。")
    }
    if (!MessageDigest.isEqual(sha256File(apk).hexBytes(), metadata.expectedSha256Hex.hexBytes())) {
      throw InstallerException("APP_INSTALL_SHA256_MISMATCH", "APK SHA-256 与已验证服务端元数据不一致。")
    }
    val archive = readArchiveInfo(context.packageManager, apk)
    if (archive.packageName != context.packageName || archive.packageName != metadata.expectedPackageName) {
      throw InstallerException("APP_INSTALL_PACKAGE_MISMATCH", "APK 包名与当前应用或已验证元数据不一致。")
    }
    if (archive.safeVersionCode() != metadata.expectedVersionCode) {
      throw InstallerException("APP_INSTALL_VERSION_MISMATCH", "APK 版本号与已验证元数据不一致。")
    }
    if (archive.versionName != metadata.expectedVersionName) {
      throw InstallerException("APP_INSTALL_VERSION_NAME_MISMATCH", "APK 版本名称与已验证元数据不一致。")
    }
    val installed = readInstalledInfo(context)
    if (metadata.expectedVersionCode <= installed.safeVersionCode()) {
      throw InstallerException("APP_INSTALL_VERSION_NOT_NEWER", "只允许安装版本号更高的 HB Group APK。")
    }
    HBAppInstallerSignerPolicy.validate(
      readSignerEvidence(installed, "当前应用"),
      readSignerEvidence(archive, "APK"),
    )
    return VerifiedApkIdentity(archive.packageName, archive.safeVersionCode())
  }

  private fun installCoordinator(context: Context) = HBAppInstallerInstallCoordinator(
    verifyIdentity = { apk, metadata -> verifyApk(context, apk, metadata) },
    launchInstaller = { target ->
      requireInstallPermission(context)
      val contentUri = FileProvider.getUriForFile(
        context,
        "${context.packageName}.hbappinstaller.fileprovider",
        target,
      )
      val intent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(contentUri, APK_MIME_TYPE)
        clipData = ClipData.newRawUri("HB Group update", contentUri)
        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
      }
      launchSystemActivity("APP_INSTALLER_UNAVAILABLE", "Android 系统安装器不可用。") {
        context.startActivity(intent)
      }
    },
  )

  private fun requireContext(): Context = appContext.reactContext?.applicationContext
    ?: throw InstallerException("APP_INSTALL_CONTEXT_UNAVAILABLE", "Android 应用上下文不可用。")

  private fun requireInstallPermission(context: Context) {
    if (!isInstallPermissionGranted(context)) {
      throw InstallerException("APP_INSTALL_PERMISSION_REQUIRED", "系统尚未允许 HB Group 安装应用更新。")
    }
  }

  private fun isInstallPermissionGranted(context: Context): Boolean = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
    context.packageManager.canRequestPackageInstalls()
  } else {
    true
  }

  private fun validatedLocalApk(context: Context, value: String): File {
    val file = validatedFileUri(value, "APP_INSTALL_URI_REJECTED")
    val parent = file.parentFile ?: throw InstallerException(
      "APP_INSTALL_PATH_REJECTED",
      "APK 不在 HB Group 专用更新目录中。",
    )
    val parents = setOf(
      downloadDirectory(context, false).canonicalFile,
      downloadDirectory(context, true).canonicalFile,
    )
    if (!isManagedApkPath(file, parents) || !file.isFile || file.length() !in 1..APK_DOWNLOAD_MAX_SIZE_BYTES) {
      throw InstallerException("APP_INSTALL_PATH_REJECTED", "安装器只接受 HB Group 专用目录中的有效 APK。")
    }
    return file
  }

  private fun validatedDownloadTarget(context: Context, value: String): File {
    val file = validatedFileUri(value, "APP_DOWNLOAD_PATH_REJECTED")
    if (!isManagedApkPath(file, setOf(downloadDirectory(context, false)))) {
      throw InstallerException("APP_DOWNLOAD_PATH_REJECTED", "APK 下载目标不在 HB Group 专用更新目录中。")
    }
    return file
  }

  private fun validatedFileUri(value: String, code: String): File {
    val uri = try { Uri.parse(value) } catch (error: Exception) {
      throw InstallerException(code, "APK 本地文件 URI 无效。", error)
    }
    // Uri.fromFile 产出的 file:///path 经 Uri.parse 解析后 authority 是空字符串而不是 null，
    // 早期用 `!= null` 判断会把所有合法的本地路径一律拒掉，自动更新因此永远下载不了。
    // 这里只拒绝真正带主机名的 file://host/path。
    if (uri.scheme != "file" || !uri.authority.isNullOrEmpty() || uri.query != null || uri.fragment != null || uri.path.isNullOrEmpty()) {
      throw InstallerException(code, "APK 只允许本地 file URI。")
    }
    return File(requireNotNull(uri.path)).canonicalFile
  }

  private fun readArchiveInfo(manager: PackageManager, file: File): PackageInfo {
    val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
      // Android 9/10 必须额外带 GET_SIGNATURES，否则 APK 的 signingInfo 恒为 null。
      val flags = archiveSigningCertificateFlags(Build.VERSION.SDK_INT)
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        manager.getPackageArchiveInfo(file.absolutePath, PackageManager.PackageInfoFlags.of(flags.toLong()))
      } else {
        @Suppress("DEPRECATION") manager.getPackageArchiveInfo(file.absolutePath, flags)
      }
    } else {
      // API 24–27 没有 signing lineage；保守地只接受当前证书完全一致。
      @Suppress("DEPRECATION") manager.getPackageArchiveInfo(file.absolutePath, PackageManager.GET_SIGNATURES)
    }
    return info ?: throw InstallerException("APP_INSTALL_ARCHIVE_INVALID", "本地文件不是可解析的 Android APK。")
  }

  private fun readInstalledInfo(context: Context): PackageInfo {
    return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
      val flags = PackageManager.GET_SIGNING_CERTIFICATES.toLong()
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        context.packageManager.getPackageInfo(context.packageName, PackageManager.PackageInfoFlags.of(flags))
      } else {
        @Suppress("DEPRECATION") context.packageManager.getPackageInfo(context.packageName, flags.toInt())
      }
    } else {
      @Suppress("DEPRECATION") context.packageManager.getPackageInfo(context.packageName, PackageManager.GET_SIGNATURES)
    }
  }

  private fun readSignerEvidence(info: PackageInfo, source: String): SignerEvidence {
    return try {
      if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
        @Suppress("DEPRECATION")
        val signatures = info.signatures ?: throw signerUnreadable(source)
        val digests = digestSignatures(signatures, source)
        if (digests.size != signatures.size) throw signerUnreadable(source)
        SignerEvidence(false, digests, digests)
      } else {
        val signingInfo = info.signingInfo ?: throw signerUnreadable(source)
        val current = signingInfo.apkContentsSigners ?: throw signerUnreadable(source)
        val currentDigests = digestSignatures(current, source)
        if (currentDigests.size != current.size) throw signerUnreadable(source)
        val multi = signingInfo.hasMultipleSigners()
        val history = if (multi) emptySet() else {
          val values = signingInfo.signingCertificateHistory ?: throw signerUnreadable(source)
          digestSignatures(values, source).also { if (it.size != values.size) throw signerUnreadable(source) }
        }
        SignerEvidence(multi, currentDigests, history)
      }
    } catch (error: InstallerException) {
      throw error
    } catch (error: Exception) {
      throw InstallerException("APP_INSTALL_SIGNER_UNREADABLE", "无法读取${source}签名证书。", error)
    }
  }

  private fun digestSignatures(signatures: Array<out Signature>, source: String): Set<String> = signatures.mapTo(linkedSetOf()) { signature ->
    val bytes = signature.toByteArray()
    if (bytes.isEmpty()) throw signerUnreadable(source)
    MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02X".format(Locale.US, it.toInt() and 0xff) }
  }

  private fun signerUnreadable(source: String) = InstallerException("APP_INSTALL_SIGNER_UNREADABLE", "无法无歧义地读取${source}签名证书。")
  private fun PackageInfo.safeVersionCode(): Long = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
    longVersionCode
  } else {
    @Suppress("DEPRECATION")
    resolveLegacyPackageVersionCode(Build.VERSION.SDK_INT, versionCode)
  }
  private fun ensureDirectory(directory: File) {
    if ((!directory.exists() && !directory.mkdirs()) || !directory.isDirectory) {
      throw InstallerException("APP_INSTALL_DIRECTORY_UNAVAILABLE", "无法创建 HB Group APK 更新目录。")
    }
  }
  private fun downloadDirectory(context: Context, persistent: Boolean) = File(if (persistent) context.filesDir else context.cacheDir, "hb-app-updates")
}

private fun validatedSize(value: Double): Long {
  if (!value.isFinite() || value % 1.0 != 0.0 || value <= 0.0 || value > APK_DOWNLOAD_MAX_SIZE_BYTES.toDouble()) {
    throw InstallerException("APP_DOWNLOAD_METADATA_INVALID", "已验证 APK 文件大小无效。")
  }
  return value.toLong()
}

private fun validatedVersionCode(value: Double): Long {
  if (!value.isFinite() || value % 1.0 != 0.0 || value <= 0.0 || value > MAX_SAFE_JAVASCRIPT_INTEGER) {
    throw InstallerException("APP_INSTALL_METADATA_INVALID", "已验证 APK 版本号无效。")
  }
  return value.toLong()
}
