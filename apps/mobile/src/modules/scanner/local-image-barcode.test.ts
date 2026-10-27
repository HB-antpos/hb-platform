import assert from "node:assert/strict";
import {
  LOCAL_IMAGE_FAILURE_MESSAGE_KEYS,
  scanBarcodeFromImage,
  type LocalImageBarcodeHit,
} from "./local-image-barcode";

const scannerOf = (hits: LocalImageBarcodeHit[]) => ({ scan: async () => hits });

async function main() {
  // 单个条码：原样返回（前导 0 还原交给相机扫码入口，这里不改写）。
  assert.deepEqual(await scanBarcodeFromImage("file:///a.jpg", scannerOf([{ type: "ean13", data: " 6921168509256 " }])), {
    ok: true,
    hit: { type: "ean13", data: "6921168509256" },
  });

  // 没有结果 / 只有空白载荷 -> no-barcode。
  assert.deepEqual(await scanBarcodeFromImage("file:///a.jpg", scannerOf([])), { ok: false, reason: "no-barcode" });
  assert.deepEqual(await scanBarcodeFromImage("file:///a.jpg", scannerOf([{ type: "qr", data: "   " }])), {
    ok: false,
    reason: "no-barcode",
  });

  // 多个不同条码 -> multiple，避免在货架照片里猜错商品。
  assert.deepEqual(
    await scanBarcodeFromImage(
      "file:///a.jpg",
      scannerOf([
        { type: "ean13", data: "6921168509256" },
        { type: "code128", data: "ABC-1" },
      ])
    ),
    { ok: false, reason: "multiple" }
  );

  // 同一条码被重复识别（含 12 位 / 补 0 后 13 位的同值）只算一个。
  const dedup = await scanBarcodeFromImage(
    "file:///a.jpg",
    scannerOf([
      { type: "ean13", data: "012345678905" },
      { type: "upc_a", data: "012345678905" },
      { type: "ean13", data: "0012345678905" },
    ])
  );
  assert.equal(dedup.ok, true);

  // 识别器抛错 -> read-failed，不向上抛。
  assert.deepEqual(
    await scanBarcodeFromImage("file:///a.jpg", {
      scan: async () => {
        throw new Error("boom");
      },
    }),
    { ok: false, reason: "read-failed" }
  );

  // 每个失败原因都必须有文案 key。
  for (const reason of ["no-barcode", "multiple", "read-failed"] as const) {
    assert.match(LOCAL_IMAGE_FAILURE_MESSAGE_KEYS[reason], /^scanner\.image/);
  }

  console.log("local-image-barcode.test.ts: ok");
}

void main();
