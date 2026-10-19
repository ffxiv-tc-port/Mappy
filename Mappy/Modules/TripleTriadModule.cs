using Mappy.Classes;

namespace Mappy.Modules;

public class TripleTriadModule : ModuleBase
{
    public override bool ProcessMarker(MarkerInfo markerInfo)
    {
        if (markerInfo is not { ObjectiveId: { } objectiveId }) return false;
        if (!System.TripleTriadCache.GetValue(objectiveId)) return false;

        SetCardRewards(markerInfo, objectiveId);
        return true;
    }

    // 閉包只在這裡建立：捕獲參數的閉包在方法開頭就配置，寫在上面等於每個動態標記每幀都配置一次。
    private static void SetCardRewards(MarkerInfo markerInfo, uint objectiveId)
        => markerInfo.SecondaryText = () => System.CardRewardCache.GetValue(objectiveId) ?? string.Empty;
}
