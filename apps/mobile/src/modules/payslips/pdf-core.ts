// 工资单 PDF 的下载 → 分享 / 查看流程（原生能力由外部注入，便于测试）：
// 先下载到应用缓存目录（文件名 Payslip_<姓名>_<支付日>.pdf），再交给系统分享 / 存储面板或 PDF 查看器，
// 无论成功失败，用完立即删除缓存文件，敏感文件不在设备上留存。
export type PayslipPdfMode = "share" | "view";

export const PDF_MIME = "application/pdf";
export const PDF_UTI = "com.adobe.pdf";

export interface PayslipPdfFileSystem {
  cacheDirectory: string | null;
  writeBase64(uri: string, base64: string): Promise<void>;
  remove(uri: string): Promise<void>;
  /** Android 用：把 file:// 转成可授权给其它应用的 content://。 */
  getContentUri(uri: string): Promise<string>;
}

export interface PayslipPdfPorts {
  platform: "ios" | "android" | string;
  fileSystem: PayslipPdfFileSystem;
  /** 分享面板（iOS 的文件 / 分享，Android 的系统分享）。 */
  share(uri: string): Promise<void>;
  isShareAvailable(): Promise<boolean>;
  /** Android：用系统 PDF 查看器打开 content URI。 */
  viewAndroid(contentUri: string): Promise<void>;
}

export interface PayslipPdfDeps {
  /** 取 PDF 的 base64（生产为 payslipsApi.fetchPdfBase64）。 */
  fetchPdfBase64(signal?: AbortSignal): Promise<string>;
  ports: PayslipPdfPorts;
}

function codedError(message: string, code: string) {
  return Object.assign(new Error(message), { code });
}

/**
 * 下载并交给系统。
 * - share：两端都走系统分享 / 存储面板（iOS 存「文件」、安卓存「下载」或转发）。
 * - view：安卓用系统 PDF 查看器（找不到查看器时退回分享面板）；iOS 没有独立查看器，走分享面板（顶部带 PDF 预览，可快速查看）。
 */
export async function openPayslipPdf(
  deps: PayslipPdfDeps,
  fileName: string,
  mode: PayslipPdfMode,
  signal?: AbortSignal,
): Promise<void> {
  const { ports } = deps;
  if (!ports.fileSystem.cacheDirectory) throw codedError("Cache directory unavailable", "PAYSLIP_PDF_INVALID");
  const base64 = await deps.fetchPdfBase64(signal);
  const fileUri = `${ports.fileSystem.cacheDirectory}${fileName}`;
  try {
    // 同名旧文件（上次异常退出遗留）先清掉，再写入。
    await ports.fileSystem.remove(fileUri);
    await ports.fileSystem.writeBase64(fileUri, base64);
    if (mode === "view" && ports.platform === "android") {
      try {
        await ports.viewAndroid(await ports.fileSystem.getContentUri(fileUri));
        return;
      } catch (error) {
        // 设备没装 PDF 查看器时 VIEW 会抛 ActivityNotFound，退回系统分享面板。
        console.warn("[payslips] 打开 PDF 查看器失败，改用分享面板", error);
      }
    }
    if (!(await ports.isShareAvailable())) throw codedError("Sharing unavailable", "PAYSLIP_SHARING_UNAVAILABLE");
    await ports.share(fileUri);
  } finally {
    await ports.fileSystem.remove(fileUri).catch(() => undefined);
  }
}
