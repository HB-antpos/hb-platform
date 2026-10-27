#!/usr/bin/env python3
"""RustDesk 服务端（hbbs/hbbr）协议级端到端检查，走公网，不依赖也不改动任何真实客户端配置。

用途：新建/迁移/重建 RustDesk 服务器后，在把设备指过去之前确认它真的可用。检查项：
1. 库副本是否生效：给一个真实设备 ID → 应返回 OFFLINE；不存在的 ID → ID_NOT_EXIST。
2. UDP 注册：RegisterPeer 要求上报公钥 → RegisterPk 写入成功 → 再注册不再要求公钥。
3. 打洞：对在线 peer 发请求，hbbs 先转发 FetchLocalAddr，peer 经新 TCP 连接回 LocalAddr 后，请求方收到
   带 ed25519 签名的设备公钥——用设备配置里的服务器公钥验签通过，证明服务器私钥就是设备信任的那一把。
4. 下发的中继地址等于 --expect-relay。
5. hbbr 中继：两端用同一 uuid 连 21117，一端发的帧另一端原样收到。

注意：第 2 步会在服务器库里写入一条 id 以 e2e 开头的合成 peer（仅存在于库，不会成为公司设备）。
脚本结束会打印清理 SQL，请在服务器上执行；不要用 Python 之外的方式改库。
手写 protobuf，字段号来自 RustDesk rendezvous.proto；hbbs 以 `-k _` 运行时请求须带 licence_key=服务器公钥。
"""
import argparse
import base64
import hashlib
import os
import socket
import sys
import threading
import time
import uuid


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def ld(field, payload):
    """length-delimited 字段。"""
    if isinstance(payload, str):
        payload = payload.encode()
    return varint((field << 3) | 2) + varint(len(payload)) + payload


def read_varint(buf, i):
    shift = result = 0
    while True:
        b = buf[i]
        i += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, i
        shift += 7


def parse(buf):
    """解析一层 protobuf：{字段号: [值...]}，长度限定字段为 bytes，varint 为 int。"""
    i, out = 0, {}
    while i < len(buf):
        tag, i = read_varint(buf, i)
        field, wire = tag >> 3, tag & 7
        if wire == 0:
            value, i = read_varint(buf, i)
        elif wire == 2:
            n, i = read_varint(buf, i)
            value, i = buf[i:i + n], i + n
        else:
            raise ValueError(f"unsupported wire type {wire}")
        out.setdefault(field, []).append(value)
    return out


def frame(msg):
    """hbb_common BytesCodec：头 1~3 字节，低 2 位 = 长度字节数-1，其余位 = 长度。"""
    n = len(msg)
    if n <= 0x3F:
        return bytes([n << 2]) + msg
    if n <= 0x3FFF:
        return ((n << 2) | 1).to_bytes(2, "little") + msg
    return ((n << 2) | 2).to_bytes(3, "little") + msg


def _recv_exact(sock, n):
    data = b""
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise EOFError("connection closed")
        data += chunk
    return data


def read_frame(sock):
    first = _recv_exact(sock, 1)
    head = first + _recv_exact(sock, first[0] & 3)
    return _recv_exact(sock, int.from_bytes(head, "little") >> 2)


# ---- ed25519 验签（RFC 8032 参考实现，纯 Python，仅用于校验服务器对设备公钥的签名） ----
_Q = 2 ** 255 - 19
_D = -121665 * pow(121666, _Q - 2, _Q) % _Q
_I = pow(2, (_Q - 1) // 4, _Q)


def _inv(x):
    return pow(x, _Q - 2, _Q)


def _xrecover(y):
    xx = (y * y - 1) * _inv(_D * y * y + 1)
    x = pow(xx, (_Q + 3) // 8, _Q)
    if (x * x - xx) % _Q:
        x = x * _I % _Q
    return _Q - x if x & 1 else x


_BY = 4 * _inv(5)
_B = (_xrecover(_BY) % _Q, _BY % _Q)


def _add(p, q):
    x1, y1 = p
    x2, y2 = q
    t = _D * x1 * x2 * y1 * y2
    return ((x1 * y2 + x2 * y1) * _inv(1 + t) % _Q, (y1 * y2 + x1 * x2) * _inv(1 - t) % _Q)


def _mul(p, e):
    if e == 0:
        return (0, 1)
    half = _mul(p, e // 2)
    half = _add(half, half)
    return _add(half, p) if e & 1 else half


def _point(s):
    y = int.from_bytes(s, "little") & ((1 << 255) - 1)
    x = _xrecover(y)
    return (_Q - x if (x & 1) != (s[31] >> 7) else x, y)


def ed25519_verify(sig, msg, pk):
    if len(sig) != 64 or len(pk) != 32:
        return False
    r, a = _point(sig[:32]), _point(pk)
    h = int.from_bytes(hashlib.sha512(sig[:32] + pk + msg).digest(), "little")
    return _mul(_B, int.from_bytes(sig[32:], "little")) == _add(r, _mul(a, h))


class Checker:
    def __init__(self, host, key, expect_relay):
        self.host, self.key, self.expect_relay = host, key, expect_relay
        self.results = []

    def check(self, name, ok, detail=""):
        self.results.append(ok)
        print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""))

    def tcp(self, port):
        s = socket.create_connection((self.host, port), timeout=8)
        s.settimeout(8)
        return s

    def punch(self, target_id):
        request = ld(1, target_id) + ld(3, self.key)  # PunchHoleRequest{id, licence_key}
        with self.tcp(21116) as s:
            s.sendall(frame(ld(8, request)))
            msg = parse(read_frame(s))
        return parse(msg[11][0]) if 11 in msg else {"raw": msg}

    def run(self, real_id, synth_id):
        # ① 库副本
        r = self.punch(real_id)
        self.check("库副本命中真实设备 ID → 返回 OFFLINE", r.get(3) == [2], f"failure={r.get(3)}")
        r = self.punch("000000001")
        self.check("不存在的 ID → 返回 ID_NOT_EXIST", 3 not in r and 2 not in r and "raw" not in r, f"fields={sorted(r)}")

        # ② UDP 注册
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        u.settimeout(5)
        u.bind(("0.0.0.0", 0))
        peer_uuid, peer_pk = os.urandom(16), os.urandom(32)

        def udp(msg):
            u.sendto(msg, (self.host, 21116))
            data, _ = u.recvfrom(2048)
            return parse(data)

        resp = udp(ld(6, ld(1, synth_id)))  # RegisterPeer
        self.check("UDP RegisterPeer：未知设备被要求上报公钥",
                   7 in resp and parse(resp[7][0]).get(2) == [1], f"{resp}")
        resp = udp(ld(15, ld(1, synth_id) + ld(2, peer_uuid) + ld(3, peer_pk)))  # RegisterPk
        self.check("UDP RegisterPk：写入新 peer 成功", 16 in resp and 1 not in parse(resp[16][0]), f"{resp}")
        resp = udp(ld(6, ld(1, synth_id)))
        self.check("UDP RegisterPeer：已登记设备不再要求公钥", 7 in resp and 2 not in parse(resp[7][0]), f"{resp}")

        # ③ 打洞：测试端与合成 peer 同一公网 IP，hbbs 走“同局域网”流程，先向 peer 要 LocalAddr。
        holder = {}
        worker = threading.Thread(target=lambda: holder.update(r=self.punch(synth_id)))
        worker.start()
        fetched = None
        try:
            data, _ = u.recvfrom(2048)
            msg = parse(data)
            if 12 in msg:  # FetchLocalAddr{socket_addr=1, relay_server=2}
                fetched = parse(msg[12][0])
                # 真实客户端经一条新的 TCP 连接把 LocalAddr 交给 hbbs；version >= 1.1.5 才会下发公钥。
                local = ld(13, ld(1, fetched[1][0]) + ld(2, bytes([10, 0, 0, 7, 0x52, 0x6C]))
                           + ld(3, (fetched.get(2) or [b""])[0]) + ld(4, synth_id) + ld(5, "1.4.9"))
                with self.tcp(21116) as c:
                    c.sendall(frame(local))
                    time.sleep(1)
        except socket.timeout:
            pass
        worker.join(15)
        r = holder.get("r", {})
        self.check("hbbs 向在线 peer 转发了 FetchLocalAddr", fetched is not None and 1 in fetched)
        signed = (r.get(2) or [b""])[0]
        server_pk = base64.b64decode(self.key)
        self.check("设备公钥带有效签名（用服务器公钥验签通过）",
                   len(signed) > 64 and ed25519_verify(signed[:64], signed[64:], server_pk), f"signed_len={len(signed)}")
        idpk = parse(signed[64:]) if len(signed) > 64 else {}
        self.check("签名内容的设备 ID 与公钥就是刚注册的那一份",
                   idpk.get(1) == [synth_id.encode()] and idpk.get(2) == [peer_pk], f"id={idpk.get(1)}")
        relay = (fetched.get(2) or [b""])[0].decode() if fetched else (r.get(4) or [b""])[0].decode()
        self.check(f"下发的中继地址是 {self.expect_relay}", relay == self.expect_relay, f"relay_server={relay!r}")

        # ④ hbbr 中继
        # 两端必须携带同一个 uuid：请求只构造一次，A、B 各发一遍，hbbr 按 uuid 配对。
        request = frame(ld(18, ld(1, synth_id) + ld(2, str(uuid.uuid4())) + ld(6, self.key)))
        a, b = self.tcp(21117), self.tcp(21117)
        a.sendall(request)
        time.sleep(0.5)
        b.sendall(request)
        time.sleep(0.5)
        a.sendall(frame(b"hb-e2e-relay-ok"))
        try:
            got = read_frame(b)
            self.check("hbbr 中继配对并转发数据(A→B)", got == b"hb-e2e-relay-ok", f"got={got!r}")
        except Exception as exc:  # noqa: BLE001
            self.check("hbbr 中继配对并转发数据(A→B)", False, repr(exc))
        a.close()
        b.close()
        return all(self.results)


def main(argv=None):
    parser = argparse.ArgumentParser(description="RustDesk 服务端协议级端到端检查")
    parser.add_argument("--host", required=True, help="RustDesk 服务器域名，例如 rd.hotbargain.vip")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--key", help="服务器公钥（id_ed25519.pub 的内容，不是私钥）")
    group.add_argument("--key-file", help="id_ed25519.pub 文件路径")
    parser.add_argument("--real-id", required=True, help="库里已有的真实设备 ID，用来验证库副本")
    parser.add_argument("--expect-relay", help="期望下发的中继地址，默认 <host>:21117")
    args = parser.parse_args(argv)
    key = args.key or open(args.key_file, encoding="utf-8").read().strip()
    if len(base64.b64decode(key, validate=True)) != 32:
        raise SystemExit("服务器公钥必须是 32 字节 base64")
    synth_id = "e2e%08d" % (int(time.time()) % 10 ** 8)  # 每次换新 ID，避免与上次遗留行的 uuid 冲突
    checker = Checker(args.host, key, args.expect_relay or f"{args.host}:21117")
    ok = checker.run(args.real_id, synth_id)
    print(f"\n{sum(checker.results)}/{len(checker.results)} 项通过")
    print("清理测试数据（在服务器上执行）：\n"
          "  sudo sqlite3 /www/rustdesk-server/data/db_v2.sqlite3 \"delete from peer where id like 'e2e%';\"\n"
          "  然后重启 rustdesk-server 容器清掉内存里的合成 peer，并确认日志含 relay-servers=[...]")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
