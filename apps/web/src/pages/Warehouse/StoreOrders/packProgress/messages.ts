import { registerPageMessages } from '../../../../i18n/registerPageMessages'

import en from './messages.en.json'
import zh from './messages.zh.json'

// 订单装箱情况与完成出库提醒的文案随懒加载代码块注册，不进入首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh, en })
