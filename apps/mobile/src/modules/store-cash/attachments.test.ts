import assert from "node:assert/strict";
import {
  anyAttachmentUrlStale,
  ATTACHMENT_URL_SAFETY_MARGIN_MS,
  isAttachmentUrlStale,
  resolveViewerPage,
  toViewerImages,
} from "./attachments";

const now = Date.parse("2026-10-07T04:00:00Z");

// ───────── 签名下载地址过期判断 ─────────
assert.equal(isAttachmentUrlStale({ urlExpiresAtUtc: "2026-10-07T04:05:00Z" }, now), false, "还有 5 分钟有效");
assert.equal(isAttachmentUrlStale({ urlExpiresAtUtc: "2026-10-07T04:00:20Z" }, now), true, "只剩 20 秒，落在安全余量内");
assert.equal(isAttachmentUrlStale({ urlExpiresAtUtc: "2026-10-07T03:59:00Z" }, now), true, "已过期");
assert.equal(
  isAttachmentUrlStale({ urlExpiresAtUtc: new Date(now + ATTACHMENT_URL_SAFETY_MARGIN_MS).toISOString() }, now),
  true,
  "恰好等于余量算过期",
);
assert.equal(isAttachmentUrlStale({ urlExpiresAtUtc: "" }, now), true, "无法解析时按过期处理，宁可多取一次");
assert.equal(anyAttachmentUrlStale([{ urlExpiresAtUtc: "2026-10-07T04:05:00Z" }, { urlExpiresAtUtc: "2026-10-07T03:00:00Z" }], now), true);
assert.equal(anyAttachmentUrlStale([], now), false);

// ───────── 查看器 ─────────
assert.deepEqual(toViewerImages([{ attachmentGuid: "a", url: "https://x/a" }]), [{ key: "a", uri: "https://x/a" }]);
assert.equal(resolveViewerPage(0, 400, 3), 0);
assert.equal(resolveViewerPage(400, 400, 3), 1);
assert.equal(resolveViewerPage(790, 400, 3), 2, "四舍五入到最近页");
assert.equal(resolveViewerPage(5000, 400, 3), 2, "不超过最后一页");
assert.equal(resolveViewerPage(-50, 400, 3), 0, "回弹时的负偏移回到第一页");
assert.equal(resolveViewerPage(100, 0, 3), 0);
assert.equal(resolveViewerPage(100, 400, 0), 0);

console.log("attachments.test.ts: ok");
