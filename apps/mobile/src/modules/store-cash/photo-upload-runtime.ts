// 照片直传的运行时实现：读本地文件 → 向后端申请签名 → 对预签名地址 PUT（带返回的全部请求头）。
// 与纯流程（photo-upload.ts）分开，让流程可以在 Node 下用假依赖测试。
import { reviewAwareFetch } from "@/modules/ios-review/network";
import { reportExternalFetchFailure } from "@/shared/logging/external-fetch-log";
import { requestCashUploadSignature } from "./api";
import { CASH_IMAGE_CONTENT_TYPE, CASH_IMAGE_MAX_BYTES } from "./constants";
import type { PhotoUploadDeps } from "./photo-upload";
import type { CashAttachmentUploadSignature } from "./types";

const UPLOAD_SOURCE = "store-cash.upload";

async function readBlob(uri: string): Promise<Blob> {
  const response = await reviewAwareFetch(uri);
  return response.blob();
}

async function readFileSize(uri: string): Promise<number> {
  const blob = await readBlob(uri);
  if (!blob.size || blob.size > CASH_IMAGE_MAX_BYTES) throw new Error("invalid image size");
  return blob.size;
}

async function putFile(uri: string, signature: CashAttachmentUploadSignature): Promise<void> {
  const blob = await readBlob(uri);
  let response: Response;
  try {
    response = await reviewAwareFetch(signature.url, {
      method: "PUT",
      // 预签名 URL 把这些头都签进去了，缺一个对象存储就会拒绝
      headers: signature.headers,
      body: blob,
    });
  } catch (error) {
    reportExternalFetchFailure({
      message: "现金单据图片上传请求失败",
      sourceType: UPLOAD_SOURCE,
      requestMethod: "PUT",
      requestUrl: signature.url,
      error,
      fileUri: uri,
    });
    throw error;
  }
  if (!response.ok) {
    reportExternalFetchFailure({
      message: "现金单据图片上传失败",
      sourceType: UPLOAD_SOURCE,
      requestMethod: "PUT",
      requestUrl: signature.url,
      statusCode: response.status,
      fileUri: uri,
    });
    throw new Error(`Upload failed with status ${response.status}`);
  }
}

/** 绑定分店后的上传依赖：每张图申请一次签名，失败重试会重新申请，不复用过期签名。 */
export function createCashPhotoUploadDeps(storeCode: string): PhotoUploadDeps {
  return {
    contentType: CASH_IMAGE_CONTENT_TYPE,
    readFileSize,
    requestSignature: ({ contentType, fileSize }) =>
      requestCashUploadSignature({ storeCode, contentType, fileSize }),
    putFile,
  };
}
