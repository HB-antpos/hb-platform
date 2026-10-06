import type { TFunction } from 'i18next'
import type { SetTemplateValidationError } from './setTemplateRules'

/** 套装模板校验错误码 → 界面文案。草稿「存为模板」与模板编辑两处共用。 */
export function getSetTemplateValidationMessage(error: SetTemplateValidationError, t: TFunction): string {
  const messages: Record<SetTemplateValidationError, string> = {
    missing_set_product_name: t('productCreation.setTemplateSetNameRequired'),
    missing_sub_items: t('productCreation.setTemplateSubItemsRequired'),
    missing_sub_item_name: t('productCreation.setTemplateSubItemNameRequired'),
    missing_sub_item_price: t('productCreation.setTemplateSubItemPriceRequired'),
    invalid_sub_item_price: t('productCreation.setTemplateSubItemPriceInvalid'),
  }
  return messages[error]
}
