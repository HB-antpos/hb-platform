package expo.modules.hbattendancesecurity

import android.util.Base64
import java.nio.charset.StandardCharsets
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec
import java.time.Instant
import java.time.LocalDate
import java.time.format.DateTimeParseException
import java.util.Locale
import java.util.UUID
import org.json.JSONObject

private const val LEGACY_PREFIX = "HBPOSE1-"
private const val V2_PREFIX = "HBPOSE2-"

internal data class EmergencyVerifiedClaims(
  val expiresAtEpochMs: Long,
  val grantId: String,
  val notBeforeEpochMs: Long,
  val storeCode: String,
) {
  fun payload(): Map<String, Any?> = mapOf(
    "expiresAtEpochMs" to expiresAtEpochMs,
    "grantId" to grantId,
    "notBeforeEpochMs" to notBeforeEpochMs,
    "storeCode" to storeCode,
  )
}

internal sealed interface EmergencyVerificationResult {
  fun payload(): Map<String, Any?>

  data class Success(val claims: EmergencyVerifiedClaims) : EmergencyVerificationResult {
    override fun payload(): Map<String, Any?> = mapOf(
      "ok" to true,
      "claims" to claims.payload(),
    )
  }

  data class Failure(val errorCode: String) : EmergencyVerificationResult {
    override fun payload(): Map<String, Any?> = mapOf(
      "ok" to false,
      "errorCode" to errorCode,
    )
  }
}

/** 与 Apple 模块相同，只在本地验证 WPF HBPOSE1/HBPOSE2 离线应急令牌。 */
internal class HBEmergencyLoginVerifier {
  fun validatePublicKey(value: EmergencyPublicKey): Boolean {
    if (
      value.algorithm != "ES256" ||
      !validKid(value.kid) ||
      !Regex("^[A-Fa-f0-9]{64}$").matches(value.fingerprintHex) ||
      value.publicKeyPem.length !in 64..8_192 ||
      !value.publicKeyPem.contains("-----BEGIN PUBLIC KEY-----") ||
      !value.publicKeyPem.contains("-----END PUBLIC KEY-----") ||
      value.publicKeyPem.contains("PRIVATE KEY")
    ) {
      return false
    }
    return try {
      val publicKey = parsePublicKey(value.publicKeyPem)
      val expected = value.fingerprintHex.hexDecode() ?: return false
      val actual = MessageDigest.getInstance("SHA-256").digest(publicKey.encoded)
      MessageDigest.isEqual(actual, expected)
    } catch (_: Exception) {
      false
    }
  }

  fun verify(input: EmergencyVerificationInput): EmergencyVerificationResult =
    when {
      input.token.startsWith(V2_PREFIX) -> verifyV2(input)
      input.token.startsWith(LEGACY_PREFIX) -> verifyLegacy(input)
      else -> failure("EMERGENCY_TOKEN_FORMAT_INVALID")
    }

  private fun verifyLegacy(input: EmergencyVerificationInput): EmergencyVerificationResult {
    val token = input.token
    if (token.isEmpty() || token.length > 2_048) {
      return failure("EMERGENCY_TOKEN_INVALID")
    }
    val parts = token.split("-", limit = 4)
    if (parts.size != 4 || parts[0] != "HBPOSE1" || !validKid(parts[1])) {
      return failure("EMERGENCY_TOKEN_FORMAT_INVALID")
    }
    val kid = parts[1]
    val matchingKeys = input.publicKeys.filter { it.kid == kid }
    if (matchingKeys.isEmpty()) return failure("EMERGENCY_TOKEN_KEY_UNKNOWN")
    if (matchingKeys.size != 1 || !validatePublicKey(matchingKeys.single())) {
      return failure("EMERGENCY_TOKEN_KEY_INVALID")
    }
    val payloadBytes = parts[2].hexDecode() ?: return failure("EMERGENCY_TOKEN_INVALID")
    val signatureBytes = parts[3].hexDecode()
      ?.takeIf { it.size == 64 }
      ?: return failure("EMERGENCY_TOKEN_INVALID")
    val signedBytes = "HBPOSE1-$kid-".toByteArray(StandardCharsets.UTF_8) + payloadBytes
    if (!verifySignature(signatureBytes, signedBytes, matchingKeys.single())) {
      return failure("EMERGENCY_TOKEN_SIGNATURE_INVALID")
    }
    val claims = validateLegacyPayload(payloadBytes)
      ?: return failure("EMERGENCY_TOKEN_PAYLOAD_INVALID")
    if (input.nowEpochMs < claims.notBeforeEpochMs) {
      return failure("EMERGENCY_TOKEN_NOT_ACTIVE")
    }
    if (input.nowEpochMs >= claims.expiresAtEpochMs) {
      return failure("EMERGENCY_TOKEN_EXPIRED")
    }
    val expectedStore = normalizedStoreCode(input.expectedStoreCode)
      ?: return failure("EMERGENCY_TOKEN_WRONG_STORE")
    if (!claims.storeCode.equals(expectedStore, ignoreCase = true)) {
      return failure("EMERGENCY_TOKEN_WRONG_STORE")
    }
    return EmergencyVerificationResult.Success(claims)
  }

  private fun verifyV2(input: EmergencyVerificationInput): EmergencyVerificationResult {
    val token = input.token
    if (token.length != 158) return failure("EMERGENCY_TOKEN_FORMAT_INVALID")
    val encoded = token.removePrefix(V2_PREFIX)
    if (!Regex("^[A-Za-z0-9_-]+$").matches(encoded)) {
      return failure("EMERGENCY_TOKEN_FORMAT_INVALID")
    }
    val decoded = encoded.base64UrlDecode()
      ?.takeIf { it.size == 112 && it.base64UrlEncode() == encoded }
      ?: return failure("EMERGENCY_TOKEN_FORMAT_INVALID")
    val body = decoded.copyOfRange(0, 48)
    val signature = decoded.copyOfRange(48, 112)
    val grantBytes = body.copyOfRange(8, 24)
    val notBeforeSeconds = body.readUInt32BigEndian(40)
    val expiresAtSeconds = body.readUInt32BigEndian(44)
    if (grantBytes.all { it == 0.toByte() } || expiresAtSeconds <= notBeforeSeconds) {
      return failure("EMERGENCY_TOKEN_PAYLOAD_INVALID")
    }

    val selector = body.copyOfRange(0, 8)
    val matchingKeys = input.publicKeys.filter {
      validKid(it.kid) && MessageDigest.isEqual(keySelector(it.kid), selector)
    }
    if (matchingKeys.isEmpty()) return failure("EMERGENCY_TOKEN_KEY_UNKNOWN")
    if (matchingKeys.size != 1 || !validatePublicKey(matchingKeys.single())) {
      return failure("EMERGENCY_TOKEN_KEY_INVALID")
    }
    val signedBytes = V2_PREFIX.toByteArray(StandardCharsets.UTF_8) + body
    if (!verifySignature(signature, signedBytes, matchingKeys.single())) {
      return failure("EMERGENCY_TOKEN_SIGNATURE_INVALID")
    }

    val storeCode = normalizedStoreCode(input.expectedStoreCode)
      ?: return failure("EMERGENCY_TOKEN_WRONG_STORE")
    val expectedStoreFingerprint = MessageDigest.getInstance("SHA-256")
      .digest(storeCode.toByteArray(StandardCharsets.UTF_8))
      .copyOfRange(0, 16)
    if (!MessageDigest.isEqual(expectedStoreFingerprint, body.copyOfRange(24, 40))) {
      return failure("EMERGENCY_TOKEN_WRONG_STORE")
    }

    val notBeforeEpochMs = notBeforeSeconds * 1_000L
    val expiresAtEpochMs = expiresAtSeconds * 1_000L
    if (input.nowEpochMs < notBeforeEpochMs) {
      return failure("EMERGENCY_TOKEN_NOT_ACTIVE")
    }
    if (input.nowEpochMs >= expiresAtEpochMs) {
      return failure("EMERGENCY_TOKEN_EXPIRED")
    }
    val grantId = grantBytes.rfcGuidString()
      ?: return failure("EMERGENCY_TOKEN_PAYLOAD_INVALID")
    return EmergencyVerificationResult.Success(
      EmergencyVerifiedClaims(
        expiresAtEpochMs = expiresAtEpochMs,
        grantId = grantId,
        notBeforeEpochMs = notBeforeEpochMs,
        storeCode = storeCode,
      ),
    )
  }

  private fun validateLegacyPayload(payloadBytes: ByteArray): EmergencyVerifiedClaims? {
    return try {
      val payload = JSONObject(String(payloadBytes, StandardCharsets.UTF_8))
      val grantId = UUID.fromString(payload.getString("grantId"))
        .takeUnless { it == ZERO_UUID }
        ?: return null
      val storeCode = payload.getString("storeCode")
      if (!validLegacyStoreCode(storeCode)) return null
      val businessDate = payload.getString("businessDate")
      if (!validBusinessDate(businessDate)) return null
      if (payload.getString("permissionProfile") != "AllPosTerminal") return null
      val issuer = payload.getString("issuer")
      if (issuer.isBlank() || issuer.trim() != issuer || issuer.length > 128) return null
      if (payload.getString("audience") != "Hbpos.Wpf") return null
      val issuedAt = Instant.parse(payload.getString("issuedAtUtc")).toEpochMilli()
      val notBefore = Instant.parse(payload.getString("notBeforeUtc")).toEpochMilli()
      val expiresAt = Instant.parse(payload.getString("expiresAtUtc")).toEpochMilli()
      if (issuedAt > expiresAt || expiresAt <= notBefore) return null
      EmergencyVerifiedClaims(
        expiresAtEpochMs = expiresAt,
        grantId = grantId.toString().lowercase(Locale.US),
        notBeforeEpochMs = notBefore,
        storeCode = storeCode,
      )
    } catch (_: Exception) {
      null
    }
  }

  private fun verifySignature(
    rawSignature: ByteArray,
    signedBytes: ByteArray,
    key: EmergencyPublicKey,
  ): Boolean {
    return try {
      Signature.getInstance("SHA256withECDSA").run {
        initVerify(parsePublicKey(key.publicKeyPem))
        update(signedBytes)
        verify(EcdsaSignatureCodec.rawToDer(rawSignature))
      }
    } catch (_: Exception) {
      false
    }
  }

  private fun parsePublicKey(pem: String): ECPublicKey {
    val body = pem
      .replace("-----BEGIN PUBLIC KEY-----", "")
      .replace("-----END PUBLIC KEY-----", "")
      .replace(Regex("\\s"), "")
    val der = Base64.decode(body, Base64.DEFAULT)
    val key = KeyFactory.getInstance("EC")
      .generatePublic(X509EncodedKeySpec(der)) as? ECPublicKey
      ?: throw IllegalArgumentException("Not an EC public key.")
    require(key.params.curve.field.fieldSize == 256) { "Not a P-256 public key." }
    return key
  }

  private fun validKid(value: String): Boolean =
    Regex("^[A-Za-z0-9]{1,32}$").matches(value)

  private fun validLegacyStoreCode(value: String): Boolean =
    value.isNotEmpty() && value.trim() == value && value.length <= 50

  private fun normalizedStoreCode(value: String): String? {
    val normalized = value.trim().uppercase(Locale.US)
    return normalized.takeIf { it.isNotEmpty() && it.length <= 50 }
  }

  private fun validBusinessDate(value: String): Boolean {
    if (!Regex("^\\d{4}-\\d{2}-\\d{2}$").matches(value)) return false
    return try {
      LocalDate.parse(value).toString() == value
    } catch (_: DateTimeParseException) {
      false
    }
  }

  private fun keySelector(kid: String): ByteArray = MessageDigest.getInstance("SHA-256")
    .digest(kid.toByteArray(StandardCharsets.UTF_8))
    .copyOfRange(0, 8)

  private fun failure(code: String) = EmergencyVerificationResult.Failure(code)

  companion object {
    private val ZERO_UUID = UUID(0L, 0L)
  }
}

/** ES256 DER/raw 转换仅服务于现有 HBPOSE1/HBPOSE2 emergency token 验签。 */
internal object EcdsaSignatureCodec {
  fun rawToDer(raw: ByteArray): ByteArray {
    require(raw.size == 64) { "Invalid raw ES256 signature." }
    val r = encodeInteger(raw.copyOfRange(0, 32))
    val s = encodeInteger(raw.copyOfRange(32, 64))
    val body = byteArrayOf(0x02, r.size.toByte()) + r +
      byteArrayOf(0x02, s.size.toByte()) + s
    return byteArrayOf(0x30, body.size.toByte()) + body
  }

  private fun encodeInteger(value: ByteArray): ByteArray {
    var offset = 0
    while (offset < value.lastIndex && value[offset] == 0.toByte()) offset += 1
    val unsigned = value.copyOfRange(offset, value.size)
    return if (unsigned[0].toInt() and 0x80 != 0) {
      byteArrayOf(0) + unsigned
    } else {
      unsigned
    }
  }
}

private fun String.hexDecode(): ByteArray? {
  if (length % 2 != 0) return null
  return try {
    ByteArray(length / 2) { index ->
      substring(index * 2, index * 2 + 2).toInt(16).toByte()
    }
  } catch (_: NumberFormatException) {
    null
  }
}

private fun String.base64UrlDecode(): ByteArray? = try {
  Base64.decode(this, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
} catch (_: IllegalArgumentException) {
  null
}

private fun ByteArray.base64UrlEncode(): String = Base64.encodeToString(
  this,
  Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING,
)

private fun ByteArray.readUInt32BigEndian(offset: Int): Long {
  require(offset >= 0 && offset + 4 <= size)
  var result = 0L
  for (index in offset until offset + 4) {
    result = (result shl 8) or (this[index].toLong() and 0xFF)
  }
  return result
}

private fun ByteArray.rfcGuidString(): String? {
  if (size != 16) return null
  val hex = joinToString("") { "%02x".format(Locale.US, it.toInt() and 0xFF) }
  return listOf(
    hex.substring(0, 8),
    hex.substring(8, 12),
    hex.substring(12, 16),
    hex.substring(16, 20),
    hex.substring(20, 32),
  ).joinToString("-")
}
