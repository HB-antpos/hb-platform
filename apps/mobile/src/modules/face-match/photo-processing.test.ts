import assert from "node:assert/strict";
import {
  FacePhotoError,
  PHOTO_PROCESSING_LEVELS,
  PHOTO_SOURCE_MAX_BYTES,
  checkPickedAsset,
  estimateBase64Bytes,
  processPickedPhoto,
  remainingPhotoSlots,
  type ManipulatedImage,
  type PhotoProcessingDeps,
} from "./photo-processing";

/** 生成指定字节数对应的 base64（内容无关紧要）。 */
function base64OfSize(bytes: number): string {
  return Buffer.alloc(bytes, 7).toString("base64");
}

// ───────── 选取阶段的当场校验：类型 ─────────
assert.equal(checkPickedAsset({ uri: "file:///a.jpg", mimeType: "image/jpeg" }), null);
assert.equal(checkPickedAsset({ uri: "file:///a.png", mimeType: "image/png" }), null);
assert.equal(checkPickedAsset({ uri: "file:///a.heic", mimeType: "image/heic" }), null, "iOS 相册的 HEIC 会被转成 JPEG，放行");
assert.equal(checkPickedAsset({ uri: "file:///a.heif", mimeType: "image/heif" }), null);
assert.equal(checkPickedAsset({ uri: "file:///a.gif", mimeType: "image/gif" }), "unsupportedType");
assert.equal(checkPickedAsset({ uri: "file:///a.webp", mimeType: "image/webp" }), "unsupportedType");
assert.equal(checkPickedAsset({ uri: "file:///a.mp4", mimeType: "video/mp4" }), "unsupportedType");
assert.equal(checkPickedAsset({ uri: "file:///a", mimeType: null, fileName: "x.GIF" }), "unsupportedType", "没有 mimeType 时看扩展名");
assert.equal(checkPickedAsset({ uri: "file:///a", mimeType: null, fileName: "x.JPG" }), null);
assert.equal(checkPickedAsset({ uri: "file:///a", mimeType: null, fileName: null }), null, "类型未知时交给解码步骤判断");
// 来源体积离谱
assert.equal(checkPickedAsset({ uri: "file:///a.jpg", mimeType: "image/jpeg", fileSize: PHOTO_SOURCE_MAX_BYTES + 1 }), "sourceTooLarge");
assert.equal(checkPickedAsset({ uri: "file:///a.jpg", mimeType: "image/jpeg", fileSize: PHOTO_SOURCE_MAX_BYTES }), null);

// ───────── base64 字节数估算 ─────────
assert.equal(estimateBase64Bytes(""), 0);
assert.equal(estimateBase64Bytes(base64OfSize(1)), 1);
assert.equal(estimateBase64Bytes(base64OfSize(2)), 2);
assert.equal(estimateBase64Bytes(base64OfSize(3)), 3);
assert.equal(estimateBase64Bytes(base64OfSize(1000)), 1000);
assert.equal(estimateBase64Bytes(base64OfSize(5 * 1024 * 1024)), 5 * 1024 * 1024);

assert.equal(remainingPhotoSlots(0), 5);
assert.equal(remainingPhotoSlots(3), 2);
assert.equal(remainingPhotoSlots(7), 0);

// ───────── 压缩流程（原生能力用假实现） ─────────
interface Harness {
  deps: PhotoProcessingDeps;
  calls: { uri: string; actions: unknown; compress: number }[];
  deleted: string[];
}

function harness(sizes: number[], options: { failOnCall?: number; thumbnail?: Partial<ManipulatedImage> } = {}): Harness {
  const calls: Harness["calls"] = [];
  const deleted: string[] = [];
  let call = 0;
  return {
    calls,
    deleted,
    deps: {
      manipulate: async (uri, actions, opts) => {
        call += 1;
        calls.push({ uri, actions, compress: opts.compress });
        if (options.failOnCall === call) throw new Error("native decode error");
        const levelIndex = call - 1;
        // 前 N 次是整图档位，之后是缩略图
        if (levelIndex < sizes.length) {
          return { uri: `file:///cache/out-${call}.jpg`, width: 2048, height: 1536, base64: base64OfSize(sizes[levelIndex]) };
        }
        return { uri: `file:///cache/thumb-${call}.jpg`, width: 480, height: 360, base64: "VEhVTUI=", ...options.thumbnail };
      },
      deleteTemp: async (uri) => {
        deleted.push(uri);
      },
    },
  };
}

const MB = 1024 * 1024;
const asset = { uri: "file:///cache/ImagePicker/pick.heic", mimeType: "image/heic", width: 4032, height: 3024 };

async function run() {
  // 第一档就满足：转成 JPEG、按长边缩放、生成缩略图，临时文件全部删除
  {
    const h = harness([2 * MB]);
    const result = await processPickedPhoto(asset, h.deps);
    assert.equal(result.byteSize, 2 * MB);
    assert.equal(result.base64.length > 0, true);
    assert.equal(result.previewDataUri, "data:image/jpeg;base64,VEhVTUI=");
    assert.equal(result.width, 2048);
    assert.equal(h.calls.length, 2, "整图一次 + 缩略图一次");
    assert.deepEqual(h.calls[0].actions, [{ resize: { width: 2048 } }], "横图按长边 2048 缩放");
    assert.equal(h.calls[0].compress, PHOTO_PROCESSING_LEVELS[0].quality);
    assert.deepEqual(h.deleted.sort(), ["file:///cache/ImagePicker/pick.heic", "file:///cache/out-1.jpg", "file:///cache/thumb-2.jpg"].sort(), "选图器临时文件、整图结果、缩略图结果全部删除");
  }

  // 竖图按高度缩放；小图不缩放
  {
    const portrait = harness([1 * MB]);
    await processPickedPhoto({ ...asset, width: 3000, height: 4000 }, portrait.deps);
    assert.deepEqual(portrait.calls[0].actions, [{ resize: { height: 2048 } }]);
    const small = harness([100]);
    await processPickedPhoto({ ...asset, width: 800, height: 600 }, small.deps);
    assert.deepEqual(small.calls[0].actions, [], "小图不放大");
  }

  // 超过 5MB 时逐级降档；最终通过
  {
    const h = harness([6 * MB, 5 * MB + 1, 4 * MB]);
    const result = await processPickedPhoto(asset, h.deps);
    assert.equal(result.byteSize, 4 * MB);
    assert.deepEqual(h.calls.slice(0, 3).map((c) => c.compress), PHOTO_PROCESSING_LEVELS.map((level) => level.quality));
    assert.deepEqual(h.calls[1].actions, [{ resize: { width: 1600 } }]);
    assert.deepEqual(h.calls[2].actions, [{ resize: { width: 1280 } }]);
    // 缩略图基于最终通过的整图文件生成
    assert.equal(h.calls[3].uri, "file:///cache/out-3.jpg");
    assert.equal(h.deleted.length, 5, "选图器 + 3 个整图中间结果 + 缩略图都被删除");
  }

  // 所有档位都超过 5MB：报错，仍然清理临时文件
  {
    const h = harness([9 * MB, 8 * MB, 7 * MB]);
    await assert.rejects(() => processPickedPhoto(asset, h.deps), (error) => error instanceof FacePhotoError && error.code === "tooLarge");
    assert.equal(h.deleted.length, 4, "失败路径同样删除全部临时文件");
  }

  // 原生解码失败
  {
    const h = harness([1 * MB], { failOnCall: 1 });
    await assert.rejects(() => processPickedPhoto(asset, h.deps), (error) => error instanceof FacePhotoError && error.code === "decodeFailed");
    assert.deepEqual(h.deleted, [asset.uri]);
  }
  // 缩略图失败也要清理整图结果
  {
    const h = harness([1 * MB], { failOnCall: 2 });
    await assert.rejects(() => processPickedPhoto(asset, h.deps), (error) => error instanceof FacePhotoError && error.code === "decodeFailed");
    assert.ok(h.deleted.includes("file:///cache/out-1.jpg"));
    assert.ok(h.deleted.includes(asset.uri));
  }

  // 类型不合规：当场拒绝，且选图器留下的临时文件立即删除；不会调用原生压缩
  {
    const h = harness([1 * MB]);
    await assert.rejects(
      () => processPickedPhoto({ uri: "file:///cache/ImagePicker/x.gif", mimeType: "image/gif" }, h.deps),
      (error) => error instanceof FacePhotoError && error.code === "unsupportedType",
    );
    assert.equal(h.calls.length, 0);
    assert.deepEqual(h.deleted, ["file:///cache/ImagePicker/x.gif"]);
  }

  // 删除失败不影响结果，也不抛错
  {
    const h = harness([1 * MB]);
    h.deps.deleteTemp = async () => {
      throw new Error("disk busy");
    };
    const result = await processPickedPhoto(asset, h.deps);
    assert.equal(result.byteSize, 1 * MB);
  }
}

run().then(
  () => console.log("photo-processing.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
