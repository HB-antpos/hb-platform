package expo.modules.hbappinstaller

import android.content.pm.PackageManager
import org.junit.Assert.assertEquals
import org.junit.Test

class HBAppInstallerArchiveFlagsTest {
  @Test
  fun `Android 9 and 10 also request legacy signatures so archive certificates are collected`() {
    for (sdkInt in listOf(28, 29)) {
      assertEquals(
        SIGNING_CERTIFICATES or LEGACY_SIGNATURES,
        archiveSigningCertificateFlags(sdkInt),
      )
    }
  }

  @Test
  fun `Android 11 and later request only signing certificates`() {
    for (sdkInt in listOf(30, 33, 36)) {
      assertEquals(SIGNING_CERTIFICATES, archiveSigningCertificateFlags(sdkInt))
    }
  }

  private companion object {
    const val SIGNING_CERTIFICATES = PackageManager.GET_SIGNING_CERTIFICATES

    @Suppress("DEPRECATION")
    const val LEGACY_SIGNATURES = PackageManager.GET_SIGNATURES
  }
}
