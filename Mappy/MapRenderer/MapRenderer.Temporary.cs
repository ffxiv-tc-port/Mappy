using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;
using Mappy.Extensions;

namespace Mappy.MapRenderer;

public partial class MapRenderer
{
    /// <summary>
    /// 一組座標相同的暫時標記，一次走訪就累積完。欄位逐一對應
    /// GroupBy → First() / FirstOrNull(IconId 不是 60493 或 0) / Count() / Last() / Max(Scale)。
    /// </summary>
    private struct TempMarkerGroup
    {
        public TempMapMarker First;
        public uint? CorrectIconId;
        public int Count;
        public uint LastType;
        public uint LastIconId;
        public int MaxScale;
    }

    // 每幀清空重用（只在繪製執行緒碰）。鍵是 Vector2(X, Y) 的來源：short 轉 float 一對一，相等性相同。
    private readonly Dictionary<(short X, short Y), int> tempGroupIndex = [];
    private readonly List<TempMarkerGroup> tempGroups = [];

    private unsafe void DrawTemporaryMarkers()
    {
        if (AgentMap.Instance()->SelectedMapSub != AgentMap.Instance()->SelectedMapId) return;

        // Group together icons based on their dataId, this is because square enix shows circles then draws the actual icon overtop
        var validMarkers = new Span<TempMapMarker>(Unsafe.AsPointer(ref AgentMap.Instance()->TempMapMarkers[0]), AgentMap.Instance()->TempMapMarkerCount);
        GroupTemporaryMarkers(validMarkers);

        for (var index = 0; index < tempGroups.Count; index++) {
            ResolveTemporaryMarker(tempGroups[index]).Draw(DrawPosition, Scale);
        }
    }

    // 整份先分完組才開始畫（與 GroupBy 第一次 MoveNext 就建好整張表相同），畫的是當時的複本。
    private void GroupTemporaryMarkers(ReadOnlySpan<TempMapMarker> markers)
    {
        tempGroupIndex.Clear();
        tempGroups.Clear();

        foreach (ref readonly var marker in markers) {
            var isCorrectIcon = marker.MapMarker.IconId is not (60493 or 0);

            if (tempGroupIndex.TryGetValue((marker.MapMarker.X, marker.MapMarker.Y), out var groupIndex)) {
                var group = tempGroups[groupIndex];

                if (group.CorrectIconId is null && isCorrectIcon) {
                    group.CorrectIconId = marker.MapMarker.IconId;
                }

                group.Count++;
                group.LastType = marker.Type;
                group.LastIconId = marker.MapMarker.IconId;
                group.MaxScale = Math.Max(group.MaxScale, marker.MapMarker.Scale);
                tempGroups[groupIndex] = group;
            }
            else {
                tempGroupIndex.Add((marker.MapMarker.X, marker.MapMarker.Y), tempGroups.Count);
                tempGroups.Add(new TempMarkerGroup
                {
                    First = marker,
                    CorrectIconId = isCorrectIcon ? marker.MapMarker.IconId : null,
                    Count = 1,
                    LastType = marker.Type,
                    LastIconId = marker.MapMarker.IconId,
                    MaxScale = marker.MapMarker.Scale,
                });
            }
        }
    }

    private static TempMapMarker ResolveTemporaryMarker(in TempMarkerGroup group)
    {
        // Make a copy of the first marker in the set, we will be mutating this copy.
        var markerCopy = group.First;

        // Get the actual iconId we want, typically the icon for the marker, not the circle
        markerCopy.MapMarker.IconId = group.CorrectIconId ?? markerCopy.MapMarker.IconId;

        // Special handling for WKS Markers (in which both icon ids are 0)
        if (markerCopy.MapMarker.IconId == 0 && group.Count == 2)
        {
            if (markerCopy.Type == 4 && group is { LastType: 6, LastIconId: 0 })
            {
                markerCopy.MapMarker.IconId = DrawHelpers.QuestionMarkIcon;
            }
        }

        // Get the actual radius value for this marker, typically the circle icon will have this value.
        markerCopy.MapMarker.Scale = group.MaxScale;

        return markerCopy;
    }
}
