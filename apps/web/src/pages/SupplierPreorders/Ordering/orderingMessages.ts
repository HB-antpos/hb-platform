// 下单页文案按页懒注册（列表页与下单页共用一份），不进首屏 i18n 包。
import { registerPageMessages } from '../../../i18n/registerPageMessages'

import orderingMessagesEn from './orderingMessages.en.json'
import orderingMessagesZh from './orderingMessages.zh.json'

registerPageMessages({ zh: orderingMessagesZh, en: orderingMessagesEn })
