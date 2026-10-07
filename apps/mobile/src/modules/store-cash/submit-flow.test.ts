import assert from "node:assert/strict";
import { createClientRequestIdHolder } from "./client-request-id";
import { buildCreateDepositRequest, type DepositDraft } from "./deposit-form";
import { createPhotoDraft, patchPhoto, type PhotoDraft } from "./photo-drafts";
import { runCashSubmit } from "./submit-flow";
import { uploadPendingPhotos, type PhotoUploadDeps } from "./photo-upload";

const photo = (key: string): PhotoDraft => createPhotoDraft(key, { uri: `file://${key}.jpg`, width: 1, height: 1, fileSize: 1 });

async function main() {
  // 模拟弱网：第一次提交时第二张照片上传失败；修好后重试成功，但「请求其实已落库、响应丢了」再重试一次
  let photos: PhotoDraft[] = [photo("a"), photo("b")];
  const uploadedUris: string[] = [];
  let failB = true;
  let guid = 0;
  const deps: PhotoUploadDeps = {
    contentType: "image/jpeg",
    readFileSize: async () => 10,
    requestSignature: async () => ({ attachmentGuid: `att-${++guid}`, url: "https://put", headers: {}, expiresAtUtc: "" }),
    putFile: async (uri) => {
      if (uri.includes("b.jpg") && failB) throw new Error("network");
      uploadedUris.push(uri);
    },
  };
  let requestCounter = 0;
  const holder = createClientRequestIdHolder(() => `client-req-${++requestCounter}`);
  const draft = (): DepositDraft => ({
    depositDate: "2026-10-07",
    coveredFromDate: null,
    coveredToDate: null,
    note: "",
    overrideReason: "",
    slips: [{ key: "s1", amountText: "100", slipNo: "", photos }],
  });
  const sentRequests: { clientRequestId: string; attachments: string[] }[] = [];
  let sendBehavior: "ok" | "networkError" = "ok";

  const run = () =>
    runCashSubmit({
      uploadPhotos: () =>
        uploadPendingPhotos(photos, deps, (key, patch) => {
          photos = patchPhoto(photos, key, patch);
        }),
      buildRequest: () => buildCreateDepositRequest(draft(), { clientRequestId: holder.current(), storeCode: "S01" }),
      send: async (request) => {
        sentRequests.push({ clientRequestId: request.clientRequestId, attachments: request.slips[0].attachmentGuids });
        if (sendBehavior === "networkError") throw new Error("Network Error");
        return { depositGuid: "d1" };
      },
    });

  // 第一次：b 上传失败 → 不发请求，a 已上传保留
  const first = await run();
  assert.deepEqual(first, { status: "uploadFailed", failedCount: 1 });
  assert.equal(sentRequests.length, 0, "照片没传全不得提交");
  assert.deepEqual(photos.map((item) => item.status), ["uploaded", "failed"]);

  // 第二次：网络恢复，只补传 b；但提交请求本身超时（服务端可能已落库）
  failB = false;
  sendBehavior = "networkError";
  const second = await run();
  assert.equal(second.status, "error");
  assert.deepEqual(uploadedUris, ["file://a.jpg", "file://b.jpg"], "a 只上传过一次，b 补传一次");
  assert.deepEqual(photos.map((item) => item.status), ["uploaded", "uploaded"], "失败后表单与已上传附件都保留");

  // 第三次：重试沿用同一个 clientRequestId、同样的附件，不再上传任何照片
  sendBehavior = "ok";
  const third = await run();
  assert.deepEqual(third, { status: "sent", result: { depositGuid: "d1" } });
  assert.equal(uploadedUris.length, 2, "重试不重复上传");
  assert.deepEqual(
    sentRequests.map((request) => request.clientRequestId),
    ["client-req-1", "client-req-1"],
    "失败重试沿用同一个 clientRequestId",
  );
  assert.deepEqual(sentRequests[0].attachments, sentRequests[1].attachments);

  // 提交成功后才换新的
  holder.rotate();
  assert.equal(holder.current(), "client-req-2", "成功后换新号");
}

async function invalidCase() {
  const outcome = await runCashSubmit({
    uploadPhotos: async () => ({ allUploaded: true, failedKeys: [] }),
    buildRequest: () => null,
    send: async () => {
      throw new Error("should not send");
    },
  });
  assert.deepEqual(outcome, { status: "invalid" }, "构造不出请求（校验未过）时不发送");
}

void main().then(invalidCase).then(() => console.log("submit-flow.test.ts: ok"));
