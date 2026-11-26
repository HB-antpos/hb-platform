#!/usr/bin/env node
// 把浏览器订货助手插件的供应商页脚本（list.js + lib/*）连同扩展接口替身打成一段脚本，
// 写进 src/features/supplier-assistant/generated/supplier-assistant-content.generated.ts，供 App 内网页窗口注入。
//
// 中文注释：
// 1. 插件源码改了要重新跑：node apps/pos-ipad/scripts/build-supplier-assistant-content.mjs
// 2. 生成文件里记了全部源文件的哈希；测试只重算哈希比对，CI 不需要 esbuild。
// 3. list.js 里的 import(chrome.runtime.getURL('lib/x.js')) 在这里换成静态路径，esbuild 会把它们打进同一段脚本。
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const appRoot = path.resolve(here, '..');
export const extensionRoot = path.resolve(appRoot, '../supplier-order-extension/src');
export const contentDir = path.join(appRoot, 'src/features/supplier-assistant/content');
export const outputFile = path.join(
  appRoot,
  'src/features/supplier-assistant/generated/supplier-assistant-content.generated.ts',
);

/** 参与打包的全部源文件（按相对路径排序后算哈希，顺序稳定）。 */
export async function listSourceFiles() {
  const { readdir } = await import('node:fs/promises');
  const libFiles = (await readdir(path.join(extensionRoot, 'lib')))
    .filter((name) => name.endsWith('.js'))
    .map((name) => path.join(extensionRoot, 'lib', name));
  return [
    path.join(extensionRoot, 'content/list.js'),
    ...libFiles,
    path.join(contentDir, 'chrome-shim.js'),
    path.join(contentDir, 'entry.js'),
  ].sort();
}

export async function computeSourceHash() {
  const hash = createHash('sha256');
  for (const file of await listSourceFiles()) {
    hash.update(path.relative(appRoot, file).split(path.sep).join('/'));
    hash.update('\0');
    hash.update(await readFile(file));
    hash.update('\0');
  }
  return hash.digest('hex');
}

export function rewriteListSource(source) {
  const rewritten = source.replace(
    /import\(chrome\.runtime\.getURL\('lib\/([a-z0-9-]+\.js)'\)\)/g,
    (_, file) => `import(${JSON.stringify(path.join(extensionRoot, 'lib', file))})`,
  );
  if (/chrome\.runtime\.getURL\(/.test(rewritten)) {
    throw new Error('list.js 里还有未替换的 chrome.runtime.getURL，请更新生成脚本');
  }
  return `export function runSupplierList(chrome) {\n${rewritten}\n}\n`;
}

async function build() {
  const esbuild = await import('esbuild');
  const listSource = await readFile(path.join(extensionRoot, 'content/list.js'), 'utf8');
  const result = await esbuild.build({
    entryPoints: [path.join(contentDir, 'entry.js')],
    bundle: true,
    write: false,
    format: 'iife',
    target: ['es2020', 'safari15'],
    minify: true,
    legalComments: 'none',
    plugins: [
      {
        name: 'hb-supplier-list',
        setup(builder) {
          builder.onResolve({ filter: /^hb-supplier-list$/ }, () => ({
            path: 'hb-supplier-list',
            namespace: 'hb-supplier-list',
          }));
          builder.onLoad({ filter: /.*/, namespace: 'hb-supplier-list' }, () => ({
            contents: rewriteListSource(listSource),
            loader: 'js',
            resolveDir: path.join(extensionRoot, 'content'),
          }));
        },
      },
    ],
  });
  const script = result.outputFiles[0].text;
  const sourceHash = await computeSourceHash();
  const extensionVersion = JSON.parse(
    await readFile(path.join(extensionRoot, '../package.json'), 'utf8'),
  ).version;
  const output = [
    '// 自动生成，请勿手改：node apps/pos-ipad/scripts/build-supplier-assistant-content.mjs',
    '// 内容 = 浏览器订货助手插件供应商页脚本（list.js + lib/*）+ App 内扩展接口替身。',
    `export const SUPPLIER_ASSISTANT_EXTENSION_VERSION = ${JSON.stringify(extensionVersion)};`,
    `export const SUPPLIER_ASSISTANT_CONTENT_SOURCE_HASH = ${JSON.stringify(sourceHash)};`,
    `export const SUPPLIER_ASSISTANT_CONTENT_SCRIPT = ${JSON.stringify(script)};`,
    '',
  ].join('\n');
  await writeFile(outputFile, output);
  console.log(`已生成 ${path.relative(process.cwd(), outputFile)}（${script.length} 字符，hash ${sourceHash.slice(0, 12)}）`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await build();
}
