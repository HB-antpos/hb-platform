import assert from "node:assert/strict";
import { CASH_IMAGE_MAX_BYTES } from "./constants";
import { CashImageProcessingError, processCashPhoto } from "./image-processing";

async function main() {
  // 横图：长边（宽）按 2048 缩放，保持完整比例不裁剪
  const landscapeActions: unknown[][] = [];
  const landscape = await processCashPhoto(
    { uri: "file://receipt.heic", width: 4032, height: 3024 },
    {
      manipulate: async (_uri, actions) => {
        landscapeActions.push(actions);
        return { uri: "file://receipt.jpg", width: 2048, height: 1536 };
      },
      readBlobSize: async () => 800_000,
    },
  );
  assert.deepEqual(landscapeActions[0], [{ resize: { width: 2048 } }]);
  assert.deepEqual(landscape, { uri: "file://receipt.jpg", width: 2048, height: 1536, fileSize: 800_000 });

  // 竖图：长边（高）按 2048 缩放
  const portraitActions: unknown[][] = [];
  await processCashPhoto(
    { uri: "file://slip.jpg", width: 3024, height: 4032 },
    {
      manipulate: async (_uri, actions) => {
        portraitActions.push(actions);
        return { uri: "file://slip-out.jpg", width: 1536, height: 2048 };
      },
      readBlobSize: async () => 500_000,
    },
  );
  assert.deepEqual(portraitActions[0], [{ resize: { height: 2048 } }]);

  // 小图不放大
  const smallActions: unknown[][] = [];
  await processCashPhoto(
    { uri: "file://small.jpg", width: 800, height: 600 },
    {
      manipulate: async (_uri, actions) => {
        smallActions.push(actions);
        return { uri: "file://small-out.jpg", width: 800, height: 600 };
      },
      readBlobSize: async () => 90_000,
    },
  );
  assert.deepEqual(smallActions[0], [{ resize: { width: 800 } }], "不放大小图");

  // 超过 5 MiB 逐级降质量，直到不超限
  const qualities: number[] = [];
  const sizes = [CASH_IMAGE_MAX_BYTES + 1, CASH_IMAGE_MAX_BYTES - 1];
  const compressed = await processCashPhoto(
    { uri: "file://big.jpg", width: 4000, height: 3000 },
    {
      manipulate: async (_uri, _actions, options) => {
        qualities.push(options.compress);
        return { uri: `file://big-${qualities.length}.jpg`, width: 1600, height: 1200 };
      },
      readBlobSize: async () => sizes.shift() ?? 0,
    },
  );
  assert.deepEqual(qualities, [0.85, 0.72], "先 0.85，超限再 0.72");
  assert.equal(compressed.fileSize, CASH_IMAGE_MAX_BYTES - 1);
  assert.equal(compressed.uri, "file://big-2.jpg", "返回的是最终上传的那张");

  // 最低档仍超限：file_too_large
  await assert.rejects(
    () =>
      processCashPhoto(
        { uri: "file://huge.jpg", width: 9000, height: 9000 },
        {
          manipulate: async () => ({ uri: "file://huge-out.jpg", width: 1280, height: 1280 }),
          readBlobSize: async () => CASH_IMAGE_MAX_BYTES + 10,
        },
      ),
    (error: unknown) => error instanceof CashImageProcessingError && error.code === "file_too_large",
  );

  // 解码失败
  await assert.rejects(
    () =>
      processCashPhoto(
        { uri: "file://broken.jpg", width: 100, height: 100 },
        {
          manipulate: async () => {
            throw new Error("decode");
          },
          readBlobSize: async () => 1,
        },
      ),
    (error: unknown) => error instanceof CashImageProcessingError && error.code === "decode_failed",
  );

  // 读取处理结果失败
  await assert.rejects(
    () =>
      processCashPhoto(
        { uri: "file://x.jpg", width: 100, height: 100 },
        {
          manipulate: async () => ({ uri: "file://y.jpg", width: 100, height: 100 }),
          readBlobSize: async () => {
            throw new Error("blob");
          },
        },
      ),
    (error: unknown) => error instanceof CashImageProcessingError && error.code === "blob_read_failed",
  );

  // 来源没带宽高：先补宽高再处理，不会把图缩成 1 像素
  const resolvedActions: unknown[][] = [];
  await processCashPhoto(
    { uri: "file://nodim.jpg", width: 0, height: undefined },
    {
      resolveSize: async () => ({ width: 3000, height: 4000 }),
      manipulate: async (_uri, actions) => {
        resolvedActions.push(actions);
        return { uri: "file://nodim-out.jpg", width: 1536, height: 2048 };
      },
      readBlobSize: async () => 300_000,
    },
  );
  assert.deepEqual(resolvedActions[0], [{ resize: { height: 2048 } }]);

  // 补宽高也失败
  await assert.rejects(
    () =>
      processCashPhoto(
        { uri: "file://nodim.jpg" },
        {
          resolveSize: async () => {
            throw new Error("no size");
          },
          manipulate: async () => ({ uri: "x", width: 1, height: 1 }),
          readBlobSize: async () => 1,
        },
      ),
    (error: unknown) => error instanceof CashImageProcessingError && error.code === "decode_failed",
  );

  console.log("image-processing.test.ts: ok");
}

void main();
