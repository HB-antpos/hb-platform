import type {
  InstallmentAppendPaymentCommand,
  InstallmentCancelCommand,
  InstallmentCreateCommand,
  InstallmentDetails,
  InstallmentHistoryQuery,
  InstallmentLine,
  InstallmentPayment,
  InstallmentPaymentCommand,
  InstallmentPaymentMethod,
  InstallmentPickupCommand,
  InstallmentRepaymentCapabilities,
  InstallmentRepaymentClaim,
  InstallmentRepaymentClaimBeginProviderCommand,
  InstallmentRepaymentClaimPrepareProviderCommand,
  InstallmentRepaymentClaimCommitCommand,
  InstallmentRepaymentClaimCreateCommand,
  InstallmentRepaymentClaimIdentity,
  InstallmentRepaymentClaimResolveCommand,
  InstallmentCancelClaim,
  InstallmentCancelClaimCommitCommand,
  InstallmentCancelClaimCreateCommand,
  InstallmentCancelClaimIdentity,
  InstallmentCancelClaimResolveCommand,
  InstallmentCancelRefundMode,
  InstallmentRefundCommand,
  InstallmentsRemotePort,
  InstallmentVoidCommand,
} from "./installment-models";

import {
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@hb/pos-api-client/transport";
import type { components } from "@hb/pos-api-client/openapi";
import type {
  InstallmentStatus,
  InstallmentSummary,
} from "@hb/pos-domain/core/contracts/installments";

type GeneratedAppendRequest =
  components["schemas"]["InstallmentAppendPaymentRequest"];
type GeneratedAppendResponse =
  components["schemas"]["InstallmentAppendPaymentResponse"];
type GeneratedCancelRequest =
  components["schemas"]["InstallmentCancelRequest"];
type GeneratedCancelResponse =
  components["schemas"]["InstallmentCancelResponse"];
type GeneratedCreateRequest =
  components["schemas"]["InstallmentCreateRequest"];
type GeneratedCreateResponse =
  components["schemas"]["InstallmentCreateResponse"];
type GeneratedPrepareProviderRequest =
  components["schemas"]["InstallmentRepaymentClaimPrepareProviderRequest"];
type GeneratedResolveRequest =
  components["schemas"]["InstallmentRepaymentClaimResolveRequest"];
type GeneratedDetails =
  components["schemas"]["InstallmentDetailsDto"];
type GeneratedHistoryResponse =
  components["schemas"]["InstallmentHistoryQueryResponse"];
type GeneratedLine = components["schemas"]["InstallmentLineDto"];
type GeneratedPayment =
  components["schemas"]["InstallmentPaymentDto"];
type GeneratedPaymentCommand =
  components["schemas"]["InstallmentPaymentCommandDto"];
type GeneratedPickupRequest =
  components["schemas"]["InstallmentConfirmPickupRequest"] &
    Readonly<{
      operationGuid?: string;
      idempotencyKey?: string;
    }>;
type GeneratedPickupResponse =
  components["schemas"]["InstallmentConfirmPickupResponse"];
type GeneratedRefundCommand =
  components["schemas"]["InstallmentRefundPaymentCommandDto"];
type GeneratedSummary =
  components["schemas"]["InstallmentSummaryDto"];
type GeneratedVoidRequest =
  components["schemas"]["InstallmentVoidRequest"] &
    Readonly<{ operationGuid?: string }>;
type GeneratedVoidResponse =
  components["schemas"]["InstallmentVoidResponse"];

const STATUS_TO_API = Object.freeze({
  Active: 1,
  PaidOff: 2,
  PickedUp: 3,
  Cancelled: 4,
} satisfies Readonly<Record<InstallmentStatus, 1 | 2 | 3 | 4>>);

const METHOD_TO_API = Object.freeze({
  cash: 1,
  card: 2,
  voucher: 3,
} satisfies Readonly<Record<InstallmentPaymentMethod, 1 | 2 | 3>>);

const REFUND_MODE_TO_API = Object.freeze({
  "original-route": 1,
  voucher: 2,
} satisfies Readonly<Record<InstallmentCancelRefundMode, 1 | 2>>);

const RESOLVE_OUTCOME_TO_API = Object.freeze({
  Released: 1,
  Declined: 2,
  Unknown: 3,
} as const);

export class HbposInstallmentsApi implements InstallmentsRemotePort {
  private readonly storeCode: string;

  public constructor(
    private readonly transport: HbposTransport,
    trustedStoreCode: string,
  ) {
    this.storeCode = requestIdentity(trustedStoreCode, "storeCode");
  }

  public async getCapabilities(): Promise<InstallmentRepaymentCapabilities> {
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "GET",
      url: "/api/v1/installments/capabilities",
    });
    const payload = unwrapHbposEnvelope(response.data);
    if (!isRecord(payload)) throw invalidResponse("capabilities");
    // 旧服务端尚未发布取消 claim 字段时，保留字段缺失而非显式写入 undefined，
    // 以兼容 exactOptionalPropertyTypes；调用方仍按缺失值 fail-closed。
    const cancelClaimCapabilities = {
      ...(payload.cancelClaimsSupported === undefined
        ? {}
        : {
            cancelClaimsSupported: responseBoolean(
              payload.cancelClaimsSupported,
              "capabilities.cancelClaimsSupported",
            ),
          }),
      ...(payload.cancelClaimsRequired === undefined
        ? {}
        : {
            cancelClaimsRequired: responseBoolean(
              payload.cancelClaimsRequired,
              "capabilities.cancelClaimsRequired",
            ),
          }),
      ...(payload.cancelPreparedClaimTtlSeconds === undefined
        ? {}
        : {
            cancelPreparedClaimTtlSeconds: responseNonNegativeInteger(
              payload.cancelPreparedClaimTtlSeconds,
              "capabilities.cancelPreparedClaimTtlSeconds",
            ),
          }),
    };
    return Object.freeze({
      repaymentClaimsSupported: responseBoolean(
        payload.repaymentClaimsSupported,
        "capabilities.repaymentClaimsSupported",
      ),
      repaymentClaimsRequired: responseBoolean(
        payload.repaymentClaimsRequired,
        "capabilities.repaymentClaimsRequired",
      ),
      repaymentClaimPrepareProviderV1: optionalResponseBoolean(
        payload.repaymentClaimPrepareProviderV1,
        "capabilities.repaymentClaimPrepareProviderV1",
      ),
      cardRepaymentSupported: optionalResponseBoolean(
        payload.cardRepaymentSupported,
        "capabilities.cardRepaymentSupported",
      ),
      crossDeviceRepaymentEnabled: responseBoolean(
        payload.crossDeviceRepaymentEnabled,
        "capabilities.crossDeviceRepaymentEnabled",
      ),
      crossDeviceCancelRefundEnabled: optionalResponseBoolean(
        payload.crossDeviceCancelRefundEnabled,
        "capabilities.crossDeviceCancelRefundEnabled",
      ),
      crossDeviceVoidEnabled: optionalResponseBoolean(
        payload.crossDeviceVoidEnabled,
        "capabilities.crossDeviceVoidEnabled",
      ),
      crossDevicePickupEnabled: optionalResponseBoolean(
        payload.crossDevicePickupEnabled,
        "capabilities.crossDevicePickupEnabled",
      ),
      preparedClaimTtlSeconds: responseNonNegativeInteger(
        payload.preparedClaimTtlSeconds,
        "capabilities.preparedClaimTtlSeconds",
      ),
      ...cancelClaimCapabilities,
    });
  }

  public async createRepaymentClaim(
    command: InstallmentRepaymentClaimCreateCommand,
  ): Promise<InstallmentRepaymentClaim> {
    const installmentGuid = requestUuid(
      command.installmentGuid,
      "installmentGuid",
    );
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/repayment-claims`,
      data: {
        operationGuid: requestUuid(command.operationGuid, "operationGuid"),
        paymentGuid: requestUuid(command.paymentGuid, "paymentGuid"),
        amount: centsToDollars(command.amountCents, "amountCents", false),
        method: METHOD_TO_API[command.method],
        idempotencyKey: requestText(
          command.idempotencyKey,
          "idempotencyKey",
          100,
        ),
      },
    });
    return this.mapRepaymentClaim(
      unwrapHbposEnvelope(response.data),
      installmentGuid,
      command.operationGuid,
    );
  }

  public async beginRepaymentClaimProvider(
    command: InstallmentRepaymentClaimBeginProviderCommand,
  ): Promise<InstallmentRepaymentClaim> {
    const identity = mapClaimIdentity(command);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: claimUrl(identity, "/begin-provider"),
      data: {
        provider: requestText(command.provider, "provider", 32),
        providerAttemptId: requestText(
          command.providerAttemptId,
          "providerAttemptId",
          128,
        ),
      },
    });
    return this.mapRepaymentClaim(
      unwrapHbposEnvelope(response.data),
      identity.installmentGuid,
      identity.operationGuid,
    );
  }

  public async prepareRepaymentClaimProvider(
    command: InstallmentRepaymentClaimPrepareProviderCommand,
  ): Promise<InstallmentRepaymentClaim> {
    const identity = mapClaimIdentity(command);
    const request: GeneratedPrepareProviderRequest = {
      paymentGuid: requestUuid(command.paymentGuid, "paymentGuid"),
      amount: centsToDollars(command.amountCents, "amountCents", false),
      method: METHOD_TO_API[command.method],
      idempotencyKey: requestText(
        command.idempotencyKey,
        "idempotencyKey",
        100,
      ),
      provider: requestText(command.provider, "provider", 32),
      providerAttemptId: requestText(
        command.providerAttemptId,
        "providerAttemptId",
        128,
      ),
    };
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: claimUrl(identity, "/prepare-provider"),
      data: request,
    });
    return this.mapRepaymentClaim(
      unwrapHbposEnvelope(response.data),
      identity.installmentGuid,
      identity.operationGuid,
    );
  }

  public async getRepaymentClaim(
    input: InstallmentRepaymentClaimIdentity,
  ): Promise<InstallmentRepaymentClaim> {
    const identity = mapClaimIdentity(input);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "GET",
      url: claimUrl(identity),
    });
    return this.mapRepaymentClaim(
      unwrapHbposEnvelope(response.data),
      identity.installmentGuid,
      identity.operationGuid,
    );
  }

  public async resolveRepaymentClaim(
    command: InstallmentRepaymentClaimResolveCommand,
  ): Promise<InstallmentRepaymentClaim> {
    const identity = mapClaimIdentity(command);
    const request: GeneratedResolveRequest = {
      outcome: RESOLVE_OUTCOME_TO_API[command.outcome],
      ...(command.cashNotCollectedConfirmed === undefined
        ? {}
        : {
            cashNotCollectedConfirmed: command.cashNotCollectedConfirmed,
          }),
      ...(command.providerAttemptId === undefined
        ? {}
        : {
            providerAttemptId:
              command.providerAttemptId === null
                ? null
                : requestText(
                    command.providerAttemptId,
                    "providerAttemptId",
                    128,
                  ),
          }),
    };
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: claimUrl(identity, "/resolve"),
      data: request,
    });
    return this.mapRepaymentClaim(
      unwrapHbposEnvelope(response.data),
      identity.installmentGuid,
      identity.operationGuid,
    );
  }

  public async commitRepaymentClaim(
    command: InstallmentRepaymentClaimCommitCommand,
  ): Promise<InstallmentRepaymentClaim> {
    const identity = mapClaimIdentity(command);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: claimUrl(identity, "/commit"),
      data: {
        reference: optionalProtectedText(command.reference, "reference"),
        reservationToken: optionalProtectedText(
          command.reservationToken,
          "reservationToken",
        ),
        cardTransactions: [...command.cardTransactions],
      },
    });
    return this.mapRepaymentClaim(
      unwrapHbposEnvelope(response.data),
      identity.installmentGuid,
      identity.operationGuid,
    );
  }

  public async createCancelClaim(
    command: InstallmentCancelClaimCreateCommand,
  ): Promise<InstallmentCancelClaim> {
    const identity = mapCancelClaimIdentity(command);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: `/api/v1/installments/${identity.installmentGuid}/cancel-claims`,
      data: {
        operationGuid: identity.operationGuid,
        idempotencyKey: requestText(command.idempotencyKey, "idempotencyKey", 100),
        reason: optionalRequestText(command.reason, "reason", 500),
        refundPlanFingerprint: requestText(
          command.refundPlanFingerprint,
          "refundPlanFingerprint",
          128,
        ),
        // 中文注释：显式下发退款方式；旧服务端会丢弃该字段，回包 refundMode 由 runtime 严格比对。
        refundMode: requestRefundMode(command.refundMode),
      },
    });
    return this.mapCancelClaim(
      unwrapHbposEnvelope(response.data),
      identity.installmentGuid,
      identity.operationGuid,
    );
  }

  public async beginCancelClaimRefund(
    input: InstallmentCancelClaimIdentity,
  ): Promise<InstallmentCancelClaim> {
    const identity = mapCancelClaimIdentity(input);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: cancelClaimUrl(identity, "/begin-refund"),
    });
    return this.mapCancelClaim(unwrapHbposEnvelope(response.data), identity.installmentGuid, identity.operationGuid);
  }

  public async getCancelClaim(
    input: InstallmentCancelClaimIdentity,
  ): Promise<InstallmentCancelClaim> {
    const identity = mapCancelClaimIdentity(input);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "GET",
      url: cancelClaimUrl(identity),
    });
    return this.mapCancelClaim(unwrapHbposEnvelope(response.data), identity.installmentGuid, identity.operationGuid);
  }

  public async resolveCancelClaim(
    command: InstallmentCancelClaimResolveCommand,
  ): Promise<InstallmentCancelClaim> {
    const identity = mapCancelClaimIdentity(command);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: cancelClaimUrl(identity, "/resolve"),
      data: { outcome: RESOLVE_OUTCOME_TO_API[command.outcome] },
    });
    return this.mapCancelClaim(unwrapHbposEnvelope(response.data), identity.installmentGuid, identity.operationGuid);
  }

  public async commitCancelClaim(
    command: InstallmentCancelClaimCommitCommand,
  ): Promise<InstallmentCancelClaim> {
    const identity = mapCancelClaimIdentity(command);
    const response = await this.transport.request<HbposEnvelope<unknown>>({
      method: "POST",
      url: cancelClaimUrl(identity, "/commit"),
      data: { refunds: command.refunds.map(mapRefundCommand) },
    });
    return this.mapCancelClaim(unwrapHbposEnvelope(response.data), identity.installmentGuid, identity.operationGuid);
  }

  public async list(
    query: InstallmentHistoryQuery,
  ): Promise<readonly InstallmentSummary[]> {
    const params: Record<string, string | number> = {
      storeCode: this.storeCode,
    };
    const deviceCode = optionalRequestText(
      query.deviceCode,
      "deviceCode",
      128,
    );
    const createdFrom = optionalIso(
      query.createdFromIso,
      "createdFromIso",
    );
    const createdTo = optionalIso(
      query.createdToIso,
      "createdToIso",
    );
    const keyword = optionalRequestText(query.keyword, "keyword", 120);
    if (deviceCode) params.deviceCode = deviceCode;
    if (createdFrom) params.createdFrom = createdFrom;
    if (createdTo) params.createdTo = createdTo;
    if (keyword) params.keyword = keyword;
    if (query.status) params.status = STATUS_TO_API[query.status];
    params.skip = requestSkip(query.skip);
    params.take = requestTake(query.take);

    const response = await this.transport.request<
      HbposEnvelope<GeneratedHistoryResponse>
    >({
      method: "GET",
      url: "/api/v1/installments/history",
      params,
    });
    const payload = unwrapHbposEnvelope(response.data);
    const orders = requiredArray(payload.orders, "response.orders");
    return Object.freeze(
      orders.map((order) => this.mapSummary(order)),
    );
  }

  public async getDetails(
    installmentGuid: string,
  ): Promise<InstallmentDetails | null> {
    const guid = requestUuid(installmentGuid, "installmentGuid");
    const response = await this.transport.request<
      HbposEnvelope<GeneratedDetails | null>
    >({
      method: "GET",
      url: `/api/v1/installments/${guid}`,
    });
    const payload = unwrapHbposEnvelope(response.data);
    return payload === null ? null : this.mapDetails(payload);
  }

  public async create(
    command: InstallmentCreateCommand,
  ): Promise<InstallmentDetails> {
    const request: GeneratedCreateRequest = {
      installmentGuid: requestUuid(
        command.installmentGuid,
        "installmentGuid",
      ),
      storeCode: this.storeCode,
      ...mapIdentity(command),
      createdAt: requestIso(command.createdAtIso, "createdAtIso"),
      totalAmount: centsToDollars(command.totalCents, "totalCents", false),
      downPaymentAmount: centsToDollars(
        command.downPaymentCents,
        "downPaymentCents",
        false,
      ),
      lines: command.lines.map(mapLineCommand),
      downPayment: mapPaymentCommand(command.downPayment),
      customerName: requestText(command.customerName, "customerName", 256),
      customerPhone: requestText(
        command.customerPhone,
        "customerPhone",
        128,
      ),
      note: optionalRequestText(command.note, "note", 2_000),
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedCreateResponse>
    >({
      method: "POST",
      url: "/api/v1/installments",
      data: request,
    });
    return this.detailsFromWriteResponse(
      unwrapHbposEnvelope(response.data).details,
    );
  }

  public async appendPayment(
    command: InstallmentAppendPaymentCommand,
  ): Promise<InstallmentDetails> {
    const installmentGuid = requestUuid(
      command.installmentGuid,
      "installmentGuid",
    );
    const payment = mapPaymentCommand(command.payment);
    const request: GeneratedAppendRequest = {
      installmentGuid,
      paymentGuid: payment.paymentGuid,
      storeCode: this.storeCode,
      ...mapIdentity(command),
      amount: payment.amount,
      method: payment.method,
      reference: payment.reference,
      reservationToken: payment.reservationToken,
      cardTransactions: payment.cardTransactions,
      idempotencyKey: payment.idempotencyKey,
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedAppendResponse>
    >({
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/payments`,
      data: request,
    });
    return this.detailsFromWriteResponse(
      unwrapHbposEnvelope(response.data).details,
    );
  }

  public async cancelWithRefund(
    command: InstallmentCancelCommand,
  ): Promise<InstallmentDetails> {
    const installmentGuid = requestUuid(
      command.installmentGuid,
      "installmentGuid",
    );
    const request: GeneratedCancelRequest = {
      installmentGuid,
      storeCode: this.storeCode,
      ...mapIdentity(command),
      cancelledAt: requestIso(
        command.cancelledAtIso,
        "cancelledAtIso",
      ),
      refunds: command.refunds.map(mapRefundCommand),
      reason: optionalRequestText(command.reason, "reason", 1_000),
      idempotencyKey: requestText(
        command.idempotencyKey,
        "idempotencyKey",
        256,
      ),
      ...(command.refundMode === undefined
        ? {}
        : { refundMode: requestRefundMode(command.refundMode) }),
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedCancelResponse>
    >({
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/cancel`,
      data: request,
    });
    return this.detailsFromWriteResponse(
      unwrapHbposEnvelope(response.data).details,
    );
  }

  public async void(
    command: InstallmentVoidCommand,
  ): Promise<InstallmentDetails> {
    const installmentGuid = requestUuid(
      command.installmentGuid,
      "installmentGuid",
    );
    const request: GeneratedVoidRequest = {
      installmentGuid,
      storeCode: this.storeCode,
      ...mapIdentity(command),
      voidedAt: requestIso(command.voidedAtIso, "voidedAtIso"),
      reason: requestText(command.reason, "reason", 1_000),
      idempotencyKey: requestText(
        command.idempotencyKey,
        "idempotencyKey",
        256,
      ),
      ...(command.operationGuid
        ? {
            operationGuid: requestUuid(
              command.operationGuid,
              "operationGuid",
            ),
          }
        : {}),
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedVoidResponse>
    >({
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/void`,
      data: request,
    });
    return this.detailsFromWriteResponse(
      unwrapHbposEnvelope(response.data).details,
    );
  }

  public async confirmPickup(
    command: InstallmentPickupCommand,
  ): Promise<InstallmentDetails> {
    const installmentGuid = requestUuid(
      command.installmentGuid,
      "installmentGuid",
    );
    const request: GeneratedPickupRequest = {
      installmentGuid,
      storeCode: this.storeCode,
      ...mapIdentity(command),
      confirmedAt: requestIso(
        command.confirmedAtIso,
        "confirmedAtIso",
      ),
      note: optionalRequestText(command.note, "note", 1_000),
      ...(command.operationGuid
        ? {
            operationGuid: requestUuid(
              command.operationGuid,
              "operationGuid",
            ),
          }
        : {}),
      ...(command.idempotencyKey
        ? {
            idempotencyKey: requestText(
              command.idempotencyKey,
              "idempotencyKey",
              256,
            ),
          }
        : {}),
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedPickupResponse>
    >({
      method: "POST",
      url: `/api/v1/installments/${installmentGuid}/pickup`,
      data: request,
    });
    return this.detailsFromWriteResponse(
      unwrapHbposEnvelope(response.data).details,
    );
  }

  private detailsFromWriteResponse(
    details: GeneratedDetails | null | undefined,
  ): InstallmentDetails {
    if (!details) throw invalidResponse("details");
    return this.mapDetails(details);
  }

  private mapSummary(
    summary: GeneratedSummary | GeneratedDetails,
    updatedAt: unknown = summary.updatedAt,
  ): InstallmentSummary {
    const storeCode = responseIdentity(summary.storeCode, "summary.storeCode");
    if (storeCode !== this.storeCode) {
      throw invalidResponse("summary.storeCode");
    }
    return Object.freeze({
      installmentGuid: responseUuid(
        summary.installmentGuid,
        "summary.installmentGuid",
      ),
      installmentNumber: responseText(
        summary.installmentNumber,
        "summary.installmentNumber",
        128,
      ),
      storeCode,
      deviceCode: responseIdentity(
        summary.deviceCode,
        "summary.deviceCode",
      ),
      cashierName: responseText(
        summary.cashierName,
        "summary.cashierName",
        256,
      ),
      customerName: responseText(
        summary.customerName,
        "summary.customerName",
        256,
      ),
      customerPhone: responseOptionalText(
        summary.customerPhone,
        "summary.customerPhone",
        128,
      ),
      createdAtIso: responseIso(
        summary.createdAt,
        "summary.createdAt",
      ),
      totalCents: responseMoneyCents(
        summary.totalAmount,
        "summary.totalAmount",
      ),
      downPaymentCents: responseMoneyCents(
        summary.downPaymentAmount,
        "summary.downPaymentAmount",
      ),
      paidCents: responseMoneyCents(
        summary.paidAmount,
        "summary.paidAmount",
      ),
      balanceCents: responseMoneyCents(
        summary.balanceAmount,
        "summary.balanceAmount",
      ),
      status: responseStatus(summary.status, "summary.status"),
      updatedAtIso: responseIso(
        updatedAt,
        "summary.updatedAt",
      ),
    });
  }

  private mapDetails(details: GeneratedDetails): InstallmentDetails {
    // 兼容尚未回填 updatedAt 的旧详情；只回退服务端 createdAt，不伪造本机时间。
    const summary = this.mapSummary(
      details,
      details.updatedAt ?? details.createdAt,
    );
    return Object.freeze({
      ...summary,
      cashierId: responseIdentity(
        details.cashierId,
        "details.cashierId",
      ),
      minimumDownPaymentCents: responseMoneyCents(
        details.minimumDownPayment,
        "details.minimumDownPayment",
      ),
      lines: Object.freeze(
        requiredArray(details.lines, "details.lines").map(mapLine),
      ),
      payments: Object.freeze(
        requiredArray(details.payments, "details.payments").map(mapPayment),
      ),
      pickupInfo: mapPickupInfo(details.pickupInfo),
      cancellationInfo: mapCancellationInfo(details.cancellationInfo),
      note: responseOptionalText(details.note, "details.note", 2_000),
    });
  }

  private mapRepaymentClaim(
    input: unknown,
    expectedInstallmentGuid: string,
    expectedOperationGuid: string,
  ): InstallmentRepaymentClaim {
    if (!isRecord(input)) throw invalidResponse("repaymentClaim");
    const installmentGuid = responseUuid(
      input.installmentGuid,
      "repaymentClaim.installmentGuid",
    );
    const operationGuid = responseUuid(
      input.operationGuid,
      "repaymentClaim.operationGuid",
    );
    if (
      installmentGuid !== expectedInstallmentGuid ||
      operationGuid !== expectedOperationGuid
    ) {
      throw invalidResponse("repaymentClaim.identity");
    }
    return Object.freeze({
      installmentGuid,
      operationGuid,
      paymentGuid: responseUuid(
        input.paymentGuid,
        "repaymentClaim.paymentGuid",
      ),
      amountCents: responseMoneyCents(
        input.amount,
        "repaymentClaim.amount",
      ),
      method: responsePaymentMethod(
        input.method,
        "repaymentClaim.method",
      ),
      idempotencyKey: responseText(
        input.idempotencyKey,
        "repaymentClaim.idempotencyKey",
        100,
      ),
      status: responseClaimStatus(
        input.status,
        "repaymentClaim.status",
      ),
      provider: responseOptionalText(
        input.provider,
        "repaymentClaim.provider",
        32,
      ),
      providerAttemptId: responseOptionalText(
        input.providerAttemptId,
        "repaymentClaim.providerAttemptId",
        128,
      ),
      createdAtIso: responseIso(
        input.createdAtUtc,
        "repaymentClaim.createdAtUtc",
      ),
      updatedAtIso: responseIso(
        input.updatedAtUtc,
        "repaymentClaim.updatedAtUtc",
      ),
      expiresAtIso:
        input.expiresAtUtc === null || input.expiresAtUtc === undefined
          ? null
          : responseIso(
              input.expiresAtUtc,
              "repaymentClaim.expiresAtUtc",
            ),
      commit: this.mapClaimCommit(input.commit),
      alreadyExists: responseBoolean(
        input.alreadyExists,
        "repaymentClaim.alreadyExists",
      ),
    });
  }

  private mapClaimCommit(input: unknown): InstallmentRepaymentClaim["commit"] {
    if (input === null || input === undefined) return null;
    if (!isRecord(input) || !isRecord(input.details)) {
      throw invalidResponse("repaymentClaim.commit");
    }
    return Object.freeze({
      details: this.mapDetails(input.details as GeneratedDetails),
      alreadyRecorded: responseBoolean(
        input.alreadyRecorded,
        "repaymentClaim.commit.alreadyRecorded",
      ),
    });
  }

  private mapCancelClaim(
    input: unknown,
    expectedInstallmentGuid: string,
    expectedOperationGuid: string,
  ): InstallmentCancelClaim {
    if (!isRecord(input)) throw invalidResponse("cancelClaim");
    const installmentGuid = responseUuid(input.installmentGuid, "cancelClaim.installmentGuid");
    const operationGuid = responseUuid(input.operationGuid, "cancelClaim.operationGuid");
    if (installmentGuid !== expectedInstallmentGuid || operationGuid !== expectedOperationGuid) {
      throw invalidResponse("cancelClaim.identity");
    }
    const commit = input.commit;
    return Object.freeze({
      installmentGuid,
      operationGuid,
      idempotencyKey: responseText(input.idempotencyKey, "cancelClaim.idempotencyKey", 100),
      refundPlanFingerprint: responseText(input.refundPlanFingerprint, "cancelClaim.refundPlanFingerprint", 128),
      refundMode: responseCancelRefundMode(input.refundMode, "cancelClaim.refundMode"),
      status: responseCancelClaimStatus(input.status, "cancelClaim.status"),
      createdAtIso: responseIso(input.createdAtUtc, "cancelClaim.createdAtUtc"),
      updatedAtIso: responseIso(input.updatedAtUtc, "cancelClaim.updatedAtUtc"),
      expiresAtIso: input.expiresAtUtc == null ? null : responseIso(input.expiresAtUtc, "cancelClaim.expiresAtUtc"),
      commit: commit == null ? null : this.mapCancelClaimCommit(commit),
      alreadyExists: responseBoolean(input.alreadyExists, "cancelClaim.alreadyExists"),
    });
  }

  private mapCancelClaimCommit(input: unknown): InstallmentCancelClaim["commit"] {
    if (!isRecord(input) || !isRecord(input.details)) throw invalidResponse("cancelClaim.commit");
    return Object.freeze({
      details: this.mapDetails(input.details as GeneratedDetails),
      alreadyCancelled: responseBoolean(input.alreadyCancelled, "cancelClaim.commit.alreadyCancelled"),
    });
  }
}

function mapClaimIdentity(input: InstallmentRepaymentClaimIdentity) {
  return Object.freeze({
    installmentGuid: requestUuid(input.installmentGuid, "installmentGuid"),
    operationGuid: requestUuid(input.operationGuid, "operationGuid"),
  });
}

function claimUrl(
  identity: InstallmentRepaymentClaimIdentity,
  suffix = "",
): string {
  return `/api/v1/installments/${identity.installmentGuid}/repayment-claims/${identity.operationGuid}${suffix}`;
}

function mapCancelClaimIdentity(input: InstallmentCancelClaimIdentity) {
  return Object.freeze({
    installmentGuid: requestUuid(input.installmentGuid, "installmentGuid"),
    operationGuid: requestUuid(input.operationGuid, "operationGuid"),
  });
}

function cancelClaimUrl(identity: InstallmentCancelClaimIdentity, suffix = ""): string {
  return `/api/v1/installments/${identity.installmentGuid}/cancel-claims/${identity.operationGuid}${suffix}`;
}

function mapIdentity(input: Readonly<{
  deviceCode: string;
  cashierId: string;
  cashierName: string;
}>) {
  return {
    deviceCode: requestIdentity(input.deviceCode, "deviceCode"),
    cashierId: requestIdentity(input.cashierId, "cashierId"),
    cashierName: requestText(input.cashierName, "cashierName", 256),
  };
}

function mapLineCommand(line: InstallmentLine): GeneratedLine {
  return {
    installmentLineGuid: requestUuid(
      line.installmentLineGuid,
      "installmentLineGuid",
    ),
    productCode: requestText(line.productCode, "productCode", 128),
    referenceCode: optionalRequestText(
      line.referenceCode,
      "referenceCode",
      128,
    ),
    displayName: requestText(line.displayName, "displayName", 512),
    lookupCode: requestText(line.lookupCode, "lookupCode", 128),
    quantity: requestQuantity(line.quantity),
    unitPrice: centsToDollars(line.unitPriceCents, "unitPriceCents", true),
    discountAmount: centsToDollars(
      line.discountCents,
      "discountCents",
      true,
    ),
    actualAmount: centsToDollars(
      line.actualAmountCents,
      "actualAmountCents",
      true,
    ),
    itemNumber: optionalRequestText(line.itemNumber, "itemNumber", 128),
  };
}

function mapPaymentCommand(
  payment: InstallmentPaymentCommand,
): Required<GeneratedPaymentCommand> {
  return {
    paymentGuid: requestUuid(payment.paymentGuid, "paymentGuid"),
    method: METHOD_TO_API[payment.method],
    amount: centsToDollars(payment.amountCents, "amountCents", false),
    reference: optionalProtectedText(payment.reference, "reference"),
    reservationToken: optionalProtectedText(
      payment.reservationToken,
      "reservationToken",
    ),
    cardTransactions: [...payment.cardTransactions],
    idempotencyKey: requestText(
      payment.idempotencyKey,
      "idempotencyKey",
      256,
    ),
  };
}

function mapRefundCommand(
  refund: InstallmentRefundCommand,
): GeneratedRefundCommand {
  return {
    paymentGuid: requestUuid(refund.paymentGuid, "refund.paymentGuid"),
    method: METHOD_TO_API[refund.method],
    amount: centsToDollars(
      refund.amountCents,
      "refund.amountCents",
      false,
    ),
    reference: optionalProtectedText(
      refund.reference,
      "refund.reference",
    ),
    cardTransactions: [...refund.cardTransactions],
    idempotencyKey: requestText(
      refund.idempotencyKey,
      "refund.idempotencyKey",
      256,
    ),
    ...(refund.originalPaymentGuid
      ? {
          originalPaymentGuid: requestUuid(
            refund.originalPaymentGuid,
            "refund.originalPaymentGuid",
          ),
        }
      : {}),
  };
}

function mapLine(line: GeneratedLine): InstallmentLine {
  return Object.freeze({
    installmentLineGuid: responseUuid(
      line.installmentLineGuid,
      "line.installmentLineGuid",
    ),
    productCode: responseText(line.productCode, "line.productCode", 128),
    referenceCode: responseOptionalText(
      line.referenceCode,
      "line.referenceCode",
      128,
    ),
    displayName: responseText(
      line.displayName,
      "line.displayName",
      512,
    ),
    lookupCode: responseText(line.lookupCode, "line.lookupCode", 128),
    quantity: responseQuantity(line.quantity, "line.quantity"),
    unitPriceCents: responseMoneyCents(
      line.unitPrice,
      "line.unitPrice",
    ),
    discountCents: responseMoneyCents(
      line.discountAmount,
      "line.discountAmount",
    ),
    actualAmountCents: responseMoneyCents(
      line.actualAmount,
      "line.actualAmount",
    ),
    itemNumber: responseOptionalText(
      line.itemNumber,
      "line.itemNumber",
      128,
    ),
  });
}

function mapPayment(payment: GeneratedPayment): InstallmentPayment {
  const safeCard = safeCardDisplay(payment.cardTransactions?.[0]);
  return Object.freeze({
    paymentGuid: responseUuid(
      payment.paymentGuid,
      "payment.paymentGuid",
    ),
    method: responsePaymentMethod(
      payment.method,
      "payment.method",
    ),
    amountCents: responseMoneyCents(
      payment.amount,
      "payment.amount",
      true,
    ),
    status: responsePaymentStatus(
      payment.status,
      "payment.status",
    ),
    recordedAtIso: responseIso(
      payment.recordedAt,
      "payment.recordedAt",
    ),
    cashierId: responseIdentity(
      payment.cashierId,
      "payment.cashierId",
    ),
    deviceCode: responseIdentity(
      payment.deviceCode,
      "payment.deviceCode",
    ),
    cardType: safeCard.cardType,
    maskedCardNumber: safeCard.maskedCardNumber,
  });
}

function mapPickupInfo(
  pickup:
    | components["schemas"]["InstallmentPickupInfoDto"]
    | null
    | undefined,
) {
  if (!pickup) return null;
  return Object.freeze({
    pickedUpAtIso: responseIso(
      pickup.pickedUpAt,
      "pickup.pickedUpAt",
    ),
    pickedUpBy: responseText(
      pickup.pickedUpBy,
      "pickup.pickedUpBy",
      256,
    ),
    note: responseOptionalText(pickup.note, "pickup.note", 1_000),
  });
}

function mapCancellationInfo(
  cancellation:
    | components["schemas"]["InstallmentCancellationInfoDto"]
    | null
    | undefined,
) {
  if (!cancellation) return null;
  return Object.freeze({
    kind:
      cancellation.kind === 1
        ? ("RefundCancel" as const)
        : cancellation.kind === 2
          ? ("VoidCancel" as const)
          : (() => {
              throw invalidResponse("cancellation.kind");
            })(),
    cancelledAtIso: responseIso(
      cancellation.cancelledAt,
      "cancellation.cancelledAt",
    ),
    cancelledBy: responseText(
      cancellation.cancelledBy,
      "cancellation.cancelledBy",
      256,
    ),
    reason: responseOptionalText(
      cancellation.reason,
      "cancellation.reason",
      1_000,
    ),
  });
}

function responseStatus(
  value: unknown,
  field: string,
): InstallmentStatus {
  if (value === 1) return "Active";
  if (value === 2) return "PaidOff";
  if (value === 3) return "PickedUp";
  if (value === 4) return "Cancelled";
  throw invalidResponse(field);
}

function responsePaymentMethod(
  value: unknown,
  field: string,
): InstallmentPaymentMethod {
  if (value === 1) return "cash";
  if (value === 2) return "card";
  if (value === 3) return "voucher";
  throw invalidResponse(field);
}

function responsePaymentStatus(
  value: unknown,
  field: string,
): "Recorded" | "Voided" {
  if (value === 1) return "Recorded";
  if (value === 2) return "Voided";
  throw invalidResponse(field);
}

function responseClaimStatus(
  value: unknown,
  field: string,
): InstallmentRepaymentClaim["status"] {
  if (value === 1 || value === "Prepared") return "Prepared";
  if (value === 2 || value === "ProviderPending") return "ProviderPending";
  if (value === 3 || value === "Committed") return "Committed";
  if (value === 4 || value === "Released") return "Released";
  if (value === 5 || value === "Declined") return "Declined";
  if (value === 6 || value === "Unknown") return "Unknown";
  throw invalidResponse(field);
}

function requestRefundMode(
  value: InstallmentCancelRefundMode,
): 1 | 2 {
  if (value !== "original-route" && value !== "voucher") {
    throw invalidRequest("refundMode");
  }
  return REFUND_MODE_TO_API[value];
}

/** 旧服务端不回传 refundMode：只能是原路退语义；未知取值一律拒绝。 */
function responseCancelRefundMode(
  value: unknown,
  field: string,
): InstallmentCancelRefundMode {
  if (value === undefined || value === null) return "original-route";
  if (value === 1 || value === "OriginalRoute") return "original-route";
  if (value === 2 || value === "Voucher") return "voucher";
  throw invalidResponse(field);
}

function responseCancelClaimStatus(
  value: unknown,
  field: string,
): InstallmentCancelClaim["status"] {
  if (value === 1 || value === "Prepared") return "Prepared";
  if (value === 2 || value === "RefundPending") return "RefundPending";
  if (value === 3 || value === "Committed") return "Committed";
  if (value === 4 || value === "Released") return "Released";
  if (value === 5 || value === "Declined") return "Declined";
  if (value === 6 || value === "Unknown") return "Unknown";
  throw invalidResponse(field);
}

function responseBoolean(value: unknown, field: string): boolean {
  if (typeof value !== "boolean") throw invalidResponse(field);
  return value;
}

function optionalResponseBoolean(value: unknown, field: string): boolean {
  return value === undefined ? false : responseBoolean(value, field);
}

function responseNonNegativeInteger(value: unknown, field: string): number {
  if (!Number.isSafeInteger(value) || Number(value) < 0) {
    throw invalidResponse(field);
  }
  return Number(value);
}

function safeCardDisplay(value: unknown): Readonly<{
  cardType: string | null;
  maskedCardNumber: string | null;
}> {
  if (!isRecord(value)) {
    return { cardType: null, maskedCardNumber: null };
  }
  const cardType = safeDisplayText(value.cardType, 64);
  const maskedCandidate = safeDisplayText(value.maskedCardNumber, 64);
  const maskedCardNumber =
    maskedCandidate &&
    /[*xX•]/u.test(maskedCandidate) &&
    !/\d{12,19}/u.test(maskedCandidate.replace(/[\s-]/gu, ""))
      ? maskedCandidate
      : null;
  return { cardType, maskedCardNumber };
}

function safeDisplayText(value: unknown, maxLength: number): string | null {
  if (typeof value !== "string") return null;
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    return null;
  }
  return normalized;
}

function requestTake(value: unknown): 20 | 50 | 51 | 100 | 200 {
  if (
    value === 20 ||
    value === 50 ||
    value === 51 ||
    value === 100 ||
    value === 200
  ) {
    return value;
  }
  throw invalidRequest("take");
}

function requestSkip(value: unknown): number {
  if (Number.isSafeInteger(value) && Number(value) >= 0) {
    return Number(value);
  }
  throw invalidRequest("skip");
}

function requestQuantity(value: unknown): number {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw invalidRequest("quantity");
  }
  const quantity = Number(value);
  if (!Number.isFinite(quantity) || quantity <= 0) {
    throw invalidRequest("quantity");
  }
  return quantity;
}

function centsToDollars(
  value: unknown,
  field: string,
  allowZero: boolean,
): number {
  if (
    !Number.isSafeInteger(value) ||
    Number(value) < (allowZero ? 0 : 1)
  ) {
    throw invalidRequest(field);
  }
  return Number(value) / 100;
}

function responseMoneyCents(
  value: unknown,
  field: string,
  allowNegative = false,
): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    (!allowNegative && value < 0)
  ) {
    throw invalidResponse(`${field}.money`);
  }
  const scaled = value * 100;
  const cents = Math.round(scaled);
  if (
    !Number.isSafeInteger(cents) ||
    Math.abs(scaled - cents) > 1e-7
  ) {
    throw invalidResponse(`${field}.money`);
  }
  return cents;
}

function responseQuantity(value: unknown, field: string): string {
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) {
    throw invalidResponse(field);
  }
  return decimalString(value);
}

function decimalString(value: number): string {
  const text = value.toString();
  if (!/[eE]/u.test(text)) return text;
  const [coefficient, exponentText] = text.toLowerCase().split("e");
  const exponent = Number(exponentText);
  const negative = coefficient?.startsWith("-") ?? false;
  const unsigned = negative ? coefficient!.slice(1) : coefficient!;
  const [whole, fraction = ""] = unsigned.split(".");
  const digits = `${whole}${fraction}`;
  const decimalIndex = whole!.length + exponent;
  const expanded =
    decimalIndex <= 0
      ? `0.${"0".repeat(-decimalIndex)}${digits}`
      : decimalIndex >= digits.length
        ? `${digits}${"0".repeat(decimalIndex - digits.length)}`
        : `${digits.slice(0, decimalIndex)}.${digits.slice(decimalIndex)}`;
  return `${negative ? "-" : ""}${expanded}`;
}

function requestIdentity(value: unknown, field: string): string {
  return requestText(value, field, 128);
}

function requestText(
  value: unknown,
  field: string,
  maxLength: number,
): string {
  if (typeof value !== "string") throw invalidRequest(field);
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw invalidRequest(field);
  }
  return normalized;
}

function optionalRequestText(
  value: unknown,
  field: string,
  maxLength: number,
): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "string") throw invalidRequest(field);
  if (value.trim().length === 0) return null;
  return requestText(value, field, maxLength);
}

function optionalProtectedText(
  value: unknown,
  field: string,
): string | null {
  if (value === null || value === undefined) return null;
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 4_096 ||
    /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(value)
  ) {
    throw invalidRequest(field);
  }
  return value;
}

function requestIso(value: unknown, field: string): string {
  if (typeof value !== "string") throw invalidRequest(field);
  const parsed = new Date(value);
  if (!Number.isFinite(parsed.getTime())) throw invalidRequest(field);
  return parsed.toISOString();
}

function optionalIso(value: unknown, field: string): string | null {
  if (value === null || value === undefined || value === "") return null;
  return requestIso(value, field);
}

function requestUuid(value: unknown, field: string): string {
  if (
    typeof value !== "string" ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
      value,
    )
  ) {
    throw invalidRequest(field);
  }
  return value.toLowerCase();
}

function responseUuid(value: unknown, field: string): string {
  try {
    return requestUuid(value, field);
  } catch {
    throw invalidResponse(field);
  }
}

function responseIdentity(value: unknown, field: string): string {
  return responseText(value, field, 128);
}

function responseText(
  value: unknown,
  field: string,
  maxLength: number,
): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw invalidResponse(field);
  }
  return value;
}

function responseOptionalText(
  value: unknown,
  field: string,
  maxLength: number,
): string | null {
  if (value === null || value === undefined || value === "") return null;
  return responseText(value, field, maxLength);
}

function responseIso(value: unknown, field: string): string {
  if (typeof value !== "string") throw invalidResponse(field);
  const parsed = new Date(value);
  if (!Number.isFinite(parsed.getTime())) throw invalidResponse(field);
  return parsed.toISOString();
}

function requiredArray<T>(
  value: readonly T[] | null | undefined,
  field: string,
): readonly T[] {
  if (!Array.isArray(value)) throw invalidResponse(field);
  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function invalidRequest(field: string): Error {
  return new Error(`Invalid installment request field: ${field}.`);
}

function invalidResponse(field: string): Error {
  return new Error(`Invalid installment response field: ${field}.`);
}
