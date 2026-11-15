import { useLocalSearchParams } from "expo-router";
import { BatchPrintScreen } from "@/modules/packing/screens/BatchPrintScreen";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

// 一批空箱标的连续打印页：创建并打印进来时带 autoprint=1（自动开始）与 copies（每个箱号的份数）。
export default function PackingBatchRoute() {
  const params = useLocalSearchParams();
  const batchGuid = firstParam(params.batchGuid);
  const copies = Number.parseInt(firstParam(params.copies), 10);
  return (
    <BatchPrintScreen
      key={batchGuid}
      batchGuid={batchGuid}
      autoPrint={firstParam(params.autoprint) === "1"}
      copies={Number.isFinite(copies) && copies > 0 ? copies : 1}
      duplicate={firstParam(params.duplicate) === "1"}
    />
  );
}
