import { Redirect, type Href, useRouter } from "expo-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Text, View } from "react-native";

import { PaymentRecoveryCenterAdapter, projectPaymentRecoveryRecord } from "@/core/runtime/payment-recovery-center-adapter";
import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  isActiveCashierBoundToDevice,
  resolveProtectedSalesRouteGate,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { paymentRecoveryText, resolvePaymentRecoveryLocale } from "@/features/payment-recovery/payment-recovery-copy";
import { PaymentRecoveryScreen } from "@/features/payment-recovery/payment-recovery-screen";
import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";

/** 每次进入重新核实设备与收银员，恢复操作只通过受保护的生产业务接口。 */
export default function PaymentRecoveryRoute() {
  const { push, dismissTo } = useRouter();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const locale = resolvePaymentRecoveryLocale(i18n.resolvedLanguage ?? i18n.language);
  const cashier = useCashierLoginStore((s) => s.activeCashier);
  const clearCashier = useCashierLoginStore((s) => s.clearActiveCashier);
  const gate = resolveProtectedSalesRouteGate(runtime.state, cashier);
  const authorized = cashier?.permissions.includes(PAYMENT_PERMISSION.view) === true;
  const [binding, setBinding] = useState<{ services: object; cashier: object; adapter: PaymentRecoveryCenterAdapter } | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const adapter = binding?.services === runtime.services && binding.cashier === cashier ? binding.adapter : null;

  useEffect(() => {
    if (gate !== "check-device-identity" || !authorized || !cashier || !runtime.services) return;
    let active = true;
    let created: PaymentRecoveryCenterAdapter | null = null;
    const services = runtime.services;
    setUnavailable(false);
    void services.deviceSession.getDeviceIdentity().then((identity) => {
      if (!active) return;
      if (!identity || !isActiveCashierBoundToDevice(cashier, identity)) { clearCashier(); return; }
      const operations = services.payments.recoveryCenter;
      if (!operations) { setUnavailable(true); return; }
      // 首次进入即耐久移交当前异常；刷新只读取，避免把正在恢复的原单再次移交。
      let initialPark: Promise<void> | null = null;
      let initialParkComplete = false;
      const ensureInitialPark = async () => {
        if (initialParkComplete) return;
        initialPark ??= operations.parkCurrent();
        try { await initialPark; initialParkComplete = true; }
        catch (error) { initialPark = null; throw error; }
      };
      created = new PaymentRecoveryCenterAdapter({
        ...operations,
        parkCurrent: () => initialParkComplete ? operations.parkCurrent() : ensureInitialPark(),
        recoverOriginalPayment: async (recordId) => {
          const result = await operations.recoverOriginalPayment(recordId);
          if (active && result !== "completed") push("/payment" as Href);
        },
        list: async () => {
          await ensureInitialPark();
          return (await operations.list()).map(projectPaymentRecoveryRecord);
        },
      });
      setBinding({ services, cashier, adapter: created });

    }).catch(() => { if (active) setUnavailable(true); });
    return () => { active = false; created?.destroy(); };
  }, [gate, authorized, cashier, clearCashier, runtime.services, push]);

  if (gate === "redirect-index") return <Redirect href={"/" as Href} />;
  if (gate === "redirect-login") return <Redirect href={"/login" as Href} />;
  if (!authorized) return <Redirect href={"/sales" as Href} />;
  if (unavailable) return (
    <View style={{ flex: 1, padding: 24, justifyContent: "center", gap: 20 }}>
      <Text>{paymentRecoveryText(locale, "state.unavailable")}</Text>
      <PosPressable onPress={() => dismissTo("/sales" as Href)} accessibilityRole="button" accessibilityLabel={paymentRecoveryText(locale, "action.backShort")} style={{ padding: 20 }}>
        <Text>{paymentRecoveryText(locale, "action.backShort")}</Text>
      </PosPressable>
    </View>
  );
  if (!adapter) return <BootstrapScreen />;
  return <PaymentRecoveryScreen service={adapter} onBack={async () => {
    // 只有耐久保存并释放原购物车成功后才离开；失败时保留当前页供核对。
    try { await adapter.startNextSale(); dismissTo("/sales" as Href); } catch { /* adapter 提供本地化错误状态。 */ }
  }} />;
}
