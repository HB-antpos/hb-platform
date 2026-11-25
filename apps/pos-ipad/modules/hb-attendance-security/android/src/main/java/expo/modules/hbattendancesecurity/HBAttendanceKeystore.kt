package expo.modules.hbattendancesecurity

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import java.security.SecureRandom
import java.util.Base64
import java.util.Locale
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

internal data class AttendanceA256Identity(
  val keyHandle: String,
  val kid: String,
) {
  fun payload(): Map<String, Any?> = mapOf(
    "keyHandle" to keyHandle,
    "kid" to kid,
  )
}

private data class WrappedAttendanceKey(
  val ciphertext: ByteArray,
  val consumed: Boolean,
  val kid: String,
  val nonce: ByteArray,
)

/**
 * HBATE1 必须把 32-byte A256 key 登记给现有后端，所以该对称 key 不是“永不导出”。
 * AndroidKeyStore 中真正不可导出的是每个身份的 AES-GCM wrapping key；attendance key
 * 只以密文存入 app-private SharedPreferences，并且登记明文只允许返回一次。
 */
internal class HBAttendanceKeystore(context: Context) {
  private val keyStore: KeyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply {
    load(null)
  }
  private val records: SharedPreferences = context.getSharedPreferences(
    ATTENDANCE_KEY_PREFERENCES,
    Context.MODE_PRIVATE,
  )
  private val secureRandom = SecureRandom()

  @Synchronized
  fun createIdentity(): AttendanceA256Identity {
    repeat(8) {
      val handle = UUID.randomUUID().toString().lowercase(Locale.US)
      val alias = aliasFor(handle)
      if (keyStore.containsAlias(alias) || hasStoredRecord(handle)) return@repeat

      val attendanceKey = ByteArray(ATTENDANCE_KEY_SIZE).also(secureRandom::nextBytes)
      val kidBytes = ByteArray(KID_BYTE_SIZE).also(secureRandom::nextBytes)
      val kid = kidBytes.base64UrlEncode()
      try {
        val wrappingKey = generateWrappingKey(alias)
        val cipher = Cipher.getInstance(WRAPPING_CIPHER).apply {
          init(Cipher.ENCRYPT_MODE, wrappingKey)
          updateAAD(wrappingAad(handle, kid))
        }
        val nonce = cipher.iv
        if (nonce.size != WRAPPING_NONCE_SIZE) {
          throw HBAttendanceSecurityException(
            AttendanceErrorCode.KEY_GENERATION_FAILED,
            "Android Keystore 生成了不兼容的包装 nonce。",
          )
        }
        val ciphertext = cipher.doFinal(attendanceKey)
        val stored = records.edit()
          .putString(ciphertextPreference(handle), ciphertext.base64UrlEncode())
          .putString(noncePreference(handle), nonce.base64UrlEncode())
          .putString(kidPreference(handle), kid)
          .putBoolean(consumedPreference(handle), false)
          .commit()
        if (!stored) {
          throw HBAttendanceSecurityException(
            AttendanceErrorCode.KEYCHAIN_FAILURE,
            "无法保存 Android 考勤包装密钥记录。",
          )
        }
        return AttendanceA256Identity(handle, kid)
      } catch (error: HBAttendanceSecurityException) {
        cleanupFailedIdentity(handle, alias)
        throw error
      } catch (error: Exception) {
        cleanupFailedIdentity(handle, alias)
        throw HBAttendanceSecurityException(
          AttendanceErrorCode.KEY_GENERATION_FAILED,
          "无法生成 Android 考勤 A256 身份。",
          error,
        )
      } finally {
        attendanceKey.fill(0)
        kidBytes.fill(0)
      }
    }
    throw HBAttendanceSecurityException(
      AttendanceErrorCode.KEY_GENERATION_FAILED,
      "无法分配考勤密钥标识。",
    )
  }

  @Synchronized
  fun hasKey(keyHandle: String): Boolean {
    val handle = validateHandle(keyHandle)
    return try {
      if (!keyStore.containsAlias(aliasFor(handle))) return false
      readRecord(handle, missingReturnsNull = true) != null
    } catch (error: HBAttendanceSecurityException) {
      throw error
    } catch (error: Exception) {
      throw storageFailure("无法检查 Android 考勤密钥。", error)
    }
  }

  @Synchronized
  fun readRegistrationKeyMaterial(keyHandle: String): String {
    val handle = validateHandle(keyHandle)
    val record = readRecord(handle)
      ?: throw missingKey()
    if (record.consumed) {
      // 合同没有新增 consumed 错误码；登记明文已不可再次读取时按 key material 不存在处理。
      throw HBAttendanceSecurityException(
        AttendanceErrorCode.KEY_NOT_FOUND,
        "考勤登记密钥材料已被读取，不能再次导出。",
      )
    }

    val attendanceKey = decrypt(handle, record)
    return try {
      val encoded = attendanceKey.base64UrlEncode()
      if (!records.edit().putBoolean(consumedPreference(handle), true).commit()) {
        throw storageFailure("无法持久化考勤登记密钥已读取状态。")
      }
      encoded
    } finally {
      // base64 字符串必须跨桥交给现有注册 API；这里只能清掉本地临时明文字节。
      attendanceKey.fill(0)
    }
  }

  @Synchronized
  fun readAttendanceKey(keyHandle: String, expectedKid: String): ByteArray {
    val handle = validateHandle(keyHandle)
    val record = readRecord(handle)
      ?: throw missingKey()
    if (record.kid != expectedKid) {
      throw attendanceInvalidArgument("kid")
    }
    return decrypt(handle, record)
  }

  @Synchronized
  fun destroyKey(keyHandle: String) {
    val handle = validateHandle(keyHandle)
    try {
      keyStore.deleteEntry(aliasFor(handle))
      if (!clearRecord(handle)) {
        throw storageFailure("无法删除 Android 考勤密钥记录。")
      }
    } catch (error: HBAttendanceSecurityException) {
      throw error
    } catch (error: Exception) {
      throw storageFailure("无法删除 Android 考勤密钥。", error)
    }
  }

  private fun generateWrappingKey(alias: String): SecretKey {
    val generator = KeyGenerator.getInstance(
      KeyProperties.KEY_ALGORITHM_AES,
      ANDROID_KEY_STORE,
    )
    generator.init(
      KeyGenParameterSpec.Builder(
        alias,
        KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
      )
        .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
        .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
        .setKeySize(256)
        .setRandomizedEncryptionRequired(true)
        .setUserAuthenticationRequired(false)
        .build(),
    )
    return generator.generateKey()
  }

  private fun decrypt(handle: String, record: WrappedAttendanceKey): ByteArray {
    val wrappingKey = try {
      (keyStore.getEntry(aliasFor(handle), null) as? KeyStore.SecretKeyEntry)
        ?.secretKey
    } catch (error: Exception) {
      throw storageFailure("无法访问 Android Keystore 包装密钥。", error)
    } ?: throw missingKey()
    return try {
      val plaintext = Cipher.getInstance(WRAPPING_CIPHER).run {
        init(
          Cipher.DECRYPT_MODE,
          wrappingKey,
          GCMParameterSpec(GCM_TAG_BITS, record.nonce),
        )
        updateAAD(wrappingAad(handle, record.kid))
        doFinal(record.ciphertext)
      }
      if (plaintext.size != ATTENDANCE_KEY_SIZE) {
        plaintext.fill(0)
        throw storageFailure("Android 考勤密钥长度无效。")
      }
      plaintext
    } catch (error: HBAttendanceSecurityException) {
      throw error
    } catch (error: Exception) {
      throw storageFailure("无法解密 Android 考勤密钥。", error)
    }
  }

  private fun readRecord(
    handle: String,
    missingReturnsNull: Boolean = false,
  ): WrappedAttendanceKey? {
    val ciphertextValue = records.getString(ciphertextPreference(handle), null)
    val nonceValue = records.getString(noncePreference(handle), null)
    val kid = records.getString(kidPreference(handle), null)
    if (ciphertextValue == null && nonceValue == null && kid == null) {
      if (missingReturnsNull) return null
      throw missingKey()
    }
    if (ciphertextValue == null || nonceValue == null || kid == null) {
      throw storageFailure("Android 考勤密钥记录不完整。")
    }
    return try {
      val ciphertext = ciphertextValue.base64UrlDecode()
      val nonce = nonceValue.base64UrlDecode()
      if (
        ciphertext.size != ATTENDANCE_KEY_SIZE + GCM_TAG_BYTES ||
        nonce.size != WRAPPING_NONCE_SIZE ||
        !ATTENDANCE_KID.matches(kid)
      ) {
        throw storageFailure("Android 考勤密钥记录无效。")
      }
      WrappedAttendanceKey(
        ciphertext = ciphertext,
        consumed = records.getBoolean(consumedPreference(handle), false),
        kid = kid,
        nonce = nonce,
      )
    } catch (error: HBAttendanceSecurityException) {
      throw error
    } catch (error: Exception) {
      throw storageFailure("无法读取 Android 考勤密钥记录。", error)
    }
  }

  private fun hasStoredRecord(handle: String): Boolean =
    records.contains(ciphertextPreference(handle)) ||
      records.contains(noncePreference(handle)) ||
      records.contains(kidPreference(handle)) ||
      records.contains(consumedPreference(handle))

  private fun cleanupFailedIdentity(handle: String, alias: String) {
    runCatching { keyStore.deleteEntry(alias) }
    clearRecord(handle)
  }

  private fun clearRecord(handle: String): Boolean = records.edit()
    .remove(ciphertextPreference(handle))
    .remove(noncePreference(handle))
    .remove(kidPreference(handle))
    .remove(consumedPreference(handle))
    .commit()

  private fun missingKey() = HBAttendanceSecurityException(
    AttendanceErrorCode.KEY_NOT_FOUND,
    "考勤签名密钥不存在。",
  )

  private fun storageFailure(
    message: String,
    cause: Throwable? = null,
  ) = HBAttendanceSecurityException(
    AttendanceErrorCode.KEYCHAIN_FAILURE,
    message,
    cause,
  )

  companion object {
    private const val ANDROID_KEY_STORE = "AndroidKeyStore"
    private const val ATTENDANCE_KEY_PREFERENCES = "hb_attendance_a256_keys"
    private const val KEY_ALIAS_PREFIX = "com.hbweb.posipad.attendance.a256.wrap."
    private const val WRAPPING_CIPHER = "AES/GCM/NoPadding"
    private const val ATTENDANCE_KEY_SIZE = 32
    private const val KID_BYTE_SIZE = 10
    private const val WRAPPING_NONCE_SIZE = 12
    private const val GCM_TAG_BITS = 128
    private const val GCM_TAG_BYTES = GCM_TAG_BITS / 8
    private val ATTENDANCE_KID = Regex("^[A-Za-z0-9_-]{14}$")
    private val HANDLE_PATTERN = Regex(
      "^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
    )

    fun validateHandle(value: String): String {
      if (!HANDLE_PATTERN.matches(value)) {
        throw attendanceInvalidArgument("keyHandle")
      }
      return value
    }

    private fun aliasFor(handle: String) = KEY_ALIAS_PREFIX + handle
    private fun ciphertextPreference(handle: String) = "$handle.ciphertext"
    private fun noncePreference(handle: String) = "$handle.nonce"
    private fun kidPreference(handle: String) = "$handle.kid"
    private fun consumedPreference(handle: String) = "$handle.consumed"

    private fun wrappingAad(handle: String, kid: String): ByteArray =
      "HB_ATTENDANCE_A256_WRAP_V1.$handle.$kid"
        .toByteArray(StandardCharsets.UTF_8)

    private fun ByteArray.base64UrlEncode(): String =
      Base64.getUrlEncoder().withoutPadding().encodeToString(this)

    private fun String.base64UrlDecode(): ByteArray =
      Base64.getUrlDecoder().decode(this)
  }
}
