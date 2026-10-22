import base64
import socket
import threading
import unittest

import e2e_check as e2e


class ProtobufTests(unittest.TestCase):
    def test_varint_roundtrip(self):
        for n in (0, 1, 127, 128, 300, 16384, 2 ** 31):
            value, end = e2e.read_varint(e2e.varint(n), 0)
            self.assertEqual((value, end), (n, len(e2e.varint(n))))

    def test_parse_reads_strings_bytes_and_varints(self):
        message = e2e.ld(1, "abc") + e2e.ld(2, b"\x00\x01") + e2e.varint(3 << 3) + e2e.varint(7)
        self.assertEqual(e2e.parse(message), {1: [b"abc"], 2: [b"\x00\x01"], 3: [7]})

    def test_register_peer_matches_the_wire_bytes_servers_accept(self):
        # RendezvousMessage{register_peer{id}}：字段 6 → 0x32，内层 id 字段 1 → 0x0a。
        self.assertEqual(e2e.ld(6, e2e.ld(1, "hbprobe")), bytes([0x32, 0x09, 0x0A, 0x07]) + b"hbprobe")


class FrameTests(unittest.TestCase):
    def roundtrip(self, size):
        left, right = socket.socketpair()
        try:
            payload = bytes(range(256)) * (size // 256) + bytes(size % 256)
            # socketpair 缓冲区很小，大帧必须边发边读，否则 sendall 会在没有读端时阻塞。
            sender = threading.Thread(target=lambda: left.sendall(e2e.frame(payload)))
            sender.start()
            right.settimeout(10)
            self.assertEqual(e2e.read_frame(right), payload)
            sender.join(10)
        finally:
            left.close()
            right.close()

    def test_one_two_and_three_byte_headers(self):
        self.assertEqual(len(e2e.frame(b"x" * 0x3F)) - 0x3F, 1)
        self.assertEqual(len(e2e.frame(b"x" * 0x40)) - 0x40, 2)
        self.assertEqual(len(e2e.frame(b"x" * 0x4000)) - 0x4000, 3)
        for size in (1, 0x3F, 0x40, 0x3FFF, 0x4000):
            self.roundtrip(size)


class Ed25519Tests(unittest.TestCase):
    # RFC 8032 第 7.1 节官方测试向量。
    VECTORS = [
        ("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
         "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
        ("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
         "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
    ]

    def test_official_vectors_verify(self):
        for pk, msg, sig in self.VECTORS:
            self.assertTrue(e2e.ed25519_verify(bytes.fromhex(sig), bytes.fromhex(msg), bytes.fromhex(pk)))

    def test_tampered_message_signature_or_key_is_rejected(self):
        pk, msg, sig = (bytes.fromhex(x) for x in self.VECTORS[1])
        self.assertFalse(e2e.ed25519_verify(sig, msg + b"\x00", pk))
        self.assertFalse(e2e.ed25519_verify(bytes([sig[0] ^ 1]) + sig[1:], msg, pk))
        self.assertFalse(e2e.ed25519_verify(sig, msg, bytes.fromhex(self.VECTORS[0][0])))
        self.assertFalse(e2e.ed25519_verify(sig[:-1], msg, pk))


class ArgumentTests(unittest.TestCase):
    def test_rejects_a_key_that_is_not_32_bytes(self):
        bad = base64.b64encode(b"\x01" * 31).decode()
        with self.assertRaises(SystemExit):
            e2e.main(["--host", "rd.example.test", "--key", bad, "--real-id", "123456789"])


if __name__ == "__main__":
    unittest.main()
