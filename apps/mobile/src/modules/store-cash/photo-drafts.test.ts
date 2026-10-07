import assert from "node:assert/strict";
import {
  allPhotosUploaded,
  appendPhotos,
  collectAttachmentGuids,
  createPhotoDraft,
  hasFailedPhoto,
  hasUploadingPhoto,
  patchPhoto,
  photosNeedingUpload,
  remainingPhotoSlots,
  removePhoto,
  resetPhotosForReupload,
  type PhotoDraft,
} from "./photo-drafts";

const photo = (key: string, patch: Partial<PhotoDraft> = {}): PhotoDraft => ({
  ...createPhotoDraft(key, { uri: `file://${key}.jpg`, width: 1000, height: 800, fileSize: 2048 }),
  ...patch,
});

// ───────── 图片数限制 ─────────
assert.equal(remainingPhotoSlots(0, 3), 3);
assert.equal(remainingPhotoSlots(2, 3), 1);
assert.equal(remainingPhotoSlots(3, 3), 0);
assert.equal(remainingPhotoSlots(5, 3), 0, "超限时不出现负数");

const first = appendPhotos([], [photo("a"), photo("b")], 3);
assert.deepEqual(first.photos.map((item) => item.key), ["a", "b"]);
assert.equal(first.accepted, 2);
assert.equal(first.rejected, 0);

const overflow = appendPhotos(first.photos, [photo("c"), photo("d"), photo("e")], 3);
assert.deepEqual(overflow.photos.map((item) => item.key), ["a", "b", "c"], "超出部分被丢弃，保持传入顺序");
assert.equal(overflow.accepted, 1);
assert.equal(overflow.rejected, 2, "调用方据此提示最多 N 张");

const full = appendPhotos(overflow.photos, [photo("x")], 3);
assert.equal(full.photos.length, 3);
assert.equal(full.accepted, 0);
assert.equal(full.rejected, 1);

// ───────── 删除与状态更新 ─────────
assert.deepEqual(removePhoto([photo("a"), photo("b")], "a").map((item) => item.key), ["b"]);
const patched = patchPhoto([photo("a"), photo("b")], "b", { status: "uploaded", attachmentGuid: "g-b" });
assert.equal(patched[0].status, "ready", "只改指定那张");
assert.equal(patched[1].status, "uploaded");
assert.equal(patched[1].attachmentGuid, "g-b");

// ───────── 上传状态判断 ─────────
const mixed = [
  photo("a", { status: "uploaded", attachmentGuid: "g-a" }),
  photo("b", { status: "failed", failureStage: "upload" }),
  photo("c"),
  photo("d", { status: "uploading" }),
];
assert.deepEqual(
  photosNeedingUpload(mixed).map((item) => item.key),
  ["b", "c"],
  "已上传成功的不重复上传，正在上传的不重复触发，失败与待传的需要上传",
);
assert.equal(hasFailedPhoto(mixed), true);
assert.equal(hasUploadingPhoto(mixed), true);
assert.equal(allPhotosUploaded(mixed), false);
assert.equal(allPhotosUploaded([photo("a", { status: "uploaded", attachmentGuid: "g" })]), true);
assert.equal(allPhotosUploaded([photo("a", { status: "uploaded" })]), false, "没有附件编号不算上传完成");
assert.equal(allPhotosUploaded([]), true, "空列表视为无需上传（是否允许为空由表单规则决定）");

// ───────── 附件编号收集 ─────────
assert.equal(collectAttachmentGuids(mixed), null, "任一张未完成就不能提交");
assert.deepEqual(
  collectAttachmentGuids([
    photo("a", { status: "uploaded", attachmentGuid: "g-a" }),
    photo("b", { status: "uploaded", attachmentGuid: "g-b" }),
  ]),
  ["g-a", "g-b"],
  "按展示顺序",
);

// ───────── 附件失效后退回待上传 ─────────
{
  const reset = resetPhotosForReupload([
    photo("a", { status: "uploaded", attachmentGuid: "g-a" }),
    photo("b", { status: "failed", failureStage: "upload" }),
  ]);
  assert.deepEqual(reset.map((item) => item.status), ["ready", "ready"]);
  assert.ok(reset.every((item) => item.attachmentGuid === undefined && item.failureStage === undefined));
  assert.deepEqual(photosNeedingUpload(reset).map((item) => item.key), ["a", "b"], "下次提交会重新上传，本地文件仍在");
  assert.equal(reset[0].uri, "file://a.jpg", "不需要重新拍照");
}

console.log("photo-drafts.test.ts: ok");
