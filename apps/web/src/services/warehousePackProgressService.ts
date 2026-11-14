import type { ApiResponse } from '../types/api'
import type { WarehousePackProgress } from '../types/packing'
import request, { RequestError, unwrapApiData } from '../utils/request'

/**
 * 仓库订单的装箱进度（二期，只读）：各商品订货 / 已拣 / 已装件数、订单名下的箱、是否对账通过。
 * 账号登录即可调用，需仓库角色或 Warehouse.Picking / Manage / ManageOrders 权限，否则 403；订单不存在为 404 ORDER_NOT_FOUND。
 * Web 账号认不出拣货人，返回里的 isMine 恒为 false，界面不使用。
 */
const API_BASE = '/api/react/v1/warehouse-picking'

/** 后端 null 字段不输出：列表 / 计数缺失时补默认值，界面不用再判空。 */
export function normalizeWarehousePackProgress(raw: WarehousePackProgress): WarehousePackProgress {
  return {
    ...raw,
    lines: raw.lines ?? [],
    cartons: raw.cartons ?? [],
    openCartonCount: raw.openCartonCount ?? 0,
    emptyCartonCount: raw.emptyCartonCount ?? 0,
    sealedCartonCount: raw.sealedCartonCount ?? 0,
    reconciled: raw.reconciled === true,
  }
}

export async function getWarehousePackProgress(orderGuid: string, signal?: AbortSignal) {
  return normalizeWarehousePackProgress(
    unwrapApiData(
      await request.get<ApiResponse<WarehousePackProgress>>(`${API_BASE}/orders/${encodeURIComponent(orderGuid)}/pack-progress`, {
        signal,
      }),
    ),
  )
}

/** 装箱情况读取失败的原因：403 无权限（静默隐藏）、404 订单不存在、其他为接口 / 网络异常。 */
export function readPackProgressFailure(error: unknown): { status: number; message: string } {
  if (error instanceof RequestError) {
    return { status: error.status, message: error.message }
  }
  return { status: 0, message: error instanceof Error ? error.message : String(error) }
}
