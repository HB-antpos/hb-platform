export interface InvoiceEmailSettingsDto {
  accounts: InvoiceEmailAccountDto[]
}

export interface InvoiceEmailAccountDto {
  id: string
  name: string
  host: string | null
  port: number
  useSsl: boolean
  checkCertificateRevocation: boolean
  username?: string | null
  hasPassword: boolean
  fromEmail: string | null
  fromName?: string | null
  maxAttachmentBytes: number
  isDefault: boolean
  updatedAtUtc?: string
  updatedBy?: string
}

export interface InvoiceEmailSettingsSaveRequest {
  accounts: InvoiceEmailAccountSaveRequest[]
}

export interface InvoiceEmailAccountSaveRequest {
  id?: string
  name: string
  host: string
  port: number
  useSsl: boolean
  checkCertificateRevocation: boolean
  username: string
  password?: string
  clearPassword: boolean
  fromEmail: string
  fromName: string
  maxAttachmentBytes: number
  isDefault: boolean
}

export interface InvoiceEmailSettingsTestRequest extends InvoiceEmailAccountSaveRequest {
  testToEmail: string
}

export interface InvoiceEmailSettingsTestResult {
  success: boolean
  message?: string
}

/** 发送邮件时可选的发件账号，只含名称与发件地址，不含 SMTP 凭据。 */
export interface InvoiceEmailSenderAccountDto {
  id: string
  name: string
  fromEmail: string
  fromName?: string | null
  isDefault: boolean
}
