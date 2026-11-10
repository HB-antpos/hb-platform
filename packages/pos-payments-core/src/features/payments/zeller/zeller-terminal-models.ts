/**
 * Zeller 直连刷卡的自有模型，与 apps/pos-wpf/src/Hbpos.Client.Wpf/Services/Zeller/ZellerTerminalModels.cs 一一对应。
 *
 * 中文注释：这里的类型都是我们自己的，不引用 Zeller 的 SDK。只有 SDK 适配层需要认识 SDK 的类型，
 * 分类、恢复和测试都只依赖这些类型。取值字符串刻意与 C# 枚举成员名逐字相同，
 * 这样 test-fixtures/zeller/ 下的黄金向量可以被 C# 和 TS 两边直接读取。
 */

/** Zeller 交易状态。Unknown 表示 SDK 返回了本程序不认识的状态，一律按结果未知处理。 */
export type ZellerTransactionStatus = "Unknown" | "Approved" | "Declined" | "Failed";

export type ZellerTransactionType = "Unknown" | "Purchase" | "Refund";

/**
 * SDK 的全部 26 种错误类型，数值与 SDK 的 ErrorType 一致，适配层按数值转换。
 * SDK 以后新增的类型落到 Unrecognised，按结果未知处理。
 */
export const ZELLER_ERROR_TYPE_CODES = {
  NetworkFailure: 0,
  SetupRequired: 1,
  OperationFailure: 2,
  OperationBusy: 3,
  OperationInterrupted: 4,
  OperationRequestInvalid: 5,
  OperationResponseInvalid: 6,
  ConnectionFailure: 7,
  FeatureNotAvailable: 8,
  PermissionsMissing: 9,
  PairedDeviceBusy: 10,
  PairedDeviceNotAvailable: 11,
  Cancelled: 12,
  InactivityTimeout: 13,
  CardDeclined: 14,
  CardBlocked: 15,
  CardNotSupported: 16,
  CardReadFailure: 17,
  CardInsertRequired: 18,
  TransactionNotFound: 19,
  TransactionResponseTimeout: 20,
  TransactionResponseInvalid: 21,
  TransactionNetworkFailure: 22,
  TransactionSystemFailure: 23,
  TransactionDeclined: 24,
  TransactionFailure: 25,
} as const;

export type ZellerKnownErrorType = keyof typeof ZELLER_ERROR_TYPE_CODES;
export type ZellerErrorType = ZellerKnownErrorType | "Unrecognised";

const ERROR_TYPE_BY_CODE: ReadonlyMap<number, ZellerKnownErrorType> = new Map(
  (Object.entries(ZELLER_ERROR_TYPE_CODES) as [ZellerKnownErrorType, number][]).map(
    ([name, code]) => [code, name],
  ),
);

/** 把 SDK 的错误数值转成我们的错误类型；不认识的数值（含非整数）一律 Unrecognised。 */
export function zellerErrorTypeFromSdkCode(code: unknown): ZellerErrorType {
  return typeof code === "number" ? (ERROR_TYPE_BY_CODE.get(code) ?? "Unrecognised") : "Unrecognised";
}

/**
 * 一笔 Zeller 交易。金额单位是分。
 *
 * 中文注释：扣款、退款的返回带 receiptText 和 approvalCode；按 reference 查回来的交易这两项为空，只有 receiptLink。
 * timestampIso 来自刷卡机时钟，实测比收银设备快十几到三十秒，不能用来匹配交易。
 */
export type ZellerTransaction = Readonly<{
  status: ZellerTransactionStatus;
  type: ZellerTransactionType;
  amountCents: number;
  transactionUuid: string | null;
  externalReference: string | null;
  responseCode?: string | null;
  responseText?: string | null;
  approvalCode?: string | null;
  rrn?: string | null;
  panMasked?: string | null;
  scheme?: string | null;
  bin?: string | null;
  merchantId?: string | null;
  terminalId?: string | null;
  receiptText?: string | null;
  receiptLink?: string | null;
  timestampIso?: string | null;
}>;

/**
 * 一次扣款或退款调用是怎么结束的。
 * - Transaction：SDK 返回了一笔交易。注意：被拒的交易也在这里，必须再看 status。
 * - Error：SDK 返回了错误类型。
 * - Abandoned：看门狗放弃了这次调用，SDK 一直没有返回。
 * - Faulted：SDK 抛出了异常。
 * - NotStarted：调用没有进入 SDK，请求确定没有发出。
 */
export type ZellerCallOutcome = "Transaction" | "Error" | "Abandoned" | "Faulted" | "NotStarted";

/** 扣款或退款调用的结果，加上调用期间收到的事件证据。 */
export type ZellerCallResult = Readonly<{
  outcome: ZellerCallOutcome;
  transaction?: ZellerTransaction | null;
  errorType?: ZellerErrorType | null;
  message?: string | null;
  /** 刷卡机已提示刷卡：请求已经到达刷卡机。 */
  reachedTerminal?: boolean;
  /** 刷卡机已读到卡，正在处理：从这一刻起钱可能已经扣了。 */
  processingStarted?: boolean;
}>;

/** 按 reference 查询的结果。succeeded 为 false 时不能把「没有交易」当成结论。 */
export type ZellerQueryResult = Readonly<{
  succeeded: boolean;
  transactions: readonly ZellerTransaction[];
  errorType?: ZellerErrorType | null;
  message?: string | null;
}>;
