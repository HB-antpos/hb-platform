import assert from 'node:assert/strict'
import {
  preserveMobilePrivacyDocumentMetadata,
  updateMobilePrivacyDocumentMetadata,
} from './documentMetadata'
import { BROWSER_EXTENSION_PRIVACY_POLICY } from '../BrowserExtensionPrivacy/browserExtensionPrivacyPolicy'
import { isPublicAppPath } from './publicPath'

assert.equal(isPublicAppPath('/login'), true)
assert.equal(isPublicAppPath('/privacy/browser-extension'), true)
assert.equal(isPublicAppPath('/privacy/browser-extension/'), true)
assert.equal(isPublicAppPath('/privacy/mobile'), true)
assert.equal(isPublicAppPath('/privacy/mobile/'), true)
assert.equal(isPublicAppPath('/support/hb-supplier-order'), true)
assert.equal(isPublicAppPath('/support/hb-supplier-order/'), true)
assert.equal(isPublicAppPath('/support/hb-supplier-ordering'), false)
assert.equal(isPublicAppPath('/support/faq'), true)
assert.equal(isPublicAppPath('/support/faq/'), true)
assert.equal(isPublicAppPath('/support/faqs'), false)
assert.equal(isPublicAppPath('/r/7K2M9QXD4TBH'), true)
assert.equal(isPublicAppPath('/r/7K2M9QXD4TBH/'), true)
assert.equal(isPublicAppPath('/r'), false)
assert.equal(isPublicAppPath('/receipts/7K2M9QXD4TBH'), false)
assert.equal(isPublicAppPath('/'), false)
assert.equal(isPublicAppPath('/shop'), false)

assert.equal(BROWSER_EXTENSION_PRIVACY_POLICY.language, 'en')
assert.equal(BROWSER_EXTENSION_PRIVACY_POLICY.publicUrl, 'https://hotbargain.vip/privacy/browser-extension')
assert.equal(BROWSER_EXTENSION_PRIVACY_POLICY.sections.length >= 8, true)

const documentMetadata = {
  documentElement: { lang: 'en-AU' },
  title: 'Hot Bargain Admin',
}
const restoreDocumentMetadata = preserveMobilePrivacyDocumentMetadata(documentMetadata)

updateMobilePrivacyDocumentMetadata(documentMetadata, 'zh', '移动应用隐私政策')
assert.equal(documentMetadata.documentElement.lang, 'zh-CN')
assert.equal(documentMetadata.title, '移动应用隐私政策 | Hot Bargain')

updateMobilePrivacyDocumentMetadata(documentMetadata, 'en', 'Mobile App Privacy Policy')
assert.equal(documentMetadata.documentElement.lang, 'en')
assert.equal(documentMetadata.title, 'Mobile App Privacy Policy | Hot Bargain')

restoreDocumentMetadata()
assert.equal(documentMetadata.documentElement.lang, 'en-AU')
assert.equal(documentMetadata.title, 'Hot Bargain Admin')

console.log('mobile privacy public route tests passed')
