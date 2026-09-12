using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Window;
using Lumina.Excel.Sheets;
using Mappy.Controllers;
using Mappy.Data;
using Mappy.Windows;

// ⚠️ FFXIVClientStructs.FFXIV.Client.Game.UI 底下也有一個 Map，會跟 Lumina 的 Map 表撞名。
//    這裡只需要 PlayerState，所以用 using 別名精準引入，不要整個命名空間拉進來。
using PlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace Mappy.Classes.MapWindowComponents;

public unsafe class MapContextMenu
{
    public void Draw(Vector2 mapDrawOffset)
    {
        using var contextMenu = ImRaii.ContextPopup("Mappy_Context_Menu");

        if (!contextMenu) return;

        if (ImGui.MenuItem("放置旗標")) {
            var cursorPosition = ImGui.GetMousePosOnOpeningCurrentPopup(); // Get initial cursor position (screen relative)

            // ⚠️ 這裡刻意繼續走 AgentMap 的 Selected* 欄位，行為與加入「移動到這裡」之前完全一樣。
            //    新功能用的是 Map 表那一份（MapCursorMath.TryGetCursorWorldPosition），原因見該檔說明。
            var scaledResult = MapCursorMath.GetCursorWorldPositionFromAgent(cursorPosition, mapDrawOffset);

            AgentMap.Instance()->FlagMarkerCount = 0;
            AgentMap.Instance()->SetFlagMapMarker(AgentMap.Instance()->SelectedTerritoryId, AgentMap.Instance()->SelectedMapId, scaledResult.X, scaledResult.Y);
            AgentChatLog.Instance()->InsertTextCommandParam(1048, false);
        }

        if (ImGui.MenuItem("移除旗標", false, AgentMap.Instance()->FlagMarkerCount is not 0)) {
            AgentMap.Instance()->FlagMarkerCount = 0;
        }

        if (System.SystemConfig.EnableTravelToHere) {
            ImGuiHelpers.ScaledDummy(5.0f);

            DrawTravelToHere(mapDrawOffset);
            DrawStopTravel();
        }

        ImGuiHelpers.ScaledDummy(5.0f);

        if (ImGui.MenuItem("以玩家為中心", false, Service.ObjectTable.LocalPlayer is not null) && Service.ObjectTable.LocalPlayer is not null) {
            System.IntegrationsController.OpenOccupiedMap();
            System.MapRenderer.CenterOnGameObject(Service.ObjectTable.LocalPlayer);
        }

        if (ImGui.MenuItem("以地圖為中心")) {
            System.SystemConfig.FollowPlayer = false;
            System.MapRenderer.DrawOffset = Vector2.Zero;
        }

        ImGuiHelpers.ScaledDummy(5.0f);

        if (ImGui.MenuItem("鎖定縮放", "", ref System.SystemConfig.ZoomLocked)) {
            SystemConfig.Save();
        }

        ImGuiHelpers.ScaledDummy(5.0f);

        if (ImGui.MenuItem("開啟任務清單", false, System.WindowManager.GetWindow<QuestListWindow>() is null)) {
            System.WindowManager.AddWindow(new QuestListWindow(), WindowFlags.OpenImmediately | WindowFlags.RequireLoggedIn);
        }

        if (ImGui.MenuItem("開啟危命任務清單", false, System.WindowManager.GetWindow<FateListWindow>() is null)) {
            System.WindowManager.AddWindow(new FateListWindow(), WindowFlags.OpenImmediately | WindowFlags.RequireLoggedIn);
        }

        if (ImGui.MenuItem("開啟旗標清單", false, System.WindowManager.GetWindow<FlagHistoryWindow>() is null)) {
            System.WindowManager.AddWindow(new FlagHistoryWindow(), WindowFlags.OpenImmediately | WindowFlags.RequireLoggedIn);
        }
    }

    /// <summary>
    /// 「移動到這裡」：插旗＋把整段前往交給 Lifestream（跨區傳送、上坐騎、飛過去都在它那邊）。
    /// 🔴 這是<b>使用者親手點下去</b>才會發生的一次性動作，沒有任何事件驅動的自動接手鏈。
    /// </summary>
    private static void DrawTravelToHere(Vector2 mapDrawOffset)
    {
        var cursorPosition = ImGui.GetMousePosOnOpeningCurrentPopup();
        var state = GetTravelState(cursorPosition, mapDrawOffset);

        if (ImGui.MenuItem("移動到這裡", state.RowNote, false, state.CanTravel) && state.CanTravel) {
            BeginTravel(state);
        }

        // ⚠️ 停用中的項目預設不回報 hover，一定要 AllowWhenDisabled 才問得到——
        //    而「為什麼不能按」正是使用者最需要看到的那一句。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.SetTooltip(state.Tooltip);
        }
    }

    /// <summary>
    /// 「停止移動」：同時中止 Lifestream 的工作佇列與 vnavmesh 的路徑。
    /// </summary>
    private static void DrawStopTravel()
    {
        var lifestreamBusy = System.LifestreamIpc.IsLifestreamBusy();
        var navBusy = System.VnavmeshIpc.IsPathRunning() || System.VnavmeshIpc.IsPathfindInProgress() || System.VnavmeshIpc.IsEnforcingStop;

        var anythingInstalled = LifestreamIpc.IsAvailable || VnavmeshIpc.IsAvailable;

        var canStop = anythingInstalled && (lifestreamBusy is true || navBusy || lifestreamBusy is null);

        // 🔑 「不知道」要在列上看得見，而且要能按——送一次停止是安全的無操作，
        //    把不確定的狀態畫成「沒有東西在跑」才是會害人的那一種。
        var rowNote = !anythingInstalled ? "需要 Lifestream／vnavmesh"
            : lifestreamBusy is true || navBusy ? string.Empty
            : lifestreamBusy is null ? "狀態未知"
            : "沒有進行中的移動";

        var tooltip = !anythingInstalled
            ? "沒有偵測到 Lifestream 或 vnavmesh，沒有可以停止的自動移動。"
            : lifestreamBusy is null
                ? "無法向 Lifestream 詢問目前狀態（可能未安裝或版本太舊）。\n仍然可以按，會送出一次停止要求。"
                : "中止 Lifestream 的工作佇列，並要求 vnavmesh 停止移動。\n" +
                  "（會持續補送停止最多 3 秒，蓋過「路徑還在背景計算」那段。）";

        if (ImGui.MenuItem("停止移動", rowNote, false, canStop) && canStop) {
            var aborted = System.LifestreamIpc.TryAbort();
            System.VnavmeshIpc.RequestStop();

            Service.Log.Information($"[Mappy] 使用者要求停止移動。Lifestream.Abort 送出={aborted}。");
            Service.ChatGui.Print("[Mappy] 已要求停止移動。");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.SetTooltip(tooltip);
        }
    }

    /// <summary>「移動到這裡」這一列現在應該長什麼樣子。</summary>
    /// <param name="CanTravel">能不能按。</param>
    /// <param name="RowNote">列上右側的灰字。<b>不能按的原因要在列上看得見</b>，不是只寫在 tooltip 裡。</param>
    /// <param name="Tooltip">完整說明（含前往計畫）。</param>
    private readonly record struct TravelState(
        bool CanTravel,
        string RowNote,
        string Tooltip,
        uint TerritoryId,
        uint MapId,
        Vector2 WorldPosition,
        bool Fly);

    private static TravelState Blocked(string rowNote, string tooltip)
        => new(false, rowNote, tooltip, 0, 0, Vector2.Zero, false);

    private static TravelState GetTravelState(Vector2 cursorPosition, Vector2 mapDrawOffset)
    {
        if (Service.ObjectTable.LocalPlayer is null) {
            return Blocked("尚未登入", "還沒進到遊戲裡，沒有可以移動的角色。");
        }

        var agent = AgentMap.Instance();
        if (agent is null) {
            return Blocked("地圖未就緒", "遊戲的地圖代理還沒準備好，稍後再試。");
        }

        var territoryId = agent->SelectedTerritoryId;
        var mapId = agent->SelectedMapId;

        if (territoryId is 0) {
            return Blocked("此地圖無法前往",
                "這張圖沒有對應的區域（世界圖／地區圖之類），上面沒有可以走過去的實際地點。\n" +
                "先切到某個區域的地圖再試。");
        }

        if (!LifestreamIpc.IsAvailable) {
            return Blocked("需要 Lifestream",
                "這個功能把「跨區傳送＋走過去」整段交給 Lifestream 處理。\n請先安裝並啟用 Lifestream。");
        }

        if (!VnavmeshIpc.IsAvailable) {
            return Blocked("需要 vnavmesh",
                "最後一段路是由 vnavmesh 尋路走過去的。\n請先安裝並啟用 vnavmesh。");
        }

        if (!MapCursorMath.TryGetCursorWorldPosition(cursorPosition, mapDrawOffset, out var worldPosition)) {
            return Blocked("座標換算失敗",
                "算不出這個點對應的世界座標（Map 表缺資料或縮放係數為 0）。\n" +
                "詳細情形會寫進 Dalamud 記錄檔。");
        }

        var busy = System.LifestreamIpc.IsLifestreamBusy();

        // 🔑 三態：問不到不等於沒在忙。畫成「可以按」會讓使用者去跟 Lifestream 手上的行程搶。
        if (busy is null) {
            return Blocked("Lifestream 狀態未知",
                "問不到 Lifestream 目前的狀態（可能版本太舊，沒有這個 IPC 端點）。\n" +
                "為了不跟它手上的行程互相打架，這裡先不讓按。");
        }

        if (busy is true) {
            return Blocked("Lifestream 忙碌中",
                "Lifestream 正在跑別的行程。\n要接手的話，先按下面的「停止移動」。");
        }

        var fly = System.SystemConfig.TravelUseFlying;

        return new TravelState(true, string.Empty,
            BuildPlanTooltip(territoryId, mapId, worldPosition, fly),
            territoryId, mapId, worldPosition, fly);
    }

    /// <summary>把「按下去之後會發生什麼」寫成一段人看得懂的計畫。</summary>
    private static string BuildPlanTooltip(uint territoryId, uint mapId, Vector2 worldPosition, bool fly)
    {
        var territory = Service.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        var map = Service.DataManager.GetExcelSheet<Map>().GetRowOrDefault(mapId);

        var placeName = map?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var subPlaceName = map?.PlaceNameSub.ValueNullable?.Name.ExtractText() ?? string.Empty;

        var destination = placeName.Length is 0 ? $"區域 #{territoryId}" : placeName;
        if (subPlaceName.Length is not 0) destination += $"（{subPlaceName}）";

        var lines = $"目標：{destination}";

        // 地圖座標只是給人看的，換算失敗就不顯示，不要硬塞一個 0 進去誤導人。
        if (map is { } mapRow && mapRow.SizeFactor is not 0) {
            var coordinateX = MapUtil.ConvertWorldCoordXZToMapCoord(worldPosition.X, mapRow.SizeFactor, mapRow.OffsetX);
            var coordinateY = MapUtil.ConvertWorldCoordXZToMapCoord(worldPosition.Y, mapRow.SizeFactor, mapRow.OffsetY);
            lines += $"\n座標：X: {coordinateX:F1}, Y: {coordinateY:F1}";
        }

        var isCrossZone = territoryId != Service.ClientState.TerritoryType;
        lines += isCrossZone
            ? "\n跨區：是——會先傳送到目標區域最近的已解鎖乙太之光，再走過去。"
            : "\n跨區：否——就在目前所在的區域裡，直接走過去。";

        if (!fly) {
            lines += "\n飛行：關閉（設定裡的「移動時使用飛行坐騎」沒有勾）。";
        }
        else {
            lines += PredictFlying(territoryId) switch
            {
                true => "\n飛行：會用飛行坐騎飛過去。",
                false => "\n飛行：這個區域不能飛，或風脈泉還沒集滿，會用走的。",
                null => "\n飛行：無法判斷這個區域能不能飛，交給 Lifestream 現場決定。",
            };
        }

        // ⚠️ 多層地圖（同一個區域好幾張圖）時，樓層資訊在世界座標的 Y 軸上，
        //    而我們只傳 X/Z——Lifestream 會從天上往下找地板，找到的可能是別的樓層。
        if (territory?.Map.RowId is { } primaryMapId && primaryMapId is not 0 && primaryMapId != mapId) {
            lines += "\n⚠️ 這是多層地圖的其中一層，實際落點可能在別的樓層。";
        }

        lines += "\n\n按下後會先在這裡插上旗標，再交給 Lifestream 自動前往。";

        return lines;
    }

    /// <returns>
    /// <see langword="null"/>＝<b>判斷不出來</b>（查不到區域資料、或玩家狀態還沒就緒）。
    /// 🔑 這只是給使用者看的預告；真正決定飛不飛的是 Lifestream，它自己會退回用走的。
    /// </returns>
    private static bool? PredictFlying(uint territoryId)
    {
        var territory = Service.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        if (territory is null) return null;

        // 先例：AutoDuty/Helpers/MovementHelper.cs 的 IsFlyingSupported（1／47／49 三種用途）。
        if (territory.Value.TerritoryIntendedUse.RowId is not (1 or 47 or 49)) return false;

        var playerState = PlayerState.Instance();
        if (playerState is null) return null;

        return playerState->IsAetherCurrentZoneComplete(territory.Value.AetherCurrentCompFlgSet.RowId);
    }

    private static void BeginTravel(TravelState state)
    {
        var agent = AgentMap.Instance();
        if (agent is null) return;

        // 順手插旗，讓使用者看得到目標在哪；刻意不呼叫 InsertTextCommandParam(1048)——
        // 那是把 <flag> 貼到聊天輸入列去，這裡不需要，也不該替使用者打字。
        agent->FlagMarkerCount = 0;
        agent->SetFlagMapMarker(state.TerritoryId, state.MapId, state.WorldPosition.X, state.WorldPosition.Y);

        if (System.LifestreamIpc.TryGoToMapPoint(state.TerritoryId, state.WorldPosition.X, state.WorldPosition.Y, state.Fly)) return;

        // 回 false＝Lifestream 一件事都沒排。這時候不能讓使用者以為它正在路上。
        Service.ChatGui.PrintError("[Mappy] Lifestream 沒有接受這次移動要求，請看聊天視窗中 Lifestream 自己的訊息。");
    }
}
