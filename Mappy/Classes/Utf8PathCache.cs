using System;
using System.Text;

namespace Mappy.Classes;

/// <summary>
/// 依遊戲 Utf8String 的原始位元組快取解碼結果；位元組相同時 <see cref="Text"/> 與 <c>Utf8String.ToString()</c>、
/// <see cref="TexPath"/> 與 <c>$"{ToString()}.tex"</c> 逐字相同。鍵就是整段路徑位元組，地圖、圖層、語言資料夾
/// 只要改了路徑就會換掉。只在繪製（框架）執行緒使用。
/// </summary>
internal sealed class Utf8PathCache
{
    private byte[] key = [];

    public string Text { get; private set; } = string.Empty;

    public string TexPath { get; private set; } = ".tex";

    public Utf8PathCache Update(ReadOnlySpan<byte> raw)
    {
        if (raw.SequenceEqual(key)) return this;

        var text = Encoding.UTF8.GetString(raw);
        Text = text;
        TexPath = $"{text}.tex";
        key = raw.ToArray();
        return this;
    }
}
