import assert from "node:assert/strict";
import {
  FACE_CAMERA_SECRET_MISSING,
  SECRET_REVEAL_CLOSED,
  closeSecretReveal,
  holdsSecret,
  maskCameraUrl,
  normalizeFaceCameraSecretResult,
  openSecretReveal,
} from "./camera-secret";

const URL = "https://hotbargain.example/api/face-ingest/store1008-door-01/SECRETTOKEN123";

// ───────── 响应归一化：只取摄像头与地址，单独的 token 字段不读取、不保留 ─────────
{
  const result = normalizeFaceCameraSecretResult({
    camera: { cameraId: "store1008-door-01", storeCode: "1008", enabled: true },
    token: "SECRETTOKEN123",
    cameraUrl: `  ${URL}  `,
  });
  assert.deepEqual(Object.keys(result).sort(), ["camera", "cameraUrl"]);
  assert.equal(result.cameraUrl, URL, "地址去首尾空白");
  assert.equal(result.camera.cameraId, "store1008-door-01");
  assert.equal(JSON.stringify(result).includes("\"token\""), false);
}

// ───────── 缺地址 / 缺摄像头：抛固定错误码，消息里没有任何响应内容 ─────────
for (const bad of [null, undefined, {}, { camera: { cameraId: "c1" } }, { cameraUrl: URL }, { camera: { cameraId: "" }, cameraUrl: URL }, { camera: { cameraId: "c1" }, cameraUrl: "   " }]) {
  assert.throws(
    () => normalizeFaceCameraSecretResult(bad),
    (error: unknown) =>
      (error as { code?: string }).code === FACE_CAMERA_SECRET_MISSING && (error as Error).message === FACE_CAMERA_SECRET_MISSING,
    `缺字段的响应 ${JSON.stringify(bad)} 要拒绝`,
  );
}

// ───────── 脱敏：保留协议 + 域名 + 路径前缀，最后一段整体掩码，不保留任何 token 字符 ─────────
{
  const masked = maskCameraUrl(URL);
  assert.equal(masked, "https://hotbargain.example/api/face-ingest/store1008-door-01/••••••••");
  assert.equal(masked.includes("SECRET"), false);
  assert.equal(masked.includes("T123"), false);
  assert.equal(maskCameraUrl(""), "••••••••");
  assert.equal(maskCameraUrl(null), "••••••••");
  assert.equal(maskCameraUrl("no-slash-token"), "••••••••", "不是合法地址整体掩码");
  assert.equal(maskCameraUrl("https://host/path/"), "••••••••", "缺最后一段整体掩码");
}

// ───────── 一次性展示状态机：打开只存地址；关闭整体换成已清空常量 ─────────
{
  const state = openSecretReveal("created", { camera: normalizeFaceCameraSecretResult({ camera: { cameraId: "c1" }, cameraUrl: URL }).camera, cameraUrl: URL });
  assert.equal(state.open, true);
  assert.equal(holdsSecret(state), true);
  if (state.open) {
    assert.equal(state.kind, "created");
    assert.equal(state.cameraId, "c1");
    assert.deepEqual(Object.keys(state).sort(), ["cameraId", "cameraUrl", "kind", "open"], "状态里只有地址，没有单独的 token");
  }
  const closed = closeSecretReveal();
  assert.equal(closed, SECRET_REVEAL_CLOSED);
  assert.equal(holdsSecret(closed), false);
  assert.equal(JSON.stringify(closed).includes("SECRET"), false);
  assert.equal(Object.isFrozen(SECRET_REVEAL_CLOSED), true);
  assert.equal(closeSecretReveal(), closed, "重复关闭仍是同一个已清空常量");
}

console.log("camera-secret.test.ts: ok");
