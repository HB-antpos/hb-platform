import assert from "node:assert/strict";
import test from "node:test";

import {
  createApkDownloadProgressForwarder,
  describeApkDownloadProgress,
} from "./apk-download-progress";

test("进度只在整数百分比前进时转发，越界与非法值被夹紧或丢弃", () => {
  const received: unknown[] = [];
  const forward = createApkDownloadProgressForwarder(200, (progress) => received.push(progress));

  forward(-10);
  forward(1);
  forward(2);
  forward(Number.NaN);
  forward(101);
  forward(500);
  forward(200);

  assert.deepEqual(received, [
    { bytesWritten: 0, totalBytes: 200 },
    { bytesWritten: 2, totalBytes: 200 },
    { bytesWritten: 101, totalBytes: 200 },
    { bytesWritten: 200, totalBytes: 200 },
  ]);
});

test("总大小无效时不转发任何进度", () => {
  const received: unknown[] = [];
  createApkDownloadProgressForwarder(0, (progress) => received.push(progress))(0);
  assert.deepEqual(received, []);
});

test("进度文案与语言无关，百分比向下取整", () => {
  assert.deepEqual(
    describeApkDownloadProgress({ bytesWritten: 12_884_902, totalBytes: 47_815_065 }),
    { percent: 26, label: "26% · 12.3 / 45.6 MB" },
  );
  assert.deepEqual(
    describeApkDownloadProgress({ bytesWritten: 5, totalBytes: 0 }),
    { percent: 0, label: "0% · 0.0 / 0.0 MB" },
  );
});
