using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;
using Mappy.Controllers;

namespace Mappy.MapRenderer;

/// <summary>
/// 路線疊加層：線畫在地圖標記底下（DrawRouteOverlayLines），點畫在角色圖示底下（DrawRouteOverlayPoints）。
/// 契約：兩段在同一幀、依序呼叫；線那一段負責準備這一幀的投影、快照與螢幕座標，點那一段沿用。
/// 每幀零配置：所有緩衝區都是重用的，文字一律走 ImU8String。
/// </summary>
public unsafe partial class MapRenderer
{
    private const float RouteHoverRadius = 6.0f;
    private const float VnavLineThickness = 3.0f;
    private const float RouteEndRingRadius = 6.0f;
    private const float RouteArrowSize = 6.0f;

    // 兩態的數值照 EurekaHelper 魔法甕候選點：已確認 alpha 0xE0／線寬 3，可能有 alpha 0x60／線寬 1.5。
    private const float ConfirmedAlpha = 0xE0 / 255.0f;
    private const float PossibleAlpha = 0x60 / 255.0f;

    private enum RouteHoverKind
    {
        None,
        VnavPath,
        EntryLine,
        EntryPoint,
    }

    private struct RouteDrawItem
    {
        public RouteOverlayEntry Entry;
        public int Start;
        public int Count;
    }

    private readonly Vector2[] vnavScreenPoints = new Vector2[RouteOverlayController.MaxPathPoints + 1];
    private int vnavScreenCount;

    private readonly List<RouteOverlayEntry> routeEntrySnapshot = [];
    private RouteDrawItem[] routeItems = new RouteDrawItem[16];
    private int routeItemCount;
    private Vector2[] routeScreenPoints = new Vector2[1024];

    private bool routeOverlayReady;
    private MapProjection routeProjection;
    private Vector2 routeMouse;
    private bool routeMouseActive;

    private RouteHoverKind routeHoverKind;
    private float routeHoverDistance;
    private int routeHoverItem;
    private int routeHoverPoint;

    private void DrawRouteOverlayLines()
    {
        routeOverlayReady = PrepareRouteOverlay();
        if (!routeOverlayReady) return;

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(routeProjection.TextureMin, routeProjection.TextureMax, true);
        try {
            for (var index = 0; index < routeItemCount; index++) {
                DrawEntryLine(drawList, index);
            }

            DrawVnavPathLine(drawList);
        }
        finally {
            drawList.PopClipRect();
        }
    }

    private void DrawRouteOverlayPoints()
    {
        if (!routeOverlayReady) return;

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(routeProjection.TextureMin, routeProjection.TextureMax, true);
        try {
            for (var index = 0; index < routeItemCount; index++) {
                DrawEntryPoints(drawList, index);
            }

            DrawVnavPathEnd(drawList);
        }
        finally {
            drawList.PopClipRect();
        }

        DrawRouteOverlayTooltip();
    }

    private bool PrepareRouteOverlay()
    {
        vnavScreenCount = 0;
        routeItemCount = 0;
        routeHoverKind = RouteHoverKind.None;
        routeHoverDistance = RouteHoverRadius;

        if (!System.SystemConfig.ShowRouteOverlay) return false;

        var agent = AgentMap.Instance();
        if (agent is null) return false;

        var sizeFactor = agent->SelectedMapSizeFactorFloat;
        if (!(sizeFactor > 0.0f)) return false;

        var territory = System.RouteOverlayController.GetMapTerritory(agent->SelectedMapId);
        if (territory is 0) return false;

        routeProjection = new MapProjection(ImGui.GetWindowPos() + DrawPosition, Scale, sizeFactor, DrawHelpers.GetMapOffsetVector());
        routeMouse = ImGui.GetMousePos();
        routeMouseActive = ImGui.IsWindowHovered() && RouteOverlayRenderer.InRect(routeMouse, routeProjection.TextureMin, routeProjection.TextureMax);

        BuildEntryScreenPoints(territory);
        BuildVnavScreenPath(territory);

        return vnavScreenCount > 0 || routeItemCount > 0;
    }

    private void BuildEntryScreenPoints(uint selectedTerritory)
    {
        System.RouteOverlayController.Store.CopyVisible(selectedTerritory, Environment.TickCount64, routeEntrySnapshot);

        var used = 0;
        foreach (var entry in routeEntrySnapshot) {
            // 來源開關在鎖外判斷：缺鍵＝開（與插件標記相同）。
            if (System.SystemConfig.IpcSourceEnabled.TryGetValue(entry.Source, out var enabled) && !enabled) continue;

            var points = entry.Points;
            if (routeScreenPoints.Length < used + points.Length) {
                Array.Resize(ref routeScreenPoints, Math.Max(used + points.Length, routeScreenPoints.Length * 2));
            }

            for (var index = 0; index < points.Length; index++) {
                routeScreenPoints[used + index] = routeProjection.ToScreen(points[index]);
            }

            if (routeItems.Length == routeItemCount) {
                Array.Resize(ref routeItems, routeItems.Length * 2);
            }

            routeItems[routeItemCount++] = new RouteDrawItem { Entry = entry, Start = used, Count = points.Length };
            used += points.Length;
        }
    }

    private void BuildVnavScreenPath(uint selectedTerritory)
    {
        var controller = System.RouteOverlayController;
        if (!System.SystemConfig.ShowVnavmeshPath || controller.VnavStatus is not VnavPathStatus.Ok) return;

        // 輪詢最多落後 250 ms：切到別的區域的地圖那一刻就不畫。
        if (selectedTerritory != Service.ClientState.TerritoryType) return;

        var waypoints = controller.VnavPoints;
        if (waypoints.IsEmpty) return;

        var count = 0;
        if (Service.ObjectTable.LocalPlayer is { } localPlayer) {
            vnavScreenPoints[count++] = routeProjection.ToScreen(localPlayer.Position);
        }

        foreach (var waypoint in waypoints) {
            vnavScreenPoints[count++] = routeProjection.ToScreen(waypoint);
        }

        vnavScreenCount = count;
    }

    private ReadOnlySpan<Vector2> GetItemScreenPoints(int itemIndex)
    {
        ref var item = ref routeItems[itemIndex];
        return routeScreenPoints.AsSpan(item.Start, item.Count);
    }

    private void DrawEntryLine(ImDrawListPtr drawList, int itemIndex)
    {
        var entry = routeItems[itemIndex].Entry;
        if (entry.Kind is not RouteOverlayKind.Path) return;

        var points = GetItemScreenPoints(itemIndex);
        if (points.Length < 2) return;

        GetEntryStyle(entry, out var color, out var outline, out var thickness);
        RouteOverlayRenderer.DrawOutlinedPolyline(drawList, points, color, outline, thickness);

        if (entry.HasArrows) {
            RouteOverlayRenderer.DrawArrows(drawList, points, color, outline, entry.State is RouteOverlayFlags.StateConfirmed ? RouteArrowSize : RouteArrowSize * 0.75f,
                routeProjection.TextureMin, routeProjection.TextureMax);
        }

        if (routeMouseActive) {
            ConsiderRouteHover(RouteOverlayRenderer.DistanceToPolyline(points, routeMouse), RouteHoverKind.EntryLine, itemIndex, -1);
        }
    }

    private void DrawEntryPoints(ImDrawListPtr drawList, int itemIndex)
    {
        var entry = routeItems[itemIndex].Entry;
        var points = GetItemScreenPoints(itemIndex);
        if (points.IsEmpty) return;

        GetEntryStyle(entry, out var color, out var outline, out var thickness);
        var state = entry.State;
        var isPath = entry.Kind is RouteOverlayKind.Path;

        // 路線只在有逐點說明或序號時才畫每個頂點（滑鼠才有東西可以指）；節點每個都畫。
        var drawVertices = !isPath || entry.Labels is not null || entry.HasNumbers;

        if (drawVertices) {
            var radius = state switch
            {
                RouteOverlayFlags.StateDone => 2.5f,
                _ when isPath => 3.0f,
                _ => 4.5f,
            };

            var numberColor = ImGui.GetColorU32(state switch
            {
                RouteOverlayFlags.StateDone => new Vector4(0.75f, 0.75f, 0.75f, 0.75f),
                RouteOverlayFlags.StatePossible => new Vector4(1.0f, 1.0f, 1.0f, 0.65f),
                _ => new Vector4(1.0f, 1.0f, 1.0f, ConfirmedAlpha),
            });
            var numberShadow = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 0.8f));
            var numberOffset = new Vector2(radius + 2.0f, -ImGui.GetFontSize() - radius * 0.5f);

            for (var index = 0; index < points.Length; index++) {
                var point = points[index];

                switch (state) {
                    case RouteOverlayFlags.StatePossible:
                        RouteOverlayRenderer.DrawRing(drawList, point, radius, color, outline, thickness);
                        break;

                    case RouteOverlayFlags.StateDone:
                        RouteOverlayRenderer.DrawDot(drawList, point, radius, color, 0);
                        break;

                    default:
                        RouteOverlayRenderer.DrawDot(drawList, point, radius, color, outline);
                        break;
                }

                if (entry.HasNumbers) {
                    RouteOverlayRenderer.DrawNumber(drawList, point + numberOffset, index + 1, numberColor, numberShadow);
                }

                if (routeMouseActive) {
                    ConsiderRouteHover(MathF.Max(Vector2.Distance(routeMouse, point) - radius, 0.0f), RouteHoverKind.EntryPoint, itemIndex, index);
                }
            }
        }

        if (isPath && state is not RouteOverlayFlags.StateDone) {
            var end = points[^1];
            RouteOverlayRenderer.DrawRing(drawList, end, RouteEndRingRadius, color, outline, state is RouteOverlayFlags.StateConfirmed ? 2.0f : 1.5f);

            if (routeMouseActive && !drawVertices) {
                ConsiderRouteHover(MathF.Max(Vector2.Distance(routeMouse, end) - RouteEndRingRadius, 0.0f), RouteHoverKind.EntryLine, itemIndex, -1);
            }
        }
    }

    private static void GetEntryStyle(RouteOverlayEntry entry, out uint color, out uint outline, out float thickness)
    {
        var red = ((entry.Rgb >> 16) & 0xFF) / 255.0f;
        var green = ((entry.Rgb >> 8) & 0xFF) / 255.0f;
        var blue = (entry.Rgb & 0xFF) / 255.0f;

        switch (entry.State) {
            case RouteOverlayFlags.StatePossible:
                color = ImGui.GetColorU32(new Vector4(red, green, blue, PossibleAlpha));
                outline = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, PossibleAlpha * 0.75f));
                thickness = 1.5f;
                break;

            case RouteOverlayFlags.StateDone:
                color = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.6f));
                outline = 0;
                thickness = 1.0f;
                break;

            default:
                color = ImGui.GetColorU32(new Vector4(red, green, blue, ConfirmedAlpha));
                outline = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, ConfirmedAlpha * 0.75f));
                thickness = 3.0f;
                break;
        }
    }

    private void DrawVnavPathLine(ImDrawListPtr drawList)
    {
        if (vnavScreenCount < 2) return;

        var points = vnavScreenPoints.AsSpan(0, vnavScreenCount);
        GetVnavColors(out var color, out var outline);

        RouteOverlayRenderer.DrawOutlinedPolyline(drawList, points, color, outline, VnavLineThickness);
        RouteOverlayRenderer.DrawArrows(drawList, points, color, outline, RouteArrowSize, routeProjection.TextureMin, routeProjection.TextureMax);

        if (routeMouseActive) {
            ConsiderRouteHover(RouteOverlayRenderer.DistanceToPolyline(points, routeMouse), RouteHoverKind.VnavPath, -1, -1);
        }
    }

    private void DrawVnavPathEnd(ImDrawListPtr drawList)
    {
        if (vnavScreenCount is 0) return;

        var end = vnavScreenPoints[vnavScreenCount - 1];
        GetVnavColors(out var color, out var outline);

        RouteOverlayRenderer.DrawRing(drawList, end, RouteEndRingRadius, color, outline, 2.0f);

        if (routeMouseActive) {
            ConsiderRouteHover(MathF.Max(Vector2.Distance(routeMouse, end) - RouteEndRingRadius, 0.0f), RouteHoverKind.VnavPath, -1, -1);
        }
    }

    private static void GetVnavColors(out uint color, out uint outline)
    {
        var configured = System.SystemConfig.VnavmeshPathColor;
        color = ImGui.GetColorU32(configured);
        outline = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, configured.W * 0.75f));
    }

    // 距離相同時後畫的贏：點比線具體。
    private void ConsiderRouteHover(float distance, RouteHoverKind kind, int itemIndex, int pointIndex)
    {
        if (!(distance <= routeHoverDistance)) return;

        routeHoverDistance = distance;
        routeHoverKind = kind;
        routeHoverItem = itemIndex;
        routeHoverPoint = pointIndex;
    }

    private void DrawRouteOverlayTooltip()
    {
        if (routeHoverKind is RouteHoverKind.None) return;

        var textScale = DrawHelpers.GetTooltipTextScale();
        var secondaryColor = System.SystemConfig.TooltipSecondaryTextColor;

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, DrawHelpers.GetTooltipAlpha());
        ImGui.BeginTooltip();
        try {
            ImGui.SetWindowFontScale(textScale);

            switch (routeHoverKind) {
                case RouteHoverKind.VnavPath:
                    ImGui.TextUnformatted("vnavmesh 目前路徑（不知道是哪個插件送的）");
                    ImGui.TextColored(secondaryColor, $"剩 {System.RouteOverlayController.VnavRemainingCount} 點");
                    break;

                case RouteHoverKind.EntryLine: {
                    var entry = routeItems[routeHoverItem].Entry;
                    ImGui.TextUnformatted($"{entry.Source} 的路線（{entry.Points.Length} 點）");
                    ImGui.TextColored(secondaryColor, GetStateText(entry.State));
                    break;
                }

                case RouteHoverKind.EntryPoint: {
                    var entry = routeItems[routeHoverItem].Entry;
                    if (entry.Labels is { } labels && labels[routeHoverPoint].Length > 0) {
                        ImGui.TextUnformatted(labels[routeHoverPoint]);
                    }
                    else {
                        ImGui.TextUnformatted($"第 {routeHoverPoint + 1} 點");
                    }

                    ImGui.TextColored(secondaryColor, $"來源：{entry.Source}（{GetStateText(entry.State)}）");
                    break;
                }
            }

            // 工具提示視窗是整個 ImGui context 共用的，字體倍率留著會污染別的插件。
            ImGui.SetWindowFontScale(1.0f);
        }
        finally {
            ImGui.EndTooltip();
            ImGui.PopStyleVar();
        }
    }

    private static string GetStateText(int state) => state switch
    {
        RouteOverlayFlags.StatePossible => "可能有",
        RouteOverlayFlags.StateDone => "已完成",
        _ => "已確認",
    };
}
