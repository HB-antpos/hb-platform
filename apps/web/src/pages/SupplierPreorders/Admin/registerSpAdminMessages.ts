// 管理端页面级文案随页面代码块懒注册，不进首屏 i18n 包；各页面与共用组件都 import 本文件，重复注册幂等。
import { registerPageMessages } from '../../../i18n/registerPageMessages'

import spAdminMessagesEn from './spAdminMessages.en.json'
import spAdminMessagesZh from './spAdminMessages.zh.json'

registerPageMessages({ zh: spAdminMessagesZh, en: spAdminMessagesEn })
