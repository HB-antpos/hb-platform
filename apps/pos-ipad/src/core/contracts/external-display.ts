import { z } from "zod";

import { MoneySchema } from "@hb/pos-domain/core/contracts/money";

export type DisplayStatus = "disconnected" | "connecting" | "ready" | "failed";
// 与 WPF 客显对齐：商品行高 72（设计画布 768 高）时购物车区一屏可完整显示 6 行。
export const CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT = 6;

const CustomerDisplayQuantitySchema = z
  .string()
  .regex(/^-?\d+(?:\.\d{1,3})?$/);

const CustomerDisplayItemSchema = z
  .object({
    name: z.string().min(1).max(160),
    quantity: CustomerDisplayQuantitySchema,
    unitPrice: MoneySchema.optional(),
    amount: MoneySchema,
    // 以下为对齐 WPF 客显追加的可选字段（旧快照缺省时按无该信息渲染）。
    /** 货号，对应 WPF 的 "Item No."。 */
    itemNumber: z.string().min(1).max(64).optional(),
    /** 条码/查询码，显示在货号右侧。 */
    lookupCode: z.string().min(1).max(64).optional(),
    /** 折扣前金额；只在该行有折扣时提供，与 discountRate 同时出现。 */
    grossAmount: MoneySchema.optional(),
    /** 折扣率（百分数，最多两位小数，如 "10"、"12.5"）；展示为 "-10%"。 */
    discountRate: z
      .string()
      .regex(/^\d{1,3}(?:\.\d{1,2})?$/)
      .optional(),
    /** 商品缩略图的本地 file URI（位于商品图缓存目录内）；客显层永不联网取图。 */
    imageUri: z.string().min(1).max(2_048).optional(),
  })
  .strict()
  .superRefine((item, context) => {
    if ((item.grossAmount === undefined) !== (item.discountRate === undefined)) {
      context.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["discountRate"],
        message: "grossAmount and discountRate must be provided together.",
      });
    }
  });

const CustomerDisplaySummarySchema = z
  .object({
    itemQuantity: CustomerDisplayQuantitySchema,
    skuCount: z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER),
    subtotal: MoneySchema,
  })
  .strict();

const CustomerDisplayAdvertSchema = z
  .object({
    kind: z.enum(["image", "video"]),
    localUri: z.string().min(1),
  })
  .strict();

const CustomerDisplaySnapshotBaseSchema = z
  .object({
    revision: z.number().int().nonnegative(),
    mode: z.enum(["idle", "cart", "payment", "change", "success"]),
    items: z.array(CustomerDisplayItemSchema).max(100),
    summary: CustomerDisplaySummarySchema.optional(),
    visibleItemStart: z.number().int().nonnegative().optional(),
    gst: MoneySchema,
    discount: MoneySchema,
    total: MoneySchema,
    change: MoneySchema,
    advert: CustomerDisplayAdvertSchema.nullable(),
  })
  .strict();

export const CustomerDisplaySnapshotSchema =
  CustomerDisplaySnapshotBaseSchema.superRefine((snapshot, context) => {
    if (snapshot.visibleItemStart === undefined) return;
    const maximumStart = Math.max(
      0,
      snapshot.items.length - CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT,
    );
    if (snapshot.visibleItemStart > maximumStart) {
      context.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["visibleItemStart"],
        message: "Visible customer display item window is out of range.",
      });
    }
  });

export type CustomerDisplaySnapshot = Readonly<
  z.infer<typeof CustomerDisplaySnapshotSchema>
>;

export interface ExternalCustomerDisplayPort {
  getStatus(): Promise<DisplayStatus>;
  setEnabled(enabled: boolean): Promise<void>;
  publish(snapshot: CustomerDisplaySnapshot): Promise<void>;
  subscribe(listener: (status: DisplayStatus) => void): () => void;
}
