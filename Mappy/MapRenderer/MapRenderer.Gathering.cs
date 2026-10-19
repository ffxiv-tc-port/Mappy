using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Extensions;

namespace Mappy.MapRenderer;

public partial class MapRenderer
{
    private unsafe void DrawGatheringMarkers()
    {
        var agent = AgentMap.Instance();
        if (agent is null) return;

        // 這份標記描述的是「目前所在區域」，翻到別張地圖時不能拿來畫。
        if (agent->SelectedMapId != agent->CurrentMapId) return;

        foreach (var marker in agent->MiniMapGatheringMarkers) {
            marker.Draw(DrawPosition, Scale);
        }
    }
}