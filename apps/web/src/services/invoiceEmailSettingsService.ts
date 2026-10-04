import type { ApiResponse } from '../types/api'
import type {
  InvoiceEmailSenderAccountDto,
  InvoiceEmailSettingsDto,
  InvoiceEmailSettingsSaveRequest,
  InvoiceEmailSettingsTestRequest,
  InvoiceEmailSettingsTestResult,
} from '../types/invoiceEmailSettings'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/invoice-email-settings'

export async function getInvoiceEmailSettings() {
  const response = await request.get<ApiResponse<InvoiceEmailSettingsDto>>(API_BASE)
  return unwrapApiData(response)
}

export async function saveInvoiceEmailSettings(payload: InvoiceEmailSettingsSaveRequest) {
  const response = await request.put<ApiResponse<InvoiceEmailSettingsDto>>(API_BASE, payload)
  return unwrapApiData(response)
}

export async function sendInvoiceEmailSettingsTestEmail(payload: InvoiceEmailSettingsTestRequest) {
  const response = await request.post<ApiResponse<InvoiceEmailSettingsTestResult>>(`${API_BASE}/test`, payload)
  return unwrapApiData(response)
}

export async function getInvoiceEmailSenderAccounts() {
  const response = await request.get<ApiResponse<InvoiceEmailSenderAccountDto[]>>(`${API_BASE}/sender-accounts`)
  return unwrapApiData(response)
}
