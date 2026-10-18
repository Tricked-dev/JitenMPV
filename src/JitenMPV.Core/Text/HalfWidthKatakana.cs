using System.Text;

namespace JitenMPV.Core.Text;

/// Narrower than NFKC on purpose: that would also fold full-width Latin and digits to ASCII.
public static class HalfWidthKatakana
{
    private const char First = '\uFF61';
    private const char Last = '\uFF9F';
    private const char VoicedMark = '\uFF9E';
    private const char SemiVoicedMark = '\uFF9F';

    /// Indexed from U+FF61: ｡｢｣､･ｦｧｨｩｪｫｬｭｮｯｰｱ…ﾝﾞﾟ.
    private const string FullWidth =
        "。「」、・ヲァィゥェォャュョッー"
        + "アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン"
        + "\u309B\u309C";

    /// Returns the same instance when there is nothing to convert.
    public static string ToFullWidth(string text)
    {
        var start = text.AsSpan().IndexOfAnyInRange(First, Last);
        if (start < 0) return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, start);

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c is < First or > Last)
            {
                sb.Append(c);
                continue;
            }

            if (c is VoicedMark or SemiVoicedMark)
            {
                // A mark with no half-width kana before it has nothing to combine with.
                sb.Append(FullWidth[c - First]);
                continue;
            }

            var kana = FullWidth[c - First];
            if (i + 1 < text.Length && text[i + 1] is VoicedMark or SemiVoicedMark)
            {
                var voiced = text[i + 1] == VoicedMark;
                sb.Append(Compose(kana, voiced) is { } composed
                    ? composed
                    : $"{kana}{(voiced ? '\u3099' : '\u309A')}");
                i++;
                continue;
            }

            sb.Append(kana);
        }

        return sb.ToString();
    }

    private static char? Compose(char kana, bool voiced) => kana switch
    {
        'ウ' when voiced => 'ヴ',
        'ワ' when voiced => 'ヷ',
        'ヲ' when voiced => 'ヺ',
        'カ' or 'キ' or 'ク' or 'ケ' or 'コ'
            or 'サ' or 'シ' or 'ス' or 'セ' or 'ソ'
            or 'タ' or 'チ' or 'ツ' or 'テ' or 'ト' when voiced => (char)(kana + 1),
        'ハ' or 'ヒ' or 'フ' or 'ヘ' or 'ホ' => (char)(kana + (voiced ? 1 : 2)),
        _ => null
    };
}
