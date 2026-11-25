package expo.modules.hbattendancesecurity

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Base64
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class HBAttendanceTokenCodecTest {
  @Test
  fun fixedVectorMatchesSwiftAndWpfHbate1Layout() {
    val key = hex(
      "000102030405060708090a0b0c0d0e0f" +
        "101112131415161718191a1b1c1d1e1f",
    )
    val nonce = hex("202122232425262728292a2b")
    val kid = "AQIDBAUGBwgJCg"
    val token = HBAttendanceTokenCodec.encrypt(
      AttendanceQrInput(
        deviceCode = "POS01",
        issuedAtEpochMs = 1_753_660_800_000,
        keyHandle = "test-only",
        kid = kid,
        storeCode = "S001",
      ),
      key = key,
      nonce = nonce,
      tokenId = UUID.fromString("00112233-4455-4677-8899-aabbccddeeff"),
    )

    assertEquals(
      "HBATE1.AQIDBAUGBwgJCg.ICEiIyQlJicoKSor." +
        "0wmEYWzNXnlc9NtketQpFy9J0MjJGGLubPI_Inm7W1k63dU1." +
        "wjhiVbh3gU077UN6Shst_Q",
      token,
    )

    val parts = token.split(".")
    val ciphertext = Base64.getUrlDecoder().decode(parts[3])
    val tag = Base64.getUrlDecoder().decode(parts[4])
    val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
      init(
        Cipher.DECRYPT_MODE,
        SecretKeySpec(key, "AES"),
        GCMParameterSpec(128, nonce),
      )
      updateAAD("HBATE1.$kid".toByteArray(Charsets.UTF_8))
    }
    val plaintext = cipher.doFinal(ciphertext + tag)
    val expected = byteArrayOf(1) +
      hex("33221100554477468899aabbccddeeff") +
      ByteBuffer.allocate(Long.SIZE_BYTES)
        .order(ByteOrder.LITTLE_ENDIAN)
        .putLong(1_753_660_800_000)
        .array() +
      byteArrayOf(4) + "S001".toByteArray() +
      byteArrayOf(5) + "POS01".toByteArray()
    assertArrayEquals(expected, plaintext)
  }

  private fun hex(value: String): ByteArray = ByteArray(value.length / 2) { index ->
    value.substring(index * 2, index * 2 + 2).toInt(16).toByte()
  }
}
