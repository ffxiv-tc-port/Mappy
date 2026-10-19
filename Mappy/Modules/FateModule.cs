using System;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.Interop;
using Mappy.Classes;
using Mappy.Extensions;

namespace Mappy.Modules;

public class FateModule : ModuleBase
{
    // 遊戲字型裡新月島 F.A.T.E. 用的等級圖示（Dalamud 的 SeIconChar 沒有列）；台服 Axis 字型有這個字。
    private const string OccultCrescentLevelIcon = "\uE037";

    // 契約：只在繪製執行緒呼叫，委派只在同一次 DrawMapMarker 期間被讀（標記當場用完不保留），所以共用。
    // 只存 FateId 與算好的剩餘時間，要顯示時再從 FateManager 重查，不保存原生指標。
    private ushort currentFateId;
    private TimeSpan currentTimeRemaining;
    private readonly Func<string?> primaryText;
    private readonly Func<string?> secondaryTextWithTime;
    private readonly Func<string?> secondaryTextProgressOnly;

    public FateModule()
    {
        primaryText = GetPrimaryText;
        secondaryTextWithTime = GetSecondaryTextWithTime;
        secondaryTextProgressOnly = GetSecondaryTextProgressOnly;
    }

    public override bool ProcessMarker(MarkerInfo markerInfo)
    {
        if (markerInfo.MarkerType is not MarkerType.Fate) return false;
        if (FindFate(markerInfo.DataId) is not { } fate) return false;

        return ApplyFate(markerInfo, fate);
    }

    private unsafe bool ApplyFate(MarkerInfo markerInfo, Pointer<FateContext> fate)
    {
        var timeRemaining = fate.GetTimeRemaining();

        currentFateId = fate.Value->FateId;
        currentTimeRemaining = timeRemaining;
        markerInfo.PrimaryText = primaryText;

        // Don't show additional information for any fate that is preparing
        if (fate.Value->State is FateState.Preparing) return true;

        if (timeRemaining >= TimeSpan.Zero) {
            markerInfo.SecondaryText = secondaryTextWithTime;

            if (timeRemaining.TotalSeconds <= 300) {
                markerInfo.RadiusColor = fate.GetColor();
                markerInfo.RadiusOutlineColor = fate.GetColor();
            }
        }
        else {
            markerInfo.SecondaryText = secondaryTextProgressOnly;
        }

        return true;
    }

    // 等價於 Fates.FirstOrNull(fate => fate.Value->FateId == dataId)：同一段 [First, Last)，取第一個相符的。
    private static unsafe Pointer<FateContext>? FindFate(uint? dataId)
    {
        foreach (var fate in FateManager.Instance()->Fates) {
            if (fate.Value->FateId == dataId) return fate;
        }

        return null;
    }

    private unsafe string? GetPrimaryText()
    {
        if (FindFate(currentFateId) is not { } fate) return string.Empty;

        // 新月島等內容的 F.A.T.E. 等級上限是 255，等級欄沒有意義。
        var level = fate.Value->MaxLevel is 255 ? "??" : fate.Value->Level.ToString();
        var levelKind = fate.Value->EurekaFate is 3 ? OccultCrescentLevelIcon : "等級 ";
        return $"{levelKind}{level} {fate.Value->Name}";
    }

    private unsafe string? GetSecondaryTextWithTime()
        => FindFate(currentFateId) is { } fate ? $"剩餘時間：{currentTimeRemaining:mm\\:ss}\n進度：{fate.Value->Progress}%" : string.Empty;

    private unsafe string? GetSecondaryTextProgressOnly()
        => FindFate(currentFateId) is { } fate ? $"進度：{fate.Value->Progress}%" : string.Empty;
}
