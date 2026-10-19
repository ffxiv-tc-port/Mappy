using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Extensions;
using Lumina.Excel.Sheets;
using Mappy.Classes;
using Mappy.Extensions;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace Mappy.MapRenderer;

public partial class MapRenderer
{
    // 契約：只在繪製執行緒用。DrawMapMarker 當場用完就放手，所以所有物件共用一個 MarkerInfo；
    // tooltipObject 只在一次 DrawMapMarker 期間有值——物件表的包裝會被就地改寫，不可留到下一幀。
    private readonly MarkerInfo gameObjectMarker = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };
    private IGameObject? tooltipObject;
    private Func<string?>? gameObjectTooltip;

    private unsafe void DrawGameObjects()
    {
        if (AgentMap.Instance()->SelectedMapId != AgentMap.Instance()->CurrentMapId) return;

        if (Service.ObjectTable is not { LocalPlayer: { } player } objectTable) return;

        if (System.SystemConfig.ShowRadar) {
            // 與 KamiLib IsBoundByDuty() 同三個旗標、同短路；那支每次呼叫都配置一個 params 陣列。
            var boundByDuty = Service.Condition[ConditionFlag.BoundByDuty] ||
                              Service.Condition[ConditionFlag.BoundByDuty56] ||
                              Service.Condition[ConditionFlag.BoundByDuty95];

            if ((boundByDuty && System.SystemConfig.ShowRadarInDuties) || !boundByDuty) {
                DrawRadar(player);
            }
        }

        // 由後往前：繪製順序與 ObjectTable.Reverse() 相同。
        for (var index = objectTable.Length - 1; index >= 0; index--) {
            if (objectTable[index] is not { } obj) continue;
            if (!obj.IsTargetable) continue;
            if (GroupManager.Instance()->MainGroup.IsEntityIdInParty(obj.EntityId)) continue;
            if (GroupManager.Instance()->MainGroup.IsEntityIdInAlliance(obj.EntityId)) continue;
            if (Vector3.Distance(obj.Position, player.Position) >= 150.0f) continue;

            uint iconId = obj.ObjectKind switch
            {
                ObjectKind.Player when GroupManager.Instance()->MainGroup.MemberCount is 0 && System.SystemConfig.ShowPlayers => 60421,
                ObjectKind.Player when System.SystemConfig.ShowPlayers => 60444,
                ObjectKind.BattleNpc when IsBoss(obj) && obj.TargetObject is null => 60402,
                ObjectKind.BattleNpc when IsBoss(obj) && obj.TargetObject is not null => 60401,
                ObjectKind.BattleNpc when obj is { SubKind: (int)BattleNpcSubKind.Enemy, TargetObject: not null } => 60422,
                ObjectKind.BattleNpc when obj is { SubKind: (int)BattleNpcSubKind.Enemy, TargetObject: null } => 60424,
                ObjectKind.BattleNpc when obj.SubKind == (int)BattleNpcSubKind.Pet => 60961,
                ObjectKind.Treasure => 60003,
                ObjectKind.GatheringPoint => System.GatheringPointIconCache.GetValue(obj.BaseId),
                ObjectKind.EventObj when IsAetherCurrent(obj) => 60653,
                _ => 0
            };

            // DrawMapMarker 對 IconId 0 什麼都不做。
            if (iconId is 0) continue;

            var markerInfo = gameObjectMarker;
            markerInfo.ResetForReuse(
                (obj.GetMapPosition() -
                 DrawHelpers.GetMapOffsetVector() +
                 DrawHelpers.GetMapCenterOffsetVector()) * Scale,
                DrawPosition,
                Scale);
            markerInfo.IconId = iconId;
            markerInfo.PrimaryText = gameObjectTooltip ??= () => tooltipObject is { } target ? GetTooltipForGameObject(target) : string.Empty;

            tooltipObject = obj;
            DrawHelpers.DrawMapMarker(markerInfo);
            tooltipObject = null;
        }
    }

    private void DrawRadar(IPlayerCharacter gameObjectCenter)
    {
        var position = ImGui.GetWindowPos() +
                       DrawPosition +
                       (gameObjectCenter.GetMapPosition() -
                        DrawHelpers.GetMapOffsetVector() +
                        DrawHelpers.GetMapCenterOffsetVector()) * Scale;

        ImGui.GetWindowDrawList().AddCircleFilled(position, 150.0f * Scale, ImGui.GetColorU32(System.SystemConfig.RadarColor));
        ImGui.GetWindowDrawList().AddCircle(position, 150.0f * Scale, ImGui.GetColorU32(System.SystemConfig.RadarOutlineColor));
    }

    private string GetTooltipForGameObject(IGameObject obj)
    {
        return obj switch
        {
            IBattleNpc { Level: > 0 } battleNpc => $"等級 {battleNpc.Level} {battleNpc.Name}",
            IPlayerCharacter { Level: > 0 } playerCharacter => $"等級 {playerCharacter.Level} {playerCharacter.Name}",
            _ => obj.ObjectKind switch
            {
                ObjectKind.GatheringPoint => System.GatheringPointNameCache.GetValue((obj.BaseId, obj.Name.ToString())) ?? string.Empty,
                ObjectKind.Treasure => obj.Name.ToString(),
                ObjectKind.EventObj when IsAetherCurrent(obj) => obj.Name.ToString(),
                _ => string.Empty
            },
        };
    }

    private unsafe bool IsAetherCurrent(IGameObject gameObject)
    {
        if (gameObject.ObjectKind is not ObjectKind.EventObj) return false;

        var csEventObject = (GameObject*)gameObject.Address;
        if (csEventObject is null) return false;

        if (csEventObject->EventHandler is null) return false;
        return csEventObject->EventHandler->Info.EventId.ContentId == EventHandlerContent.AetherCurrent;
    }

    private bool IsBoss(IGameObject chara) => Service.DataManager.GetExcelSheet<BNpcBase>().GetRow(chara.BaseId).Rank is 2 or 6;
}
