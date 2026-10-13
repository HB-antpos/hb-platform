import assert from "node:assert/strict";
import { isAppCacheFile } from "./photo-runtime";

const CACHE_IOS = "file:///var/mobile/Containers/Data/Application/AAAA/Library/Caches/";
const CACHE_ANDROID = "file:///data/user/0/com.hbweb.expo/cache/";

// 选图器 / 压缩器的产物在应用缓存目录里：允许删除
assert.equal(isAppCacheFile(`${CACHE_IOS}ImagePicker/1234.jpeg`, CACHE_IOS), true);
assert.equal(isAppCacheFile(`${CACHE_IOS}ImageManipulator/abcd.jpg`, CACHE_IOS), true);
assert.equal(isAppCacheFile(`${CACHE_ANDROID}ImagePicker/x.jpg`, CACHE_ANDROID), true);

// 缓存目录之外的文件（相册原图、文档目录、其他 App 的 content:// 地址）一律不删
assert.equal(isAppCacheFile("file:///var/mobile/Media/DCIM/100APPLE/IMG_0001.HEIC", CACHE_IOS), false);
assert.equal(isAppCacheFile("file:///var/mobile/Containers/Data/Application/AAAA/Documents/keep.jpg", CACHE_IOS), false);
assert.equal(isAppCacheFile("content://media/external/images/media/42", CACHE_ANDROID), false);
assert.equal(isAppCacheFile("ph://ABC-123", CACHE_IOS), false);
// 目录穿越
assert.equal(isAppCacheFile(`${CACHE_IOS}../Documents/keep.jpg`, CACHE_IOS), false);
assert.equal(isAppCacheFile(`${CACHE_IOS}ImagePicker/../../Documents/keep.jpg`, CACHE_IOS), false);
// 取不到缓存目录时不删任何东西
assert.equal(isAppCacheFile(`${CACHE_IOS}ImagePicker/1234.jpeg`, null), false);
assert.equal(isAppCacheFile(`${CACHE_IOS}ImagePicker/1234.jpeg`, undefined), false);
assert.equal(isAppCacheFile(`${CACHE_IOS}ImagePicker/1234.jpeg`, ""), false);

console.log("photo-runtime.test.ts: ok");
