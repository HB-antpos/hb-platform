// 打包入口：先装好扩展接口替身，再运行插件原样的供应商页脚本。
// 生成脚本会把 hb-supplier-list 虚拟模块换成 list.js 的内容（包成 runSupplierList(chrome)）。
// hb-supplier-list 是生成脚本里的虚拟模块（list.js 包成函数），只在打包时存在。
// eslint-disable-next-line import/no-unresolved
import { runSupplierList } from 'hb-supplier-list';

import { createChromeShim } from './chrome-shim.js';

(function () {
  // 同一页面只装一次：SPA 内跳转或重复注入都不重复挂按钮。
  if (window.__hbSupplierAssistant) return;
  const { chrome, control } = createChromeShim(window, window.__hbSupplierAssistantInitialStorage || {});
  Object.defineProperty(window, '__hbSupplierAssistant', {
    value: control,
    configurable: false,
    enumerable: false,
    writable: false,
  });
  runSupplierList(chrome);
})();
