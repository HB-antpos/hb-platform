package expo.modules.hbattendancesecurity

import expo.modules.kotlin.records.Field
import expo.modules.kotlin.records.Record

private const val MAX_SAFE_JAVASCRIPT_INTEGER = 9_007_199_254_740_991.0
private val ATTENDANCE_KID = Regex("^[A-Za-z0-9_-]{1,64}$")

internal data class AttendanceQrInput(
  val deviceCode: String,
  val issuedAtEpochMs: Long,
  val keyHandle: String,
  val kid: String,
  val storeCode: String,
)

internal class AttendanceQrRequestRecord : Record {
  @Field var deviceCode: String = ""
  @Field var issuedAtEpochMs: Double = 0.0
  @Field var keyHandle: String = ""
  @Field var kid: String = ""
  @Field var storeCode: String = ""

  fun validated(): AttendanceQrInput {
    if (
      !issuedAtEpochMs.isFinite() ||
      issuedAtEpochMs % 1.0 != 0.0 ||
      issuedAtEpochMs < 0.0 ||
      issuedAtEpochMs > MAX_SAFE_JAVASCRIPT_INTEGER
    ) {
      throw attendanceInvalidArgument("issuedAtEpochMs")
    }
    if (!ATTENDANCE_KID.matches(kid)) {
      throw attendanceInvalidArgument("kid")
    }
    return AttendanceQrInput(
      deviceCode = validatedCode(deviceCode, "deviceCode"),
      issuedAtEpochMs = issuedAtEpochMs.toLong(),
      keyHandle = HBAttendanceKeystore.validateHandle(keyHandle),
      kid = kid,
      storeCode = validatedCode(storeCode, "storeCode"),
    )
  }
}

internal data class EmergencyPublicKey(
  val algorithm: String,
  val fingerprintHex: String,
  val kid: String,
  val publicKeyPem: String,
)

internal class EmergencyPublicKeyRecord : Record {
  @Field var algorithm: String = ""
  @Field var fingerprintHex: String = ""
  @Field var kid: String = ""
  @Field var publicKeyPem: String = ""

  fun value() = EmergencyPublicKey(
    algorithm = algorithm,
    fingerprintHex = fingerprintHex,
    kid = kid,
    publicKeyPem = publicKeyPem,
  )
}

internal data class EmergencyVerificationInput(
  val expectedStoreCode: String,
  val nowEpochMs: Long,
  val publicKeys: List<EmergencyPublicKey>,
  val token: String,
)

internal class EmergencyVerificationRequestRecord : Record {
  @Field var expectedStoreCode: String = ""
  @Field var nowEpochMs: Double = 0.0
  @Field var publicKeys: List<EmergencyPublicKeyRecord> = emptyList()
  @Field var token: String = ""

  fun validated(): EmergencyVerificationInput {
    if (
      !nowEpochMs.isFinite() ||
      nowEpochMs % 1.0 != 0.0 ||
      nowEpochMs < 0.0 ||
      nowEpochMs > MAX_SAFE_JAVASCRIPT_INTEGER
    ) {
      throw attendanceInvalidArgument("nowEpochMs")
    }
    if (
      token.length > 2_048 ||
      (!token.startsWith("HBPOSE1-") && !token.startsWith("HBPOSE2-"))
    ) {
      throw attendanceInvalidArgument("token")
    }
    if (publicKeys.size > 128) {
      throw attendanceInvalidArgument("publicKeys")
    }
    return EmergencyVerificationInput(
      expectedStoreCode = validatedCode(expectedStoreCode, "expectedStoreCode"),
      nowEpochMs = nowEpochMs.toLong(),
      publicKeys = publicKeys.map(EmergencyPublicKeyRecord::value),
      token = token,
    )
  }
}

private fun validatedCode(value: String, field: String): String {
  val bytes = value.toByteArray(Charsets.UTF_8)
  if (
    value.isEmpty() ||
    value.trim() != value ||
    value.length > 50 ||
    bytes.size > 150 ||
    value.any { it.code < 0x20 || it.code == 0x7F }
  ) {
    throw attendanceInvalidArgument(field)
  }
  return value
}
