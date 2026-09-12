using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Extensions;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer
{
    /// <summary>
    /// AgentMap 有兩份標記陣列：MapMarkers 給大地圖、MiniMapMarkers 給小地圖。
    /// 上游 Mappy 只畫前者，所以其他外掛寫進 MiniMapMarkers 的標記在 Mappy 接管大地圖之後就看不到了。
    /// ⚠️ 這些偏移沒有對台服執行檔做過離線驗證。偏移若不對，讀到的會是垃圾資料而不是崩潰，所以下面的健全性閘門是必要的。
    /// </summary>
    private uint lastMiniMapMarkerDiagnosticMapId;

    private void DrawMiniMapMarkers()
    {
        if (!System.SystemConfig.ShowMiniMapMarkers) return;

        var agent = AgentMap.Instance();
        if (agent is null) return;

        // 這份標記描述的是「目前所在區域」，翻到別張地圖時不能拿來畫。
        if (agent->SelectedMapId != agent->CurrentMapId) return;

        var miniMapMarkers = agent->MiniMapMarkers;
        int count = agent->MiniMapMarkerCount;

        if (count is 0) return;

        // 計數超出陣列容量＝結構對不上，再讀下去就是讀到陣列外的欄位。
        if (count > miniMapMarkers.Length) {
            LogMiniMapMarkerDiagnostic(agent, count, $"count 超出陣列容量 {miniMapMarkers.Length}");
            return;
        }

        // MapMarkerBase 的座標是「世界座標乘以 16」，換算成貼圖座標是 X / 16 + 1024，
        // 要落在 [0, 2048] 才可能在圖上，也就是原始值必須在正負 16384 之間。
        // 個別標記落在圖外是正常的（DrawMapMarker 自己會濾掉），但「全部都在圖外」
        // 幾乎一定代表偏移對不上，這種時候整批跳過。
        var plausibleCount = 0;

        for (var index = 0; index < count; index++) {
            ref var marker = ref miniMapMarkers[index].MapMarker;

            if (marker.X is > -16384 and < 16384 && marker.Y is > -16384 and < 16384) {
                plausibleCount++;
            }
        }

        if (plausibleCount is 0) {
            LogMiniMapMarkerDiagnostic(agent, count, "座標全數落在地圖範圍之外");
            return;
        }

        // 與大地圖那份標記重覆的不要畫第二次。
        var drawnMarkers = new HashSet<(short X, short Y, uint IconId)>();

        var mapMarkers = agent->MapMarkers;
        var mapMarkerCount = agent->MapMarkerCount < mapMarkers.Length ? agent->MapMarkerCount : mapMarkers.Length;

        for (var index = 0; index < mapMarkerCount; index++) {
            ref var existing = ref mapMarkers[index].MapMarker;
            drawnMarkers.Add((existing.X, existing.Y, existing.IconId));
        }

        for (var index = 0; index < count; index++) {
            ref var entry = ref miniMapMarkers[index];
            ref var marker = ref entry.MapMarker;

            if (marker.IconId is 0) continue;

            // 乙太之光（DataType 3）與城內乙太網水晶（DataType 4）大地圖那份本來就有，
            // 而且才是權威：小地圖這份的同一座水晶座標/圖示不必逐位相同，
            // 下面的去重鍵擋不住，畫出來就是數量不對。
            // ⚠️ DataType 同語意家族是合理推論非實機實證，推論錯的後果只是少畫幾個標記，不會多畫也不會崩。
            // 📌 其他外掛用 AddMiniMapMarker 寫入的自訂標記 DataType 恆為 0，不受影響。
            if (entry.DataType is 3 or 4) continue;

            if (!drawnMarkers.Add((marker.X, marker.Y, marker.IconId))) continue;

            marker.Draw(DrawPosition, Scale);
        }
    }

    /// <summary>
    /// 每張地圖只印一次，避免每幀洗版。使用者跑 LogLevel 1，所以一律用 Information。
    /// </summary>
    private void LogMiniMapMarkerDiagnostic(AgentMap* agent, int count, string reason)
    {
        if (lastMiniMapMarkerDiagnosticMapId == agent->SelectedMapId) return;
        lastMiniMapMarkerDiagnosticMapId = agent->SelectedMapId;

        var miniMapMarkers = agent->MiniMapMarkers;
        var sampleCount = count < 3 ? count : 3;
        if (sampleCount > miniMapMarkers.Length) sampleCount = miniMapMarkers.Length;

        var sampleIcons = new List<uint>();
        for (var index = 0; index < sampleCount; index++) {
            sampleIcons.Add(miniMapMarkers[index].MapMarker.IconId);
        }

        Service.Log.Information(
            $"[Mappy] MapId {agent->SelectedMapId} 的小地圖標記整批略過（{reason}）：" +
            $"count = {count}，前 {sampleCount} 筆 IconId = [{string.Join(", ", sampleIcons)}]。");
    }
}
