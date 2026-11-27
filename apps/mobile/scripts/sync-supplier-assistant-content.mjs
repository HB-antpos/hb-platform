#!/usr/bin/env node
// 把大屏收银 App（apps/pos-ipad）的「订货助手」网页注入逻辑同步到员工移动端。
//
// 中文注释：
// 1. 插件源码（apps/supplier-order-extension/src）改了：先跑 iPad 的生成脚本
//    node apps/pos-ipad/scripts/build-supplier-assistant-content.mjs，再跑本脚本。
// 2. 只是逐字拷贝；src/modules/supplier-assistant/supplier-assistant-shared-sync.test.ts 校验两端一致。
import { copyFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const mobileDir = path.resolve(here, '../src/modules/supplier-assistant');
const ipadDir = path.resolve(here, '../../pos-ipad/src/features/supplier-assistant');

const SHARED_FILES = [
  'supplier-assistant-background.ts',
  'content/chrome-shim.js',
  'content/entry.js',
  'generated/supplier-assistant-content.generated.ts',
];

for (const file of SHARED_FILES) {
  const target = path.join(mobileDir, file);
  await mkdir(path.dirname(target), { recursive: true });
  await copyFile(path.join(ipadDir, file), target);
  console.log(`已同步 ${file}`);
}
