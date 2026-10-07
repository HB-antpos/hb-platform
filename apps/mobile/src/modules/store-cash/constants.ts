import { PERMISSIONS } from "@/shared/utils/access";
import type { CashExpenseCategory } from "./types";

/** 现金管理五个权限码（后端 Permissions.Cash.*）；界面按 context.capabilities 控制按钮，这里只用于入口可见性。 */
export const STORE_CASH_PERMISSIONS = {
  overviewView: PERMISSIONS.Cash.OverviewView,
  depositCreate: PERMISSIONS.Cash.DepositCreate,
  expenseCreate: PERMISSIONS.Cash.ExpenseCreate,
  void: PERMISSIONS.Cash.Void,
  allStoresView: PERMISSIONS.Cash.AllStoresView,
} as const;

/** 支出类别，顺序即界面展示顺序；界面文案由 i18n 的 categories.* 提供（T2 只叫 T2）。 */
export const CASH_EXPENSE_CATEGORIES: readonly CashExpenseCategory[] = [
  "Salary",
  "Purchase",
  "T2",
  "Other",
];

/** 购物类别必须至少带 1 张收据。 */
export const CASH_RECEIPT_REQUIRED_CATEGORY: CashExpenseCategory = "Purchase";

/** 工资类别可填收款人姓名。 */
export const CASH_PAYEE_CATEGORY: CashExpenseCategory = "Salary";

/** 与后端 StoreCashConstants.MaxAmount 一致：单笔金额上限（澳元）。 */
export const CASH_MAX_AMOUNT = 10_000_000;

/** 每张存单至少 1 张照片（上限见 context.maxImagesPerSlip）。 */
export const CASH_MIN_IMAGES_PER_SLIP = 1;

/** 作废原因、手选日结原因、存款差异原因都至少两个字（与服务端 MinReasonLength 一致）。 */
export const CASH_REASON_MIN_LENGTH = 2;

/** 按日明细接口一次最多 93 天（含两端）。 */
export const CASH_DAILY_MAX_DAYS = 93;

/** 按日明细默认展示最近 14 天（含当天）。 */
export const CASH_DAILY_DEFAULT_DAYS = 14;

/** 记录列表每页条数。 */
export const CASH_LIST_PAGE_SIZE = 20;

/** 图片直传上限：5 MiB、长边不超过 2048。 */
export const CASH_IMAGE_MAX_BYTES = 5 * 1024 * 1024;
export const CASH_IMAGE_MAX_SIDE = 2048;
export const CASH_IMAGE_CONTENT_TYPE = "image/jpeg";

/** 备注、原因等自由文本的前端长度上限（服务端另有校验，这里只防止误输入超长内容）。 */
export const CASH_NOTE_MAX_LENGTH = 500;
export const CASH_SLIP_NO_MAX_LENGTH = 64;
export const CASH_PAYEE_NAME_MAX_LENGTH = 100;

/** 后端业务错误码（StoreCashConstants.ErrorCodes），用于映射中英文提示。 */
export const CASH_ERROR_CODES = [
  "CASH_INVALID_REQUEST",
  "CASH_STORE_NOT_FOUND",
  "CASH_STORE_FORBIDDEN",
  "CASH_RECORD_NOT_FOUND",
  "CASH_DATE_OUT_OF_RANGE",
  "CASH_ATTACHMENT_INVALID",
  "CASH_ATTACHMENT_REQUIRED",
  "CASH_OVERRIDE_REASON_REQUIRED",
  "CASH_OPENING_EXISTS",
  "CASH_VOID_NOT_ALLOWED",
  "CASH_CLOSE_SOURCE_UNAVAILABLE",
  "CASH_CLOSE_NOT_FOUND",
  "CASH_CONFLICT",
] as const;

export type CashErrorCode = (typeof CASH_ERROR_CODES)[number];
