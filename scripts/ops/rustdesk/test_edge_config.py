"""边缘机 WSS 配置的防回退检查：WebSocket 端口只能绑本机，nginx 只暴露两个 WS 路径。"""
import re
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent


class EdgeWssConfigTests(unittest.TestCase):
    def test_websocket_ports_bind_loopback_only(self):
        text = (HERE / "compose.wss.yml").read_text(encoding="utf-8")
        ports = re.findall(r"-\s*'([^']+)'", text)
        self.assertEqual(sorted(ports), ["127.0.0.1:21118:21118/tcp", "127.0.0.1:21119:21119/tcp"])

    def test_main_compose_does_not_publish_websocket_ports(self):
        text = (HERE / "compose.yml").read_text(encoding="utf-8")
        self.assertNotRegex(text, r"2111[89]")

    def test_wss_proxy_exposes_only_the_two_websocket_paths(self):
        text = (HERE / "edge-nginx" / "rd-wss.conf").read_text(encoding="utf-8")
        self.assertEqual(re.findall(r"location\s*=\s*(/\S+)\s*\{", text), ["/ws/id", "/ws/relay"])
        self.assertIn("proxy_pass http://127.0.0.1:21118;", text)
        self.assertIn("proxy_pass http://127.0.0.1:21119;", text)
        # 其余路径一律 404。
        self.assertRegex(text, r"location\s*/\s*\{\s*return 404;\s*\}")

    def test_acme_site_only_serves_challenge_path(self):
        text = (HERE / "edge-nginx" / "rd-acme.conf").read_text(encoding="utf-8")
        self.assertEqual(re.findall(r"location\s+(\S+)\s*\{", text), ["/.well-known/acme-challenge/", "/"])
        self.assertRegex(text, r"location\s*/\s*\{\s*return 404;\s*\}")


if __name__ == "__main__":
    unittest.main()
