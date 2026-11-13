import { useLocalSearchParams } from "expo-router";
import { ReceiveScreen } from "@/modules/packing";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function PackingReceiveRoute() {
  const params = useLocalSearchParams();
  // 首页扫到发给本店的箱 / 托盘、或从待签收列表点进来时带着 code；同一个 code 只在进入时查一次。
  const code = firstParam(params.code);
  return <ReceiveScreen key={code} initialCode={code} />;
}
