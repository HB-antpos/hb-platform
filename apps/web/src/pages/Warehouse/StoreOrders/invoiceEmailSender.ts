import type { InvoiceEmailSenderAccountDto } from '../../../types/invoiceEmailSettings'

/**
 * 决定发票邮件弹窗里默认选中的发件账号：
 * 上次选过且仍存在的账号优先（连续给多家分店发票时不用反复切换），否则用默认账号，再否则取第一个。
 */
export function resolveInvoiceEmailSenderAccountId(
  accounts: InvoiceEmailSenderAccountDto[],
  previousAccountId?: string,
): string | undefined {
  if (previousAccountId && accounts.some((account) => account.id === previousAccountId)) {
    return previousAccountId
  }

  return (accounts.find((account) => account.isDefault) ?? accounts[0])?.id
}

export function formatInvoiceEmailSenderAccountLabel(account: InvoiceEmailSenderAccountDto) {
  const name = account.name.trim()
  return !name || name === account.fromEmail ? account.fromEmail : `${name} <${account.fromEmail}>`
}
