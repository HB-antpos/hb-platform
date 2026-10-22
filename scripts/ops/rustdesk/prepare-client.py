#!/usr/bin/env python3
"""按 RustDesk 1.4.9 官方文件名配置协议生成免填服务器的原版客户端。"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil

parser = argparse.ArgumentParser()
parser.add_argument("--source", type=Path, required=True)
parser.add_argument("--public-key", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
# 服务器域名只允许公司两个地址，与 WPF RemoteMaintenanceServerPolicy 保持一致；默认沿用历史地址。
parser.add_argument("--server-host", choices=["hotbargain.vip", "rd.hotbargain.vip"], default="hotbargain.vip")
args = parser.parse_args()
expected_sha = "eaedeb0088e687bf46f7c46a9c6ea5493ce51f3134dfd6acbedb47b5b9136274"
if hashlib.file_digest(args.source.open("rb"), "sha256").hexdigest() != expected_sha:
    raise SystemExit("客户端不是已验证的官方 1.4.9 Windows x64 文件")
public_key = args.public_key.read_text().strip()
if len(base64.b64decode(public_key, validate=True)) != 32:
    raise SystemExit("服务器公钥格式无效")
# 只封装公共服务器地址和公钥；不包含密码、访问令牌或服务器私钥。
config = {"host": f"{args.server_host}:21116", "key": public_key, "relay": f"{args.server_host}:21117"}
encoded = base64.urlsafe_b64encode(json.dumps(config, separators=(",", ":")).encode()).decode().rstrip("=")[::-1]
filename = "rustdesk--" + encoded + ".exe"
if len(filename) > 240:
    raise SystemExit("配置文件名超出安全长度")
args.output.mkdir(parents=True, exist_ok=True)
destination = args.output / filename
if destination.exists():
    raise SystemExit("目标已存在，请使用新的输出目录")
shutil.copyfile(args.source, destination)
if hashlib.file_digest(destination.open("rb"), "sha256").hexdigest() != expected_sha:
    raise SystemExit("复制后的客户端校验失败")
manifest = {"version": "1.4.9", "fileName": filename, "sha256": expected_sha, "sizeBytes": destination.stat().st_size}
(args.output / "rustdesk-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print("免配置客户端已生成，原版二进制 SHA-256 验证通过；请保留下载文件名。")
