using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace Mappy.Classes;

/// <summary>
/// 「地圖視窗裡的游標位置」→「遊戲世界座標（X/Z）」的換算。
/// 兩份算法刻意都留著：插旗走遊戲的 Selected* 欄位（行為完全沒有變動），自動移動只用 Map 表自己算。
/// 🔴 因為那些欄位是否已經跟上 SelectedMapId 無法離線證明，猜錯的失敗形式是「靜靜地把人送到偏掉幾百碼的地方」。
/// </summary>
public static unsafe class MapCursorMath
{
    /// <summary>兩份算法差多少以上要寫診斷（世界座標單位，1 單位約等於 0.02 個地圖格）。</summary>
    private const float DivergenceThreshold = 1.0f;

    /// <summary>
    /// 同一個診斷最短的重印間隔。
    /// 🔴 右鍵選單開著的時候這支<b>每幀</b>都會被呼叫，不節流的話 log 會被同一行灌爆。
    /// </summary>
    private static readonly TimeSpan DivergenceLogInterval = TimeSpan.FromSeconds(10);

    private static DateTime lastDivergenceLogAt = DateTime.MinValue;

    /// <summary>
    /// 把游標的螢幕座標換成地圖貼圖上的像素座標（已經扣掉「地圖中心在 1024,1024」那個位移）。
    /// </summary>
    private static Vector2 GetTextureOffsetFromCenter(Vector2 cursorScreenPosition, Vector2 mapDrawOffset)
    {
        var textureClickLocation = (cursorScreenPosition - mapDrawOffset - System.MapRenderer.DrawPosition) / MapRenderer.MapRenderer.Scale;

        return textureClickLocation - DrawHelpers.GetMapCenterOffsetVector();
    }

    /// <summary>
    /// 「放置旗標」自古以來用的算法：完全走 <c>AgentMap</c> 的 Selected* 欄位。
    /// </summary>
    /// <remarks>⚠️ 這支是為了<b>不改變既有行為</b>而保留的，新功能請用
    /// <see cref="TryGetCursorWorldPosition"/>。</remarks>
    public static Vector2 GetCursorWorldPositionFromAgent(Vector2 cursorScreenPosition, Vector2 mapDrawOffset)
    {
        var centered = GetTextureOffsetFromCenter(cursorScreenPosition, mapDrawOffset);

        return centered / DrawHelpers.GetMapScaleFactor() + DrawHelpers.GetRawMapOffsetVector();
    }

    /// <summary>
    /// 把游標位置換成世界座標，<b>只用 <c>Map</c> 表的靜態資料</b>。
    /// 換不出來時回 <see langword="false"/>，<b>而且不會給一個看起來很正常的 0</b>——
    /// 呼叫端要把這次點擊整個放棄，不是拿 <c>(0, 0)</c> 去走。
    /// </summary>
    public static bool TryGetCursorWorldPosition(Vector2 cursorScreenPosition, Vector2 mapDrawOffset, out Vector2 worldPosition)
    {
        worldPosition = default;

        var agent = AgentMap.Instance();
        if (agent is null) return false;

        var mapId = agent->SelectedMapId;
        if (mapId is 0) return false;

        var map = Service.DataManager.GetExcelSheet<Map>().GetRowOrDefault(mapId);
        if (map is null) return false;

        // SizeFactor 進到公式裡是分母，0 會算出無限大。
        var sizeFactor = map.Value.SizeFactor;
        if (sizeFactor is 0) return false;

        var centered = GetTextureOffsetFromCenter(cursorScreenPosition, mapDrawOffset);
        if (!float.IsFinite(centered.X) || !float.IsFinite(centered.Y)) return false;

        // 🔴 offset 要取負號：Lumina Map.OffsetX 與 AgentMap.SelectedOffsetX 符號相反。
        var fromSheet = centered * 100.0f / sizeFactor - new Vector2(map.Value.OffsetX, map.Value.OffsetY);

        if (!float.IsFinite(fromSheet.X) || !float.IsFinite(fromSheet.Y)) return false;

        LogDivergenceIfAny(mapId, cursorScreenPosition, mapDrawOffset, fromSheet);

        worldPosition = fromSheet;
        return true;
    }

    /// <remarks>
    /// 📌 用 <c>Information</c> 是刻意的——使用者跑的是 LogLevel 1，<c>Debug</c> 收得到但單檔數十萬行會淹沒，
    /// 而這一行正是「請把 log 貼給我」時唯一有價值的證據。
    /// </remarks>
    private static void LogDivergenceIfAny(uint mapId, Vector2 cursorScreenPosition, Vector2 mapDrawOffset, Vector2 fromSheet)
    {
        var agent = AgentMap.Instance();
        if (agent is null) return;

        // AgentMap 那一份的 scale 是分母，0 就不用比了（也代表那份根本不能用）。
        if (agent->SelectedMapSizeFactorFloat is 0.0f) return;

        var fromAgent = GetCursorWorldPositionFromAgent(cursorScreenPosition, mapDrawOffset);
        if (!float.IsFinite(fromAgent.X) || !float.IsFinite(fromAgent.Y)) return;

        var difference = Vector2.Distance(fromSheet, fromAgent);
        if (difference <= DivergenceThreshold) return;

        var now = DateTime.UtcNow;
        if (now - lastDivergenceLogAt < DivergenceLogInterval) return;
        lastDivergenceLogAt = now;

        Service.Log.Information(
            $"[Mappy] 座標換算兩法不一致：Map 表算出 ({fromSheet.X:F1}, {fromSheet.Y:F1})、" +
            $"AgentMap 算出 ({fromAgent.X:F1}, {fromAgent.Y:F1})，相差 {difference:F1}。" +
            $"SelectedMapId={mapId}、SelectedTerritoryId={agent->SelectedTerritoryId}、" +
            $"SelectedOffset=({agent->SelectedOffsetX}, {agent->SelectedOffsetY})、" +
            $"SelectedMapSizeFactorFloat={agent->SelectedMapSizeFactorFloat:F3}。" +
            "「移動到這裡」採用的是 Map 表那一份。");
    }
}
