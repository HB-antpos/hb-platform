import { BatchListScreen } from "@/modules/packing/screens/BatchListScreen";

// 批量箱标的最近批次列表；按文件路径引入，没进这个页面就不加载它。
export default function PackingBatchListRoute() {
  return <BatchListScreen />;
}
