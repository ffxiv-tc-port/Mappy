using System;
using System.Numerics;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Mappy.Classes;
using MarkerInfo = Mappy.Classes.MarkerInfo;

namespace Mappy.Extensions;

// MapMarkerData struct represents dynamic markers that have information like radius, and other fields.
public static class MapMarkerDataExtensions
{
    // 契約：只在繪製執行緒呼叫。DrawMapMarker 當場用完 MarkerInfo 與委派、不保留，所以共用一個實例；
    // 委派讀 current（這次 Draw 的標記複本），它只在那一次 DrawMapMarker 期間有值。
    private static MapMarkerData current;
    private static readonly MarkerInfo Scratch = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };
    private static readonly Func<string?> PrimaryTooltip = () => GetMarkerPrimaryText(current);

    public static void Draw(this MapMarkerData marker, Vector2 offset, float scale)
    {
        if ((marker.Flags & 1) == 1) return;

        var markerInfo = Scratch;
        markerInfo.ResetForReuse(
            (marker.Position.AsMapVector() * DrawHelpers.GetMapScaleFactor() - DrawHelpers.GetMapOffsetVector() + DrawHelpers.GetMapCenterOffsetVector()) * scale,
            offset,
            scale);
        markerInfo.IconId = marker.IconId;
        markerInfo.Radius = marker.Radius;
        markerInfo.RadiusColor = System.SystemConfig.AreaColor;
        markerInfo.RadiusOutlineColor = System.SystemConfig.AreaOutlineColor;
        markerInfo.PrimaryText = PrimaryTooltip;
        markerInfo.IsDynamicMarker = true;
        markerInfo.ObjectiveId = marker.ObjectiveId;
        markerInfo.MarkerType = (MarkerType)marker.MarkerType;
        markerInfo.DataId = marker.DataId;

        current = marker;
        DrawHelpers.DrawMapMarker(markerInfo);
        current = default;
    }

    private static unsafe string GetMarkerPrimaryText(MapMarkerData marker)
    {
        if (marker.TooltipString is null) return string.Empty;
        if (marker.TooltipString->StringPtr.Value is null) return string.Empty;
        if (marker.TooltipString->StringPtr.ExtractText().IsNullOrEmpty()) return string.Empty;

        var text = marker.TooltipString->StringPtr.ExtractText();
        return marker.RecommendedLevel is 0 ? text : $"等級 {marker.RecommendedLevel} {text}";
    }
}
