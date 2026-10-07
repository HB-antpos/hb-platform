import assert from "node:assert/strict";
import { createPhotoDraft, patchPhoto, type PhotoDraft } from "./photo-drafts";
import { uploadPendingPhotos, uploadSinglePhoto, type PhotoUploadDeps, type PhotoUploadPatch } from "./photo-upload";
import type { CashAttachmentUploadSignature } from "./types";

const photo = (key: string, patch: Partial<PhotoDraft> = {}): PhotoDraft => ({
  ...createPhotoDraft(key, { uri: `file://${key}.jpg`, width: 1000, height: 800, fileSize: 999 }),
  ...patch,
});

function makeDeps(options: { failUploadFor?: Set<string>; failSignatureFor?: Set<string> } = {}) {
  const calls = { signatures: [] as { contentType: string; fileSize: number }[], puts: [] as string[] };
  let guidCounter = 0;
  const deps: PhotoUploadDeps = {
    contentType: "image/jpeg",
    // 实际字节数取自文件名长度，便于确认签名请求用的是读到的真实大小而不是草稿里声明的 999
    readFileSize: async (uri) => uri.length,
    requestSignature: async (request) => {
      calls.signatures.push(request);
      if (options.failSignatureFor?.has(String(calls.signatures.length))) throw new Error("signature failed");
      guidCounter += 1;
      const signature: CashAttachmentUploadSignature = {
        attachmentGuid: `att-${guidCounter}`,
        url: `https://storage.example/put/${guidCounter}`,
        headers: { "Content-Type": "image/jpeg", "x-amz-meta": "1" },
        expiresAtUtc: "2026-10-07T05:00:00Z",
      };
      return signature;
    },
    putFile: async (uri, signature) => {
      calls.puts.push(`${uri}->${signature.url}`);
      if (options.failUploadFor?.has(uri)) throw new Error("upload failed");
    },
  };
  return { deps, calls };
}

async function main() {
  // ───────── 单张：成功 ─────────
  {
    const { deps, calls } = makeDeps();
    const result = await uploadSinglePhoto(photo("a"), deps);
    assert.deepEqual(result, { status: "uploaded", attachmentGuid: "att-1" });
    assert.equal(calls.signatures[0].fileSize, "file://a.jpg".length, "签名请求以实际读到的字节数为准");
    assert.equal(calls.signatures[0].contentType, "image/jpeg");
  }

  // ───────── 单张：各阶段失败不抛错，返回失败阶段 ─────────
  {
    const { deps } = makeDeps({ failUploadFor: new Set(["file://a.jpg"]) });
    const result = await uploadSinglePhoto(photo("a"), deps);
    assert.equal(result.status, "failed");
    assert.equal(result.failureStage, "upload");
  }
  {
    const { deps } = makeDeps({ failSignatureFor: new Set(["1"]) });
    const result = await uploadSinglePhoto(photo("a"), deps);
    assert.equal(result.status, "failed");
    assert.equal(result.failureStage, "signature");
  }
  {
    const { deps } = makeDeps();
    const result = await uploadSinglePhoto(photo("a"), { ...deps, readFileSize: async () => 0 });
    assert.equal(result.status, "failed");
    assert.equal(result.failureStage, "read", "空文件在读取阶段失败");
  }

  // ───────── 批量：失败的就地重试，已成功的不重复上传 ─────────
  {
    const failing = new Set(["file://b.jpg"]);
    const { deps, calls } = makeDeps({ failUploadFor: failing });
    let photos: PhotoDraft[] = [photo("a"), photo("b"), photo("c")];
    const apply = (key: string, patch: PhotoUploadPatch) => {
      photos = patchPhoto(photos, key, patch);
    };

    const firstRun = await uploadPendingPhotos(photos, deps, apply);
    assert.equal(firstRun.allUploaded, false);
    assert.deepEqual(firstRun.failedKeys, ["b"]);
    assert.deepEqual(photos.map((item) => item.status), ["uploaded", "failed", "uploaded"]);
    assert.equal(photos[1].failureStage, "upload");
    assert.equal(calls.signatures.length, 3);

    // 修好网络后重试：只会再传 b，a 与 c 不再碰
    failing.clear();
    const putsBefore = calls.puts.length;
    const secondRun = await uploadPendingPhotos(photos, deps, apply);
    assert.equal(secondRun.allUploaded, true);
    assert.deepEqual(photos.map((item) => item.status), ["uploaded", "uploaded", "uploaded"]);
    assert.equal(calls.puts.length - putsBefore, 1, "已上传成功的不重复上传");
    assert.equal(calls.signatures.length, 4, "重试只重新申请失败那张的签名");
    assert.equal(photos[1].failureStage, undefined, "重试成功后清掉失败阶段");
    assert.ok(photos.every((item) => item.attachmentGuid));
  }

  // ───────── 批量：uploading 的不重复触发；全部已上传时什么都不做 ─────────
  {
    const { deps, calls } = makeDeps();
    const patches: string[] = [];
    const done = await uploadPendingPhotos(
      [photo("a", { status: "uploaded", attachmentGuid: "g" }), photo("b", { status: "uploading" })],
      deps,
      (key) => patches.push(key),
    );
    assert.equal(calls.signatures.length, 0);
    assert.equal(patches.length, 0);
    assert.equal(done.allUploaded, false, "仍有一张在上传，整体不算完成");
  }
  {
    const { deps, calls } = makeDeps();
    const done = await uploadPendingPhotos([photo("a", { status: "uploaded", attachmentGuid: "g" })], deps, () => undefined);
    assert.equal(done.allUploaded, true);
    assert.equal(calls.signatures.length, 0);
  }

  // ───────── 并发上限 ─────────
  {
    let running = 0;
    let peak = 0;
    const { deps } = makeDeps();
    const slow: PhotoUploadDeps = {
      ...deps,
      putFile: async () => {
        running += 1;
        peak = Math.max(peak, running);
        await new Promise((resolve) => setTimeout(resolve, 5));
        running -= 1;
      },
    };
    await uploadPendingPhotos([photo("a"), photo("b"), photo("c"), photo("d"), photo("e")], slow, () => undefined, 2);
    assert.equal(peak, 2, "弱网下同时上传不超过 2 张");
  }

  console.log("photo-upload.test.ts: ok");
}

void main();
