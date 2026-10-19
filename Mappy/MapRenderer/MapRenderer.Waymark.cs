using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;
using FieldMarker = Lumina.Excel.Sheets.FieldMarker;
using MarkerInfo = Mappy.Classes.MarkerInfo;

namespace Mappy.MapRenderer;

public partial class MapRenderer
{
    private readonly List<FieldMarker> fieldMarkers = Service.DataManager.GetExcelSheet<FieldMarker>().Where(marker => marker.MapIcon is not 0).ToList();

    // 場地標點與旗標共用：DrawMapMarker 當場用完、不保留（只在繪製執行緒碰）。
    private readonly MarkerInfo sharedMarker = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };

    private unsafe void DrawFieldMarkers()
    {
        if (AgentMap.Instance()->CurrentMapId != AgentMap.Instance()->SelectedMapId) return;
        var markerSpan = MarkingController.Instance()->FieldMarkers;

        for (var index = 0; index < 8; index++) {
            if (markerSpan[index] is { Active: true } marker) {
                var markerPosition =
                    new Vector2(marker.X, marker.Z) / 1000.0f * DrawHelpers.GetMapScaleFactor()
                    - DrawHelpers.GetMapOffsetVector()
                    + DrawHelpers.GetMapCenterOffsetVector();

                var markerInfo = sharedMarker;
                markerInfo.ResetForReuse(markerPosition * Scale, DrawPosition, Scale);
                markerInfo.IconId = fieldMarkers[index].MapIcon;
                DrawHelpers.DrawMapMarker(markerInfo);
            }
        }
    }
}
