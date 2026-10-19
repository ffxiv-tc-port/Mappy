using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Extensions;
using Mappy.Extensions;

namespace Mappy.MapRenderer;

public partial class MapRenderer
{
    /// <summary>
    /// 一組 (DataId, Position) 相同的動態標記，一次走訪就累積完。欄位語意逐一對應
    /// GroupBy → First() / FirstOrNull(IconId 不是 60493) / Max(Radius)。
    /// </summary>
    private struct DynamicMarkerGroup
    {
        public MapMarkerData First;
        public uint? CorrectIconId;
        public float MaxRadius;
    }

    // 每幀清空重用（只在繪製執行緒碰）；鍵的相等比較與 GroupBy 同樣是預設比較子，分組結果與順序相同。
    private readonly Dictionary<(ushort DataId, Vector3 Position), int> dynamicGroupIndex = [];
    private readonly List<DynamicMarkerGroup> dynamicGroups = [];

    private unsafe void DrawDynamicMarkers()
    {
        // Group together icons based on their dataId, this is because square enix shows circles then draws the actual icon overtop
        dynamicGroupIndex.Clear();
        dynamicGroups.Clear();

        foreach (var marker in AgentMap.Instance()->EventMarkers) {
            if (dynamicGroupIndex.TryGetValue((marker.DataId, marker.Position), out var groupIndex)) {
                var group = dynamicGroups[groupIndex];

                if (group.CorrectIconId is null && marker.IconId is not 60493) {
                    group.CorrectIconId = marker.IconId;
                }

                // 與 Enumerable.Max(float) 相同：NaN 排在所有數字之前，只有全部是 NaN 才得 NaN。
                if (float.IsNaN(group.MaxRadius) || marker.Radius > group.MaxRadius) {
                    group.MaxRadius = marker.Radius;
                }

                dynamicGroups[groupIndex] = group;
            }
            else {
                dynamicGroupIndex.Add((marker.DataId, marker.Position), dynamicGroups.Count);
                dynamicGroups.Add(new DynamicMarkerGroup
                {
                    First = marker,
                    CorrectIconId = marker.IconId is not 60493 ? marker.IconId : null,
                    MaxRadius = marker.Radius,
                });
            }
        }

        foreach (var group in dynamicGroups) {
            // Make a copy of the first marker in the set, we will be mutating this copy.
            var markerCopy = group.First;

            // Get the actual iconId we want, typically the icon for the fate, not the circle
            markerCopy.IconId = group.CorrectIconId ?? markerCopy.IconId;

            // Get the actual radius value for this marker, typically the circle icon will have this value.
            markerCopy.Radius = group.MaxRadius;

            // Disable radius markings for housing areas
            if (HousingManager.Instance()->CurrentTerritory is not null) {
                markerCopy.Radius = 0.0f;
            }

            markerCopy.Draw(DrawPosition, Scale);
        }
    }
}