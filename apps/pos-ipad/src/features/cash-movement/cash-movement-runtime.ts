import type {
  CashMovementReasonCode,
  CashMovementValidationCode,
  CashMovementVoidDenial,
  LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";

/**
 * 界面（presenter）看到的现金存入 / 取出运行时端口。
 * 授权、构造记录、落库、唤醒上传、开钱箱、打印都在组合根实现；界面只关心结果。
 * 约定：一旦返回 kind = "saved"，记录就已经耐久落库，之后钱箱 / 打印失败只是提示，绝不回滚。
 */

/** 今日（门店营业日）记录视图：记录按发生时间升序，已冲正的带 voidedByMovementGuid。 */
export type CashMovementTodayView = Readonly<{
  /** 今天的门店营业日 yyyy-MM-dd（按门店营业时区，不是设备时区）。 */
  businessDate: string;
  businessTimeZone: string;
  movements: readonly LocalCashMovement[];
  /** 今天最近一次已归档日结的保存时间；没有为 null（冲正资格判断用）。 */
  latestDailyCloseSavedAtIso: string | null;
  /** 冲正抵消后的今日存入 / 取出合计。 */
  cashInCents: number;
  cashOutCents: number;
}>;

export type CashMovementHardwareOutcome = Readonly<{
  /** skipped = 本机设置里没有启用 / 没有配置钱箱（不提示）。 */
  drawer: "opened" | "failed" | "skipped";
  /** skipped = 打印机未启用或未配置，对操作员同样按「没打印出来」提示。 */
  print: "printed" | "failed" | "skipped";
}>;

export type CashMovementWriteResult =
  | (Readonly<{ kind: "saved"; movement: LocalCashMovement }> &
      CashMovementHardwareOutcome)
  /** 没有获得授权（当前收银员无权限且主管授权失败 / 取消），没有记录任何内容。 */
  | Readonly<{ kind: "denied"; reason: "denied" | "cancelled" }>
  /** 本地校验没通过（界面本应已拦住，这里是兜底）。 */
  | Readonly<{ kind: "invalid"; code: CashMovementValidationCode }>
  /** 冲正被库内规则拒绝（并发或日结刚保存）。 */
  | Readonly<{ kind: "void-not-allowed"; denial: CashMovementVoidDenial | null }>
  /** 落库失败，没有记录任何内容。 */
  | Readonly<{ kind: "failed" }>;

export type CashMovementReprintResult = Readonly<{
  kind: "printed" | "failed" | "not-found";
}>;

export type CashMovementRuntime = Readonly<{
  listToday(): Promise<CashMovementTodayView>;
  /** movementGuid 由界面为这一次提交生成一次，防重复点击与重试。 */
  submit(
    input: Readonly<{
      movementGuid: string;
      reasonCode: CashMovementReasonCode;
      amountCents: number;
      note: string | null;
    }>,
  ): Promise<CashMovementWriteResult>;
  /** 冲正：原因码与金额取自库里的原记录，界面只给原记录 guid 与冲正备注。 */
  voidMovement(
    input: Readonly<{
      movementGuid: string;
      originalMovementGuid: string;
      note: string;
    }>,
  ): Promise<CashMovementWriteResult>;
  reprint(movementGuid: string): Promise<CashMovementReprintResult>;
}>;

/** 销售页入口与组合根的对接面：开关、订阅与创建运行时。 */
export type PosCashMovementRuntimeService = Readonly<{
  /** 本机开关 cashMovement.enabled，只决定销售页入口是否显示；默认 false。 */
  isCashMovementEnabled(): boolean;
  subscribeCashMovementEnabled(listener: () => void): () => void;
  /** 绑定当前收银员会话；没有可信收银员时抛 CURRENT_CASHIER_REQUIRED。 */
  createRuntime(): CashMovementRuntime;
}>;
