import assert from "node:assert/strict";
import {
  CONTAINER_DETAIL_IMAGE_FAILURE_MAX_ENTRIES,
  CONTAINER_DETAIL_IMAGE_FAILURE_TTL_MS,
  countContainerDetailImageFailures,
  hasContainerDetailImageFailed,
  rememberContainerDetailImageFailure,
  resetContainerDetailImageFailures,
} from "./container-detail-image-failures";

const T0 = 1_000_000;

// 没记过的地址：不算失败
resetContainerDetailImageFailures();
assert.equal(hasContainerDetailImageFailed("https://x/a.jpg", T0), false);

// 记住后在有效期内一律算失败（同一地址反复挂载不再请求）
rememberContainerDetailImageFailure("https://x/a.jpg", T0);
assert.equal(hasContainerDetailImageFailed("https://x/a.jpg", T0), true);
assert.equal(hasContainerDetailImageFailed("https://x/a.jpg", T0 + CONTAINER_DETAIL_IMAGE_FAILURE_TTL_MS - 1), true);
assert.equal(hasContainerDetailImageFailed("https://x/b.jpg", T0), false, "只影响失败的那个地址");

// 到期后放行一次重试（网络抖动导致的失败不会永久吞图），且条目被清掉
assert.equal(hasContainerDetailImageFailed("https://x/a.jpg", T0 + CONTAINER_DETAIL_IMAGE_FAILURE_TTL_MS), false);
assert.equal(countContainerDetailImageFailures(), 0, "过期条目被清理");

// 重复记同一个地址：刷新时间，不重复占位
resetContainerDetailImageFailures();
rememberContainerDetailImageFailure("https://x/a.jpg", T0);
rememberContainerDetailImageFailure("https://x/a.jpg", T0 + 1000);
assert.equal(countContainerDetailImageFailures(), 1);
assert.equal(hasContainerDetailImageFailed("https://x/a.jpg", T0 + 1000 + CONTAINER_DETAIL_IMAGE_FAILURE_TTL_MS - 1), true, "刷新后的有效期从新时间算起");

// 条数有上限：超出时淘汰最早的，最新的保留
resetContainerDetailImageFailures();
const max = CONTAINER_DETAIL_IMAGE_FAILURE_MAX_ENTRIES;
for (let index = 0; index < max + 5; index += 1) {
  rememberContainerDetailImageFailure(`https://x/${index}.jpg`, T0);
}
assert.equal(countContainerDetailImageFailures(), max, "不会无限增长");
assert.equal(hasContainerDetailImageFailed("https://x/0.jpg", T0), false, "最早的被淘汰");
assert.equal(hasContainerDetailImageFailed("https://x/4.jpg", T0), false);
assert.equal(hasContainerDetailImageFailed("https://x/5.jpg", T0), true);
assert.equal(hasContainerDetailImageFailed(`https://x/${max + 4}.jpg`, T0), true, "最新的保留");

resetContainerDetailImageFailures();
assert.equal(countContainerDetailImageFailures(), 0);

console.log("container-detail-image-failures.test.ts: ok");
