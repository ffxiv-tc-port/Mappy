using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Extensions;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer
{
    /// <summary>
    /// AgentMap 有兩份標記陣列：MapMarkers 給大地圖、MiniMapMarkers 給小地圖。
    /// 上游 Mappy 只畫前者，所以其他外掛寫進 MiniMapMarkers 的標記在 Mappy 接管大地圖之後就看不到了。
    /// 陣列位置、筆數上限與座標算法對過台服 CreateMiniMapMarkers；改版後偏移若不對，讀到的是垃圾資料而不是崩潰，所以下面的健全性閘門仍要留著。
    /// </summary>
    private uint lastMiniMapMarkerDiagnosticMapId;

    /// <summary>去重用的集合，每幀開頭清空重用（只在繪製執行緒碰）。</summary>
    private readonly HashSet<(short X, short Y, uint IconId)> drawnMiniMapMarkers = [];

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

        // 小地圖這份的座標是「世界座標乘以 16」，世界座標落在正負 1024 之內，
        // 原始值必須在正負 16384 之間才可能在圖上。
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

        // 兩份座標系不同：大地圖＝(貼圖座標-1024)×16，小地圖＝世界座標×16（遊戲先截成整數再×16），
        // 只在 SizeFactor 1、Offset 0 的地圖才逐位相同；其他地圖照舊直接比、直接畫＝同一個標記多一個畫在錯位置。
        var sizeFactor = agent->CurrentMapSizeFactorFloat;
        if (!(sizeFactor > 0.0f)) {
            LogMiniMapMarkerDiagnostic(agent, count, $"SizeFactor = {sizeFactor} 無法換算");
            return;
        }

        var offsetX = agent->CurrentOffsetX;
        var offsetY = agent->CurrentOffsetY;

        // 與大地圖那份標記重覆的不要畫第二次：大地圖那份照遊戲同一套截斷換進小地圖座標再比。
        var drawnMarkers = drawnMiniMapMarkers;
        drawnMarkers.Clear();

        var mapMarkers = agent->MapMarkers;
        var mapMarkerCount = agent->MapMarkerCount < mapMarkers.Length ? agent->MapMarkerCount : mapMarkers.Length;

        for (var index = 0; index < mapMarkerCount; index++) {
            ref var existing = ref mapMarkers[index].MapMarker;
            drawnMarkers.Add((MapToMiniMapCoord(existing.X, sizeFactor, offsetX), MapToMiniMapCoord(existing.Y, sizeFactor, offsetY), existing.IconId));
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

            var mapSpaceMarker = marker;
            mapSpaceMarker.X = MiniMapToMapCoord(marker.X, sizeFactor, offsetX);
            mapSpaceMarker.Y = MiniMapToMapCoord(marker.Y, sizeFactor, offsetY);
            mapSpaceMarker.Draw(DrawPosition, Scale);
        }
    }

    /// <summary>
    /// 大地圖座標換成小地圖座標，逐步照 CreateMiniMapMarkers：(貼圖-1024) 除 SizeFactor 後截斷、加 Offset、×16。
    /// 遊戲自己產生的那份兩邊會逐位相同，這是去重鍵能命中的前提。
    /// </summary>
    private static short MapToMiniMapCoord(short mapCoord, float sizeFactor, short offset)
        => (short)(((int)(mapCoord / 16 / sizeFactor) + offset) * 16);

    /// <summary>小地圖座標換回大地圖座標（貼圖座標-1024 再×16）；世界座標被截過，誤差最多 SizeFactor 個貼圖像素。</summary>
    private static short MiniMapToMapCoord(short miniMapCoord, float sizeFactor, short offset)
    {
        var mapCoord = MathF.Round((miniMapCoord / 16.0f - offset) * sizeFactor * 16.0f);
        return (short)Math.Clamp(mapCoord, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// 每張地圖只印一次，避免每幀洗版。要使用者回報的診斷，所以用 Information。
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
