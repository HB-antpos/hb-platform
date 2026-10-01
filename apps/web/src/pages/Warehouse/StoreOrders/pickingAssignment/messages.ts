import { registerPageMessages } from '../../../../i18n/registerPageMessages'

import en from './messages.en.json'
import zh from './messages.zh.json'

// 拣货派单与分单打印的文案随订单页代码块懒加载，不进入首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh, en })
