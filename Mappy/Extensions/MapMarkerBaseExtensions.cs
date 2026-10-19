using System;
using System.Drawing;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;

namespace Mappy.Extensions;

// Represents standard non-dynamic map markers, things that don't change, and may reference datasheet data with their key data
public static class MapMarkerBaseExtensions
{
    // 契約：只在繪製執行緒呼叫。DrawMapMarker 當場用完 MarkerInfo 與委派、不保留，所以共用一個實例；
    // 委派讀 current（這次 Draw 的標記複本），副文字等滑鼠移上去才解析，同一次呼叫內讀到的位元組相同。
    private static MapMarkerBase current;
    private static readonly MarkerInfo Scratch = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };
    private static readonly Func<string?> PrimaryTooltip = () => GetPrimaryText(current);

    public static void Draw(this MapMarkerBase marker, Vector2 offset, float scale)
    {
        var markerInfo = Scratch;
        markerInfo.ResetForReuse(
            // Divide by 16, as it seems they use a fixed scalar
            // Add 1024 * scale, to offset from top-left, to center-based coordinate
            // Add offset for drawing relative to map when its moved around
            new Vector2(marker.X, marker.Y) / 16.0f * scale + DrawHelpers.GetMapCenterOffsetVector() * scale,
            offset,
            scale);
        markerInfo.Radius = marker.Scale;
        markerInfo.RadiusColor = KnownColor.MediumPurple.Vector();
        markerInfo.IconId = marker.IconId;
        markerInfo.PrimaryText = PrimaryTooltip;

        current = marker;
        DrawHelpers.DrawMapMarker(markerInfo);
        current = default;
    }

    private static string? GetPrimaryText(MapMarkerBase marker)
    {
        var tooltipText = marker.Subtext.AsDalamudSeString();

        return tooltipText.TextValue.IsNullOrEmpty() && System.SystemConfig.ShowMiscTooltips ? System.TooltipCache.GetValue(marker.IconId) : tooltipText.ToString();
    }
}
