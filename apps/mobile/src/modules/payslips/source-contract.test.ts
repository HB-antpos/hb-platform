// 源码契约：守住工资单页几条不能悄悄改掉的约束（接口路径、权限闸门、敏感文件处理、不新增原生依赖）。
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(directory, "../../..");
const read = (name: string) => readFileSync(join(directory, name), "utf8");
const productionFiles = readdirSync(directory).filter((name) => /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) && name !== "fixtures.ts");

const apiCore = read("api-core.ts");
const pdf = read("pdf.ts");
const pdfCore = read("pdf-core.ts");
const detailScreen = read("PayslipDetailScreen.tsx");
const emailSheet = read("PayslipEmailSheet.tsx");
const listScreen = read("PayslipsListScreen.tsx");

// 1) 接口路径与后端 api/react/v1/myob/payslips 一致；所有请求带 no-store
assert.match(apiCore, /"\/react\/v1\/myob\/payslips"/);
for (const segment of ["companies", "detail", "hours", "email-target", "email", "pdf"]) {
  assert.match(apiCore, new RegExp(`\\$\\{PAYSLIPS_BASE_PATH\\}/${segment}\``), `缺少 ${segment} 接口`);
}
assert.match(apiCore, /"Cache-Control": "no-store"/);
assert.match(apiCore, /responseType: "arraybuffer"/);
assert.match(apiCore, /%PDF|"%PDF"/, "下载后要校验 PDF 魔数");

// 2) 不新增原生依赖：只用已装的 expo-file-system(legacy) / expo-sharing / expo-intent-launcher，没有 WebView / PDF 查看库
const packageJson = JSON.parse(readFileSync(join(mobileRoot, "package.json"), "utf8")) as { dependencies: Record<string, string> };
for (const name of productionFiles) {
  const source = read(name);
  for (const match of source.matchAll(/from "([^"@./][^"]*|@[^"/]+\/[^"]+)"|import\("([^"]+)"\)/g)) {
    const specifier = (match[1] ?? match[2]).split("/").slice(0, match[1]?.startsWith("@") || match[2]?.startsWith("@") ? 2 : 1).join("/");
    if (specifier.startsWith("@/") || specifier === "react" || specifier === "react-native") continue;
    assert.ok(specifier in packageJson.dependencies, `${name} 引用了未登记的依赖 ${specifier}`);
  }
}
assert.doesNotMatch(productionFiles.map(read).join("\n"), /react-native-webview|react-native-pdf|expo-print|expo-web-browser/);
assert.match(pdf, /expo-file-system\/legacy/);
assert.match(pdf, /expo-sharing/);

// 3) 敏感文件：只写缓存目录，用完（含失败）立即删除；不写文档目录
assert.match(pdfCore, /finally \{[\s\S]*fileSystem\.remove\(fileUri\)/);
assert.doesNotMatch(pdf + pdfCore, /documentDirectory/);

// 4) 按钮闸门：后端返回的 canDownloadPdf / canSendEmail 才渲染对应入口；邮件弹层只在 canSendEmail 时挂载
assert.match(detailScreen, /detail\.canDownloadPdf \? <ViewPdfRow/);
assert.match(detailScreen, /detail\.canDownloadPdf \? \(\s*<Button/);
assert.match(detailScreen, /detail\.canSendEmail \? \(\s*<Button/);
assert.match(detailScreen, /detail\.canSendEmail \? \(\s*<PayslipEmailSheet/);
assert.match(detailScreen, /showBar = Boolean\(detail && \(detail\.canDownloadPdf \|\| detail\.canSendEmail\)\)/);

// 5) 邮件：改收件人必须勾选核对（发送按钮由 canSubmitEmail 控制）；失败时弹层不关闭，保留内容；成功才回调父页面
assert.match(emailSheet, /canSubmitEmail\(evaluation, confirmed/);
assert.match(emailSheet, /catch \(error\) \{\s*if \(mounted\.current\) setErrorText/);
assert.match(emailSheet, /onSent\(/);
assert.match(emailSheet, /dismissable=\{!busy\}/);

// 6) 入口守卫：登录、非审核壳、两个查看权限任一；先过守卫再渲染内容
assert.match(listScreen, /if \(blocked\) return <PayslipMessage/);
assert.match(detailScreen, /if \(blocked\) return <PayslipMessage/);
assert.match(read("use-payslip-guard.ts"), /iosReviewOfflineGuardActive/);
assert.match(read("access.ts"), /ViewManagedStore\) \|\| hasPermission\(PERMISSIONS\.Myob\.Payslip\.ViewAllStores/);

// 7) 不打印工资数据：生产代码没有 console.log / info / debug（pdf-core 仅允许一条 warn，且不含数据）
for (const name of productionFiles) {
  assert.doesNotMatch(read(name), /console\.(log|info|debug)/, `${name} 不应打印日志`);
}
assert.equal((pdfCore.match(/console\./g) ?? []).length, 1);

// 8) 路由文件存在：列表 + 明细，隐藏原生 header
for (const route of ["_layout.tsx", "index.tsx", "detail.tsx"]) {
  assert.ok(existsSync(join(mobileRoot, "app/(shell)/payslips", route)), `缺少路由 ${route}`);
}
assert.match(readFileSync(join(mobileRoot, "app/(shell)/payslips/_layout.tsx"), "utf8"), /headerShown: false/);

// 9) 英文界面不拼中文标点：状态行里的冒号 / 分隔都走 punctuation.ts
assert.match(listScreen, /labelColon\(language\)/);
assert.match(emailSheet, /labelColon\(language\)/);
for (const name of productionFiles) {
  assert.doesNotMatch(read(name).replace(/\/\/.*$/gm, "").replace(/\/\*[\s\S]*?\*\//g, ""), /[：（）、「」]/, `${name} 不应在代码字符串里硬编码中文标点`);
}
