// 商品查询「折扣 / 促销 / 多码」三种提示铃音的纯合成逻辑，不依赖 expo，便于单测。
// 与 scan-sound.ts 里的正弦拼接提示音不同：这里是带泛音和指数衰减的铃声，
// 比纯正弦更清脆、更容易和「找到 / 找不到」区分；音高都在 700Hz 以上，Zebra 小喇叭能放出。

export type ScanHighlightKind = "discount" | "promotion" | "multi_code";

export interface BellNote {
  frequency: number;
  startMs: number;
  durationMs: number;
  volume: number;
  /** 衰减速度：值越大尾音越短，默认 6。 */
  decay?: number;
}

export const BELL_SAMPLE_RATE = 8000;

// [相对基频倍数, 相对振幅]；铃声的金属感来自略偏离整数倍的 3.01。
const BELL_PARTIALS: readonly (readonly [number, number])[] = [
  [1, 1],
  [2, 0.45],
  [3.01, 0.2],
];
const BELL_PARTIAL_TOTAL = BELL_PARTIALS.reduce((sum, [, amplitude]) => sum + amplitude, 0);
const ATTACK_SECONDS = 0.004;
const PEAK_LIMIT = 0.98;

export const BELL_PATTERNS: Record<ScanHighlightKind, BellNote[]> = {
  // 折扣：单声清亮铃，带余音。
  discount: [{ frequency: 1175, startMs: 0, durationMs: 520, volume: 0.7 }],
  // 促销：三连铃上行（C6 → E6 → G6）。
  promotion: [
    { frequency: 1047, startMs: 0, durationMs: 380, volume: 0.6 },
    { frequency: 1319, startMs: 170, durationMs: 380, volume: 0.6 },
    { frequency: 1568, startMs: 340, durationMs: 520, volume: 0.7 },
  ],
  // 多码：木琴式「嗒嗒叮」，衰减更快。
  multi_code: [
    { frequency: 784, startMs: 0, durationMs: 220, volume: 0.65, decay: 9 },
    { frequency: 784, startMs: 140, durationMs: 220, volume: 0.65, decay: 9 },
    { frequency: 1175, startMs: 280, durationMs: 300, volume: 0.65, decay: 9 },
  ],
};

export function getBellPatternDurationMs(kind: ScanHighlightKind) {
  return Math.max(...BELL_PATTERNS[kind].map((note) => note.startMs + note.durationMs));
}

/** 把铃音渲染成 -1..1 的单声道采样。高于奈奎斯特频率的泛音直接丢弃，避免混叠出刺耳杂音。 */
export function renderBellPattern(notes: BellNote[]): number[] {
  const totalSamples = Math.ceil(
    (Math.max(...notes.map((note) => note.startMs + note.durationMs)) / 1000) * BELL_SAMPLE_RATE
  );
  const samples = new Array<number>(totalSamples).fill(0);
  const nyquist = BELL_SAMPLE_RATE / 2;

  for (const note of notes) {
    const offset = Math.floor((note.startMs / 1000) * BELL_SAMPLE_RATE);
    const length = Math.floor((note.durationMs / 1000) * BELL_SAMPLE_RATE);
    const decay = note.decay ?? 6;
    const partials = BELL_PARTIALS.filter(([multiple]) => note.frequency * multiple < nyquist);

    for (let index = 0; index < length && offset + index < totalSamples; index += 1) {
      const seconds = index / BELL_SAMPLE_RATE;
      // 4ms 淡入避免爆音，之后按指数衰减。
      const envelope =
        Math.exp((-decay * seconds) / (note.durationMs / 1000)) * Math.min(1, seconds / ATTACK_SECONDS);
      let tone = 0;
      for (const [multiple, amplitude] of partials) {
        tone += amplitude * Math.sin(2 * Math.PI * note.frequency * multiple * seconds);
      }
      samples[offset + index] += (tone / BELL_PARTIAL_TOTAL) * note.volume * envelope;
    }
  }

  const peak = samples.reduce((max, value) => Math.max(max, Math.abs(value)), 0);
  if (peak > PEAK_LIMIT) {
    const scale = PEAK_LIMIT / peak;
    return samples.map((value) => value * scale);
  }
  return samples;
}
