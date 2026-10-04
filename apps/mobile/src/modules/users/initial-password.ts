// 店长新建店员时的初始密码：只用于首次登录，员工登录后会被要求改成自己的密码。

/** 去掉 0/O、1/l/I 等容易看错的字符，方便店长当面念给员工或手抄。 */
const LETTERS = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
const DIGITS = "23456789";
const ALPHABET = LETTERS + DIGITS;
const GROUP_LENGTH = 4;

/** 返回指定长度的随机字节；生产传入加密安全随机源（expo-crypto），测试可注入固定序列。 */
export type RandomBytes = (count: number) => Uint8Array;

function pick(source: string, byte: number) {
  // 256 不是字母表长度的整数倍，取模有极小偏差；初始密码一次性使用且会被强制修改，可以接受。
  return source[byte % source.length];
}

/**
 * 生成形如 Hb7k-2qMx 的 8 位初始密码（含一个连字符，共 9 个字符）。
 * 保证至少一个字母和一个数字，满足后端 6–100 位的长度要求。
 */
export function generateInitialPassword(randomBytes: RandomBytes): string {
  const bytes = randomBytes(GROUP_LENGTH * 2 + 2);
  const chars = Array.from({ length: GROUP_LENGTH * 2 }, (_, index) => pick(ALPHABET, bytes[index]));
  // 用最后两个随机字节决定强制放置字母和数字的位置，避免全字母或全数字。
  const letterIndex = bytes[GROUP_LENGTH * 2] % chars.length;
  let digitIndex = bytes[GROUP_LENGTH * 2 + 1] % chars.length;
  if (digitIndex === letterIndex) digitIndex = (digitIndex + 1) % chars.length;
  if (!/[a-zA-Z]/.test(chars.join(""))) chars[letterIndex] = pick(LETTERS, bytes[letterIndex]);
  if (!/[0-9]/.test(chars.join(""))) chars[digitIndex] = pick(DIGITS, bytes[digitIndex]);
  return `${chars.slice(0, GROUP_LENGTH).join("")}-${chars.slice(GROUP_LENGTH).join("")}`;
}

/** 复制给员工的登录信息文本：只含用户名与初始密码，不含任何个人资料。 */
export function buildLoginCredentialText(input: {
  heading: string;
  usernameLabel: string;
  passwordLabel: string;
  username: string;
  password: string;
  footer: string;
}) {
  return [
    input.heading,
    `${input.usernameLabel}: ${input.username}`,
    `${input.passwordLabel}: ${input.password}`,
    input.footer,
  ].join("\n");
}
