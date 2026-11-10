import {
  createZellerPurchaseReference,
  createZellerRefundReference,
  formatZellerPayment,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-payment-reference";
import {
  classifyZellerCall,
  classifyZellerVerification,
  type ZellerAttemptExpectation,
  type ZellerCallClassification,
  type ZellerCallDecision,
  type ZellerVerificationClassification,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-result-classifier";
import {
  ZELLER_NO_CALL_EVIDENCE,
  accumulateZellerCallEvidence,
  mapZellerSdkCallException,
  mapZellerSdkCallResponse,
  mapZellerSdkPairingResponse,
  mapZellerSdkQueryResponse,
  type ZellerCallEvidence,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-sdk-mapper";
import type {
  ZellerCallResult,
  ZellerTransaction,
} from "@hb/pos-payments-core/features/payments/zeller/zeller-terminal-models";

/**
 * Zeller 真机验证（B0）的执行逻辑：调用 SDK、收集事件证据、走我们自己的转换层和分类器，
 * 把每一步的原始结果和我们的结论记成一条日志。纯逻辑，不依赖 React 和 SDK 包，
 * SDK 以结构类型注入，测试里用假终端替代。
 *
 * 中文注释：验证页的目的不是收款，而是确认三件事：SDK 在本机（新架构 + WebView）能不能跑、
 * SDK 的真实返回和我们按类型声明写的转换层是否对得上、分类器在真机场景下的结论是否正确。
 * 所以日志同时保留 SDK 的原始返回和我们的结论，便于事后对照。
 */

/** SDK 的一次进行中的调用：可 await，扣款 / 退款还能取消和订阅事件。 */
export type ZellerProbeOperation = PromiseLike<unknown> & {
  cancel?: () => void;
  onEvent?: (callback: (event: unknown) => void) => void;
};

/** 验证页用到的 SDK 方法（@zeller-public/payments-sdk-react-native 的 TerminalClient 结构上满足）。 */
export type ZellerProbeTerminal = Readonly<{
  initialise(): ZellerProbeOperation;
  setup(): ZellerProbeOperation;
  purchase(options: { amount: number; reference: string }): ZellerProbeOperation;
  refund(options: { amount: number; purchase: string; reference: string }): ZellerProbeOperation;
  getTransactions(options: { reference: string }): ZellerProbeOperation;
}>;

export type ZellerProbeStep = "initialise" | "setup" | "purchase" | "refund" | "query";

export type ZellerProbeTone = "good" | "bad" | "warn" | "neutral";

export type ZellerProbeEntry = Readonly<{
  step: ZellerProbeStep;
  startedAtIso: string;
  elapsedMs: number;
  tone: ZellerProbeTone;
  /** 一行结论，例如 `purchase → Approved`。 */
  headline: string;
  /** 明细：发出的参数、收到的事件、SDK 原始返回、我们的转换和分类结论。 */
  lines: readonly string[];
}>;

export type ZellerProbePorts = Readonly<{
  /** 生成本次尝试的 GUID（小写 D 格式）。每次扣款 / 退款都必须是新的：Zeller 不对 reference 去重。 */
  newGuid(): string;
  nowMs(): number;
  nowIso(): string;
  /** 把进行中的调用交给界面，供「取消」按钮使用；调用结束后以 null 通知。 */
  onOperation?(operation: ZellerProbeOperation | null): void;
}>;

/** 一笔已有结论的扣款，后续的查询和退款以它为对象。 */
export type ZellerProbePurchase = Readonly<{
  expectation: ZellerAttemptExpectation;
  /** 调用结束时的结论；需要按 reference 查询才能定论的三种会被「查询」按钮用作 pending。 */
  decision: ZellerCallDecision;
  transactionUuid: string | null;
}>;

export async function probeInitialise(
  terminal: ZellerProbeTerminal,
  ports: ZellerProbePorts,
): Promise<ZellerProbeEntry> {
  return probePairing("initialise", () => terminal.initialise(), ports);
}

export async function probeSetup(
  terminal: ZellerProbeTerminal,
  ports: ZellerProbePorts,
): Promise<ZellerProbeEntry> {
  return probePairing("setup", () => terminal.setup(), ports);
}

export async function probePurchase(
  terminal: ZellerProbeTerminal,
  amountCents: number,
  ports: ZellerProbePorts,
): Promise<Readonly<{ entry: ZellerProbeEntry; purchase: ZellerProbePurchase }>> {
  const reference = createZellerPurchaseReference(ports.newGuid());
  const expectation: ZellerAttemptExpectation = { type: "Purchase", amountCents, reference };
  const call = await runCall(
    () => terminal.purchase({ amount: amountCents, reference }),
    ports,
  );
  const classification = classifyZellerCall(call.result, expectation);
  const transactionUuid = classification.transaction?.transactionUuid ?? null;
  return {
    entry: {
      step: "purchase",
      startedAtIso: call.startedAtIso,
      elapsedMs: call.elapsedMs,
      tone: toneOfCall(classification),
      headline: `purchase → ${classification.decision}`,
      lines: [
        `request amount=${amountCents} reference=${reference}`,
        ...describeCall(call, classification),
        ...orderReferenceLine(classification, reference),
      ],
    },
    purchase: { expectation, decision: classification.decision, transactionUuid },
  };
}

export async function probeRefund(
  terminal: ZellerProbeTerminal,
  original: Readonly<{ transactionUuid: string; amountCents: number }>,
  ports: ZellerProbePorts,
): Promise<ZellerProbeEntry> {
  const reference = createZellerRefundReference(ports.newGuid());
  const expectation: ZellerAttemptExpectation = {
    type: "Refund",
    amountCents: original.amountCents,
    reference,
  };
  // 退款用原交易的 transactionUuid 指向原交易，不用 reference（一个 reference 可能对应多笔）。
  const call = await runCall(
    () =>
      terminal.refund({
        amount: original.amountCents,
        purchase: original.transactionUuid,
        reference,
      }),
    ports,
  );
  const classification = classifyZellerCall(call.result, expectation);
  return {
    step: "refund",
    startedAtIso: call.startedAtIso,
    elapsedMs: call.elapsedMs,
    tone: toneOfCall(classification),
    headline: `refund → ${classification.decision}`,
    lines: [
      `request amount=${original.amountCents} purchase=${original.transactionUuid} reference=${reference}`,
      ...describeCall(call, classification),
    ],
  };
}

export async function probeQuery(
  terminal: ZellerProbeTerminal,
  purchase: ZellerProbePurchase,
  ports: ZellerProbePorts,
): Promise<ZellerProbeEntry> {
  const startedAtIso = ports.nowIso();
  const startedMs = ports.nowMs();
  let raw: unknown;
  let thrown: string | null = null;
  try {
    raw = await terminal.getTransactions({ reference: purchase.expectation.reference });
  } catch (error) {
    thrown = describeThrown(error);
  }
  const elapsedMs = Math.round(ports.nowMs() - startedMs);
  // SDK 约定不抛异常；真抛了就按查询失败处理，「没有交易」不能成为结论。
  const query =
    thrown === null
      ? mapZellerSdkQueryResponse(raw)
      : mapZellerSdkQueryResponse(new Error(thrown));

  // 已有定论的扣款（批准 / 拒付 / 未发出）不需要再分类，只展示查到了什么。
  const pending = pendingDecisionOf(purchase.decision);
  const verification =
    pending === null
      ? null
      : classifyZellerVerification(pending, query, purchase.expectation);

  return {
    step: "query",
    startedAtIso,
    elapsedMs,
    tone: !query.succeeded ? "bad" : verification ? toneOfVerification(verification) : "neutral",
    headline: !query.succeeded
      ? `query → failed (${query.errorType ?? "Unrecognised"})`
      : `query → ${query.transactions.length} transaction(s)${
          verification ? ` → ${verification.decision}` : ""
        }`,
    lines: [
      `request reference=${purchase.expectation.reference}`,
      ...(thrown === null ? [] : [`sdk threw: ${thrown}`]),
      `sdk raw: ${stringifyRaw(raw)}`,
      ...query.transactions.map(
        (transaction, index) => `mapped[${index}] ${describeTransaction(transaction)}`,
      ),
      ...(verification
        ? [
            `verification pending=${pending} decision=${verification.decision} unknownReason=${verification.unknownReason}`,
          ]
        : [`verification skipped: purchase already ${purchase.decision}`]),
    ],
  };
}

/** 整份日志的纯文本，供系统分享面板发出。 */
export function buildZellerProbeReport(
  header: readonly string[],
  entries: readonly ZellerProbeEntry[],
): string {
  const body = entries.flatMap((entry, index) => [
    "",
    `#${index + 1} ${entry.startedAtIso} ${entry.headline} (${entry.elapsedMs} ms)`,
    ...entry.lines.map((line) => `  ${line}`),
  ]);
  return [...header, ...body].join("\n");
}

type CallRun = Readonly<{
  startedAtIso: string;
  elapsedMs: number;
  events: readonly string[];
  raw: unknown;
  thrown: string | null;
  result: ZellerCallResult;
}>;

async function runCall(
  start: () => ZellerProbeOperation,
  ports: ZellerProbePorts,
): Promise<CallRun> {
  const startedAtIso = ports.nowIso();
  const startedMs = ports.nowMs();
  const events: string[] = [];
  let evidence: ZellerCallEvidence = ZELLER_NO_CALL_EVIDENCE;
  let raw: unknown;
  let thrown: string | null = null;
  try {
    const operation = start();
    operation.onEvent?.((event) => {
      evidence = accumulateZellerCallEvidence(evidence, event);
      events.push(`+${Math.round(ports.nowMs() - startedMs)}ms ${stringifyRaw(event)}`);
    });
    ports.onOperation?.(operation);
    raw = await operation;
  } catch (error) {
    thrown = describeThrown(error);
  } finally {
    ports.onOperation?.(null);
  }
  return {
    startedAtIso,
    elapsedMs: Math.round(ports.nowMs() - startedMs),
    events,
    raw,
    thrown,
    result:
      thrown === null
        ? mapZellerSdkCallResponse(raw, evidence)
        : mapZellerSdkCallException(new Error(thrown), evidence),
  };
}

async function probePairing(
  step: "initialise" | "setup",
  start: () => ZellerProbeOperation,
  ports: ZellerProbePorts,
): Promise<ZellerProbeEntry> {
  const startedAtIso = ports.nowIso();
  const startedMs = ports.nowMs();
  let raw: unknown;
  let thrown: string | null = null;
  try {
    const operation = start();
    ports.onOperation?.(operation);
    raw = await operation;
  } catch (error) {
    thrown = describeThrown(error);
  } finally {
    ports.onOperation?.(null);
  }
  const pairing = mapZellerSdkPairingResponse(thrown === null ? raw : new Error(thrown));
  return {
    step,
    startedAtIso,
    elapsedMs: Math.round(ports.nowMs() - startedMs),
    tone: pairing.isPaired ? "good" : "bad",
    headline: pairing.isPaired
      ? `${step} → paired`
      : `${step} → not paired (${pairing.errorType ?? "Unrecognised"})`,
    lines: [
      ...(thrown === null ? [] : [`sdk threw: ${thrown}`]),
      `sdk raw: ${stringifyRaw(raw)}`,
      ...(pairing.message ? [`message: ${pairing.message}`] : []),
    ],
  };
}

function describeCall(call: CallRun, classification: ZellerCallClassification): string[] {
  return [
    ...call.events.map((event) => `event ${event}`),
    ...(call.thrown === null ? [] : [`sdk threw: ${call.thrown}`]),
    `sdk raw: ${stringifyRaw(call.raw)}`,
    `mapped outcome=${call.result.outcome}` +
      ` errorType=${call.result.errorType ?? "-"}` +
      ` reachedTerminal=${String(call.result.reachedTerminal ?? false)}` +
      ` processingStarted=${String(call.result.processingStarted ?? false)}`,
    ...(call.result.transaction
      ? [`mapped ${describeTransaction(call.result.transaction)}`]
      : []),
    `classified decision=${classification.decision}` +
      ` failureKind=${classification.failureKind}` +
      ` unknownReason=${classification.unknownReason}` +
      ` rebuildTerminalFirst=${String(classification.rebuildTerminalFirst)}` +
      ` serialGateBreached=${String(classification.serialGateBreached)}`,
  ];
}

/** 批准时顺带给出将来写进订单付款记录的引用，确认它能按 WPF / 服务端的格式生成。 */
function orderReferenceLine(
  classification: ZellerCallClassification,
  reference: string,
): string[] {
  const transactionUuid = classification.transaction?.transactionUuid;
  if (classification.decision !== "Approved" || !transactionUuid) return [];
  try {
    return [`order reference: ${formatZellerPayment(reference, transactionUuid)}`];
  } catch {
    return [`order reference: cannot be formatted (transactionUuid=${transactionUuid})`];
  }
}

function describeTransaction(transaction: ZellerTransaction): string {
  return (
    `status=${transaction.status} type=${transaction.type} amountCents=${transaction.amountCents}` +
    ` transactionUuid=${transaction.transactionUuid ?? "-"}` +
    ` externalReference=${transaction.externalReference ?? "-"}` +
    ` responseCode=${transaction.responseCode ?? "-"}` +
    ` timestampIso=${transaction.timestampIso ?? "-"}`
  );
}

function pendingDecisionOf(
  decision: ZellerCallDecision,
): "VerifyThenNotPaid" | "VerifyThenDeclined" | "Unknown" | null {
  return decision === "VerifyThenNotPaid" ||
    decision === "VerifyThenDeclined" ||
    decision === "Unknown"
    ? decision
    : null;
}

function toneOfCall(classification: ZellerCallClassification): ZellerProbeTone {
  switch (classification.decision) {
    case "Approved":
      return "good";
    case "Unknown":
      return "warn";
    default:
      return "bad";
  }
}

function toneOfVerification(verification: ZellerVerificationClassification): ZellerProbeTone {
  switch (verification.decision) {
    case "Approved":
      return "good";
    case "KeepUnknown":
      return "warn";
    default:
      return "bad";
  }
}

function describeThrown(error: unknown): string {
  if (error instanceof Error) return error.message || error.name;
  return typeof error === "string" ? error : "non-error value thrown";
}

const RAW_LIMIT = 1500;

/**
 * SDK 原始返回的文本形式。Error 对象的 type / message 不可枚举，要单独取出；
 * 小票原文很长且对验证无用，截掉。
 */
function stringifyRaw(value: unknown): string {
  if (value === undefined) return "undefined";
  if (value instanceof Error) {
    const type = (value as { type?: unknown }).type;
    return `Error{type=${typeof type === "string" ? JSON.stringify(type) : "-"}, message=${JSON.stringify(value.message)}}`;
  }
  let text: string;
  try {
    text = JSON.stringify(value, (key, item: unknown) =>
      key === "receiptText" || key === "emvTagsPrint" ? "<omitted>" : item,
    );
  } catch {
    text = "<unserialisable>";
  }
  return text.length > RAW_LIMIT ? `${text.slice(0, RAW_LIMIT)}…(truncated)` : text;
}
