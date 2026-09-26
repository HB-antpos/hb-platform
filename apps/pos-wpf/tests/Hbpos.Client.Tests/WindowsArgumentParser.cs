using System.Runtime.InteropServices;

namespace Hbpos.Client.Tests;

/// <summary>
/// 用 Windows 自带的 CommandLineToArgvW 拆分命令行，验证传给更新窗口的参数能被原样取回。
/// </summary>
internal static class WindowsArgumentParser
{
    public static string[] Parse(string arguments)
    {
        // 前面补一个程序名，避免首个参数按程序路径的特殊规则解析。
        var pointer = CommandLineToArgvW("updater.exe " + arguments, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("CommandLineToArgvW failed.");
        }

        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!)
                .ToArray();
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
