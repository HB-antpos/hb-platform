import { Redirect, router, type Href } from "expo-router";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  resolvePosEntryRoute,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";

export default function IndexScreen() {
  const runtime = usePosRuntime();
  const activeCashier = useCashierLoginStore(
    (state) => state.activeCashier,
  );
  const target = resolvePosEntryRoute(runtime.state, activeCashier);

  if (target) {
    return <Redirect href={target as Href} />;
  }

  return (
    <BootstrapScreen
      onOpenNetworkCheck={() => router.push("/network-check" as Href)}
    />
  );
}
