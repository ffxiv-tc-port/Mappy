using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Classes;
using KamiLib.Extensions;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Mappy.Classes;
using Mappy.Extensions;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace Mappy.Controllers;

public unsafe class IntegrationsController : IDisposable
{
    private readonly Hook<AgentMap.Delegates.ShowMap>? showMapHook;
    private readonly Hook<AgentMap.Delegates.OpenMap>? openMapHook;

    public IntegrationsController()
    {
        // 位址沒解出時不建那一個 hook（HookFromAddress 對 0 位址會擲，插件建構子就中斷）；兩個各自獨立。
        var showMap = AgentMap.MemberFunctionPointers.ShowMap;
        if (showMap is null)
        {
            Service.Log.Warning("[Mappy] 找不到 AgentMap.ShowMap 的位址（遊戲改版後特徵碼對不上），不攔截遊戲的開地圖動作：「保持開啟」與戰鬥中／切換區域時不開地圖等條件停用，其餘功能照常。");
        }
        else
        {
            showMapHook ??= Service.Hooker.HookFromAddress<AgentMap.Delegates.ShowMap>(showMap, OnShowHook);
        }

        var openMap = AgentMap.MemberFunctionPointers.OpenMap;
        if (openMap is null)
        {
            Service.Log.Warning("[Mappy] 找不到 AgentMap.OpenMap 的位址（遊戲改版後特徵碼對不上），從任務／採集／旗標／傳送連結開地圖時不會自動切換地圖與置中，其餘功能照常。");
        }
        else
        {
            openMapHook ??= Service.Hooker.HookFromAddress<AgentMap.Delegates.OpenMap>(openMap, OnOpenMapHook);
        }

        if (!IsPvPBlocked())
        {
            EnableIntegrations();
        }

        Service.ClientState.EnterPvP += ApplyPvPPolicy;
        Service.ClientState.LeavePvP += ApplyPvPPolicy;
    }

    public void Dispose()
    {
        DisableIntegrations();

        showMapHook?.Dispose();
        openMapHook?.Dispose();

        Service.ClientState.EnterPvP -= ApplyPvPPolicy;
        Service.ClientState.LeavePvP -= ApplyPvPPolicy;
    }

    /// <summary>
    /// PvP 區域是否要把地圖讓給遊戲原生介面（＝上游原本的行為）。
    /// 設定「PvP 中允許開啟地圖」開啟時永遠回傳 false。
    /// </summary>
    public static bool IsPvPBlocked()
        => Service.ClientState is { IsPvP: true } && !System.SystemConfig.AllowInPvP;

    /// <summary>
    /// 依目前所在區域與設定，決定要不要接管地圖。切換 PvP 進出、或使用者改設定時呼叫。
    /// </summary>
    public void ApplyPvPPolicy()
    {
        if (IsPvPBlocked())
        {
            DisableIntegrations();
            return;
        }

        if (Service.ClientState is { IsPvP: true })
        {
            Service.Log.Information("[Mappy] 位於 PvP 區域，依設定「PvP 中允許開啟地圖」繼續接管地圖。");
        }

        EnableIntegrations();
    }

    private void EnableIntegrations()
    {
        Service.Log.Debug("Enabling Integrations");

        showMapHook?.Enable();
        openMapHook?.Enable();

        System.AreaMapController.EnableIntegrations();
        System.FlagController.EnableIntegrations();
    }

    private void DisableIntegrations()
    {
        Service.Log.Debug("Disabling Integrations");

        showMapHook?.Disable();
        openMapHook?.Disable();

        System.AreaMapController.DisableIntegrations();
        System.FlagController.DisableIntegrations();
    }

    private void OnShowHook(AgentMap* agent, bool a1, bool a2) =>
        HookSafety.ExecuteSafe(() =>
        {
            Service.Log.Debug("[OnShow] Beginning Show");

            if (!ShouldShowMap())
            {
                Service.Log.Debug("[OnShow] Condition to open map is rejected, aborting.");
                return;
            }

            if (AgentMap.Instance()->AddonId is not 0 && AgentMap.Instance()->CurrentMapId != AgentMap.Instance()->SelectedMapId)
            {
                if (!System.SystemConfig.KeepOpen)
                {
                    AgentMap.Instance()->Hide();
                }

                Service.Log.Debug("[OnShow] Vanilla tried to return to current map, aborted.");
                return;
            }

            if (System.SystemConfig.KeepOpen)
            {
                Service.Log.Debug("[OnShow] Keeping Open");
                return;
            }

            showMapHook!.Original(agent, a1, a2);
        }, Service.Log, "Exception during OnShowHook");

    private void OnOpenMapHook(AgentMap* agent, OpenMapInfo* mapInfo) =>
        HookSafety.ExecuteSafe(() =>
        {
            openMapHook!.Original(agent, mapInfo);

            switch (mapInfo->Type)
            {
                case MapType.QuestLog:
                    ProcessQuestLink(agent, mapInfo);
                    break;

                case MapType.GatheringLog:
                    ProcessGatheringLink(agent);
                    break;

                case MapType.FlagMarker:
                    ProcessFlagLink(agent);
                    break;

                case MapType.Bozja:
                    ProcessForayLink(agent, mapInfo);
                    break;

                case MapType.MobHunt:
                case MapType.SharedFate:
                case MapType.Teleport:
                case MapType.Treasure:
                    ProcessTeleportLink(agent, mapInfo);
                    break;

                // This appears to get triggered after a Teleport/Shared Fate teleport event.
                case MapType.Centered:

                case MapType.AetherCurrent:
                default:
                    Service.Log.Debug($"[OpenMap] Ignoring MapType: {mapInfo->Type}");
                    break;
            }

            if (System.SystemConfig.AutoZoom)
            {
                MapRenderer.MapRenderer.Scale = DrawHelpers.GetMapScaleFactor() * System.SystemConfig.AutoZoomScaleFactor;
            }
        }, Service.Log, "Exception during OpenMap");

    private void ProcessQuestLink(AgentMap* agent, OpenMapInfo* mapInfo)
    {
        Service.Log.Debug("[OpenMap] Processing QuestLog Event");

        var targetMapId = mapInfo->MapId;

        if (GetMapIdForQuest(mapInfo) is { } foundMapId)
        {
            Service.Log.Debug($"[OpenMap] GetMapIdForQuest identified Quest Target Map as MapId: {foundMapId}");

            if (targetMapId is 0)
            {
                Service.Log.Debug($"[OpenMap] targetMapId was {targetMapId} using foundMapId: {foundMapId}");
                targetMapId = foundMapId;
            }
        }

        if (agent->SelectedMapId != targetMapId)
        {
            Service.Log.Debug($"[OpenMap] Opening MapId: {targetMapId}");
            OpenMap(targetMapId);
        }
        else
        {
            Service.Log.Debug($"[OpenMap] Already in MapId: {targetMapId}, aborting.");
        }

        if (System.SystemConfig.CenterOnQuest)
        {
            ref var targetMarker = ref agent->TempMapMarkers[0].MapMarker;
            CenterOnMarker(targetMarker);
            Service.Log.Debug($"[OpenMap] Centering Map on X = {targetMarker.X}, Y = {targetMarker.Y}");
        }

        System.MapWindow.ProcessingCommand = true;
    }

    private static void ProcessGatheringLink(AgentMap* agent)
    {
        Service.Log.Debug("[OpenMap] Processing GatheringLog Event");

        if (System.SystemConfig.CenterOnGathering)
        {
            ref var targetMarker = ref agent->TempMapMarkers[0].MapMarker;

            CenterOnMarker(targetMarker);
            Service.Log.Debug($"[OpenMap] Centering Map on X = {targetMarker.X}, Y = {targetMarker.Y}");
        }

        System.MapWindow.ProcessingCommand = true;
    }

    private static void ProcessFlagLink(AgentMap* agent)
    {
        Service.Log.Debug("[OpenMap] Processing FlagMarker Event");

        if (System.SystemConfig.CenterOnFlag)
        {
            ref var targetMarker = ref agent->FlagMapMarkers[0].MapMarker;

            CenterOnMarker(targetMarker);
            Service.Log.Debug($"[OpenMap] Centering Map on X = {targetMarker.X}, Y = {targetMarker.Y}");
        }

        System.MapWindow.ProcessingCommand = true;
    }

    private static void ProcessForayLink(AgentMap* agent, OpenMapInfo* mapInfo)
    {
        Service.Log.Debug("[OpenMap] Processing Bozja Event");

        var eventMarker = agent->EventMarkers.FirstOrNull(marker => marker.DataId == mapInfo->FateId && marker.Flags == 0x40);
        if (eventMarker is not null)
        {
            CenterOnMarker(eventMarker.Value);
        }

        System.MapWindow.ProcessingCommand = true;
    }

    private void ProcessTeleportLink(AgentMap* agent, OpenMapInfo* mapInfo)
    {
        Service.Log.Debug("[OpenMap] Processing Teleport Event");

        var targetMapId = mapInfo->MapId;

        if (agent->CurrentMapId != targetMapId)
        {
            Service.Log.Debug($"[OpenMap] Opening MapId: {targetMapId}");

            OpenMap(mapInfo->MapId);

            System.MapWindow.ProcessingCommand = true;

            return;
        }

        Service.Log.Debug($"[OpenMap] Already in MapId: {targetMapId}, aborting.");
        System.MapWindow.ProcessingCommand = true;
    }

    public void OpenMap(uint mapId)
    {
        AgentMap.Instance()->OpenMapByMapId(mapId, 0, true);

        // Since this is effecting state, we need to keep an eye on it for potential issues.
        AgentMap.Instance()->ResetMapMarkers();
    }

    public void OpenOccupiedMap() => OpenMap(AgentMap.Instance()->CurrentMapId);

    private static void CenterOnMarker(MapMarkerBase marker)
    {
        var coordinates = new Vector2(marker.X, marker.Y) / 16.0f * DrawHelpers.GetMapScaleFactor() - DrawHelpers.GetMapOffsetVector();

        System.SystemConfig.FollowPlayer = false;
        System.MapRenderer.DrawOffset = -coordinates;
    }

    private static void CenterOnMarker(MapMarkerData marker)
    {
        var coordinates = marker.Position.AsMapVector() * DrawHelpers.GetMapScaleFactor() - DrawHelpers.GetMapOffsetVector();

        System.SystemConfig.FollowPlayer = false;
        System.MapRenderer.DrawOffset = -coordinates;
    }

    public static bool ShouldShowMap()
    {
        if (Service.ClientState is { IsLoggedIn: false }) return false;
        if (IsPvPBlocked()) return false;
        if (System.SystemConfig.HideInCombat && Service.Condition.IsInCombat()) return false;
        if (System.SystemConfig.HideBetweenAreas && Service.Condition.IsBetweenAreas()) return false;
        if (System.SystemConfig.HideWithGameGui && !IsNamePlateAddonVisible()) return false;
        if (System.SystemConfig.HideWithGameGui && Control.Instance()->TargetSystem.TargetModeIndex is 1) return false;

        return true;
    }

    private static bool IsNamePlateAddonVisible() => !RaptureAtkUnitManager.Instance()->UiFlags.HasFlag(UIModule.UiFlags.Nameplates);

    private uint? GetMapIdForQuest(OpenMapInfo* mapInfo)
    {
        // 🔴 TitleString 是 Utf8String,它的 ToString() 把整段位元組當 UTF-8 直接解碼,
        //    完全不剝 SeString payload。從聊天連結／任務日誌開地圖時這個標題常常帶 payload,
        //    解出來會混進 U+FFFD 與控制位元組;而比對的另一端是 Lumina 的 ExtractText()
        //    (payload 已剝掉)⇒ 兩端基準不同、string.Equals 恆假。
        //    後果是靜默的:找不到對應地圖就落回預設行為,使用者只會覺得「連結開錯地圖」。
        var mapTitle = new ReadOnlySeStringSpan(mapInfo->TitleString.AsSpan()).ExtractText();

        // 標題只有名稱，同名任務／理符不少（例如「冒險者入門」×8）⇒ 每一步都收齊同名候選再挑，不取第一筆。
        // OpenMapInfo.QuestId 的語意沒驗證過，只拿來在同名候選之間挑，不單獨採信。
        var hintId = mapInfo->QuestId;
        var hits = new List<QuestTitleMatch.Candidate>();

        foreach (var leveQuest in QuestManager.Instance()->LeveQuests)
        {
            if (leveQuest.LeveId is 0) continue;

            var leveData = Service.DataManager.GetExcelSheet<Leve>().GetRow(leveQuest.LeveId);
            if (!QuestTitleMatch.NameEquals(leveData.Name.ExtractText(), mapTitle)) continue;

            hits.Add(new QuestTitleMatch.Candidate(leveQuest.LeveId, leveData.LevelStart.Value.Map.RowId));
        }

        if (Decide(hits, id => hintId == id, mapTitle, hintId, "進行中的理符", out var leveMap) is { } leveDone)
            return leveDone ? leveMap : null;

        foreach (var quest in QuestManager.Instance()->NormalQuests)
        {
            if (quest.QuestId is 0) continue;

            // Is this the quest we are looking for?
            var questData = Service.DataManager.GetExcelSheet<Quest>().GetRow(quest.QuestId + 65536u);
            if (!QuestTitleMatch.NameEquals(questData.Name.ExtractText(), mapTitle)) continue;

            var mapId = questData
                .TodoParams.FirstOrDefault(param => param.ToDoCompleteSeq == quest.Sequence)
                .ToDoLocation.FirstOrDefault(location => location is not { RowId: 0, ValueNullable: null })
                .Value.Map.RowId;
            hits.Add(new QuestTitleMatch.Candidate(questData.RowId, mapId));
        }

        if (Decide(hits, id => IsQuestHint(hintId, id), mapTitle, hintId, "進行中的任務", out var activeMap) is { } activeDone)
            return activeDone ? activeMap : null;

        foreach (var quest in Service.DataManager.GetExcelSheet<Quest>())
        {
            if (quest is not { IssuerLocation: { IsValid: true, RowId: not 0 } }) continue;
            if (!QuestTitleMatch.NameEquals(quest.Name.ExtractText(), mapTitle)) continue;

            hits.Add(new QuestTitleMatch.Candidate(quest.RowId, quest.IssuerLocation.Value.Map.RowId));
        }

        // 同名時先排除已完成的（可接的優先）；排除後一個不剩就維持全部候選。
        if (hits.Count > 1 && QuestManager.MemberFunctionPointers.IsQuestComplete is not null)
        {
            var open = hits.FindAll(hit => !QuestManager.IsQuestComplete(hit.Id));
            if (open.Count > 0)
                hits = open;
        }

        return Decide(hits, id => IsQuestHint(hintId, id), mapTitle, hintId, "任務表", out var issuerMap) is true ? issuerMap : null;
    }

    /// <returns>null＝這一步沒有同名候選、往下一步找；true＝挑到唯一（<paramref name="mapId"/>）；false＝同名分不出，不做。</returns>
    private static bool? Decide(List<QuestTitleMatch.Candidate> hits, Func<uint, bool> hintMatches, string mapTitle, uint hintId,
                                string source, out uint mapId)
    {
        var outcome = QuestTitleMatch.Pick(hits, hintId is 0 ? null : hintMatches, out mapId);
        if (outcome is QuestTitleMatch.Outcome.Ambiguous)
        {
            Service.Log.Information($"[Mappy] 地圖標題「{mapTitle}」在{source}裡有 {hits.Count} 筆同名、指向不同地圖，分不出是哪一筆，不採用名稱反查的結果" +
                                    $"（候選 {string.Join("、", hits.Select(hit => $"{hit.Id}→地圖 {hit.MapId}"))}；OpenMapInfo.QuestId={hintId}）。");
        }
        else if (outcome is QuestTitleMatch.Outcome.Unique && hits.Count > 1)
        {
            Service.Log.Information($"[Mappy] 地圖標題「{mapTitle}」在{source}裡有 {hits.Count} 筆同名，已挑出地圖 {mapId}（OpenMapInfo.QuestId={hintId}）。");
        }

        var result = outcome switch
        {
            QuestTitleMatch.Outcome.Unique => true,
            QuestTitleMatch.Outcome.Ambiguous => false,
            _ => (bool?)null,
        };
        hits.Clear();
        return result;
    }

    // 任務 ID 在表裡是 65536 起跳、在 QuestManager 裡是去掉高位的 ushort；提示是哪一種不確定，兩種都認。
    private static bool IsQuestHint(uint hintId, uint questRowId) => hintId == questRowId || hintId == (questRowId & 0xFFFF);
}