using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Text.ReadOnly;
using Mappy.Classes;

namespace Mappy.Extensions;

public static class TempMapMarkerExtensions
{
    public static void Draw(this TempMapMarker marker, Vector2 offset, float scale)
    {
        DrawHelpers.DrawMapMarker(new MarkerInfo
        {
            // Divide by 16, as it seems they use a fixed scalar
            // Add 1024 * scale, to offset from top-left, to center-based coordinate
            // Add offset for drawing relative to map when its moved around
            Position = (new Vector2(marker.MapMarker.X, marker.MapMarker.Y) / 16.0f * DrawHelpers.GetMapScaleFactor() + DrawHelpers.GetCombinedOffsetVector()) * scale,
            Offset = offset,
            Scale = scale,
            IconId = marker.MapMarker.IconId,
            Radius = marker.MapMarker.Scale,
            // 🔴 TooltipText 是 Utf8String:ToString() 整段位元組 raw UTF-8 解碼、不剝 SeString payload,
            //    帶 payload 的提示文字會直接畫成 U+FFFD 與控制位元組。改走 Lumina 的 ExtractText(),
            //    與同目錄 MapMarkerBaseExtensions 先轉 SeString 再取文字同基準;純文字時逐字相同。
            // ⚠️ 刻意維持「用到才解」的 lambda:這支每幀對每個標記呼叫一次,
            //    提示文字只有滑鼠停上去才需要。
            PrimaryText = () => new ReadOnlySeStringSpan(marker.TooltipText.AsSpan()).ExtractText(),
        });
    }
}