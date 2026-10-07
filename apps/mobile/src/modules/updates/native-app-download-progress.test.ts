import assert from "node:assert/strict";
import { describeNativeAppDownloadProgress } from "./native-app-download-progress";
import { createDownloadProgressForwarder } from "./native-app-update";

assert.deepEqual(
  describeNativeAppDownloadProgress({ bytesWritten: 12_884_902, totalBytes: 47_815_065 }),
  { percent: 26, label: "26% · 12.3 / 45.6 MB" },
  "百分比向下取整，MB 保留一位小数且与语言无关",
);
assert.deepEqual(
  describeNativeAppDownloadProgress({ bytesWritten: 99, totalBytes: 40 }),
  { percent: 100, label: "100% · 0.0 / 0.0 MB" },
  "已写入超过总大小时夹紧到 100%",
);
assert.deepEqual(
  describeNativeAppDownloadProgress({ bytesWritten: 5, totalBytes: 0 }),
  { percent: 0, label: "0% · 0.0 / 0.0 MB" },
);

{
  const received: unknown[] = [];
  const forward = createDownloadProgressForwarder(0, (progress) => received.push(progress));
  forward(0);
  assert.deepEqual(received, [], "总大小无效时不转发");
}

console.log("native-app-download-progress.test.ts: ok");
