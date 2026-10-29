import assert from "node:assert/strict";
import {
  BELL_PATTERNS,
  BELL_SAMPLE_RATE,
  getBellPatternDurationMs,
  renderBellPattern,
  type ScanHighlightKind,
} from "./scan-bell-tones";

const kinds = Object.keys(BELL_PATTERNS) as ScanHighlightKind[];
assert.deepEqual([...kinds].sort(), ["discount", "multi_code", "promotion"]);

for (const kind of kinds) {
  const samples = renderBellPattern(BELL_PATTERNS[kind]);

  // 时长 = 最后一个音结束的时刻。
  assert.equal(samples.length, Math.ceil((getBellPatternDurationMs(kind) / 1000) * BELL_SAMPLE_RATE), `${kind} 时长`);

  // 不能有 NaN / 越界，否则 16 位 PCM 会爆音。
  let peak = 0;
  for (const value of samples) {
    assert.ok(Number.isFinite(value), `${kind} 含非有限采样`);
    peak = Math.max(peak, Math.abs(value));
  }
  assert.ok(peak <= 0.98 + 1e-9, `${kind} 峰值 ${peak} 不得削波`);
  assert.ok(peak >= 0.2, `${kind} 音量不能太小（${peak}）`);

  // 衰减：结尾比起音安静得多，没有拖尾爆音。
  const tail = samples.slice(-Math.floor(BELL_SAMPLE_RATE * 0.02));
  const tailPeak = tail.reduce((max, value) => Math.max(max, Math.abs(value)), 0);
  assert.ok(tailPeak < peak * 0.25, `${kind} 结尾应已衰减（${tailPeak} vs ${peak}）`);

  // 所有基频都要在 Zebra 小喇叭能放出的范围内（>=500Hz），且低于奈奎斯特。
  for (const note of BELL_PATTERNS[kind]) {
    assert.ok(note.frequency >= 500 && note.frequency < BELL_SAMPLE_RATE / 2, `${kind} 基频 ${note.frequency}`);
  }
}

// 三种提示音必须互相不同（时长或音符数不同），否则听不出区别。
const signatures = kinds.map((kind) => `${BELL_PATTERNS[kind].length}:${getBellPatternDurationMs(kind)}`);
assert.equal(new Set(signatures).size, kinds.length, "三种铃音节奏必须各不相同");

console.log("scan-bell-tones.test.ts: ok");
