using System;
using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Mappy.Classes;
using Mappy.Extensions;
using ExcelDynamicEvent = Lumina.Excel.Sheets.DynamicEvent;

namespace Mappy.Modules;

/// <summary>
/// 新月島／博茲雅的緊急遭遇戰標記：名稱、目前階段的剩餘時間、參加人數與戰鬥進度。
/// 只在繪製執行緒呼叫；每次重新取 director，不保存任何原生指標。
/// </summary>
public class PublicContent : ModuleBase
{
    // 各內容遊戲自己的階段用語，依 Register／Warmup／Battle 排列。
    private static readonly string[] OccultCrescentPhases = ["正在募集人員", "正在準備開始", "戰鬥開始"];
    private static readonly string[] BozjaPhases = ["參戰募集中", "開戰準備中", "戰鬥中"];

    // 名稱一律查 DynamicEvent 表：原生結構裡的名稱是字串指標，刻意不解參。
    private readonly Dictionary<ushort, string> names = [];

    // 委派只在同一次 DrawMapMarker 期間被讀（標記當場用完不保留），所以共用；欄位都是算好的值，沒有指標。
    private string currentName = string.Empty;
    private string currentPhase = string.Empty;
    private TimeSpan? currentRemaining;
    private int currentParticipants;
    private int currentMaxParticipants;
    private int currentProgress;
    private readonly Func<string?> nameText;
    private readonly Func<string?> detailsText;

    public PublicContent()
    {
        nameText = () => currentName;
        detailsText = () => BuildDetails(currentPhase, currentRemaining, currentParticipants, currentMaxParticipants, currentProgress);
    }

    public override unsafe bool ProcessMarker(MarkerInfo markerInfo)
    {
        if (markerInfo.MarkerType is not MarkerType.DynamicEvent) return false;
        if (markerInfo.DataId is not { } dataId) return false;
        if (!TryGetEventContainer(out var container, out var phases)) return false;

        foreach (ref readonly var dynamicEvent in container->Events) {
            if (dynamicEvent.State is DynamicEventState.Inactive || dynamicEvent.MapMarker.DataId != dataId) continue;

            return ApplyDetails(markerInfo, in dynamicEvent, phases);
        }

        return false;
    }

    private bool ApplyDetails(MarkerInfo markerInfo, in DynamicEvent dynamicEvent, string[] phases)
    {
        var phaseIndex = dynamicEvent.State switch
        {
            DynamicEventState.Register => 0,
            DynamicEventState.Warmup => 1,
            DynamicEventState.Battle => 2,
            _ => -1,
        };

        if (phaseIndex < 0) return false;

        var name = GetName(dynamicEvent.DynamicEventId);
        var maxParticipants = (int)dynamicEvent.MaxParticipants;

        currentName = name;
        currentPhase = phases[phaseIndex];
        currentRemaining = dynamicEvent.GetTimeRemaining(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        currentMaxParticipants = maxParticipants;
        currentParticipants = maxParticipants > 0 && dynamicEvent.Participants <= maxParticipants ? dynamicEvent.Participants : -1;
        currentProgress = dynamicEvent.State is DynamicEventState.Battle && dynamicEvent.Progress <= 100 ? dynamicEvent.Progress : -1;

        if (name.Length > 0) {
            markerInfo.PrimaryText = nameText;
        }

        markerInfo.SecondaryText = detailsText;
        return true;
    }

    private static unsafe bool TryGetEventContainer(out DynamicEventContainer* container, out string[] phases)
    {
        container = null;
        phases = BozjaPhases;

        // 特徵碼沒解析到時產生的包裝會擲例外；EventFramework 還沒建好時，遊戲自己的 GetInstance 會解參空指標。
        if (EventFramework.StaticAddressPointers.ppInstance is null) return false;
        if (PublicContentOccultCrescent.MemberFunctionPointers.GetInstance is null) return false;
        if (PublicContentBozja.MemberFunctionPointers.GetInstance is null) return false;
        if (EventFramework.Instance() is null) return false;

        // 兩個 GetInstance 在遊戲裡自己比對 director 類型，不是該內容就回 null。
        var occultCrescent = PublicContentOccultCrescent.GetInstance();
        if (occultCrescent is not null) {
            container = &occultCrescent->DynamicEventContainer;
            phases = OccultCrescentPhases;
            return true;
        }

        var bozja = PublicContentBozja.GetInstance();
        if (bozja is null) return false;

        container = &bozja->DynamicEventContainer;
        return true;
    }

    private string GetName(ushort dynamicEventId)
    {
        if (names.TryGetValue(dynamicEventId, out var name)) return name;

        name = Service.DataManager.GetExcelSheet<ExcelDynamicEvent>().GetRowOrDefault(dynamicEventId)?.Name.ExtractText() ?? string.Empty;
        names[dynamicEventId] = name;
        return name;
    }

    private static string BuildDetails(string phase, TimeSpan? remaining, int participants, int maxParticipants, int progress)
    {
        var text = new StringBuilder(phase);

        if (remaining is { } time) {
            text.Append($"　剩餘時間：{(int)time.TotalMinutes:D2}:{time.Seconds:D2}");
        }

        if (participants >= 0) {
            text.Append($"\n參加人數：{participants}/{maxParticipants}");
        }

        if (progress >= 0) {
            text.Append($"\n進度：{progress}%");
        }

        return text.ToString();
    }
}
