import { createAudioPlayer } from "expo-audio";
import type { ScanFeedbackState } from "@/modules/scanner/types";

type PlayableScanStatus = Exclude<ScanFeedbackState["status"], "ready" | "scanning">;
type GeneratedToneKey =
  | PlayableScanStatus
  | "barcode_captured"
  | "attendance_punch_success";

interface ToneStep {
  durationMs: number;
  frequency: number;
  volume?: number;
}

const SAMPLE_RATE = 8000;
const PCM_MAX = 32767;
const WAV_HEADER_SIZE = 44;
const PLAYER_CLEANUP_PADDING_MS = 300;

const SOUND_PATTERNS: Record<GeneratedToneKey, ToneStep[]> = {
  barcode_captured: [
    { frequency: 660, durationMs: 55, volume: 0.24 },
  ],
  attendance_punch_success: [
    { frequency: 784, durationMs: 85, volume: 0.34 },
    { frequency: 0, durationMs: 25 },
    { frequency: 988, durationMs: 95, volume: 0.38 },
    { frequency: 0, durationMs: 25 },
    { frequency: 1319, durationMs: 150, volume: 0.42 },
  ],
  found: [
    { frequency: 988, durationMs: 90, volume: 0.32 },
  ],
  added: [
    { frequency: 880, durationMs: 70, volume: 0.35 },
    { frequency: 0, durationMs: 25 },
    { frequency: 1175, durationMs: 110, volume: 0.4 },
  ],
  multiple: [
    { frequency: 784, durationMs: 100, volume: 0.32 },
    { frequency: 0, durationMs: 35 },
    { frequency: 988, durationMs: 100, volume: 0.32 },
  ],
  price_update_required: [
    { frequency: 988, durationMs: 85, volume: 0.32 },
    { frequency: 0, durationMs: 30 },
    { frequency: 1319, durationMs: 110, volume: 0.34 },
    { frequency: 0, durationMs: 30 },
    { frequency: 988, durationMs: 120, volume: 0.32 },
  ],
  // Zebra 小喇叭放不出 400Hz 以下的低音，会被扫码头自带的解码提示音盖住；
  // 找不到商品改用喇叭能清楚放出的中频、高音量三连音，与成功的上扬双音明显区分。
  not_found: [
    { frequency: 640, durationMs: 170, volume: 0.8 },
    { frequency: 0, durationMs: 70 },
    { frequency: 640, durationMs: 170, volume: 0.8 },
    { frequency: 0, durationMs: 70 },
    { frequency: 640, durationMs: 170, volume: 0.8 },
  ],
  // 暂停供货：由高到低两个长音。节奏（两长 vs 三短）与走向（下降 vs 平）都和「找不到」不同，
  // 两个频率都高于 400Hz，Zebra 小喇叭能清楚放出。
  supply_paused: [
    { frequency: 1047, durationMs: 220, volume: 0.8 },
    { frequency: 0, durationMs: 60 },
    { frequency: 698, durationMs: 320, volume: 0.8 },
  ],
  blocked: [
    { frequency: 440, durationMs: 180, volume: 0.32 },
  ],
  error: [
    { frequency: 330, durationMs: 90, volume: 0.36 },
    { frequency: 0, durationMs: 25 },
    { frequency: 262, durationMs: 120, volume: 0.36 },
    { frequency: 0, durationMs: 25 },
    { frequency: 220, durationMs: 150, volume: 0.36 },
  ],
};

const base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

const soundUriCache = new Map<GeneratedToneKey, string>();
const activePlayers = new Set<ReturnType<typeof createAudioPlayer>>();

function writeAscii(view: DataView, offset: number, value: string) {
  for (let index = 0; index < value.length; index += 1) {
    view.setUint8(offset + index, value.charCodeAt(index));
  }
}

function encodeBase64(bytes: Uint8Array) {
  let output = "";

  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index] ?? 0;
    const second = bytes[index + 1] ?? 0;
    const third = bytes[index + 2] ?? 0;
    const chunk = (first << 16) | (second << 8) | third;

    output += base64Alphabet[(chunk >> 18) & 63];
    output += base64Alphabet[(chunk >> 12) & 63];
    output += index + 1 < bytes.length ? base64Alphabet[(chunk >> 6) & 63] : "=";
    output += index + 2 < bytes.length ? base64Alphabet[chunk & 63] : "=";
  }

  return output;
}

function createWaveDataUri(pattern: ToneStep[]) {
  const totalSamples = pattern.reduce(
    (sum, step) => sum + Math.max(1, Math.floor((step.durationMs / 1000) * SAMPLE_RATE)),
    0
  );
  const pcmByteLength = totalSamples * 2;
  const buffer = new ArrayBuffer(WAV_HEADER_SIZE + pcmByteLength);
  const view = new DataView(buffer);

  writeAscii(view, 0, "RIFF");
  view.setUint32(4, 36 + pcmByteLength, true);
  writeAscii(view, 8, "WAVE");
  writeAscii(view, 12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, SAMPLE_RATE, true);
  view.setUint32(28, SAMPLE_RATE * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeAscii(view, 36, "data");
  view.setUint32(40, pcmByteLength, true);

  let sampleIndex = 0;
  let byteOffset = WAV_HEADER_SIZE;

  for (const step of pattern) {
    const sampleCount = Math.max(1, Math.floor((step.durationMs / 1000) * SAMPLE_RATE));
    const volume = step.volume ?? 0.3;

    for (let index = 0; index < sampleCount; index += 1) {
      const amplitude =
        step.frequency > 0
          ? Math.sin((2 * Math.PI * step.frequency * sampleIndex) / SAMPLE_RATE) * volume
          : 0;

      view.setInt16(byteOffset, Math.round(amplitude * PCM_MAX), true);
      byteOffset += 2;
      sampleIndex += 1;
    }
  }

  return `data:audio/wav;base64,${encodeBase64(new Uint8Array(buffer))}`;
}

function getPatternDurationMs(key: GeneratedToneKey) {
  return SOUND_PATTERNS[key].reduce((sum, step) => sum + step.durationMs, 0);
}

function getSoundUri(key: GeneratedToneKey) {
  const cached = soundUriCache.get(key);
  if (cached) {
    return cached;
  }

  const uri = createWaveDataUri(SOUND_PATTERNS[key]);
  soundUriCache.set(key, uri);
  return uri;
}

export function preloadScanFeedbackSounds() {
  for (const key of Object.keys(SOUND_PATTERNS) as GeneratedToneKey[]) {
    getSoundUri(key);
  }
}

function playGeneratedTone(key: GeneratedToneKey) {
  try {
    const player = createAudioPlayer({ uri: getSoundUri(key) });
    // createAudioPlayer 不会自动释放；短音播放期间保留引用，结束后释放原生资源。
    activePlayers.add(player);
    setTimeout(() => {
      activePlayers.delete(player);
      try {
        player.remove();
      } catch (error) {
        console.warn("[scan-sound] failed to release sound", error);
      }
    }, getPatternDurationMs(key) + PLAYER_CLEANUP_PADDING_MS);
    player.play();
  } catch (error) {
    console.warn("[scan-sound] failed to play sound", error);
  }
}

export function playBarcodeCapturedSound() {
  playGeneratedTone("barcode_captured");
}

export function playAttendancePunchSuccessSound() {
  playGeneratedTone("attendance_punch_success");
}

export function playScanFeedbackSound(status: ScanFeedbackState["status"]) {
  if (status === "ready" || status === "scanning") {
    return;
  }

  playGeneratedTone(status);
}
