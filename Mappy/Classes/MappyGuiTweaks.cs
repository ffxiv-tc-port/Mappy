using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Mappy.Classes;

public static class MappyGuiTweaks
{
    // 工具列每幀都畫這些按鈕；圖示字串只取決於圖示，第一次用到才建立。只在繪製執行緒使用。
    private static readonly Dictionary<FontAwesomeIcon, string> IconLabels = [];

    internal static string GetIconLabel(FontAwesomeIcon icon)
    {
        if (!IconLabels.TryGetValue(icon, out var label)) {
            label = icon.ToIconString();
            IconLabels.Add(icon, label);
        }

        return label;
    }

    public static bool IconButton(FontAwesomeIcon icon, string id, string? tooltip)
    {
        using var imRaiiId = ImRaii.PushId(id);

        bool result;

        using (Service.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push()) {
            result = ImGui.Button(GetIconLabel(icon));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && tooltip is not null) {
            ImGui.SetTooltip(tooltip);
        }

        return result;
    }

    /// <summary>
    /// 畫法與 KamiLib <c>ImGuiTweaks.Checkbox(label, ref value, hintText)</c> 相同，
    /// 但說明圖示在停用區段裡也回應滑鼠。
    /// </summary>
    /// <remarks>
    /// KamiLib 那一支用 Dalamud 的 <c>ImGuiComponents.HelpMarker</c>，內部判不帶 AllowWhenDisabled 的 hover，
    /// 停用時說明永遠不出現；KamiLib 是子模組（別的外掛也釘它），所以在本外掛內另寫。
    /// </remarks>
    public static bool Checkbox(string label, ref bool value, string? hintText)
    {
        using var group = ImRaii.Group();

        var result = ImGui.Checkbox(label, ref value);

        if (hintText is not null) {
            HelpMarker(hintText);
        }

        return result;
    }

    /// <summary>與 Dalamud <c>ImGuiComponents.HelpMarker(helpText)</c> 同一畫法，停用時說明也會出現。</summary>
    private static void HelpMarker(string helpText)
    {
        ImGui.SameLine();

        using (ImRaii.PushFont(UiBuilder.IconFont)) {
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;

        // 不帶旗標的 hover 為假 ⇔ 圖示停用：只有這時把 Alpha 推回 1（停用區段的 Alpha 已乘上 DisabledAlpha），啟用時不推。
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f, !ImGui.IsItemHovered());
        using (ImRaii.Tooltip()) {
            using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 35.0f)) {
                ImGui.Text(helpText);
            }
        }
    }
}