package expo.modules.hbappinstaller

import android.content.pm.PackageManager
import android.os.Build

/**
 * 读取未安装 APK（getPackageArchiveInfo）签名证书所需的标志位。
 *
 * Android 9/10（API 28/29）的 getPackageArchiveInfo 只在带 GET_SIGNATURES 时才收集证书；
 * 仅传 GET_SIGNING_CERTIFICATES 时 signingDetails 为 UNKNOWN，signingInfo 恒为 null，
 * 校验会把合法 APK 判成“签名证书不可读”。API 30 起任一标志都会收集证书。
 * 两个标志同时传入时，signingInfo 仍按 GET_SIGNING_CERTIFICATES 语义生成（含轮换历史）。
 */
internal fun archiveSigningCertificateFlags(sdkInt: Int): Int =
  if (sdkInt < Build.VERSION_CODES.R) {
    @Suppress("DEPRECATION")
    PackageManager.GET_SIGNING_CERTIFICATES or PackageManager.GET_SIGNATURES
  } else {
    PackageManager.GET_SIGNING_CERTIFICATES
  }
