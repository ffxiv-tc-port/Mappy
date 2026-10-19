using System;

namespace Mappy.Classes;

/// <summary>
/// 每幀都要的顯示文字先格式化進呼叫端的堆疊緩衝區，內容與上一次逐字相同就交回同一個字串，變了才配置新的。
/// 只在繪製執行緒使用。
/// </summary>
internal sealed class ReusedText
{
    private string text = string.Empty;

    public string Get(ReadOnlySpan<char> chars)
    {
        if (!chars.SequenceEqual(text)) text = chars.ToString();
        return text;
    }
}
