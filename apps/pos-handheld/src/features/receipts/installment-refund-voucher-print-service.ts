import {
  encodeRefundVoucherDocuments,
  type RefundVoucherReceiptSettingsPort,
} from "@hb/pos-receipt-core/features/receipts/refund-voucher-receipt-renderer";
import type { RenderedReturnReceipt } from "@hb/pos-receipt-core/features/receipts/return-receipt-renderer";

import type { InstallmentRefundVoucherPrintMaterialPort } from "@/core/db/sqlite-installment-refund-voucher-print-material";

/** 分期取消签发的退款券打印任务号前缀，须与迁移中放行外部订单身份的触发器保持一致。 */
export const INSTALLMENT_REFUND_VOUCHER_JOB_PREFIX = "installment-refund-voucher:";

export type InstallmentRefundVoucherPrintQueuePort = Readonly<{
  enqueueInstallmentRefundVoucherPrintJob(input: Readonly<{
    jobId: string;
    installmentGuid: string;
    printerId: string;
    receiptBytes: Uint8Array;
  }>): Promise<"created" | "existing">;
}>;

export type InstallmentRefundVoucherPrintServiceOptions = Readonly<{
  materials: InstallmentRefundVoucherPrintMaterialPort;
  /** 冻结设置里带 voucherTerms 时，券面「VOUCHER TERMS」按总部下发的正文打印；缺省走内置默认文案。 */
  settings: RefundVoucherReceiptSettingsPort;
  printQueue: InstallmentRefundVoucherPrintQueuePort;
  trustedStoreCode: string;
  now(): Date;
  /**
   * 券面「Valid until」按此业务时区取日历日（IANA 名）；缺省沿用 Australia/Brisbane，
   * 非法时区名只会省略到期行，不影响出票。
   */
  businessTimeZone?: string;
  requestPrintDrain(): Promise<unknown>;
}>;

/**
 * 与 WPF 对齐：取消分期成功后为每张退款代金券出一张独立券面（CODE128+QR），
 * 补打已取消分期时同样追加券面。打印只是收尾，任何失败都不能改变取消结果。
 */
export class InstallmentRefundVoucherPrintService {
  public constructor(
    private readonly options: InstallmentRefundVoucherPrintServiceOptions,
  ) {}

  /** 渲染本机为该分期单签发的全部退款券；本机没有退款券或打印未启用时返回 null。 */
  public async renderVouchers(
    installmentGuid: string,
    orderLabel: string,
  ): Promise<RenderedReturnReceipt | null> {
    const vouchers = await this.options.materials.listApprovedRefundVouchers(
      installmentGuid,
      this.options.trustedStoreCode,
    );
    if (vouchers.length === 0) return null;
    const settings = await this.options.settings.getFrozenReturnReceiptSettings();
    if (!settings) return null;
    return encodeRefundVoucherDocuments({
      settings,
      storeCode: this.options.trustedStoreCode,
      orderLabel,
      vouchers,
      printedAt: this.options.now(),
      ...(this.options.businessTimeZone !== undefined
        ? { businessTimeZone: this.options.businessTimeZone }
        : {}),
    });
  }

  /**
   * 取消提交成功后入队一次自动打印。任务号由分期单派生（已取消为终态），
   * 重放时任务已存在即视为完成，不重复出票；随后请求排空打印队列。
   */
  public async printAfterCancel(
    installmentGuid: string,
    orderLabel: string,
  ): Promise<"queued" | "existing" | "none"> {
    const rendered = await this.renderVouchers(installmentGuid, orderLabel);
    if (!rendered) return "none";
    const result = await this.options.printQueue.enqueueInstallmentRefundVoucherPrintJob({
      jobId: `${INSTALLMENT_REFUND_VOUCHER_JOB_PREFIX}${installmentGuid}`,
      installmentGuid,
      printerId: rendered.printerId,
      receiptBytes: rendered.receiptBytes,
    });
    await this.options.requestPrintDrain();
    return result === "created" ? "queued" : "existing";
  }
}
