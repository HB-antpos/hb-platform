// 下单人员授权页的页面级文案随页面懒注册，不进首屏 i18n 包；重复注册幂等。
import { registerPageMessages } from '../../../i18n/registerPageMessages'

import spOrderersMessagesEn from './spOrderersMessages.en.json'
import spOrderersMessagesZh from './spOrderersMessages.zh.json'

registerPageMessages({ zh: spOrderersMessagesZh, en: spOrderersMessagesEn })
