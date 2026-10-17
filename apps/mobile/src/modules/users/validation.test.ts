import { extractApiErrorMessage } from "../../shared/api/error-message";
import { validatePasswordValue, validateStoreUserForm, validateUsername } from "./validation";

const messages: Record<string, string> = {
  "messages.emailInvalid": "Invalid email",
  "messages.passwordRequired": "Enter a password",
  "messages.passwordTooShort": "Password must be at least 6 characters",
  "messages.passwordTooLong": "Password must be 100 characters or fewer",
  "messages.usernameLength": "Username must be 3 to 50 characters",
  "messages.usernameRequired": "Enter a username",
};

function t(key: string) {
  return messages[key] ?? key;
}

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

const validForm = {
  username: "staff001",
  fullName: "Staff One",
  email: "staff@example.com",
  phone: "",
  status: true,
};

assertEqual(
  validateStoreUserForm({ ...validForm, email: "not-email" }, t),
  "Invalid email",
  "edit form rejects invalid email"
);

assertEqual(
  validateStoreUserForm({ ...validForm, username: "ab" }, t),
  "Username must be 3 to 50 characters",
  "edit form rejects short usernames"
);

assertEqual(
  validatePasswordValue("12345", t),
  "Password must be at least 6 characters",
  "reset password rejects short passwords"
);

assertEqual(
  extractApiErrorMessage(
    {
      response: {
        data: {
          success: false,
          message: "请求参数验证失败",
          details: {
            Password: {
              errors: ["密码长度必须在6-100个字符之间"],
            },
          },
        },
      },
    },
    "Fallback"
  ),
  "密码长度必须在6-100个字符之间",
  "api error extracts validation details"
);

assertEqual(
  extractApiErrorMessage(
    {
      response: {
        data: {
          success: false,
          message: "用户名已存在",
        },
      },
    },
    "Fallback"
  ),
  "用户名已存在",
  "api error extracts envelope message"
);

// 账号单字段校验（新增整页表单按字段显示错误）：与整表校验口径一致。
assertEqual(validateUsername("  ", t), "Enter a username", "账号必填");
assertEqual(validateUsername("ab", t), "Username must be 3 to 50 characters", "账号至少 3 个字符");
assertEqual(validateUsername("a".repeat(51), t), "Username must be 3 to 50 characters", "账号最多 50 个字符");
assertEqual(validateUsername(" emily.chen@gmail.com ", t), null, "合法账号（去首尾空白）");
