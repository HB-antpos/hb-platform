package expo.modules.hbattendancesecurity

import android.os.SystemClock
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition

class HBAttendanceSecurityModule : Module() {
  private val keyStorage: HBAttendanceKeystore by lazy {
    val context = appContext.reactContext?.applicationContext
      ?: throw HBAttendanceSecurityException(
        AttendanceErrorCode.KEYCHAIN_FAILURE,
        "Android 应用上下文不可用。",
      )
    HBAttendanceKeystore(context)
  }
  private val emergencyVerifier = HBEmergencyLoginVerifier()

  override fun definition() = ModuleDefinition {
    Name("HBAttendanceSecurity")

    Function("getSystemUptimeMilliseconds") {
      // elapsedRealtime 包含休眠时间且不受墙钟回拨影响，供 TS 维持可信时间锚点。
      SystemClock.elapsedRealtime().toDouble()
    }

    AsyncFunction("createA256Identity") {
      keyStorage.createIdentity().payload()
    }

    AsyncFunction("hasA256Key") { keyHandle: String ->
      keyStorage.hasKey(keyHandle)
    }

    AsyncFunction("readRegistrationKeyMaterial") { keyHandle: String ->
      keyStorage.readRegistrationKeyMaterial(keyHandle)
    }

    AsyncFunction("issueAttendanceQr") { record: AttendanceQrRequestRecord ->
      val input = record.validated()
      val attendanceKey = keyStorage.readAttendanceKey(
        input.keyHandle,
        input.kid,
      )
      val token = try {
        try {
          HBAttendanceTokenCodec.encrypt(input, attendanceKey)
        } catch (error: Exception) {
          throw HBAttendanceSecurityException(
            AttendanceErrorCode.TOKEN_GENERATION_FAILED,
            "无法生成 Android 考勤二维码内容。",
            error,
          )
        }
      } finally {
        // 对称考勤密钥必须短暂解密才能生成 HBATE1；使用后立即覆盖临时数组。
        attendanceKey.fill(0)
      }
      // 只把二维码图像交给 JS，避免 HBATE1 token 落入业务状态或日志。
      mapOf("imageUri" to HBAttendanceQrRenderer.renderDataUri(token))
    }

    AsyncFunction("destroyA256Key") { keyHandle: String ->
      keyStorage.destroyKey(keyHandle)
    }

    AsyncFunction("validateEs256P256PublicKey") { record: EmergencyPublicKeyRecord ->
      emergencyVerifier.validatePublicKey(record.value())
    }

    AsyncFunction("verifyEs256P256Token") { record: EmergencyVerificationRequestRecord ->
      emergencyVerifier.verify(record.validated()).payload()
    }
  }
}
