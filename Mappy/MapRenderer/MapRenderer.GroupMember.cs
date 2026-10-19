using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer
{
    // 契約：只在繪製執行緒用。成員不給閉包捕獲（每格會複製整個 PartyMember 進閉包，連被略過的空格也是）；
    // 提示委派只在一次 DrawMapMarker 期間讀 tooltipMemberIndex，當場從 GroupManager 重查那一格，不保存指標。
    private readonly MarkerInfo groupMemberMarker = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };
    private Func<string?>? groupMemberTooltip;
    private int tooltipMemberIndex = -1;
    private bool tooltipMemberIsAlliance;

    private void DrawGroupMembers()
    {
        var partyMembers = GroupManager.Instance()->MainGroup.PartyMembers[..GroupManager.Instance()->MainGroup.MemberCount];

        for (var index = 0; index < partyMembers.Length; index++) {
            ref var partyMember = ref partyMembers[index];
            if (partyMember.EntityId is 0xE0000000) continue;
            if (partyMember.TerritoryType != AgentMap.Instance()->SelectedTerritoryId) continue;

            DrawGroupMember(ref partyMember, 60421, index, false);
        }

        var allianceMembers = GroupManager.Instance()->MainGroup.AllianceMembers;

        for (var index = 0; index < allianceMembers.Length; index++) {
            ref var allianceMember = ref allianceMembers[index];
            if (allianceMember.EntityId is 0xE0000000) continue;
            if (AgentMap.Instance()->SelectedMapId != AgentMap.Instance()->CurrentMapId) continue;

            DrawGroupMember(ref allianceMember, 60403, index, true);
        }
    }

    private void DrawGroupMember(ref PartyMember member, uint iconId, int index, bool isAlliance)
    {
        var markerInfo = groupMemberMarker;
        markerInfo.ResetForReuse(
            (new Vector2(member.Position.X, member.Position.Z) * DrawHelpers.GetMapScaleFactor() -
             DrawHelpers.GetMapOffsetVector() +
             DrawHelpers.GetMapCenterOffsetVector()) * Scale,
            DrawPosition,
            Scale);
        markerInfo.IconId = iconId;
        markerInfo.PrimaryText = groupMemberTooltip ??= GetGroupMemberTooltip;

        tooltipMemberIndex = index;
        tooltipMemberIsAlliance = isAlliance;
        DrawHelpers.DrawMapMarker(markerInfo);
        tooltipMemberIndex = -1;
    }

    private string? GetGroupMemberTooltip()
    {
        if (tooltipMemberIndex < 0) return string.Empty;

        ref var group = ref GroupManager.Instance()->MainGroup;
        ref var member = ref tooltipMemberIsAlliance ? ref group.AllianceMembers[tooltipMemberIndex] : ref group.PartyMembers[tooltipMemberIndex];

        return $"等級 {member.Level} {member.NameString}";
    }
}
