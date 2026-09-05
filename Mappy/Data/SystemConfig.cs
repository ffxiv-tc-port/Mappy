using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Serialization;
using Dalamud.Interface;
using KamiLib.Configuration;

namespace Mappy.Data;

public enum CenterTarget
{
    [Description("停用")]
    Disabled = 0,

    [Description("玩家")]
    Player = 1,

    [Description("地圖")]
    Map = 2,
}

[Flags]
public enum FadeMode
{
    [Description("永遠")]
    Always = 1 << 0,

    [Description("移動時")]
    WhenMoving = 1 << 2,

    [Description("取得焦點時")]
    WhenFocused = 1 << 3,

    [Description("失去焦點時")]
    WhenUnFocused = 1 << 4,
}

public class SystemConfig : CharacterConfiguration
{
    public bool UseLinearZoom = false;
    public float ZoomSpeed = 0.25f;
    public float IconScale = 0.50f;
    public bool ShowMiscTooltips = true;
    public bool HideWithGameGui = true;
    public bool HideBetweenAreas = false;
    public bool HideInCombat = false;

    // 台服追加：上游在 PvP 區域會強制關閉 Mappy 並讓出給遊戲原生地圖。
    // 預設改為允許，關閉此選項即回到上游行為。
    public bool AllowInPvP = true;

    public bool KeepOpen = false;
    public bool FollowOnOpen = false;
    public bool FollowPlayer = true;
    public CenterTarget CenterOnOpen = CenterTarget.Disabled;
    public bool ScalePlayerCone = false;
    public float ConeSize = 150.0f;
    public bool ShowRadar = true;
    public bool ShowRadarInDuties = false;
    public Vector4 RadarColor = KnownColor.Gray.Vector() with { W = 0.10f };
    public Vector4 RadarOutlineColor = KnownColor.Gray.Vector() with { W = 0.30f };
    public bool HideWindowFrame = false;
    public bool HideWindowBackground = false;
    public bool EnableShiftDragMove = false;
    public bool LockWindow = false;
    public float FadePercent = 0.60f;
    public FadeMode FadeMode = FadeMode.WhenUnFocused | FadeMode.WhenMoving;
    public Vector2 WindowPosition = new(1024.0f, 700.0f);
    public Vector2 WindowSize = new(500.0f, 500.0f);
    public bool AlwaysShowToolbar = false;
    public bool ShowToolbarOnHover = true;
    public bool ScaleWithZoom = true;
    public bool AcceptedSpoilerWarning = false;
    public Vector4 AreaColor = KnownColor.CornflowerBlue.Vector() with { W = 0.33f };
    public Vector4 AreaOutlineColor = KnownColor.CornflowerBlue.Vector() with { W = 0.30f };
    public Vector4 PlayerConeColor = KnownColor.CornflowerBlue.Vector() with { W = 0.33f };
    public Vector4 PlayerConeOutlineColor = KnownColor.CornflowerBlue.Vector() with { W = 1.0f };
    public bool CenterOnFlag = true;
    public bool CenterOnGathering = true;
    public bool CenterOnQuest = true;
    public bool LockCenterOnMap = false;
    public bool ShowCoordinateBar = true;
    public float ToolbarFade = 0.33f;
    public float CoordinateBarFade = 0.66f;
    public Vector4 CoordinateTextColor = KnownColor.White.Vector();
    public bool ZoomLocked = false;
    public bool ShowPlayers = true;
    public bool SetFlagOnFateClick = false;
    public bool ShowPlayerIcon = true;
    public float PlayerIconScale = 1.0f;
    public float MapScale = 1.0f;
    public bool AutoZoom = false;
    public bool ShowRegionLabel = true;
    public bool ShowMapLabel = true;
    public bool ShowAreaLabel = true;
    public bool ShowSubAreaLabel = true;
    public bool NoFocusOnAppear = false;
    public float LargeAreaTextScale = 1.5f;
    public float SmallAreaTextScale = 1.0f;
    public bool ShowTextLabels = true;
    public bool ShowFogOfWar = true;
    public bool ScaleTextWithZoom = true;
    public float AutoZoomScaleFactor = 0.33f;

    // 台服追加：地圖標記工具提示（危命任務、乙太之光那種浮出來的小框）的文字大小與不透明度。
    // 上游的工具提示完全吃 ImGui 預設值，既有的文字比例與淡化選項都管不到它。
    public float TooltipTextScale = 1.0f;
    public float TooltipOpacity = 1.0f;

    // 工具提示是畫在地圖視窗的 Alpha 樣式之內的，所以本來就會跟著地圖一起淡化。
    // 預設 true ＝ 維持原本行為；關掉之後工具提示只吃上面的 TooltipOpacity。
    public bool TooltipFollowsMapFade = true;

    // 台服追加：在其他玩家的圖示底下墊一個色點，用來一眼分出好友／同部隊／一般玩家。
    // 小隊與團隊成員由 DrawGroupMembers 另外處理，不在這裡上色。
    // 台服追加：補畫遊戲寫給小地圖用的那份標記（其他外掛也會往裡面塞）。
    public bool ShowMiniMapMarkers = true;

    public bool ShowSocialMarkers = true;
    public Vector4 FriendMarkerColor = KnownColor.Gold.Vector() with { W = 0.85f };
    public Vector4 FreeCompanyMarkerColor = KnownColor.MediumSeaGreen.Vector() with { W = 0.85f };
    public Vector4 OtherPlayerMarkerColor = KnownColor.Gray.Vector() with { W = 0.25f };
    public Vector4 SocialMarkerOutlineColor = KnownColor.Black.Vector() with { W = 0.60f };
    public float SocialMarkerRadius = 7.0f;

    // 台服追加：地圖右鍵的「移動到這裡」——把跨區傳送與尋路整段交給 Lifestream。
    // 🔴 只有使用者親手點右鍵選單時才會動，沒有任何事件驅動的自動接手鏈。
    public bool EnableTravelToHere = true;

    // 台服追加：「移動到這裡」允許使用飛行坐騎。
    // ⚠️ 不可飛的區域由 Lifestream 自己退回用走的，這裡不需要（也不該）替它判斷。
    public bool TravelUseFlying = true;

    // 台服追加：其他外掛透過 IPC 放上來的標記，逐「來源」的開關。
    // 來源第一次出現時會自動以 true 加進來。
    public Dictionary<string, bool> IpcSourceEnabled = [];

    // Do not persist this setting
    [JsonIgnore]
    public bool DebugMode = false;

    public static SystemConfig Load() => Service.PluginInterface.LoadConfigFile("System.config.json", () => new SystemConfig());

    public static void Save() => Service.PluginInterface.SaveConfigFile("System.config.json", System.SystemConfig);
}
