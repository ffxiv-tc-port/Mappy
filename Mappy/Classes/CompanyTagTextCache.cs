using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;

namespace Mappy.Classes;

/// <summary>
/// 部隊簡稱的原始位元組 → 與 Dalamud <c>ICharacter.CompanyTag.TextValue</c> 逐字相同的字串（未命中時呼叫的就是同一個
/// <c>SeString.Parse(span).TextValue</c>）。鍵＝第一個 0 之前的位元組（正是 Parse 吃進去的範圍）補 0 塞進 ulong，
/// 那段位元組裡沒有 0，所以不同內容不會撞鍵。只在繪製執行緒使用；不保存任何指標，只存位元組換算出的值。
/// </summary>
internal sealed class CompanyTagTextCache
{
    // 只是上限保險：同時看得到的部隊簡稱種類遠小於此，滿了整個清掉重建。
    private const int MaxEntries = 512;

    private readonly Dictionary<ulong, string> texts = [];

    public string GetText(ReadOnlySpan<byte> tag)
    {
        var end = tag.IndexOf((byte)0);
        var bytes = end < 0 ? tag : tag[..end];
        if (bytes.Length > sizeof(ulong)) return SeString.Parse(tag).TextValue;

        ulong key = 0;
        for (var index = 0; index < bytes.Length; index++) {
            key |= (ulong)bytes[index] << (8 * index);
        }

        if (texts.TryGetValue(key, out var text)) return text;
        if (texts.Count >= MaxEntries) texts.Clear();

        text = SeString.Parse(tag).TextValue;
        texts.Add(key, text);
        return text;
    }
}
