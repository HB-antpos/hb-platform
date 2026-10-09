import {
  Redirect,
  type Href,
  useLocalSearchParams,
  useRouter,
} from "expo-router";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import type { PosAuthorizedFulfilmentActionResult } from "@/core/runtime/production-pos-service-composition";
import {
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import {
  INSTALLMENTS_CREATE_PERMISSION,
  resolveInstallmentsRuntimeFactory,
} from "@/features/installments";
import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";
import {
  installmentCreatePaymentEntry,
  installmentRepaymentPaymentEntry,
  PaymentScreen,
  regularPaymentEntry,
  resolvePaymentLocale,
  UnifiedPaymentFacade,
  type PaymentScreenPresenter,
  type PaymentInstallmentModeControl,
  type PaymentPresenterState,
  type PaymentReceiptPrintOutcome,
  type RegularPaymentEntry,
  type UnifiedPaymentEntry,
  type UnifiedPaymentFacadeDependencies,
} from "@/features/payments/ui";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";

type PaymentPresenterBinding = Readonly<{
  services: object;
  cashier: object;
  entry: UnifiedPaymentEntry;
  presenter: PaymentScreenPresenter;
}>;

type PaymentEntryParseResult =
  | Readonly<{ kind: "recovery" }>
  | Readonly<{ kind: "entry"; entry: UnifiedPaymentEntry }>
  | Readonly<{ kind: "invalid" }>;

type PaymentRouteParams = Readonly<
  Record<string, string | string[] | undefined>
>;

type UnifiedPaymentBinding = Readonly<{
  facade: UnifiedPaymentFacade;
  installmentsAvailable: boolean;
  regularAvailable: boolean;
}>;

const UNAVAILABLE_REGULAR_LANE: UnifiedPaymentFacadeDependencies["regular"] =
  Object.freeze({
    createPresenter(): never {
      throw new Error("REGULAR_PAYMENT_RUNTIME_UNAVAILABLE");
    },
    hasRecoveryRequired: async () => false,
  });

const UNAVAILABLE_INSTALLMENT_LANE: UnifiedPaymentFacadeDependencies["installments"] =
  Object.freeze({
    prepareCreateCheckout(): never {
      throw new Error("INSTALLMENT_PAYMENT_RUNTIME_UNAVAILABLE");
    },
    createCheckoutPresenter(): never {
      throw new Error("INSTALLMENT_PAYMENT_RUNTIME_UNAVAILABLE");
    },
    hasRecoveryRequired: async () => false,
  });

/** 只接受 sales route 生成的最小 checkout 上下文；支付事实仍由组合根复核。 */
export default function PaymentRoute() {
  const { dismissTo, push } = useRouter();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const activeCashier = useCashierLoginStore((state) => state.activeCashier);
  const clearActiveCashier = useCashierLoginStore(
    (state) => state.clearActiveCashier,
  );
  const params = useLocalSearchParams() as PaymentRouteParams;
  const paramsFingerprint = paymentRouteParamsFingerprint(params);
  const entry = useMemo(
    () => parsePaymentEntryFingerprint(paramsFingerprint),
    [paramsFingerprint],
  );
  const requestedEntry = useMemo(
    () => (entry.kind === "entry" ? entry.entry : null),
    [entry],
  );
  const regularEntry: RegularPaymentEntry | null =
    requestedEntry?.kind === "regular" ? requestedEntry : null;
  const unifiedPayment = useMemo<UnifiedPaymentBinding | null>(
    () => {
      const services = runtime.services;
      if (!services) return null;
      const regularAvailable = services.payments.status === "available";
      const installments = resolveInstallmentsRuntimeFactory(services);
      return {
        facade: new UnifiedPaymentFacade({
          regular: regularAvailable
            ? services.payments
            : UNAVAILABLE_REGULAR_LANE,
          installments:
            installments ?? UNAVAILABLE_INSTALLMENT_LANE,
        }),
        installmentsAvailable: installments !== null,
        regularAvailable,
      };
    },
    [runtime.services],
  );
  const gate = resolveProtectedSalesRouteGate(runtime.state, activeCashier);
  const [binding, setBinding] = useState<PaymentPresenterBinding | null>(null);
  const [creationFailed, setCreationFailed] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [installmentModeIssue, setInstallmentModeIssue] = useState<
    PaymentInstallmentModeControl["issue"]
  >(null);
  const presenterRef = useRef<PaymentScreenPresenter | null>(null);
  const presenter =
    binding?.services === runtime.services && binding.cashier === activeCashier
      ? binding.presenter
      : null;
  const presenterState = usePaymentPresenterState(presenter);
  const printCurrentReceipt = useMemo<
    ((orderGuid: string) => Promise<PaymentReceiptPrintOutcome>) | undefined
  >(() => {
    const services = runtime.services;
    if (!services?.fulfilment.reprintCurrentReceipt) return undefined;
    return async (orderGuid) =>
      mapReceiptPrintResult(
        await services.fulfilment.reprintCurrentReceipt(orderGuid),
      );
  }, [runtime.services]);

  useEffect(() => {
    if (
      gate !== "check-device-identity" ||
      !activeCashier ||
      !runtime.services ||
      !unifiedPayment ||
      entry.kind === "invalid"
    ) {
      setBinding(null);
      return undefined;
    }

    const services = runtime.services;
    const cashier = activeCashier;
    let cancelled = false;
    setBinding(null);
    setCreationFailed(false);
    setUnavailable(false);
    setInstallmentModeIssue(null);

    // 恢复事实始终覆盖 URL；两账本同时阻塞时由 facade 固定选择普通支付。
    void unifiedPayment.facade.resolveRecovery().then(
      (recovery) => {
        if (cancelled) return;
        const nextEntry =
          recovery.kind === "ready"
            ? recovery.entry
            : requestedEntry;
        if (
          !nextEntry ||
          !isPaymentLaneAvailable(nextEntry, unifiedPayment)
        ) {
          setUnavailable(true);
          return;
        }
        try {
          const nextPresenter =
            unifiedPayment.facade.createPresenter(nextEntry);
          if (cancelled) {
            nextPresenter.destroy();
            return;
          }
          presenterRef.current = nextPresenter;
          setBinding({
            services,
            cashier,
            entry: nextEntry,
            presenter: nextPresenter,
          });
        } catch {
          if (!cancelled) {
            clearActiveCashier();
            setCreationFailed(true);
          }
        }
      },
      () => {
        if (!cancelled) {
          clearActiveCashier();
          setCreationFailed(true);
        }
      },
    );

    return () => {
      cancelled = true;
      presenterRef.current?.destroy();
      presenterRef.current = null;
    };
  }, [
    activeCashier,
    clearActiveCashier,
    entry.kind,
    gate,
    requestedEntry,
    runtime.services,
    unifiedPayment,
  ]);

  if (gate === "redirect-index") {
    return <Redirect href={"/" as Href} />;
  }
  if (gate === "redirect-login" || creationFailed) {
    return <Redirect href={"/login" as Href} />;
  }
  if (entry.kind === "invalid" || unavailable) {
    return <Redirect href={"/sales" as Href} />;
  }
  if (!presenter) {
    return <BootstrapScreen />;
  }

  const installmentModeControl = resolveInstallmentModeControl({
    entry: binding?.entry ?? null,
    regularEntry,
    installmentsAvailable: unifiedPayment?.installmentsAvailable ?? false,
    permissions: activeCashier?.permissions ?? [],
    presenterState,
    issue: installmentModeIssue,
    onToggle: (enabled) => {
      const activeBinding = binding;
      const activeEntry = activeBinding?.entry;
      if (
        !activeBinding ||
        !activeEntry ||
        !unifiedPayment ||
        isInstallmentModeSwitchLocked(presenterState)
      ) {
        return;
      }

      const activeIsInstallment = isInstallmentPaymentEntry(activeEntry);
      if (enabled === activeIsInstallment) return;

      if (
        enabled &&
        activeEntry.kind === "regular" &&
        regularEntry &&
        canToggleInstallmentMode(
          activeCashier?.permissions ?? [],
          unifiedPayment.installmentsAvailable,
        )
      ) {
        try {
          const nextEntry = unifiedPayment.facade.prepareInstallmentCreate();
          const nextPresenter = unifiedPayment.facade.createPresenter(nextEntry);
          activeBinding.presenter.destroy();
          presenterRef.current = nextPresenter;
          setInstallmentModeIssue(null);
          setBinding({
            services: activeBinding.services,
            cashier: activeBinding.cashier,
            entry: nextEntry,
            presenter: nextPresenter,
          });
        } catch {
          // 新建分期失败时普通付款仍是唯一可信账本，不能登出或导走收银员。
          setInstallmentModeIssue("unavailable");
        }
        return;
      }

      if (!enabled && activeEntry.kind === "installment-create" && regularEntry) {
        try {
          const nextPresenter = unifiedPayment.facade.createPresenter(regularEntry);
          activeBinding.presenter.destroy();
          presenterRef.current = nextPresenter;
          setInstallmentModeIssue(null);
          setBinding({
            services: activeBinding.services,
            cashier: activeBinding.cashier,
            entry: regularEntry,
            presenter: nextPresenter,
          });
        } catch {
          setInstallmentModeIssue("unavailable");
        }
      }
    },
  });

  return (
    <PaymentScreen
      locale={resolvePaymentLocale(i18n.resolvedLanguage ?? i18n.language)}
      onBack={() => dismissTo("/sales" as Href)}
      onOpenRecoveryCenter={() => push("/payment-recovery" as Href)}
      onComplete={() => dismissTo("/sales" as Href)}
      presenter={presenter}
      {...(installmentModeControl ? { installmentModeControl } : {})}
      {...(printCurrentReceipt ? { onPrintReceipt: printCurrentReceipt } : {})}
    />
  );
}

function mapReceiptPrintResult(
  result: PosAuthorizedFulfilmentActionResult,
): PaymentReceiptPrintOutcome {
  switch (result.state) {
    case "Printed":
      return "completed";
    case "Ambiguous":
    case "recovery-required":
      return "unknown";
    case "Completed":
    case "Unknown":
    case "Failed":
    case "denied":
    case "not-found":
    case "not-retryable":
    default:
      return "failed";
  }
}

function usePaymentPresenterState(
  presenter: PaymentScreenPresenter | null,
): PaymentPresenterState | null {
  const [state, setState] = useState<PaymentPresenterState | null>(null);

  useEffect(() => {
    if (!presenter) {
      setState(null);
      return undefined;
    }
    const update = () => setState(presenter.getState());
    update();
    return presenter.subscribe(update);
  }, [presenter]);

  return state;
}

function resolveInstallmentModeControl(input: Readonly<{
  entry: UnifiedPaymentEntry | null;
  regularEntry: RegularPaymentEntry | null;
  installmentsAvailable: boolean;
  permissions: readonly string[];
  presenterState: PaymentPresenterState | null;
  issue: PaymentInstallmentModeControl["issue"];
  onToggle(enabled: boolean): void;
}>): PaymentInstallmentModeControl | undefined {
  const { entry } = input;
  if (!entry || entry.kind === "recovery") return undefined;

  if (
    entry.kind === "installment-repayment" ||
    (entry.kind === "installment-create" && !input.regularEntry)
  ) {
    return {
      enabled: true,
      locked: true,
      issue: null,
      onToggle: input.onToggle,
    };
  }

  if (
    !input.regularEntry ||
    !canToggleInstallmentMode(input.permissions, input.installmentsAvailable)
  ) {
    return undefined;
  }

  return {
    enabled: isInstallmentPaymentEntry(entry),
    locked: isInstallmentModeSwitchLocked(input.presenterState),
    issue: input.issue,
    onToggle: input.onToggle,
  };
}

function canToggleInstallmentMode(
  permissions: readonly string[],
  installmentsAvailable: boolean,
): boolean {
  if (!installmentsAvailable) return false;
  const granted = new Set(permissions.map((permission) => permission.trim()));
  return (
    granted.has(INSTALLMENTS_CREATE_PERMISSION) &&
    granted.has(PAYMENT_PERMISSION.view) &&
    granted.has(PAYMENT_PERMISSION.confirm)
  );
}

function isInstallmentPaymentEntry(entry: UnifiedPaymentEntry): boolean {
  return (
    entry.kind === "installment-create" ||
    entry.kind === "installment-repayment" ||
    (entry.kind === "recovery" && entry.ledger === "installment")
  );
}

function isInstallmentModeSwitchLocked(
  state: PaymentPresenterState | null,
): boolean {
  if (!state) return true;
  return (
    !state.initialized ||
    state.busy ||
    state.attemptId != null ||
    state.orderGuid != null ||
    state.allowedActions.recover
  );
}

function parsePaymentEntry(
  input: PaymentRouteParams,
): PaymentEntryParseResult {
  const presentKeys = Object.keys(input).filter(
    (key) => input[key] !== undefined,
  );
  if (presentKeys.length === 0) return { kind: "recovery" };
  if (
    presentKeys.some((key) => !PAYMENT_PARAM_KEYS.has(key)) ||
    presentKeys.some((key) => typeof input[key] !== "string")
  ) {
    return { kind: "invalid" };
  }

  const flow = input.flow;
  if (flow === undefined || flow === "regular") {
    const expectedKeys =
      flow === "regular"
        ? REGULAR_PAYMENT_KEYS_WITH_FLOW
        : REGULAR_PAYMENT_KEYS;
    if (!hasExactKeys(presentKeys, expectedKeys)) {
      return { kind: "invalid" };
    }
    const revision = parseNonNegativeInteger(String(input.revision));
    const totalCents = parsePositiveInteger(String(input.totalCents));
    if (revision === null || totalCents === null) {
      return { kind: "invalid" };
    }
    try {
      return {
        kind: "entry",
        entry: regularPaymentEntry({
          checkoutIntentId: String(input.checkoutIntentId),
          expectedCartRevision: revision,
          total: { currency: "AUD", cents: totalCents },
        }),
      };
    } catch {
      return { kind: "invalid" };
    }
  }

  if (flow === "installment-create") {
    if (!hasExactKeys(presentKeys, INSTALLMENT_CREATE_KEYS)) {
      return { kind: "invalid" };
    }
    const revision = parseNonNegativeInteger(String(input.revision));
    if (revision === null) return { kind: "invalid" };
    try {
      return {
        kind: "entry",
        entry: installmentCreatePaymentEntry({
          checkoutIntentId: String(input.checkoutIntentId),
          expectedCartRevision: revision,
        }),
      };
    } catch {
      return { kind: "invalid" };
    }
  }

  if (flow === "installment-repayment") {
    if (!hasExactKeys(presentKeys, INSTALLMENT_REPAYMENT_KEYS)) {
      return { kind: "invalid" };
    }
    try {
      return {
        kind: "entry",
        entry: installmentRepaymentPaymentEntry(
          String(input.installmentGuid),
        ),
      };
    } catch {
      return { kind: "invalid" };
    }
  }

  return { kind: "invalid" };
}

function paymentRouteParamsFingerprint(
  input: PaymentRouteParams,
): string {
  return JSON.stringify(
    Object.keys(input)
      .filter((key) => input[key] !== undefined)
      .sort()
      .map((key) => [key, input[key]]),
  );
}

function parsePaymentEntryFingerprint(
  fingerprint: string,
): PaymentEntryParseResult {
  const entries = JSON.parse(fingerprint) as readonly (
    readonly [string, string | string[]]
  )[];
  return parsePaymentEntry(Object.fromEntries(entries));
}

function parseNonNegativeInteger(value: string): number | null {
  if (!/^\d+$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

function parsePositiveInteger(value: string): number | null {
  const parsed = parseNonNegativeInteger(value);
  return parsed !== null && parsed > 0 ? parsed : null;
}

function hasExactKeys(
  actual: readonly string[],
  expected: ReadonlySet<string>,
): boolean {
  return (
    actual.length === expected.size &&
    actual.every((key) => expected.has(key))
  );
}

function isPaymentLaneAvailable(
  entry: UnifiedPaymentEntry,
  binding: UnifiedPaymentBinding,
): boolean {
  if (
    entry.kind === "regular" ||
    (entry.kind === "recovery" && entry.ledger === "regular")
  ) {
    return binding.regularAvailable;
  }
  return binding.installmentsAvailable;
}

const PAYMENT_PARAM_KEYS = new Set([
  "flow",
  "checkoutIntentId",
  "revision",
  "totalCents",
  "installmentGuid",
]);
const REGULAR_PAYMENT_KEYS = new Set([
  "checkoutIntentId",
  "revision",
  "totalCents",
]);
const REGULAR_PAYMENT_KEYS_WITH_FLOW = new Set([
  "flow",
  ...REGULAR_PAYMENT_KEYS,
]);
const INSTALLMENT_CREATE_KEYS = new Set([
  "flow",
  "checkoutIntentId",
  "revision",
]);
const INSTALLMENT_REPAYMENT_KEYS = new Set([
  "flow",
  "installmentGuid",
]);
