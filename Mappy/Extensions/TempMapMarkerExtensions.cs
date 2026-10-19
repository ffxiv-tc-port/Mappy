using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Text.ReadOnly;
using Mappy.Classes;

namespace Mappy.Extensions;

public static class TempMapMarkerExtensions
{
    // 契約：只在繪製執行緒呼叫。DrawMapMarker 當場用完 MarkerInfo 與委派、不保留，所以共用一個實例；
    // 委派讀 current（這次 Draw 的標記複本），它只在那一次 DrawMapMarker 期間有值。
    private static TempMapMarker current;
    private static readonly MarkerInfo Scratch = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };

    // 🔴 TooltipText 是 Utf8String，ToString() 不剝 SeString payload；走 Lumina 的 ExtractText()，純文字時逐字相同。
    // 刻意維持「用到才解」：提示文字只有滑鼠停上去才需要。
    private static readonly Func<string?> PrimaryTooltip = () => new ReadOnlySeStringSpan(current.TooltipText.AsSpan()).ExtractText();

    public static void Draw(this TempMapMarker marker, Vector2 offset, float scale)
    {
        var markerInfo = Scratch;
        markerInfo.ResetForReuse(
            // Divide by 16, as it seems they use a fixed scalar
            // Add 1024 * scale, to offset from top-left, to center-based coordinate
            // Add offset for drawing relative to map when its moved around
            (new Vector2(marker.MapMarker.X, marker.MapMarker.Y) / 16.0f * DrawHelpers.GetMapScaleFactor() + DrawHelpers.GetCombinedOffsetVector()) * scale,
            offset,
            scale);
        markerInfo.IconId = marker.MapMarker.IconId;
        markerInfo.Radius = marker.MapMarker.Scale;
        markerInfo.PrimaryText = PrimaryTooltip;

        current = marker;
        DrawHelpers.DrawMapMarker(markerInfo);
        current = default;
    }
}
