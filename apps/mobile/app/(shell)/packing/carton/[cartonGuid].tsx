import { useLocalSearchParams } from "expo-router";
import { CartonScreen } from "@/modules/packing";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function PackingCartonRoute() {
  const params = useLocalSearchParams();
  const cartonGuid = firstParam(params.cartonGuid);
  // 首页扫到空箱标启用后带 activated=1 进来，装箱页顶部提示「已启用这张空箱标」。
  const justActivated = firstParam(params.activated) === "1";
  // 「继续装下一箱」是同一路由换参数：按箱号重建页面，不带上一只箱的扫码状态。
  return <CartonScreen key={cartonGuid} cartonGuid={cartonGuid} justActivated={justActivated} />;
}
