import assert from "node:assert/strict";
import { openPayslipPdf, type PayslipPdfPorts } from "./pdf-core";

interface Harness {
  ports: PayslipPdfPorts;
  events: string[];
  files: Set<string>;
}

function harness(overrides: Partial<PayslipPdfPorts> = {}, platform = "ios", cacheDirectory: string | null = "file:///cache/"): Harness {
  const events: string[] = [];
  const files = new Set<string>();
  const ports: PayslipPdfPorts = {
    platform,
    fileSystem: {
      cacheDirectory,
      writeBase64: async (uri, base64) => {
        events.push(`write:${uri}:${base64}`);
        files.add(uri);
      },
      remove: async (uri) => {
        events.push(`remove:${uri}`);
        files.delete(uri);
      },
      getContentUri: async (uri) => `content://${uri.replace("file:///", "")}`,
    },
    share: async (uri) => void events.push(`share:${uri}`),
    isShareAvailable: async () => true,
    viewAndroid: async (uri) => void events.push(`view:${uri}`),
    ...overrides,
  };
  return { ports, events, files };
}

const FILE = "Payslip_ALEX-MORGAN_2026-10-29.pdf";
const URI = `file:///cache/${FILE}`;
const fetchPdfBase64 = async () => "BASE64";

async function run() {
  // 分享：下载 → 写缓存 → 系统分享 → 删除缓存文件
  {
    const { ports, events, files } = harness();
    await openPayslipPdf({ fetchPdfBase64, ports }, FILE, "share");
    assert.deepEqual(events, [`remove:${URI}`, `write:${URI}:BASE64`, `share:${URI}`, `remove:${URI}`]);
    assert.equal(files.size, 0, "用完删除缓存文件");
  }

  // iOS 查看：没有独立 PDF 查看器，走分享面板
  {
    const { ports, events } = harness({}, "ios");
    await openPayslipPdf({ fetchPdfBase64, ports }, FILE, "view");
    assert.equal(events.some((event) => event.startsWith("share:")), true);
    assert.equal(events.some((event) => event.startsWith("view:")), false);
  }

  // 安卓查看：系统 PDF 查看器（content URI），不走分享，仍然删除缓存
  {
    const { ports, events, files } = harness({}, "android");
    await openPayslipPdf({ fetchPdfBase64, ports }, FILE, "view");
    assert.equal(events.includes(`view:content://cache/${FILE}`), true);
    assert.equal(events.some((event) => event.startsWith("share:")), false);
    assert.equal(files.size, 0);
  }

  // 安卓没有查看器时退回分享面板
  {
    const { ports, events } = harness({ viewAndroid: async () => Promise.reject(new Error("ActivityNotFound")) }, "android");
    const warn = console.warn;
    console.warn = () => undefined;
    try {
      await openPayslipPdf({ fetchPdfBase64, ports }, FILE, "view");
    } finally {
      console.warn = warn;
    }
    assert.equal(events.includes(`share:${URI}`), true);
  }

  // 分享失败：错误上抛，缓存文件照样删除
  {
    const { ports, files } = harness({ share: async () => Promise.reject(new Error("share failed")) });
    await assert.rejects(() => openPayslipPdf({ fetchPdfBase64, ports }, FILE, "share"), /share failed/);
    assert.equal(files.size, 0);
  }

  // 设备不支持分享：给出 PAYSLIP_SHARING_UNAVAILABLE，缓存文件照样删除
  {
    const { ports, files } = harness({ isShareAvailable: async () => false });
    await assert.rejects(
      () => openPayslipPdf({ fetchPdfBase64, ports }, FILE, "share"),
      (error: unknown) => (error as { code?: string }).code === "PAYSLIP_SHARING_UNAVAILABLE",
    );
    assert.equal(files.size, 0);
  }

  // 下载失败：什么文件都没写
  {
    const { ports, events } = harness();
    await assert.rejects(() => openPayslipPdf({ fetchPdfBase64: async () => Promise.reject(new Error("403")), ports }, FILE, "share"), /403/);
    assert.deepEqual(events, []);
  }

  // 没有缓存目录：直接报错，不发请求
  {
    let fetched = false;
    const { ports } = harness({}, "ios", null);
    await assert.rejects(() => openPayslipPdf({ fetchPdfBase64: async () => ((fetched = true), "x"), ports }, FILE, "share"));
    assert.equal(fetched, false);
  }
}

run();
