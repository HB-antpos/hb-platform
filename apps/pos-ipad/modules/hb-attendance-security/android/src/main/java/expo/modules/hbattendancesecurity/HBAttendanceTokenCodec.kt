package expo.modules.hbattendancesecurity

import android.graphics.Bitmap
import android.graphics.Color
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.MultiFormatWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.util.Base64
import java.util.EnumMap
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 与现有 Swift/WPF 完全一致的 HBATE1 A256GCM codec。
 * ES256 只属于 HBEmergencyLoginVerifier，绝不能用于考勤 QR。
 */
internal object HBAttendanceTokenCodec {
  private val secureRandom = SecureRandom()

  fun encrypt(input: AttendanceQrInput, key: ByteArray): String {
    val nonce = ByteArray(NONCE_SIZE).also(secureRandom::nextBytes)
    return try {
      encrypt(input, key, nonce, UUID.randomUUID())
    } finally {
      nonce.fill(0)
    }
  }

  internal fun encrypt(
    input: AttendanceQrInput,
    key: ByteArray,
    nonce: ByteArray,
    tokenId: UUID,
  ): String {
    require(key.size == A256_KEY_SIZE) { "Invalid A256 key." }
    require(nonce.size == NONCE_SIZE) { "Invalid HBATE1 nonce." }
    val plaintext = encodePayload(input, tokenId)
    return try {
      val aad = "$TOKEN_PREFIX.${input.kid}".toByteArray(StandardCharsets.UTF_8)
      val sealed = Cipher.getInstance(CIPHER).run {
        init(
          Cipher.ENCRYPT_MODE,
          SecretKeySpec(key, "AES"),
          GCMParameterSpec(GCM_TAG_BITS, nonce),
        )
        updateAAD(aad)
        doFinal(plaintext)
      }
      require(sealed.size >= GCM_TAG_BYTES) { "Invalid AES-GCM output." }
      val ciphertext = sealed.copyOfRange(0, sealed.size - GCM_TAG_BYTES)
      val tag = sealed.copyOfRange(sealed.size - GCM_TAG_BYTES, sealed.size)
      val token = listOf(
        TOKEN_PREFIX,
        input.kid,
        nonce.base64UrlEncode(),
        ciphertext.base64UrlEncode(),
        tag.base64UrlEncode(),
      ).joinToString(".")
      require(token.length <= MAX_TOKEN_LENGTH) { "HBATE1 token is too long." }
      token
    } finally {
      plaintext.fill(0)
    }
  }

  private fun encodePayload(input: AttendanceQrInput, tokenId: UUID): ByteArray {
    val storeBytes = validatedCode(input.storeCode)
    val deviceBytes = validatedCode(input.deviceCode)
    return ByteArrayOutputStream(
      1 + 16 + Long.SIZE_BYTES + 1 + storeBytes.size + 1 + deviceBytes.size,
    ).use { payload ->
      payload.write(1)
      payload.write(toDotNetGuidBytes(tokenId))
      payload.write(
        ByteBuffer.allocate(Long.SIZE_BYTES)
          .order(ByteOrder.LITTLE_ENDIAN)
          .putLong(input.issuedAtEpochMs)
          .array(),
      )
      payload.write(storeBytes.size.toByte().toInt())
      payload.write(storeBytes)
      payload.write(deviceBytes.size.toByte().toInt())
      payload.write(deviceBytes)
      payload.toByteArray()
    }
  }

  private fun validatedCode(value: String): ByteArray {
    val bytes = value.toByteArray(StandardCharsets.UTF_8)
    require(
      value.isNotEmpty() &&
        value.trim() == value &&
        value.length <= 50 &&
        bytes.size <= 150 &&
        value.none { it.code < 0x20 || it.code == 0x7F },
    ) { "Invalid HBATE1 code." }
    return bytes
  }

  private fun toDotNetGuidBytes(uuid: UUID): ByteArray {
    val bytes = ByteBuffer.allocate(16)
      .order(ByteOrder.BIG_ENDIAN)
      .putLong(uuid.mostSignificantBits)
      .putLong(uuid.leastSignificantBits)
      .array()
    // .NET Guid.ToByteArray() 的前三段是小端，最后 8 字节保持 RFC 顺序。
    bytes.swapAt(0, 3)
    bytes.swapAt(1, 2)
    bytes.swapAt(4, 5)
    bytes.swapAt(6, 7)
    return bytes
  }

  private fun ByteArray.swapAt(first: Int, second: Int) {
    val value = this[first]
    this[first] = this[second]
    this[second] = value
  }

  private fun ByteArray.base64UrlEncode(): String =
    Base64.getUrlEncoder().withoutPadding().encodeToString(this)

  const val TOKEN_PREFIX = "HBATE1"
  private const val CIPHER = "AES/GCM/NoPadding"
  private const val A256_KEY_SIZE = 32
  private const val NONCE_SIZE = 12
  private const val GCM_TAG_BITS = 128
  private const val GCM_TAG_BYTES = GCM_TAG_BITS / 8
  private const val MAX_TOKEN_LENGTH = 600
}

internal object HBAttendanceQrRenderer {
  fun renderDataUri(token: String): String {
    try {
      val hints = EnumMap<EncodeHintType, Any>(EncodeHintType::class.java).apply {
        // 与 iPad 端一致用 Q 级纠错（约 25%）：收银页的码正中压了倒计时圆标，M 级余量偏紧。
        put(EncodeHintType.ERROR_CORRECTION, ErrorCorrectionLevel.Q)
        put(EncodeHintType.MARGIN, 2)
        put(EncodeHintType.CHARACTER_SET, "UTF-8")
      }
      val matrix = MultiFormatWriter().encode(
        token,
        BarcodeFormat.QR_CODE,
        640,
        640,
        hints,
      )
      val pixels = IntArray(matrix.width * matrix.height)
      for (y in 0 until matrix.height) {
        for (x in 0 until matrix.width) {
          pixels[y * matrix.width + x] = if (matrix[x, y]) Color.BLACK else Color.WHITE
        }
      }
      val bitmap = Bitmap.createBitmap(
        pixels,
        matrix.width,
        matrix.height,
        Bitmap.Config.ARGB_8888,
      )
      try {
        val output = ByteArrayOutputStream()
        if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)) {
          throw HBAttendanceSecurityException(
            AttendanceErrorCode.QR_RENDER_FAILED,
            "考勤二维码 PNG 编码失败。",
          )
        }
        return "data:image/png;base64," +
          Base64.getEncoder().encodeToString(output.toByteArray())
      } finally {
        bitmap.recycle()
      }
    } catch (error: HBAttendanceSecurityException) {
      throw error
    } catch (error: Exception) {
      throw HBAttendanceSecurityException(
        AttendanceErrorCode.QR_RENDER_FAILED,
        "无法生成 Android 考勤二维码图像。",
        error,
      )
    }
  }
}
