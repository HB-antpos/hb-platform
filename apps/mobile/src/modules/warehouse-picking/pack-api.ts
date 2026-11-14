import { apiClient } from "@/shared/api/client";
import { normalizeCartonDetail } from "../packing/api-normalization";
import { BASE, pickerConfig } from "./api";
import { normalizePackProgress, normalizeSetCartonItem } from "./pack-api-normalization";

// 拣货与装箱联动（二期）：这些接口都走拣货人身份（账号或设备凭证 + X-Picker-Ticket），
// 与 warehouse-picking/api.ts 现有请求完全同一套请求头与错误处理，PDA 设备会话也能用。

const order = (orderGuid: string) => `${BASE}/orders/${encodeURIComponent(orderGuid)}`;

/** 装箱进度：各商品订货 / 拣货 / 已装件数，本订单的箱与对账状态。 */
export async function fetchPackProgress(orderGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${order(orderGuid)}/pack-progress`, { ...pickerConfig(), signal });
  return normalizePackProgress(response.data);
}

/** 新建一只关联本订单的箱；不传去向时默认发往订货单的分店。 */
export async function createOrderCarton(orderGuid: string) {
  const response = await apiClient.post(`${order(orderGuid)}/cartons`, {}, pickerConfig());
  return normalizeCartonDetail(response.data);
}

/** 封箱；返回箱详情，客户端据此打印标签。重复封箱是幂等的。这一版不传托盘，箱封好后在「扫码打包」里组托盘。 */
export async function sealOrderCarton(orderGuid: string, cartonGuid: string, weightKg: number | null) {
  const response = await apiClient.post(
    `${order(orderGuid)}/cartons/${encodeURIComponent(cartonGuid)}/seal`,
    weightKg == null ? {} : { weightKg },
    pickerConfig(),
  );
  return normalizeCartonDetail(response.data);
}

/** 先拣后装：把商品在这只箱里的件数设为绝对值（0 移除），重复提交结果相同；超装只提示不拦截。 */
export async function setOrderCartonItem(orderGuid: string, cartonGuid: string, productCode: string, quantity: number) {
  const response = await apiClient.put(
    `${order(orderGuid)}/cartons/${encodeURIComponent(cartonGuid)}/items/${encodeURIComponent(productCode)}`,
    { quantity },
    pickerConfig(),
  );
  return normalizeSetCartonItem(response.data);
}
