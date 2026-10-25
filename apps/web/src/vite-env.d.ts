/// <reference types="vite/client" />

declare const __CENTER_LOG_BUILD_CONFIGURED__: boolean

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL: string
  readonly VITE_DEV_PROXY_TARGET: string
  readonly VITE_CENTER_LOG_PROJECT?: string
  readonly VITE_CENTER_LOG_KEY?: string
  readonly VITE_CENTER_LOG_ENVIRONMENT?: string
  readonly VITE_CENTER_LOG_SERVICE_NAME?: string
  /** 考勤工时与工资 / 审核工作台开发期 mock 开关（=1 时走本地造数，后端合入前用）。 */
  readonly VITE_ATTENDANCE_MOCK?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
