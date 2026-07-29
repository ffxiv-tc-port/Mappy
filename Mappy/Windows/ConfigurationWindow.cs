using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using KamiLib.Classes;
using KamiLib.CommandManager;
using KamiLib.Extensions;
using KamiLib.Window;
using Mappy.Classes;
using Mappy.Data;

namespace Mappy.Windows;

public class ConfigurationWindow : Window
{
    private readonly TabBar tabBar = new("mappy_tab_bar", [
        new IconConfigurationTab(),
        new MapFunctionsTab(),
        new StyleOptionsTab(),
        new PlayerOptionsTab(),
    ]);

    public ConfigurationWindow() : base("Mappy－設定", new Vector2(500.0f, 580.0f))
    {
        System.CommandManager.RegisterCommand(new CommandHandler
        {
            Delegate = _ => System.ConfigWindow.Toggle(), ActivationPath = "/",
        });
    }

    protected override void DrawContents() => tabBar.Draw();
}

public class MapFunctionsTab : ITabItem
{
    public string Name => "地圖功能";

    public bool Disabled => false;

    public void Draw()
    {
        var configChanged = false;

        ImGuiTweaks.Header("縮放選項");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("使用線性縮放", ref System.SystemConfig.UseLinearZoom);
            configChanged |= ImGui.Checkbox("圖示隨縮放比例調整", ref System.SystemConfig.ScaleWithZoom);
            configChanged |= ImGui.Checkbox("文字標籤隨縮放比例調整", ref System.SystemConfig.ScaleTextWithZoom);

            ImGuiHelpers.ScaledDummy(5.0f);

            configChanged |= ImGuiTweaks.Checkbox("自動縮放", ref System.SystemConfig.AutoZoom, "依地圖大小自動設定合適的縮放比例。");
            configChanged |= ImGui.SliderFloat("自動縮放比例", ref System.SystemConfig.AutoZoomScaleFactor, 0.20f, 1.00f);

            ImGuiHelpers.ScaledDummy(5.0f);

            configChanged |= ImGui.SliderFloat("縮放速度", ref System.SystemConfig.ZoomSpeed, 0.001f, 0.500f);
            configChanged |= ImGui.SliderFloat("圖示比例", ref System.SystemConfig.IconScale, 0.10f, 3.0f);
        }

        ImGuiTweaks.Header("開啟地圖時");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("開啟時跟隨玩家", ref System.SystemConfig.FollowOnOpen);

            ImGuiHelpers.ScaledDummy(5.0f);

            DrawCenterModeRadio();
        }

        ImGuiTweaks.Header("連結行為");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("以地圖旗標為中心", ref System.SystemConfig.CenterOnFlag);
            configChanged |= ImGui.Checkbox("以採集區域為中心", ref System.SystemConfig.CenterOnGathering);
            configChanged |= ImGui.Checkbox("以任務為中心", ref System.SystemConfig.CenterOnQuest);
        }

        ImGuiTweaks.Header("其他選項");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("顯示其他工具提示", ref System.SystemConfig.ShowMiscTooltips);
            configChanged |= ImGui.Checkbox("置中後鎖定地圖", ref System.SystemConfig.LockCenterOnMap);
            configChanged |= ImGui.Checkbox("顯示其他玩家", ref System.SystemConfig.ShowPlayers);
            configChanged |= ImGui.Checkbox("地圖出現時不取得焦點", ref System.SystemConfig.NoFocusOnAppear);

            ImGuiHelpers.ScaledDummy(5.0f);

            configChanged |= ImGui.Checkbox("顯示文字標籤", ref System.SystemConfig.ShowTextLabels);
            configChanged |= ImGui.DragFloat("大型標籤比例", ref System.SystemConfig.LargeAreaTextScale, 0.01f, 1.0f, 4.0f);
            configChanged |= ImGui.DragFloat("小型標籤比例", ref System.SystemConfig.SmallAreaTextScale, 0.01f, 0.5f, 3.0f);

            ImGuiHelpers.ScaledDummy(5.0f);

            configChanged |= ImGui.Checkbox("顯示戰爭迷霧", ref System.SystemConfig.ShowFogOfWar);

            ImGuiHelpers.ScaledDummy(5.0f);

            configChanged |= ImGui.Checkbox("偵錯模式", ref System.SystemConfig.DebugMode);
        }

        ImGuiTweaks.Header("工具列");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("永遠顯示", ref System.SystemConfig.AlwaysShowToolbar);
            configChanged |= ImGui.Checkbox("滑鼠指向時顯示", ref System.SystemConfig.ShowToolbarOnHover);

            ImGuiHelpers.ScaledDummy(5.0f);
            configChanged |= ImGui.DragFloat("不透明度##toolbar", ref System.SystemConfig.ToolbarFade, 0.01f, 0.0f, 1.0f);
        }

        ImGuiTweaks.Header("座標");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("顯示座標列", ref System.SystemConfig.ShowCoordinateBar);

            ImGuiHelpers.ScaledDummy(5.0f);
            configChanged |= ImGuiTweaks.ColorEditWithDefault("文字顏色", ref System.SystemConfig.CoordinateTextColor, KnownColor.White.Vector());

            ImGuiHelpers.ScaledDummy(5.0f);
            configChanged |= ImGui.DragFloat("不透明度##coordinatebar", ref System.SystemConfig.CoordinateBarFade, 0.01f, 0.0f, 1.0f);
        }

        if (configChanged) {
            SystemConfig.Save();
        }
    }

    private void DrawCenterModeRadio()
    {
        var enumObject = System.SystemConfig.CenterOnOpen;
        var firstLine = true;

        foreach (Enum enumValue in Enum.GetValues(enumObject.GetType())) {
            if (!firstLine) ImGui.SameLine();

            if (ImGui.RadioButton(enumValue.GetDescription(), enumValue.Equals(enumObject))) {
                System.SystemConfig.CenterOnOpen = (CenterTarget)enumValue;
                SystemConfig.Save();
            }

            firstLine = false;
        }

        ImGui.SameLine();
        ImGui.Text("\t\t開啟時置中於");
    }
}

public class StyleOptionsTab : ITabItem
{
    public string Name => "外觀";

    public bool Disabled => false;

    public void Draw()
    {
        var configChanged = false;

        ImGuiTweaks.Header("視窗選項");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("保持開啟", ref System.SystemConfig.KeepOpen);
            configChanged |= ImGui.Checkbox("鎖定視窗位置", ref System.SystemConfig.LockWindow);
            configChanged |= ImGui.Checkbox("隱藏視窗邊框", ref System.SystemConfig.HideWindowFrame);
            configChanged |= ImGui.Checkbox("隱藏視窗背景", ref System.SystemConfig.HideWindowBackground);
            configChanged |= ImGui.Checkbox("允許按住 Shift 拖曳視窗邊框", ref System.SystemConfig.EnableShiftDragMove);
        }

        ImGuiTweaks.Header("視窗隱藏條件");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("隨遊戲介面隱藏", ref System.SystemConfig.HideWithGameGui);
            configChanged |= ImGui.Checkbox("切換區域時隱藏", ref System.SystemConfig.HideBetweenAreas);
            configChanged |= ImGui.Checkbox("戰鬥中隱藏", ref System.SystemConfig.HideInCombat);
        }

        ImGuiTweaks.Header("視窗標題");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("顯示地區文字", ref System.SystemConfig.ShowRegionLabel);
            configChanged |= ImGui.Checkbox("顯示地圖文字", ref System.SystemConfig.ShowMapLabel);
            configChanged |= ImGui.Checkbox("顯示區域文字", ref System.SystemConfig.ShowAreaLabel);
            configChanged |= ImGui.Checkbox("顯示子區域文字", ref System.SystemConfig.ShowSubAreaLabel);
        }

        ImGuiTweaks.Header("視窗位置");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.DragFloat2("視窗座標", ref System.SystemConfig.WindowPosition);
            configChanged |= ImGui.DragFloat2("視窗大小", ref System.SystemConfig.WindowSize);
        }

        ImGuiTweaks.Header("淡化選項");
        using (ImRaii.PushIndent()) {
            using (var columns = ImRaii.Table("fade_options_toggles", 2)) {
                if (!columns) return;

                var value = System.SystemConfig.FadeMode;
                ImGui.TableNextColumn();

                foreach (Enum enumValue in Enum.GetValues(value.GetType())) {
                    var isFlagSet = value.HasFlag(enumValue);
                    if (ImGuiComponents.ToggleButton(enumValue.ToString(), ref isFlagSet)) {
                        var sourceValue = Convert.ToInt32(value);
                        var targetValue = Convert.ToInt32(enumValue);

                        if (value.HasFlag(enumValue)) {
                            System.SystemConfig.FadeMode = (FadeMode)Enum.ToObject(value.GetType(), sourceValue & ~targetValue);
                        }
                        else {
                            System.SystemConfig.FadeMode = (FadeMode)Enum.ToObject(value.GetType(), sourceValue | targetValue);
                        }

                        configChanged = true;
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(enumValue.GetDescription());

                    ImGui.TableNextColumn();
                }
            }

            configChanged |= ImGui.DragFloat("淡化後不透明度", ref System.SystemConfig.FadePercent, 0.01f, 0.05f, 1.0f);
        }

        ImGuiTweaks.Header("區域外觀");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGuiTweaks.ColorEditWithDefault("區域顏色", ref System.SystemConfig.AreaColor, KnownColor.CornflowerBlue.Vector() with { W = 0.33f });
            configChanged |= ImGuiTweaks.ColorEditWithDefault("區域外框顏色", ref System.SystemConfig.AreaOutlineColor,
                KnownColor.CornflowerBlue.Vector() with { W = 0.30f });
        }

        if (configChanged) {
            if (System.MapWindow.SizeConstraints is { } constraints) {
                System.SystemConfig.WindowSize.X = MathF.Max(System.SystemConfig.WindowSize.X, constraints.MinimumSize.X);
                System.SystemConfig.WindowSize.Y = MathF.Max(System.SystemConfig.WindowSize.Y, constraints.MinimumSize.Y);
            }

            System.MapWindow.RefreshTitle();
            SystemConfig.Save();
        }
    }
}

public class PlayerOptionsTab : ITabItem
{
    public string Name => "玩家";

    public bool Disabled => false;

    public void Draw()
    {
        var configChanged = false;

        ImGuiTweaks.Header("方向錐選項");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("縮放玩家方向錐", ref System.SystemConfig.ScalePlayerCone);

            ImGuiHelpers.ScaledDummy(5.0f);
            configChanged |= ImGui.DragFloat("方向錐大小", ref System.SystemConfig.ConeSize, 0.25f);

            ImGuiHelpers.ScaledDummy(5.0f);
            configChanged |= ImGuiTweaks.ColorEditWithDefault("方向錐顏色", ref System.SystemConfig.PlayerConeColor, KnownColor.CornflowerBlue.Vector() with { W = 0.33f });
            configChanged |= ImGuiTweaks.ColorEditWithDefault("方向錐外框顏色", ref System.SystemConfig.PlayerConeOutlineColor,
                KnownColor.CornflowerBlue.Vector() with { W = 1.00f });
        }

        ImGuiTweaks.Header("雷達選項");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("顯示雷達範圍", ref System.SystemConfig.ShowRadar);
            configChanged |= ImGui.Checkbox("任務中顯示", ref System.SystemConfig.ShowRadarInDuties);

            ImGuiHelpers.ScaledDummy(5.0f);

            configChanged |= ImGuiTweaks.ColorEditWithDefault("雷達區域顏色", ref System.SystemConfig.RadarColor, KnownColor.Gray.Vector() with { W = 0.10f });
            configChanged |= ImGuiTweaks.ColorEditWithDefault("雷達外框顏色", ref System.SystemConfig.RadarOutlineColor, KnownColor.Gray.Vector() with { W = 0.30f });
        }

        ImGuiTweaks.Header("玩家圖示選項");
        using (ImRaii.PushIndent()) {
            configChanged |= ImGui.Checkbox("顯示玩家圖示", ref System.SystemConfig.ShowPlayerIcon);

            ImGuiHelpers.ScaledDummy(5.0f);
            configChanged |= ImGui.DragFloat("玩家圖示大小", ref System.SystemConfig.PlayerIconScale, 0.05f);
        }

        if (configChanged) {
            SystemConfig.Save();
        }
    }
}

public class IconConfigurationTab : ITabItem
{
    public string Name => "圖示設定";

    public bool Disabled => false;

    private IconSetting? currentSetting;

    public void Draw()
    {
        using (var leftChild = ImRaii.Child("left_child", new Vector2(48.0f * ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemSpacing.X, ImGui.GetContentRegionAvail().Y))) {
            if (leftChild) {
                using var selectionList = ImRaii.ListBox("iconSelection", ImGui.GetContentRegionAvail());

                foreach (var (iconId, settings) in System.IconConfig.IconSettingMap.OrderBy(pairData => pairData.Key)) {
                    if (iconId is 0) continue;
                    if (DrawHelpers.IsDisallowedIcon(iconId)) continue;

                    var texture = Service.TextureProvider.GetFromGameIcon(iconId).GetWrapOrEmpty();
                    var cursorStart = ImGui.GetCursorScreenPos();
                    if (ImGui.Selectable($"##iconSelect{iconId}", currentSetting == settings, ImGuiSelectableFlags.None, ImGuiHelpers.ScaledVector2(32.0f, 32.0f))) {
                        currentSetting = currentSetting == settings ? null : settings;
                    }

                    ImGui.SetCursorScreenPos(cursorStart);
                    ImGui.Image(texture.Handle, texture.Size / 2.0f * ImGuiHelpers.GlobalScale);
                }
            }
        }

        ImGui.SameLine();

        using (var rightChild = ImRaii.Child("right_child", ImGui.GetContentRegionAvail(), false, ImGuiWindowFlags.NoScrollbar)) {
            if (rightChild) {
                if (currentSetting is null) {
                    using var textColor = ImRaii.PushColor(ImGuiCol.Text, KnownColor.Orange.Vector());

                    ImGui.SetCursorPosY(ImGui.GetContentRegionAvail().Y / 2.0f);
                    ImGuiHelpers.CenteredText("請選擇要編輯的圖示");
                }
                else {
                    // Draw background texture
                    var settingsChanged = false;
                    var texture = Service.TextureProvider.GetFromGameIcon(currentSetting.IconId).GetWrapOrEmpty();
                    var smallestAxis = MathF.Min(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y);

                    if (ImGui.GetContentRegionAvail().X > ImGui.GetContentRegionAvail().Y) {
                        var remainingSpace = ImGui.GetContentRegionAvail().X - smallestAxis;
                        ImGui.SetCursorPosX(remainingSpace / 2.0f);
                    }

                    ImGui.Image(texture.Handle, new Vector2(smallestAxis, smallestAxis), Vector2.Zero, Vector2.One, new Vector4(1.0f, 1.0f, 1.0f, 0.20f));
                    ImGui.SetCursorPos(Vector2.Zero);

                    // Draw settings
                    ImGuiTweaks.Header($"設定標記 #{currentSetting.IconId}");
                    using (ImRaii.PushIndent()) {
                        settingsChanged |= ImGui.Checkbox("隱藏圖示", ref currentSetting.Hide);
                        settingsChanged |= ImGui.Checkbox("允許工具提示", ref currentSetting.AllowTooltip);
                        settingsChanged |= ImGui.Checkbox("允許點擊互動", ref currentSetting.AllowClick);

                        ImGuiHelpers.ScaledDummy(5.0f);
                        settingsChanged |= ImGuiTweaks.ColorEditWithDefault("顏色", ref currentSetting.Color, KnownColor.White.Vector());

                        ImGuiHelpers.ScaledDummy(5.0f);
                        settingsChanged |= ImGui.DragFloat("圖示比例", ref currentSetting.Scale, 0.01f, 0.05f, 20.0f);
                    }

                    ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - 25.0f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Button("重設為預設值", new Vector2(ImGui.GetContentRegionAvail().X, 25.0f * ImGuiHelpers.GlobalScale))) {
                        currentSetting.Reset();
                        System.IconConfig.Save();
                    }

                    if (settingsChanged) {
                        System.IconConfig.Save();
                    }
                }
            }
        }
    }
}
