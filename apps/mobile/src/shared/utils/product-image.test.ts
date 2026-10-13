import assert from "node:assert/strict";
import { productThumbnailUri } from "./product-image";

const COS = "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/A100.jpg";

assert.equal(productThumbnailUri(COS, 120), `${COS}?imageMogr2/thumbnail/120x120/format/webp`, "COS 原图追加缩略图参数");
assert.equal(productThumbnailUri(` ${COS} `, 80), `${COS}?imageMogr2/thumbnail/80x80/format/webp`, "去掉首尾空白");
assert.equal(
  productThumbnailUri("http://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/257/X.jpg", 80),
  "http://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/257/X.jpg?imageMogr2/thumbnail/80x80/format/webp",
  "另一个 COS 桶、http 协议同样处理",
);
assert.equal(productThumbnailUri("https://example.com/a.jpg", 120), "https://example.com/a.jpg", "非 COS 外链原样返回");
assert.equal(
  productThumbnailUri("https://evil.example.com/x.cos.ap-shanghai.myqcloud.com/a.jpg", 120),
  "https://evil.example.com/x.cos.ap-shanghai.myqcloud.com/a.jpg",
  "域名必须以 COS 主机开头，路径里带 COS 字样的外链不处理",
);
assert.equal(productThumbnailUri(`${COS}?v=1`, 120), `${COS}?v=1`, "已带查询参数的不追加");
assert.equal(productThumbnailUri("   ", 120), null, "空白按没有图片");
assert.equal(productThumbnailUri(undefined, 120), null);
assert.equal(productThumbnailUri(null, 120), null);

console.log("product-image.test.ts: ok");
