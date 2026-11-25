package expo.modules.hbattendancesecurity

import expo.modules.kotlin.exception.CodedException

internal object AttendanceErrorCode {
  const val INVALID_ARGUMENT = "ATTENDANCE_SECURITY_INVALID_ARGUMENT"
  const val KEY_NOT_FOUND = "ATTENDANCE_KEY_NOT_FOUND"
  // 与既有 Swift/TS 合同保持同一码值；Android 上涵盖 Keystore 与私有存储故障。
  const val KEYCHAIN_FAILURE = "ATTENDANCE_KEYCHAIN_FAILURE"
  const val KEY_GENERATION_FAILED = "ATTENDANCE_KEY_GENERATION_FAILED"
  const val TOKEN_GENERATION_FAILED = "ATTENDANCE_TOKEN_GENERATION_FAILED"
  const val QR_RENDER_FAILED = "ATTENDANCE_QR_RENDER_FAILED"
}

internal class HBAttendanceSecurityException(
  code: String,
  message: String,
  cause: Throwable? = null,
) : CodedException(code, message, cause)

internal fun attendanceInvalidArgument(field: String) =
  HBAttendanceSecurityException(
    AttendanceErrorCode.INVALID_ARGUMENT,
    "考勤安全参数无效：$field",
  )
