import type { components } from "@hb/pos-api-client/openapi";
import type {
  InstallmentStatus,
  InstallmentSummary,
} from "@hb/pos-domain/core/contracts/installments";

export type InstallmentPaymentMethod = "cash" | "card" | "voucher";
// 中文注释：manual-card 只用于分期续付（独立刷卡机收款、收银员确认，对齐 WPF #1195），首付 / 创建分期不接受。
export type InstallmentCardProvider = "square" | "linkly-cloud" | "manual-card";
export type InstallmentPaymentStatus = "Recorded" | "Voided";
export type InstallmentDeviceScope = "store" | "device";
export type InstallmentDatePreset =
  | "all"
  | "today"
  | "last7"
  | "last30"
  | "custom";
export type InstallmentDateFilter = Readonly<{
  preset: InstallmentDatePreset;
  fromDate: string | null;
  toDate: string | null;
}>;

export type InstallmentLine = Readonly<{
  installmentLineGuid: string;
  productCode: string;
  referenceCode: string | null;
  displayName: string;
  lookupCode: string;
  quantity: string;
  unitPriceCents: number;
  discountCents: number;
  actualAmountCents: number;
  itemNumber: string | null;
}>;

export type InstallmentPayment = Readonly<{
  paymentGuid: string;
  method: InstallmentPaymentMethod;
  amountCents: number;
  status: InstallmentPaymentStatus;
  recordedAtIso: string;
  cashierId: string;
  deviceCode: string;
  cardType: string | null;
  maskedCardNumber: string | null;
}>;

export type InstallmentPickupInfo = Readonly<{
  pickedUpAtIso: string;
  pickedUpBy: string;
  note: string | null;
}>;

export type InstallmentCancellationInfo = Readonly<{
  kind: "RefundCancel" | "VoidCancel";
  cancelledAtIso: string;
  cancelledBy: string;
  reason: string | null;
}>;

export type InstallmentDetails = InstallmentSummary &
  Readonly<{
    cashierId: string;
    minimumDownPaymentCents: number;
    lines: readonly InstallmentLine[];
    payments: readonly InstallmentPayment[];
    pickupInfo: InstallmentPickupInfo | null;
    cancellationInfo: InstallmentCancellationInfo | null;
    note: string | null;
  }>;

export type InstallmentHistoryQuery = Readonly<{
  deviceCode?: string | null;
  createdFromIso?: string | null;
  createdToIso?: string | null;
  keyword: string | null;
  skip: number;
  status: InstallmentStatus | null;
  take: 20 | 50 | 51 | 100 | 200;
}>;

type CardTransaction =
  components["schemas"]["CardTransactionDto"];

export type InstallmentRepaymentCapabilities = Readonly<{
  repaymentClaimsSupported: boolean;
  repaymentClaimsRequired: boolean;
  /** 新两段式 claim provider 绑定端点；旧服务端缺失时由 API 适配为 false。 */
  repaymentClaimPrepareProviderV1?: boolean;
  /** 旧 API 缺失时适配器必须填 false，禁止假定 Card 可用。 */
  cardRepaymentSupported: boolean;
  /**
   * 服务端放开的刷卡续付通道（已归一为小写：square / linkly / zeller / manual）。
   * 旧服务端缺失或为 null 时保持 undefined，调用方回退到只看 cardRepaymentSupported，不得据此收紧。
   */
  cardRepaymentProviders?: readonly string[];
  /**
   * 服务端支持主管结案卡状态不明的刷卡续付（blocking 查询 + supervisor-resolve）。
   * 旧服务端缺失时保持 undefined，按不支持处理，界面不展示结案入口。
   */
  cardRepaymentSupervisorResolveSupported?: boolean;
  crossDeviceRepaymentEnabled: boolean;
  crossDeviceCancelRefundEnabled: boolean;
  crossDeviceVoidEnabled: boolean;
  crossDevicePickupEnabled: boolean;
  preparedClaimTtlSeconds: number;
  /** Optional during staggered API rollout; undefined is fail-closed for cancellation. */
  cancelClaimsSupported?: boolean;
  cancelClaimsRequired?: boolean;
  cancelPreparedClaimTtlSeconds?: number;
}>;

export type InstallmentCancelClaimStatus =
  | "Prepared"
  | "RefundPending"
  | "Committed"
  | "Released"
  | "Declined"
  | "Unknown";

/**
 * 取消退款方式，对应服务端 InstallmentCancelRefundMode：
 * original-route=1 原路退回；voucher=2 每笔已记录原付款都改发一张退款代金券。
 */
export type InstallmentCancelRefundMode = "original-route" | "voucher";

export type InstallmentCancelClaim = Readonly<{
  installmentGuid: string;
  operationGuid: string;
  idempotencyKey: string;
  refundPlanFingerprint: string;
  /** 旧服务端不回传该字段时按原路退处理；调用方必须与本地选择严格比对。 */
  refundMode: InstallmentCancelRefundMode;
  status: InstallmentCancelClaimStatus;
  createdAtIso: string;
  updatedAtIso: string;
  expiresAtIso: string | null;
  commit: Readonly<{ details: InstallmentDetails; alreadyCancelled: boolean }> | null;
  alreadyExists: boolean;
}>;

export type InstallmentCancelClaimIdentity = Readonly<{
  installmentGuid: string;
  operationGuid: string;
}>;

export type InstallmentCancelClaimCreateCommand =
  InstallmentCancelClaimIdentity &
    Readonly<{
      idempotencyKey: string;
      reason: string | null;
      refundPlanFingerprint: string;
      refundMode: InstallmentCancelRefundMode;
    }>;

export type InstallmentCancelClaimResolveCommand =
  InstallmentCancelClaimIdentity &
    Readonly<{ outcome: "Released" | "Declined" | "Unknown" }>;

export type InstallmentCancelClaimCommitCommand =
  InstallmentCancelClaimIdentity &
    Readonly<{ refunds: readonly InstallmentRefundCommand[] }>;

export type InstallmentRepaymentClaimStatus =
  | "Prepared"
  | "ProviderPending"
  | "Committed"
  | "Released"
  | "Declined"
  | "Unknown";

export type InstallmentRepaymentClaim = Readonly<{
  installmentGuid: string;
  operationGuid: string;
  paymentGuid: string;
  amountCents: number;
  method: InstallmentPaymentMethod;
  idempotencyKey: string;
  status: InstallmentRepaymentClaimStatus;
  provider: string | null;
  providerAttemptId: string | null;
  createdAtIso: string;
  updatedAtIso: string;
  expiresAtIso: string | null;
  commit: Readonly<{
    details: InstallmentDetails;
    alreadyRecorded: boolean;
  }> | null;
  alreadyExists: boolean;
  /** 发起这笔续付的收银机；主管在别的收银机上结案时据此提示。旧服务端不返回，此时缺省。 */
  claimantDeviceCode?: string | null;
}>;

/** 主管对卡状态不明的刷卡续付做的人工结论（对应服务端 ConfirmedCharged / ConfirmedNotCharged）。 */
export type InstallmentSupervisorDecision = "charged" | "not-charged";

/**
 * 占用某个分期、卡状态不明的刷卡续付（任意收银机发起的）：主管可据此结案。
 * 只含界面需要的安全描述符；不含 providerAttemptId、卡引用等受保护材料。
 */
export type InstallmentPendingCardRepayment = Readonly<{
  installmentGuid: string;
  operationGuid: string;
  amountCents: number;
  provider: string;
  claimantDeviceCode: string | null;
  status: "ProviderPending" | "Unknown";
  updatedAtIso: string;
}>;

export type InstallmentRepaymentClaimSupervisorResolveCommand =
  InstallmentRepaymentClaimIdentity &
    Readonly<{
      decision: InstallmentSupervisorDecision;
      /** 必填，最长 200：核实了哪里的交易记录。 */
      reason: string;
      /** 选填，最长 100：银行批准码、RRN 等核实依据。 */
      reference: string | null;
    }>;

export type InstallmentCashRepaymentPreparation = Readonly<{
  installmentGuid: string;
  amountCents: number;
  /** 仅用于性能关联的截断 sha256 标识，不是原始 operationGuid。 */
  operationHash: string;
  path?: "prepare-provider-v1" | "legacy-create-begin" | "recovery";
  /** 收款方式：现金（核对钱箱）或手动刷卡（核对独立刷卡机）；缺省按现金。 */
  collection?: "cash" | "manual-card";
}>;

/** 仍在等顾客刷卡（或结果待确认）、可由原收银员向刷卡通道发取消的续付刷卡。 */
export type InstallmentCardRepaymentCancellation = Readonly<{
  installmentGuid: string;
  amountCents: number;
  provider: "square" | "linkly-cloud";
}>;

/**
 * 取消刷卡续付的结论：
 * cancelled = 通道证明未扣款，占用已释放，可重新收款；
 * approved = 顾客其实已经付了，按成功入账（返回最新详情）。
 * 无法证明时不返回，直接抛恢复错误，保持原阻断。
 */
export type InstallmentCardRepaymentCancelOutcome =
  | Readonly<{ outcome: "cancelled" }>
  | Readonly<{ outcome: "approved"; details: InstallmentDetails }>;

export type InstallmentRepaymentClaimCreateCommand = Readonly<{
  installmentGuid: string;
  operationGuid: string;
  paymentGuid: string;
  amountCents: number;
  method: InstallmentPaymentMethod;
  idempotencyKey: string;
}>;

export type InstallmentRepaymentClaimIdentity = Readonly<{
  installmentGuid: string;
  operationGuid: string;
}>;

export type InstallmentRepaymentClaimBeginProviderCommand =
  InstallmentRepaymentClaimIdentity &
    Readonly<{
      provider: string;
      providerAttemptId: string;
    }>;

export type InstallmentRepaymentClaimPrepareProviderCommand =
  InstallmentRepaymentClaimCreateCommand &
    Readonly<{
      provider: string;
      providerAttemptId: string;
    }>;

export type InstallmentRepaymentClaimResolveCommand =
  InstallmentRepaymentClaimIdentity &
    Readonly<{
      outcome: "Released" | "Declined" | "Unknown";
      cashNotCollectedConfirmed?: boolean;
      providerAttemptId?: string | null;
    }>;

export type InstallmentRepaymentClaimCommitCommand =
  InstallmentRepaymentClaimIdentity &
    Readonly<{
      reference: string | null;
      reservationToken: string | null;
      cardTransactions: readonly CardTransaction[];
    }>;

export type InstallmentPaymentCommand = Readonly<{
  paymentGuid: string;
  method: InstallmentPaymentMethod;
  amountCents: number;
  reference: string | null;
  reservationToken: string | null;
  cardTransactions: readonly CardTransaction[];
  idempotencyKey: string;
}>;

export type InstallmentRefundCommand = Readonly<{
  paymentGuid: string;
  originalPaymentGuid?: string;
  method: InstallmentPaymentMethod;
  amountCents: number;
  reference: string | null;
  cardTransactions: readonly CardTransaction[];
  idempotencyKey: string;
}>;

export type InstallmentIdentity = Readonly<{
  deviceCode: string;
  cashierId: string;
  cashierName: string;
}>;

export type InstallmentCreateCommand = InstallmentIdentity &
  Readonly<{
    installmentGuid: string;
    createdAtIso: string;
    totalCents: number;
    downPaymentCents: number;
    lines: readonly InstallmentLine[];
    downPayment: InstallmentPaymentCommand;
    customerName: string;
    customerPhone: string;
    note: string | null;
  }>;

export type InstallmentAppendPaymentCommand = InstallmentIdentity &
  Readonly<{
    installmentGuid: string;
    payment: InstallmentPaymentCommand;
  }>;

export type InstallmentCancelCommand = InstallmentIdentity &
  Readonly<{
    installmentGuid: string;
    cancelledAtIso: string;
    refunds: readonly InstallmentRefundCommand[];
    reason: string | null;
    idempotencyKey: string;
    /** 旧 action 没有该字段，一律视为原路退。 */
    refundMode?: InstallmentCancelRefundMode;
  }>;

export type InstallmentVoidCommand = InstallmentIdentity &
  Readonly<{
    installmentGuid: string;
    voidedAtIso: string;
    reason: string;
    operationGuid?: string;
    idempotencyKey: string;
  }>;

export type InstallmentPickupCommand = InstallmentIdentity &
  Readonly<{
    installmentGuid: string;
    confirmedAtIso: string;
    note: string | null;
    operationGuid?: string;
    idempotencyKey?: string;
  }>;

/**
 * 这是已完成支付编排之后的低层 Hbpos Port。银行卡/券的授权、Unknown 恢复、
 * 退款 attempt 与受保护引用必须由上层 runtime 完成，React UI 不接触这些材料。
 */
export interface InstallmentsRemotePort {
  getCapabilities(): Promise<InstallmentRepaymentCapabilities>;
  createRepaymentClaim(
    command: InstallmentRepaymentClaimCreateCommand,
  ): Promise<InstallmentRepaymentClaim>;
  beginRepaymentClaimProvider(
    command: InstallmentRepaymentClaimBeginProviderCommand,
  ): Promise<InstallmentRepaymentClaim>;
  prepareRepaymentClaimProvider(
    command: InstallmentRepaymentClaimPrepareProviderCommand,
  ): Promise<InstallmentRepaymentClaim>;
  getRepaymentClaim(
    identity: InstallmentRepaymentClaimIdentity,
  ): Promise<InstallmentRepaymentClaim>;
  resolveRepaymentClaim(
    command: InstallmentRepaymentClaimResolveCommand,
  ): Promise<InstallmentRepaymentClaim>;
  /** 占用该分期的续付 claim（同店任意收银机）；没有占用时返回 null。 */
  getBlockingRepaymentClaim(
    installmentGuid: string,
  ): Promise<InstallmentRepaymentClaim | null>;
  /** 主管结案卡状态不明的刷卡续付；需要分期取消权限，结论由服务端落账并留痕。 */
  supervisorResolveRepaymentClaim(
    command: InstallmentRepaymentClaimSupervisorResolveCommand,
  ): Promise<InstallmentRepaymentClaim>;
  commitRepaymentClaim(
    command: InstallmentRepaymentClaimCommitCommand,
  ): Promise<InstallmentRepaymentClaim>;
  createCancelClaim(
    command: InstallmentCancelClaimCreateCommand,
  ): Promise<InstallmentCancelClaim>;
  beginCancelClaimRefund(
    identity: InstallmentCancelClaimIdentity,
  ): Promise<InstallmentCancelClaim>;
  getCancelClaim(
    identity: InstallmentCancelClaimIdentity,
  ): Promise<InstallmentCancelClaim>;
  resolveCancelClaim(
    command: InstallmentCancelClaimResolveCommand,
  ): Promise<InstallmentCancelClaim>;
  commitCancelClaim(
    command: InstallmentCancelClaimCommitCommand,
  ): Promise<InstallmentCancelClaim>;
  list(
    query: InstallmentHistoryQuery,
  ): Promise<readonly InstallmentSummary[]>;
  getDetails(
    installmentGuid: string,
  ): Promise<InstallmentDetails | null>;
  create(
    command: InstallmentCreateCommand,
  ): Promise<InstallmentDetails>;
  appendPayment(
    command: InstallmentAppendPaymentCommand,
  ): Promise<InstallmentDetails>;
  cancelWithRefund(
    command: InstallmentCancelCommand,
  ): Promise<InstallmentDetails>;
  void(command: InstallmentVoidCommand): Promise<InstallmentDetails>;
  confirmPickup(
    command: InstallmentPickupCommand,
  ): Promise<InstallmentDetails>;
}
