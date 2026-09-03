using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace Mappy.Classes;

/// <summary>
/// 「地圖視窗裡的游標位置」→「遊戲世界座標（X/Z）」的換算。
/// </summary>
/// <remarks>
/// <para>
/// 這裡有兩份算法，<b>刻意兩份都留著</b>：
/// </para>
/// <list type="number">
/// <item><see cref="FromAgent"/>：拿 <c>AgentMap</c> 的 <c>SelectedOffsetX/Y</c> 與
/// <c>SelectedMapSizeFactorFloat</c> 算。這是「放置旗標」自古以來用的那一份，
/// <b>行為完全沒有變動</b>。</item>
/// <item><see cref="FromMapSheet"/>：拿 <c>Map</c> 表以 <c>AgentMap.SelectedMapId</c>
/// 查 <c>OffsetX</c>／<c>OffsetY</c>／<c>SizeFactor</c> 自己算。新的「移動到這裡」用這一份。</item>
/// </list>
/// <para>
/// 🔴 <b>為什麼新功能不敢用 AgentMap 那一份</b>：<c>SelectedOffsetX/Y</c> 與
/// <c>SelectedMapSizeFactorFloat</c> 是「遊戲已經切到選中的那張圖」之後才會被寫成新值的欄位。
/// Mappy 允許在不換區的情況下瀏覽別的地圖，這時那三個欄位是否已經跟上 <c>SelectedMapId</c>
/// <b>無法離線證明</b>，而猜錯的失敗形式是「靜靜地把人送到偏掉幾百碼的地方」——不會報錯。
/// 插旗只是畫個圖示，偏了看得出來；自動移動偏了會讓角色跑去別的地方。
/// 所以自動移動一律用 <c>Map</c> 表的靜態資料自己算。
/// </para>
/// <para>
/// 📌 <b>兩份算法在數學上是同一條。</b>Dalamud <c>MapUtil.GetMapCoordinates</c> 逐字寫著
/// 「Excel 的 <c>Map</c> 與 <c>AgentMap</c> 的 offset 符號相反」（附上游 issue 1029），
/// 呼叫時傳的是 <c>-agentMap-&gt;CurrentOffsetX</c>；而 <c>SelectedMapSizeFactorFloat</c>
/// 就是 <c>SizeFactor / 100</c>。代入之後：
/// <c>(pixel - 1024) / (SizeFactor / 100) + SelectedOffset</c>
/// ≡ <c>(pixel - 1024) * 100 / SizeFactor - Map.OffsetX</c>。
/// </para>
/// <para>
/// ⚠️ 但那是「AgentMap 欄位已經跟上」時才成立的恆等式。所以
/// <see cref="TryGetCursorWorldPosition"/> 在兩者差超過 1 個單位時會寫一行
/// <c>Information</c> 診斷——那是**實機校準用的**，正常情況下永遠不會出現。
/// </para>
/// </remarks>
public static unsafe class MapCursorMath
{
    /// <summary>兩份算法差多少以上要寫診斷（世界座標單位，1 單位約等於 0.02 個地圖格）。</summary>
    private const float DivergenceThreshold = 1.0f;

    /// <summary>
    /// 同一個診斷最短的重印間隔。
    /// </summary>
    /// <remarks>
    /// 🔴 右鍵選單開著的時候這支<b>每幀</b>都會被呼叫（tooltip 要即時算前往計畫）。
    /// 不節流的話一旦真的對不上，log 會被同一行灌爆——而那正是我們想請使用者貼給我們的那份 log。
    /// </remarks>
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
    /// </summary>
    /// <param name="cursorScreenPosition">游標的螢幕座標。</param>
    /// <param name="mapDrawOffset">地圖子視窗開始繪製的螢幕位置（<c>MapWindow.MapDrawOffset</c>）。</param>
    /// <param name="worldPosition">換算結果，X 是世界 X、Y 是世界 <b>Z</b>。</param>
    /// <returns>
    /// 換不出來時回 <see langword="false"/>，<b>而且不會給一個看起來很正常的 0</b>——
    /// 呼叫端要把這次點擊整個放棄，不是拿 <c>(0, 0)</c> 去走。
    /// </returns>
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

    /// <summary>
    /// 實機校準用：兩份算法對不上時寫一行 <c>Information</c>。
    /// </summary>
    /// <remarks>
    /// 📌 用 <c>Information</c> 是刻意的——使用者跑的是 LogLevel 1，<c>Debug</c> 收得到但單檔數十萬行會淹沒，
    /// 而這一行正是「請把 log 貼給我」時唯一有價值的證據。
    /// 只有在使用者親手點右鍵選單時才會算，不是每幀。
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
