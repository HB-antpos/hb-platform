// 工资单 PDF 的原生接线：expo-file-system（legacy API，与仓库其它 PDF 流程一致）+ expo-sharing + expo-intent-launcher。
// 全部动态 import，避免未用到时把原生模块拖进首屏；纯流程在 pdf-core.ts（有单测）。
import { Platform } from "react-native";
import { payslipsApi } from "./api";
import { buildPayslipFileName } from "./api-core";
import { openPayslipPdf as openPayslipPdfCore, PDF_MIME, PDF_UTI, type PayslipPdfMode, type PayslipPdfPorts } from "./pdf-core";
import type { PayslipKey } from "./types";

async function createPorts(): Promise<PayslipPdfPorts> {
  const FileSystem = await import("expo-file-system/legacy");
  return {
    platform: Platform.OS,
    fileSystem: {
      cacheDirectory: FileSystem.cacheDirectory ?? null,
      writeBase64: (uri, base64) => FileSystem.writeAsStringAsync(uri, base64, { encoding: FileSystem.EncodingType.Base64 }),
      remove: (uri) => FileSystem.deleteAsync(uri, { idempotent: true }),
      getContentUri: (uri) => FileSystem.getContentUriAsync(uri),
    },
    share: async (uri) => {
      const Sharing = await import("expo-sharing");
      await Sharing.shareAsync(uri, { mimeType: PDF_MIME, UTI: PDF_UTI });
    },
    isShareAvailable: async () => (await import("expo-sharing")).isAvailableAsync(),
    viewAndroid: async (contentUri) => {
      const IntentLauncher = await import("expo-intent-launcher");
      await IntentLauncher.startActivityAsync("android.intent.action.VIEW", { data: contentUri, type: PDF_MIME, flags: 1 });
    },
  };
}

/** 下载某张工资单的 PDF 并打开系统分享 / 查看；用完删除缓存文件。 */
export async function downloadAndOpenPayslipPdf(
  key: PayslipKey,
  employeeName: string,
  mode: PayslipPdfMode,
  signal?: AbortSignal,
): Promise<void> {
  await openPayslipPdfCore(
    { fetchPdfBase64: (abort) => payslipsApi.fetchPdfBase64(key, abort), ports: await createPorts() },
    buildPayslipFileName(employeeName, key.paymentDate),
    mode,
    signal,
  );
}
