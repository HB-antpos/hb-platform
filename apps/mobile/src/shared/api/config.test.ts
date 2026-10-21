import {
  API_PORT,
  API_HOST_PRESETS,
  DEFAULT_API_HOST,
  buildApiBaseUrl,
  normalizeApiHost,
} from "./config";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { getCurrentApiHost, getStoredApiHost, setStoredApiHost } from "./config";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

assertEqual(DEFAULT_API_HOST, "hotbargain.vip", "default API host uses production domain");
assertEqual(API_PORT, "5002", "API port uses published backend port");
assertEqual(
  buildApiBaseUrl("hotbargain.vip"),
  "https://hotbargain.vip/api",
  "production API base URL uses HTTPS Nginx proxy and api path"
);
assertEqual(
  buildApiBaseUrl("hotbargain.group"),
  "https://hotbargain.group/api",
  "AWS 测速服务器域名同样走 HTTPS 代理且不带端口（iOS 会拦截域名的明文 HTTP）"
);
assertEqual(
  buildApiBaseUrl("pos.hotbargain.vip"),
  "https://pos.hotbargain.vip/api",
  "EdgeOne 边缘入口同样必须走 HTTPS 且不带端口（否则会被拼成 http://域名:5002，被 iOS ATS 拦截）"
);
assertEqual(
  buildApiBaseUrl("192.168.31.247"),
  "http://192.168.31.247:5002/api",
  "local API base URL keeps direct backend port"
);
assertEqual(
  normalizeApiHost("http://192.168.31.247:5002/api"),
  "192.168.31.247",
  "normalization strips protocol, port, and path"
);
assertEqual(
  normalizeApiHost("https://hotbargain.vip/api"),
  "hotbargain.vip",
  "normalization keeps only the hostname for domains"
);
assertEqual(
  API_HOST_PRESETS.map((preset) => preset.host).join(","),
  "hotbargain.vip,192.168.31.247",
  "server presets include production first and local fallback second"
);

async function testStoredApiHostCache() {
  const originalGetString = AppAsyncStorage.getString;
  const originalSetString = AppAsyncStorage.setString;
  let getCalls = 0;
  let setCalls = 0;
  let saveError: Error | null = null;
  let rejectOldSave: ((error: Error) => void) | null = null;
  let read: { resolve: (value: string | null) => void; reject: (error: Error) => void } | null = null;

  AppAsyncStorage.getString = async () => {
    getCalls += 1;
    if (getCalls === 1) {
      throw new Error("storage temporarily unavailable");
    }
    if (getCalls === 3) return "persisted.example.test";
    return new Promise<string | null>((resolve, reject) => {
      read = { resolve, reject };
    });
  };
  AppAsyncStorage.setString = async (_key, value) => {
    setCalls += 1;
    if (saveError) throw saveError;
    if (value === "old.example.test") {
      await new Promise<never>((_resolve, reject) => { rejectOldSave = reject; });
    }
  };

  try {
    await getStoredApiHost().then(
      () => { throw new Error("failed host read should reject"); },
      (error: Error) => assertEqual(error.message, "storage temporarily unavailable", "failed host read propagates")
    );

    const first = getStoredApiHost();
    const second = getStoredApiHost();
    assertEqual(getCalls, 2, "concurrent callers share one in-flight storage read");

    saveError = new Error("pending read save failure");
    await setStoredApiHost("never-saved.example.test").then(
      () => { throw new Error("failed save should reject"); },
      () => assertEqual(getCurrentApiHost(), DEFAULT_API_HOST, "failed save cannot publish an unconfirmed host")
    );
    saveError = null;
    assertEqual(getCalls, 2, "failed save preserves the original in-flight read");
    const switched = await setStoredApiHost("http://192.168.31.247:5002/api");
    assertEqual(switched, "192.168.31.247", "host switch normalizes immediately");
    assertEqual(getCurrentApiHost(), "192.168.31.247", "host switch updates memory immediately");
    assertEqual(setCalls, 2, "host switch persists once");

    const pendingRead = read as { resolve: (value: string | null) => void; reject: (error: Error) => void } | null;
    if (!pendingRead) throw new Error("test storage read was not started");
    pendingRead.resolve("stale.example.test");
    const staleReadResults = await Promise.all([first, second]);
    assertEqual(staleReadResults[0], "192.168.31.247", "pending callers use the switched host");
    assertEqual(staleReadResults[1], "192.168.31.247", "shared pending caller uses the switched host");
    assertEqual(getCurrentApiHost(), "192.168.31.247", "stale read cannot overwrite switched host");

    const cached = await getStoredApiHost();
    assertEqual(cached, "192.168.31.247", "initialized host returns cached switch");
    assertEqual(getCalls, 2, "initialized host avoids another storage read");

    saveError = new Error("storage write unavailable");
    await setStoredApiHost("failed.example.test").then(
      () => { throw new Error("failed host write should reject"); },
      (error: Error) => assertEqual(error.message, "storage write unavailable", "failed host write propagates")
    );
    saveError = null;
    assertEqual(await getStoredApiHost(), "192.168.31.247", "failed write preserves the last confirmed host");
    assertEqual(getCalls, 2, "failed write does not discard a confirmed host");

    const oldSave = setStoredApiHost("old.example.test");
    await Promise.resolve();
    const newHost = await setStoredApiHost("new.example.test");
    assertEqual(newHost, "new.example.test", "new host switch succeeds while old save is pending");
    const pendingOldSaveReject = rejectOldSave as ((error: Error) => void) | null;
    if (!pendingOldSaveReject) throw new Error("old save was not held pending");
    pendingOldSaveReject(new Error("old save failed"));
    await oldSave.then(
      () => { throw new Error("old host write should reject"); },
      (error: Error) => assertEqual(error.message, "old save failed", "old host write propagates")
    );
    assertEqual(await getStoredApiHost(), "new.example.test", "old save failure cannot clear newer host");
  } finally {
    AppAsyncStorage.getString = originalGetString;
    AppAsyncStorage.setString = originalSetString;
  }
}

testStoredApiHostCache().catch((error) => {
  setTimeout(() => { throw error; }, 0);
});
