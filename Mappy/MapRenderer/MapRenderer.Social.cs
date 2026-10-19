using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;
using Mappy.Extensions;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using StatusFlags = Dalamud.Game.ClientState.Objects.Enums.StatusFlags;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer
{
    private readonly CompanyTagTextCache companyTagTexts = new();

    /// <summary>
    /// 在其他玩家圖示底下墊一個色點，用來分辨好友／同部隊／一般玩家。
    /// 必須排在 DrawGameObjects 之前，色點才會在圖示底下而不是蓋住圖示。
    /// 部隊簡稱從本幀列舉到的包裝的 Address 讀位元組（Dalamud 的 CompanyTag 屬性解參的就是這個指標），當場用完、不跨幀保存；
    /// 不走 CompanyTag 是因為它每次都重新解析成 SeString，每個人每幀都要配置。好友旗標仍走託管屬性。
    /// </summary>
    private void DrawSocialMarkers()
    {
        if (!System.SystemConfig.ShowSocialMarkers) return;

        var agent = AgentMap.Instance();
        if (agent is null) return;
        if (agent->SelectedMapId != agent->CurrentMapId) return;

        if (Service.ObjectTable is not { LocalPlayer: { } localPlayer }) return;

        var localCompanyTag = GetCompanyTag(localPlayer);

        foreach (var obj in Service.ObjectTable) {
            if (obj is not IPlayerCharacter character) continue;
            if (character.EntityId == localPlayer.EntityId) continue;
            if (!character.IsTargetable) continue;

            // 與 DrawGameObjects 一樣的 150y 範圍，兩邊看到的人才會一致。
            if (Vector3.Distance(character.Position, localPlayer.Position) >= 150.0f) continue;

            // 小隊／團隊成員由 DrawGroupMembers 畫，這裡不重覆上色。
            if (GroupManager.Instance()->MainGroup.IsEntityIdInParty(character.EntityId)) continue;
            if (GroupManager.Instance()->MainGroup.IsEntityIdInAlliance(character.EntityId)) continue;

            if (GetSocialColor(character, localCompanyTag) is not { } color) continue;

            DrawSocialDot(character, color);
        }
    }

    private string GetCompanyTag(IPlayerCharacter character)
        => companyTagTexts.GetText(((CSCharacter*)character.Address)->FreeCompanyTag);

    /// <summary>
    /// 優先序：好友 &gt; 同部隊 &gt; 一般玩家。
    /// </summary>
    private Vector4? GetSocialColor(IPlayerCharacter character, string localCompanyTag)
    {
        if (character.StatusFlags.HasFlag(StatusFlags.Friend)) {
            return System.SystemConfig.FriendMarkerColor;
        }

        // 沒有加入部隊的人 CompanyTag 是空字串，空字串不能拿來配對，否則所有無部隊玩家會互相配成同部隊。
        if (localCompanyTag.Length is not 0 &&
            string.Equals(GetCompanyTag(character), localCompanyTag, StringComparison.Ordinal)) {
            return System.SystemConfig.FreeCompanyMarkerColor;
        }

        // 一般玩家只有在「顯示其他玩家」也開著時才畫，否則會出現沒有圖示的孤立色點。
        if (System.SystemConfig.ShowPlayers) {
            return System.SystemConfig.OtherPlayerMarkerColor;
        }

        return null;
    }

    private void DrawSocialDot(IPlayerCharacter character, Vector4 color)
    {
        var position = ImGui.GetWindowPos() +
                       DrawPosition +
                       (character.GetMapPosition() -
                        DrawHelpers.GetMapOffsetVector() +
                        DrawHelpers.GetMapCenterOffsetVector()) * Scale;

        var radius = System.SystemConfig.SocialMarkerRadius * (System.SystemConfig.ScaleWithZoom ? Scale : 1.0f);
        if (radius < 1.0f) return;

        ImGui.GetWindowDrawList().AddCircleFilled(position, radius, ImGui.GetColorU32(color));
        ImGui.GetWindowDrawList().AddCircle(position, radius, ImGui.GetColorU32(System.SystemConfig.SocialMarkerOutlineColor), 0, 2.0f);
    }
}
