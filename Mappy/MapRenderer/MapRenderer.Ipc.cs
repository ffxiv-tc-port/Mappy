using System.Drawing;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Classes;
using Mappy.Classes;
using Mappy.Controllers;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer
{
    /// <summary>
    /// 畫其他外掛透過 IPC 放上來的標記。排在最後，這些標記會蓋在其他東西上面。
    ///
    /// 這裡刻意不走 DrawHelpers.DrawMapMarker：那條路徑第一次看到某個 IconId 就會
    /// 寫一次 IconConfig，外部來源的圖示不該把使用者的圖示設定清單灌爆。
    /// 代價是這些標記沒有逐 icon 的個別設定，改為在設定頁提供逐「來源」的開關
    /// ——對使用者來說「關掉狩獵列車的標記」本來就比「關掉 60561 號圖示」好懂。
    /// </summary>
    private void DrawIpcMarkers()
    {
        var agent = AgentMap.Instance();
        if (agent is null) return;

        var scaleFactor = DrawHelpers.GetMapScaleFactor();
        if (scaleFactor is 0.0f) return;

        foreach (var marker in System.MarkerIpcController.GetMarkersForMap(agent->SelectedMapId)) {
            DrawIpcMarker(marker, scaleFactor);
        }
    }

    /// <summary>
    /// 地圖座標（介面上顯示的 X/Y）換算成貼圖座標。
    ///
    /// 遊戲的正向公式是 mapCoord = 41 / c * (texture / 2048) + 1，其中 c = SelectedMapSizeFactorFloat，
    /// texture 已經含了 1024 的置中位移。反過來就是下面這行。
    /// </summary>
    private static Vector2 MapCoordinatesToTexturePosition(Vector2 mapCoordinates, float scaleFactor)
        => (mapCoordinates - Vector2.One) * scaleFactor * 2048.0f / 41.0f;

    private void DrawIpcMarker(IpcMarker marker, float scaleFactor)
    {
        var position = MapCoordinatesToTexturePosition(marker.MapCoordinates, scaleFactor) * Scale;

        // 與 DrawMapMarker 相同的畫面外剔除。
        if (position.X < 0.0f || position.X > 2048.0f * Scale) return;
        if (position.Y < 0.0f || position.Y > 2048.0f * Scale) return;

        var texture = Service.TextureProvider.GetFromGameIcon(marker.IconId).GetWrapOrEmpty();
        var iconScale = (System.SystemConfig.ScaleWithZoom ? Scale : 1.0f) * System.SystemConfig.IconScale;
        var iconSize = texture.Size * iconScale;

        if (iconSize.X < 1.0f || iconSize.Y < 1.0f) return;

        ImGui.SetCursorPos(position + DrawPosition - iconSize / 2.0f);
        ImGui.Image(texture.Handle, iconSize);

        if (!ImGui.IsItemHovered()) return;

        using var tooltip = ImRaii.Tooltip();

        ImGui.Image(texture.Handle, ImGuiHelpers.ScaledVector2(32.0f, 32.0f));
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7.5f * ImGuiHelpers.GlobalScale);

        // 沒有提示文字時至少要讓使用者看得出這個標記是誰放的。
        ImGui.TextUnformatted(marker.Tooltip.Length is 0 ? marker.Source : marker.Tooltip);

        if (marker.Tooltip.Length is not 0) {
            ImGuiTweaks.TextColoredUnformatted(KnownColor.Gray.Vector(), $"來源：{marker.Source}");
        }
    }
}
