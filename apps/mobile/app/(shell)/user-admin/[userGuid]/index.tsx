// 旧「用户详情」路由：已并入统一人员详情 /users/[userGuid]（userGuid 为 "new" 时转去 /users/new），这里只做重定向。
export { LegacyDetailRedirect as default } from "@/modules/people/detail/legacy-redirects";
