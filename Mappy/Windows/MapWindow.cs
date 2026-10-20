using System.Drawing;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Classes;
using KamiLib.CommandManager;
using KamiLib.Window;
using Lumina.Excel.Sheets;
using Mappy.Classes;
using Mappy.Classes.MapWindowComponents;
using Mappy.Controllers;
using Mappy.Data;
using Map = Lumina.Excel.Sheets.Map;

namespace Mappy.Windows;

public class MapWindow : Window
{
    public Vector2 MapDrawOffset { get; private set; }
    public HoverFlags HoveredFlags { get; private set; }
    public bool ProcessingCommand { get; set; }

    private bool isDragStarted;
    private Vector2 lastWindowSize;
    private uint lastMapId;
    private uint lastAreaPlaceNameId;
    private uint lastSubAreaPlaceNameId;
    private uint lastMissingMapId;
    private bool territoryInfoMissingLogged;

    private readonly MapToolbar mapToolbar = new();
    private readonly MapCoordinateBar mapCoordinateBar = new();
    private readonly MapContextMenu mapContextMenu = new();
    private readonly DeferredSaveGate boundsSaveGate = new();

    public MapWindow() : base("###MappyMapWindow", new Vector2(400.0f, 250.0f))
    {
        UpdateTitle();

        DisableWindowSounds = true;
        RegisterCommands();
    }

    public override bool DrawConditions() => IntegrationsController.ShouldShowMap();

    public override void PreDraw()
    {
        base.PreDraw();
        ForceMainWindow = ShouldStayInGameViewport();
    }

    private static bool HidesBackground() => System.SystemConfig.HideWindowBackground || System.SystemConfig.HideWindowFrame;

    // 視窗只要有一角超出遊戲畫面，ImGui 就把它放進自己的 OS 視窗；實機上那個視窗是黑底，隱藏的背景與淡化都透不到遊戲畫面。
    // 只在視窗中心還在遊戲畫面內時才固定（刻意不全部固定：整個拖到別的螢幕的視窗被固定回來會看不見）。
    private static bool ShouldStayInGameViewport()
    {
        if (!HidesBackground()) return false;

        var mainViewport = ImGui.GetMainViewport();
        var center = System.SystemConfig.WindowPosition + System.SystemConfig.WindowSize / 2.0f;
        var min = mainViewport.Pos;
        var max = mainViewport.Pos + mainViewport.Size;
        return center.X >= min.X && center.Y >= min.Y && center.X < max.X && center.Y < max.Y;
    }

    public override void PreOpenCheck()
    {
        if (System.SystemConfig.KeepOpen) {
            IsOpen = true;
        }

        if (Service.ClientState is { IsLoggedIn: false }) IsOpen = false;
        if (IntegrationsController.IsPvPBlocked()) IsOpen = false;
    }

    public override void OnOpen()
    {
        if (ProcessingCommand) {
            ProcessingCommand = false;
            System.SystemConfig.FollowPlayer = false;
            return;
        }

        if (System.SystemConfig.FollowOnOpen) {
            System.IntegrationsController.OpenOccupiedMap();
            System.SystemConfig.FollowPlayer = true;
        }

        switch (System.SystemConfig.CenterOnOpen) {
            case CenterTarget.Player when Service.ObjectTable.LocalPlayer is { } localPlayer:
                System.MapRenderer.CenterOnGameObject(localPlayer);
                break;

            case CenterTarget.Map:
                System.SystemConfig.FollowPlayer = false;
                System.MapRenderer.DrawOffset = Vector2.Zero;
                break;

            case CenterTarget.Disabled:
            default:
                break;
        }
    }

    protected override void DrawContents()
    {
        UpdateTitle();
        UpdateStyle();
        UpdateSizePosition();

        HoveredFlags = HoverFlags.Nothing;

        if (WindowBounds.IsBoundedBy(ImGui.GetMousePos(), ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + ImGui.GetContentRegionMax())) {
            HoveredFlags |= HoverFlags.Window;
        }

        MapDrawOffset = ImGui.GetCursorScreenPos();
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, System.SystemConfig.FadePercent, ShouldFade());

        // 隱藏背景時子視窗的 ChildBg 也不畫：Dalamud 樣式的 ChildBg 可能不透明，會在地圖貼圖外留一塊深色底。
        ImRaii.IEndObject renderChild;
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero, HidesBackground())) {
            renderChild = ImRaii.Child("render_child", ImGui.GetContentRegionAvail(), false, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
        }

        using (renderChild) {
            if (!renderChild) return;
            if (!System.SystemConfig.AcceptedSpoilerWarning) {
                DrawSpoilerWarning();
                return;
            }

            DrawMapElements();

            // Reset Draw Position for Overlay Extras
            ImGui.SetCursorPos(Vector2.Zero);
            DrawToolbar();
            DrawCoordinateBar();
        }

        if (ImGui.IsItemHovered()) {
            HoveredFlags |= HoverFlags.WindowInnerFrame;
        }

        // Process Inputs
        ProcessInputs();
    }

    private void DrawMapElements()
    {
        System.MapRenderer.DrawBaseTexture();
        if (ImGui.IsItemHovered()) {
            HoveredFlags |= HoverFlags.MapTexture;
        }

        System.MapRenderer.DrawDynamicElements();
    }

    private void DrawToolbar()
    {
        if (!ShouldShowToolbar()) return;

        using (ImRaii.Group()) {
            mapToolbar.Draw();
        }

        if (ImGui.IsItemHovered()) {
            HoveredFlags |= HoverFlags.Toolbar;
        }
    }

    private void DrawCoordinateBar()
    {
        if (!System.SystemConfig.ShowCoordinateBar) return;

        using (ImRaii.Group()) {
            mapCoordinateBar.Draw(HoveredFlags.HasFlag(HoverFlags.MapTexture), MapDrawOffset);
        }

        if (ImGui.IsItemHovered()) {
            HoveredFlags |= HoverFlags.CoordinateBar;
        }
    }

    private bool ShouldShowToolbar()
    {
        if (isDragStarted) return false;
        if (System.SystemConfig.ShowToolbarOnHover && HoveredFlags.Any()) return true;
        if (System.SystemConfig.AlwaysShowToolbar) return true;

        return false;
    }

    private unsafe void UpdateTitle()
    {
        // 建構子（插件載入當下）也會走到：AgentModule 還沒建好或 TerritoryInfo 位址解不出時不改標題，不讓插件載入失敗。
        var agentMap = AgentMap.Instance();
        if (agentMap == null) return;

        if (TerritoryInfo.Addresses.Instance.Value == 0) {
            if (!territoryInfoMissingLogged) {
                territoryInfoMissingLogged = true;
                Service.Log.Warning("[Mappy] 找不到 TerritoryInfo 的位址（遊戲改版後特徵碼對不上），地圖視窗標題不顯示地區名稱，其餘功能照常。");
            }
            return;
        }

        var territoryInfo = TerritoryInfo.Instance();

        var mapChanged = lastMapId != agentMap->SelectedMapId;
        var areaChanged = lastAreaPlaceNameId != territoryInfo->AreaPlaceNameId;
        var subAreaChanged = lastSubAreaPlaceNameId != territoryInfo->SubAreaPlaceNameId;
        var locationChanged = mapChanged || areaChanged || subAreaChanged;

        if (!locationChanged) return;
        var subLocationString = string.Empty;

        // 台服的 Map 表不保證涵蓋所有區域（PvP／新內容尤其），GetRow 對缺列會擲例外。
        // 查不到就不加標題文字，並印一行 Information 級診斷。
        var selectedMapId = agentMap->SelectedMapId;
        var mapData = Service.DataManager.GetExcelSheet<Map>().GetRowOrDefault(selectedMapId);

        if (mapData is null) {
            if (lastMissingMapId != selectedMapId) {
                lastMissingMapId = selectedMapId;
                Service.Log.Information($"[Mappy] Map 表中找不到 MapId {selectedMapId}，本次不套用地圖標題文字。");
            }
        }
        else {
            if (System.SystemConfig.ShowRegionLabel) {
                var mapRegionName = mapData.Value.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (mapRegionName != string.Empty) subLocationString += $" - {mapRegionName}";
            }

            if (System.SystemConfig.ShowMapLabel) {
                var mapName = mapData.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (mapName != string.Empty) subLocationString += $" - {mapName}";
            }
        }

        // Don't show specific locations if we aren't there.
        if (agentMap->SelectedMapId == agentMap->CurrentMapId) {
            if (territoryInfo->AreaPlaceNameId is not 0 && System.SystemConfig.ShowAreaLabel) {
                var areaLabel = Service.DataManager.GetExcelSheet<PlaceName>().GetRowOrDefault(territoryInfo->AreaPlaceNameId);
                if (areaLabel is not null) subLocationString += $" - {areaLabel.Value.Name}";
            }

            if (territoryInfo->SubAreaPlaceNameId is not 0 && System.SystemConfig.ShowSubAreaLabel) {
                var subAreaLabel = Service.DataManager.GetExcelSheet<PlaceName>().GetRowOrDefault(territoryInfo->SubAreaPlaceNameId);
                if (subAreaLabel is not null) subLocationString += $" - {subAreaLabel.Value.Name}";
            }
        }

        WindowName = $"Mappy 地圖{subLocationString}###MappyMapWindow";

        lastMapId = agentMap->SelectedMapId;
        lastAreaPlaceNameId = territoryInfo->AreaPlaceNameId;
        lastSubAreaPlaceNameId = territoryInfo->SubAreaPlaceNameId;
    }

    public void RefreshTitle()
    {
        lastMapId = 0;
        lastAreaPlaceNameId = 0;
        lastSubAreaPlaceNameId = 0;
    }

    private void ProcessInputs()
    {
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
            ImGui.OpenPopup("Mappy_Context_Menu");
        }
        else {
            if (HoveredFlags.Any()) {
                if (System.SystemConfig.EnableShiftDragMove && ImGui.GetIO().KeyShift) {
                    Flags &= ~ImGuiWindowFlags.NoMove;
                }
                else {
                    ProcessMouseScroll();
                    ProcessMapDragStart();
                    Flags |= ImGuiWindowFlags.NoMove;
                }
            }

            ProcessMapDragDragging();
            ProcessMapDragEnd();
        }

        // Draw Context Menu
        mapContextMenu.Draw(MapDrawOffset);
    }

    private unsafe void UpdateStyle()
    {
        if (System.SystemConfig.HideWindowFrame) {
            Flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground;
        }
        else {
            Flags &= ~(ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground);
        }

        if (System.SystemConfig.LockWindow) {
            Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        }
        else {
            Flags &= ~(ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
        }

        if (System.SystemConfig.NoFocusOnAppear) {
            Flags |= ImGuiWindowFlags.NoFocusOnAppearing;
        }
        else {
            Flags &= ~ImGuiWindowFlags.NoFocusOnAppearing;
        }

        if (System.SystemConfig.HideWindowBackground) {
            Flags |= ImGuiWindowFlags.NoBackground;
        }
        else {
            Flags &= ~ImGuiWindowFlags.NoBackground;
        }

        if (Service.KeyState[VirtualKey.ESCAPE] && IsFocused && !IsMapLocked()) {
            AgentMap.Instance()->Hide();
        }

        if (System.SystemConfig.FollowPlayer && Service.ObjectTable is { LocalPlayer: { } localPlayer }) {
            System.MapRenderer.CenterOnGameObject(localPlayer);
        }

        if (System.SystemConfig.LockCenterOnMap) {
            System.SystemConfig.FollowPlayer = false;
            System.MapRenderer.DrawOffset = Vector2.Zero;
        }
    }

    private void UpdateSizePosition()
    {
        var systemConfig = System.SystemConfig;
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var boundsChanged = false;

        if (!IsFocused) {
            if (windowPosition != systemConfig.WindowPosition) {
                ImGui.SetWindowPos(systemConfig.WindowPosition);
            }

            if (windowSize != systemConfig.WindowSize) {
                ImGui.SetWindowSize(systemConfig.WindowSize);
            }
        }
        else {
            // If focused
            if (systemConfig.WindowPosition != windowPosition) {
                systemConfig.WindowPosition = windowPosition;
                boundsChanged = true;
            }

            if (systemConfig.WindowSize != windowSize) {
                systemConfig.WindowSize = windowSize;
                boundsChanged = true;
            }
        }

        // 拖曳／縮放中每幀都在變，只在放開滑鼠那一幀存一次（拖到一半失去焦點也照樣在放開後存）。
        if (boundsSaveGate.Observe(boundsChanged, ImGui.IsMouseDown(ImGuiMouseButton.Left))) {
            SystemConfig.Save();
        }
    }

    private static void DrawSpoilerWarning()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Orange.Vector())) {
            const string warningLine1 = "警告：Mappy 不會避免劇透，並會顯示所有地圖資訊。";
            const string warningLine2 = "若你不希望看到劇透，請勿使用 Mappy。";

            ImGui.SetCursorPos(ImGui.GetContentRegionAvail() / 2.0f - (ImGui.CalcTextSize(warningLine1) * 2.0f) with { X = 0.0f });
            ImGuiHelpers.CenteredText(warningLine1);
            ImGuiHelpers.CenteredText(warningLine2);
        }

        ImGuiHelpers.ScaledDummy(30.0f);
        ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X / 3.0f);
        using (ImRaii.Disabled(!(ImGui.GetIO().KeyShift && ImGui.GetIO().KeyCtrl))) {
            if (ImGui.Button("我瞭解了", new Vector2(ImGui.GetContentRegionAvail().X / 2.0f, 23.0f * ImGuiHelpers.GlobalScale))) {
                System.SystemConfig.AcceptedSpoilerWarning = true;
                SystemConfig.Save();
            }

            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1.0f)) {
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                    ImGui.SetTooltip("按住 Shift + Ctrl 再點擊按鈕以啟用");
                }
            }
        }
    }

    public override unsafe void OnClose()
    {
        AgentMap.Instance()->Hide();

        SystemConfig.Save();
    }

    private void ProcessMouseScroll()
    {
        if (System.SystemConfig.ZoomLocked) return;
        if (ImGui.GetIO().MouseWheel is 0) return;
        if (!HoveredFlags.HasFlag(HoverFlags.WindowInnerFrame)) return;

        if (System.SystemConfig.UseLinearZoom) {
            MapRenderer.MapRenderer.Scale += System.SystemConfig.ZoomSpeed * ImGui.GetIO().MouseWheel;
        }
        else {
            MapRenderer.MapRenderer.Scale *= 1.0f + System.SystemConfig.ZoomSpeed * ImGui.GetIO().MouseWheel;
        }
    }

    private void ProcessMapDragDragging()
    {
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && isDragStarted) {
            System.MapRenderer.DrawOffset += ImGui.GetMouseDragDelta() / MapRenderer.MapRenderer.Scale;
            ImGui.ResetMouseDragDelta();
        }
    }

    private void ProcessMapDragEnd()
    {
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
            isDragStarted = false;
        }
    }

    private void ProcessMapDragStart()
    {
        // Don't allow a drag to start if the window size is changing
        if (ImGui.GetWindowSize() == lastWindowSize) {
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !isDragStarted) {
                isDragStarted = true;
                System.SystemConfig.FollowPlayer = false;
            }
        }
        else {
            lastWindowSize = ImGui.GetWindowSize();
            isDragStarted = false;
        }
    }

    private unsafe bool ShouldFade() =>
        System.SystemConfig.FadeMode.HasFlag(FadeMode.Always) ||
        System.SystemConfig.FadeMode.HasFlag(FadeMode.WhenFocused) && IsFocused ||
        System.SystemConfig.FadeMode.HasFlag(FadeMode.WhenMoving) && AgentMap.Instance()->IsPlayerMoving ||
        System.SystemConfig.FadeMode.HasFlag(FadeMode.WhenUnFocused) && !IsFocused;

    private void RegisterCommands()
    {
        System.CommandManager.RegisterCommand(new ToggleCommandHandler
        {
            UseShowHideText = true,
            BaseActivationPath = "/map",
            EnableDelegate = _ => System.MapWindow.UnCollapseOrShow(),
            DisableDelegate = _ => System.MapWindow.Close(),
            ToggleDelegate = _ => System.MapWindow.UnCollapseOrToggle(),
        });

        System.CommandManager.RegisterCommand(new CommandHandler
        {
            ActivationPath = "/map/follow",
            Delegate = _ =>
            {
                System.SystemConfig.FollowPlayer = true;
                SystemConfig.Save();
            },
        });

        System.CommandManager.RegisterCommand(new CommandHandler
        {
            ActivationPath = "/map/unfollow",
            Delegate = _ =>
            {
                System.SystemConfig.FollowPlayer = false;
                SystemConfig.Save();
            },
        });

        System.CommandManager.RegisterCommand(new ToggleCommandHandler
        {
            BaseActivationPath = "/autofollow",
            EnableDelegate = _ =>
            {
                System.SystemConfig.FollowOnOpen = true;
                SystemConfig.Save();
            },
            DisableDelegate = _ =>
            {
                System.SystemConfig.FollowOnOpen = false;
                SystemConfig.Save();
            },
            ToggleDelegate = _ =>
            {
                System.SystemConfig.FollowOnOpen = !System.SystemConfig.FollowOnOpen;
                SystemConfig.Save();
            },
        });

        System.CommandManager.RegisterCommand(new ToggleCommandHandler
        {
            BaseActivationPath = "/keepopen",
            EnableDelegate = _ =>
            {
                System.SystemConfig.KeepOpen = true;
                SystemConfig.Save();
            },
            DisableDelegate = _ =>
            {
                System.SystemConfig.KeepOpen = false;
                SystemConfig.Save();
            },
            ToggleDelegate = _ =>
            {
                System.SystemConfig.KeepOpen = !System.SystemConfig.KeepOpen;
                SystemConfig.Save();
            },
        });

        System.CommandManager.RegisterCommand(new CommandHandler
        {
            ActivationPath = "/center/player",
            Delegate = _ =>
            {
                if (Service.ObjectTable.LocalPlayer is { } localPlayer) {
                    System.MapRenderer.CenterOnGameObject(localPlayer);
                }
            },
        });

        System.CommandManager.RegisterCommand(new CommandHandler
        {
            ActivationPath = "/center/map",
            Delegate = _ =>
            {
                System.SystemConfig.FollowPlayer = false;
                System.MapRenderer.DrawOffset = Vector2.Zero;
            },
        });
    }

    private static unsafe bool IsMapLocked()
    {
        var addon = Service.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon is null || addon->RootNode is null) return false;

        return (addon->Param & 0x8_0000) > 0;
    }
}
