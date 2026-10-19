using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Extensions;

namespace Mappy.MapRenderer;

public partial class MapRenderer
{
    // 標記數在迴圈開始時讀一次就定下來：點地圖連結會在迴圈中途重設標記，照舊走完原本的數量。
    private unsafe void DrawStaticMapMarkers()
    {
        var count = AgentMap.Instance()->MapMarkerCount;

        for (var index = 0; index < count; index++) {
            ref var marker = ref AgentMap.Instance()->MapMarkers[index];

            if (marker.MapMarker.IconId is 0) continue;

            marker.Draw(index, DrawPosition, Scale);
        }
    }

    private unsafe void DrawStaticTextMarkers()
    {
        var count = AgentMap.Instance()->MapMarkerCount;

        for (var index = 0; index < count; index++) {
            ref var marker = ref AgentMap.Instance()->MapMarkers[index];

            if (marker.MapMarker.IconId is not 0) continue;
            if (marker.MapMarker.Index is 0) continue;
            if (marker.MapMarker.SubtextOrientation is 0) continue;

            marker.Draw(index, DrawPosition, Scale);
        }
    }
}