import { BatchCreateScreen } from "@/modules/packing/screens/BatchCreateScreen";

// 批量创建空箱标；按文件路径引入，没进这个页面就不加载它。
export default function PackingBatchNewRoute() {
  return <BatchCreateScreen />;
}
