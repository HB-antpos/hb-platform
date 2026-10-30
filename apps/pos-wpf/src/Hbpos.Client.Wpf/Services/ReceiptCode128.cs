namespace Hbpos.Client.Wpf.Services;

/// <summary>Code 128 的一段条或空，<see cref="Modules"/> 为占用的模块数（1~4）。</summary>
public readonly record struct ReceiptCode128Run(bool IsBar, int Modules);

/// <summary>Code 128 编码结果。<see cref="ModuleCount"/> 不含两侧静区。</summary>
public sealed record ReceiptCode128Encoding(IReadOnlyList<ReceiptCode128Run> Runs, int ModuleCount);

/// <summary>
/// Code 128（B/C 两个字符集）编码器，移植自 <c>packages/pos-receipt-core/src/features/receipts/receipt-code128.ts</c>。
/// 用动态规划在 B/C 集合之间选码字数最少的组合：纯数字长段走 C 集合两位压成一个码字，
/// 所以 GUID 里的数字段会变短，但扫码结果仍是原始字符串。
/// 纯逻辑，供位图降级渲染使用；原生 SDK 条码由打印机固件自己编码，不经过这里。
/// </summary>
public static class ReceiptCode128
{
    private const int StartB = 104;
    private const int StartC = 105;
    private const int CodeB = 100;
    private const int CodeC = 99;
    private const int Stop = 106;

    /// <summary>每个码字 6 位「条空条空条空」宽度；终止符 7 位（末尾多一条）。</summary>
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222",
        "122213", "122312", "132212", "221213", "221312", "231212",
        "112232", "122132", "122231", "113222", "123122", "123221",
        "223211", "221132", "221231", "213212", "223112", "312131",
        "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321",
        "112313", "132113", "132311", "211313", "231113", "231311",
        "112133", "112331", "132131", "113123", "113321", "133121",
        "313121", "211331", "231131", "213113", "213311", "213131",
        "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124",
        "121421", "141122", "141221", "112214", "112412", "122114",
        "122411", "142112", "142211", "241211", "221114", "413111",
        "241112", "134111", "111242", "121142", "121241", "114212",
        "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113",
        "114311", "411113", "411311", "113141", "114131", "311141",
        "411131", "211412", "211214", "211232", "2331112"
    ];

    /// <summary>字符串能否用 B 集合表示：非空且全部是可打印 ASCII（0x20~0x7E）。</summary>
    public static bool CanEncode(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.All(ch => ch is >= ' ' and <= '~');
    }

    /// <summary>编码为条空序列；内容为空或含非可打印 ASCII 时抛 <see cref="ArgumentException"/>。</summary>
    public static ReceiptCode128Encoding Encode(string value)
    {
        if (!CanEncode(value))
        {
            throw new ArgumentException("Code 128 value must be non-empty printable ASCII.", nameof(value));
        }

        // memo[index, set]：从 index 开始、当前处于 set（0=B，1=C）时，剩余部分的最短码字序列。
        var memo = new int[value.Length + 1, 2][];
        int[] Solve(int index, int set)
        {
            if (index == value.Length)
            {
                return [];
            }

            var cached = memo[index, set];
            if (cached is not null)
            {
                return cached;
            }

            int[]? best = null;
            void Consider(int[] candidate)
            {
                // 严格小于才替换：与 TS 版 reduce 一致，码字数相同时保留先加入的候选。
                if (best is null || candidate.Length < best.Length)
                {
                    best = candidate;
                }
            }

            if (set == 0)
            {
                Consider([value[index] - 32, .. Solve(index + 1, 0)]);
                if (IsDigitPair(value, index))
                {
                    Consider([CodeC, PairValue(value, index), .. Solve(index + 2, 1)]);
                }
            }
            else
            {
                if (IsDigitPair(value, index))
                {
                    Consider([PairValue(value, index), .. Solve(index + 2, 1)]);
                }

                Consider([CodeB, .. Solve(index, 0)]);
            }

            memo[index, set] = best!;
            return best!;
        }

        var fromB = Solve(0, 0);
        var fromC = Solve(0, 1);
        // 起始符选码字更少的一边；相等时偏向 B（与 TS 版一致）。
        var (startCode, body) = fromC.Length < fromB.Length ? (StartC, fromC) : (StartB, fromB);

        var dataCodes = new List<int>(body.Length + 3) { startCode };
        dataCodes.AddRange(body);

        // 校验和 = (起始符 + Σ 码字 × 位置) mod 103，位置从 1 起算。
        var checksum = 0;
        for (var i = 0; i < dataCodes.Count; i++)
        {
            checksum += i == 0 ? dataCodes[i] : dataCodes[i] * i;
        }

        checksum %= 103;

        var runs = new List<ReceiptCode128Run>();
        var moduleCount = 0;
        foreach (var code in dataCodes.Append(checksum).Append(Stop))
        {
            foreach (var widthChar in Patterns[code])
            {
                var modules = widthChar - '0';
                // 条空交替，第一段永远是条。
                runs.Add(new ReceiptCode128Run(runs.Count % 2 == 0, modules));
                moduleCount += modules;
            }
        }

        return new ReceiptCode128Encoding(runs, moduleCount);
    }

    private static bool IsDigitPair(string value, int index)
    {
        return index + 1 < value.Length && value[index] is >= '0' and <= '9' && value[index + 1] is >= '0' and <= '9';
    }

    private static int PairValue(string value, int index)
    {
        return (value[index] - '0') * 10 + (value[index + 1] - '0');
    }
}
