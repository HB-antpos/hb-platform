import { spawnSync } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { build } from 'esbuild'

const tests = [
  {
    name: 'app-downloads-logic',
    entryPoint: 'src/pages/System/AppDownloads/logic.test.ts',
  },
  {
    name: 'mobile-app-build-service',
    entryPoint: 'src/services/mobileAppBuildService.test.ts',
    define: { 'import.meta.env': '{}' },
  },
  {
    name: 'app-update-policy-logic',
    entryPoint: 'src/pages/System/AppDownloads/appUpdatePolicyLogic.test.ts',
  },
  {
    name: 'app-update-policy-service',
    entryPoint: 'src/services/appUpdatePolicyService.test.ts',
    define: { 'import.meta.env': '{}' },
  },
  {
    name: 'app-update-policy-request-logic',
    entryPoint: 'src/pages/System/AppDownloads/appUpdatePolicyRequestLogic.test.ts',
  },
  {
    name: 'mobile-android-native-policy-logic',
    entryPoint: 'src/pages/System/AppDownloads/mobileAndroidNativePolicyLogic.test.ts',
  },
  {
    name: 'mobile-ota-policy-logic',
    entryPoint: 'src/pages/System/AppDownloads/mobileOtaPolicyLogic.test.ts',
  },
  {
    name: 'mobile-ota-policy-service',
    entryPoint: 'src/services/mobileOtaPolicyService.test.ts',
    define: { 'import.meta.env': '{}' },
  },
  {
    name: 'pos-handheld-update-policy-logic',
    entryPoint: 'src/pages/System/AppDownloads/posHandheldUpdatePolicyLogic.test.ts',
  },
  {
    name: 'pos-handheld-update-policy-service',
    entryPoint: 'src/services/posHandheldUpdatePolicyService.test.ts',
    define: { 'import.meta.env': '{}' },
  },
  {
    name: 'service-api-token-panel-logic',
    entryPoint: 'src/pages/System/AppDownloads/serviceApiTokenPanelLogic.test.ts',
  },
  {
    name: 'service-api-token-service',
    entryPoint: 'src/services/serviceApiTokenService.test.ts',
    define: { 'import.meta.env': '{}' },
  },
]

const outputDirectory = mkdtempSync(join(tmpdir(), 'hbweb-app-downloads-'))

try {
  for (const test of tests) {
    const outfile = join(outputDirectory, `${test.name}.mjs`)
    await build({
      entryPoints: [test.entryPoint],
      bundle: true,
      platform: 'node',
      format: 'esm',
      outfile,
      define: test.define,
    })

    const result = spawnSync(process.execPath, [outfile], {
      cwd: process.cwd(),
      stdio: 'inherit',
    })
    if (result.status !== 0) {
      throw new Error(`${test.entryPoint} 执行失败`)
    }
  }
} finally {
  // 关键位置：只清理本进程由 mkdtemp 创建的唯一目录，避免并行测试互相覆盖。
  rmSync(outputDirectory, { recursive: true, force: true })
}
