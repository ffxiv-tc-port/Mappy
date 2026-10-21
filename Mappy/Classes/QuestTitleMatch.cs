using System;
using System.Collections.Generic;

namespace Mappy.Classes;

/// <summary>
/// 地圖標題只有任務／理符名稱時，把名稱對回唯一的目標地圖。
/// 同名多筆時不取第一筆：先看遊戲給的 ID 提示，再看是否全部指向同一張地圖，都不成立就回「分不出」。
/// 不碰 Dalamud：離線探針直接編這個檔。
/// </summary>
public static class QuestTitleMatch
{
    private const char QuestIconA = (char)0xE0BE;
    private const char QuestIconB = (char)0xE0BF;

    public enum Outcome { None, Unique, Ambiguous }

    public readonly record struct Candidate(uint Id, uint MapId);

    /// <summary>
    /// 去掉名稱開頭的任務圖示（私用區 U+E0BE／U+E0BF）與其後的空白。
    /// 台服 Quest.Name 有上千列帶這個圖示；地圖標題帶不帶不確定，兩邊都剝才不會因此比不中。
    /// </summary>
    public static ReadOnlySpan<char> StripQuestIcon(ReadOnlySpan<char> name)
    {
        var start = 0;
        while (start < name.Length && name[start] is QuestIconA or QuestIconB)
            start++;
        return start == 0 ? name : name[start..].TrimStart();
    }

    public static bool NameEquals(string sheetName, string title)
        => StripQuestIcon(sheetName).Equals(StripQuestIcon(title), StringComparison.OrdinalIgnoreCase);

    /// <param name="hintMatches">遊戲給的 ID 提示是否指向這個候選；null＝沒有提示。</param>
    public static Outcome Pick(IReadOnlyList<Candidate> hits, Func<uint, bool>? hintMatches, out uint mapId)
    {
        mapId = 0;
        if (hits.Count == 0)
            return Outcome.None;
        if (hits.Count == 1)
        {
            mapId = hits[0].MapId;
            return Outcome.Unique;
        }

        if (hintMatches != null)
        {
            var hinted = -1;
            for (var i = 0; i < hits.Count; i++)
            {
                if (!hintMatches(hits[i].Id))
                    continue;
                if (hinted >= 0)
                {
                    hinted = -1;
                    break;
                }
                hinted = i;
            }
            if (hinted >= 0)
            {
                mapId = hits[hinted].MapId;
                return Outcome.Unique;
            }
        }

        for (var i = 1; i < hits.Count; i++)
        {
            if (hits[i].MapId != hits[0].MapId)
                return Outcome.Ambiguous;
        }
        mapId = hits[0].MapId;
        return Outcome.Unique;
    }
}
