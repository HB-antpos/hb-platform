import type { ApiResponse } from '../types/api'
import type {
  ActivateLinklyConfigurationRequest,
  CreateLinklyTerminalRequest,
  DeleteLinklyDeviceSelectionRequest,
  LinklyTerminalManagementDto,
  PaymentTerminalSettingsDto,
  PaymentTerminalEnvironment,
  UpdateLinklyDeviceSelectionRequest,
  UpdateLinklyTerminalRequest,
  UpdateSquareTokenRequest,
} from '../types/paymentTerminalSettings'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/payment-terminal-settings'

export async function getPaymentTerminalSettings(storeCode?: string) {
  const response = await request.get<ApiResponse<PaymentTerminalSettingsDto>>(API_BASE, {
    params: { storeCode },
  })
  return unwrapApiData(response)
}

export async function saveSquareToken(payload: UpdateSquareTokenRequest, storeCode?: string) {
  const response = await request.put<ApiResponse<PaymentTerminalSettingsDto>>(`${API_BASE}/square`, payload, {
    params: { storeCode },
  })
  return unwrapApiData(response)
}

export async function getLinklyTerminals(
  storeCode: string,
  environment: PaymentTerminalEnvironment,
) {
  const response = await request.get<ApiResponse<LinklyTerminalManagementDto>>(`${API_BASE}/linkly-terminals`, {
    params: { storeCode, environment },
  })
  return unwrapApiData(response)
}

export async function createLinklyTerminal(payload: CreateLinklyTerminalRequest) {
  const response = await request.post<ApiResponse<LinklyTerminalManagementDto>>(
    `${API_BASE}/linkly-terminals`,
    payload,
  )
  return unwrapApiData(response)
}

export async function updateLinklyTerminal(
  terminalId: string,
  payload: UpdateLinklyTerminalRequest,
) {
  const response = await request.put<ApiResponse<LinklyTerminalManagementDto>>(
    `${API_BASE}/linkly-terminals/${encodeURIComponent(terminalId)}`,
    payload,
  )
  return unwrapApiData(response)
}

export async function updateLinklyDeviceSelection(
  deviceCode: string,
  payload: UpdateLinklyDeviceSelectionRequest,
) {
  const response = await request.put<ApiResponse<LinklyTerminalManagementDto>>(
    `${API_BASE}/linkly-device-selections/${encodeURIComponent(deviceCode)}`,
    payload,
  )
  return unwrapApiData(response)
}

export async function deleteLinklyDeviceSelection(
  deviceCode: string,
  payload: DeleteLinklyDeviceSelectionRequest,
) {
  const response = await request.delete<ApiResponse<LinklyTerminalManagementDto>>(
    `${API_BASE}/linkly-device-selections/${encodeURIComponent(deviceCode)}`,
    { data: payload },
  )
  return unwrapApiData(response)
}

export async function activateLinklyConfiguration(payload: ActivateLinklyConfigurationRequest) {
  const response = await request.post<ApiResponse<LinklyTerminalManagementDto>>(
    `${API_BASE}/linkly-activation`,
    payload,
  )
  return unwrapApiData(response)
}
