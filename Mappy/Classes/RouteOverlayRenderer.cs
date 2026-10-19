using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace Mappy.Classes;

/// <summary>
/// 世界座標 → 地圖視窗的螢幕座標。與玩家圖示（MapRenderer.Player）同一條式子、同一個運算順序。
/// </summary>
public readonly struct MapProjection
{
    public const float TextureSize = 2048.0f;

    public readonly Vector2 Origin;
    public readonly float Zoom;
    public readonly float SizeFactor;
    public readonly Vector2 ScaledOffset;

    /// <param name="origin">貼圖左上角的螢幕座標（視窗位置＋DrawPosition）。</param>
    /// <param name="zoom">MapRenderer.Scale。</param>
    /// <param name="sizeFactor">SelectedMapSizeFactorFloat。</param>
    /// <param name="scaledOffset">DrawHelpers.GetMapOffsetVector()（已乘上 sizeFactor）。</param>
    public MapProjection(Vector2 origin, float zoom, float sizeFactor, Vector2 scaledOffset)
    {
        Origin = origin;
        Zoom = zoom;
        SizeFactor = sizeFactor;
        ScaledOffset = scaledOffset;
    }

    public Vector2 TextureMin => Origin;

    public Vector2 TextureMax => Origin + new Vector2(TextureSize * Zoom, TextureSize * Zoom);

    public Vector2 ToScreen(Vector3 world)
        => Origin + (new Vector2(world.X, world.Z) * SizeFactor - ScaledOffset + new Vector2(1024.0f, 1024.0f)) * Zoom;
}

/// <summary>
/// 路線疊加層的純繪製與幾何。只碰呼叫端給的 draw list 與參數，不讀設定、不讀遊戲記憶體、不配置記憶體。
/// </summary>
public static class RouteOverlayRenderer
{
    public const float ArrowSpacing = 80.0f;

    private const int MaxArrowsPerLine = 1024;

    public static void DrawOutlinedPolyline(ImDrawListPtr drawList, ReadOnlySpan<Vector2> points, uint color, uint outline, float thickness)
    {
        if (points.Length < 2) return;

        ref var first = ref MemoryMarshal.GetReference(points);
        if (outline != 0) {
            drawList.AddPolyline(ref first, points.Length, outline, ImDrawFlags.None, thickness + 2.0f);
        }

        drawList.AddPolyline(ref first, points.Length, color, ImDrawFlags.None, thickness);
    }

    /// <summary>沿線每 <see cref="ArrowSpacing"/> px 一個方向箭頭；只走 clip 範圍內的那一段，畫面外的長線段不逐個試。</summary>
    public static void DrawArrows(ImDrawListPtr drawList, ReadOnlySpan<Vector2> points, uint color, uint outline, float size, Vector2 clipMin, Vector2 clipMax)
    {
        var margin = new Vector2(size * 2.0f, size * 2.0f);
        var boxMin = clipMin - margin;
        var boxMax = clipMax + margin;

        // traveled＝這一段起點之前已走過的長度；第一個箭頭在半個間距處。
        var traveled = 0.0f;
        var drawn = 0;

        for (var i = 1; i < points.Length; i++) {
            var start = points[i - 1];
            var delta = points[i] - start;
            var length = delta.Length();
            if (!(length > 0.001f) || !float.IsFinite(length)) continue;

            if (ClipSegment(start, delta, boxMin, boxMax, out var t0, out var t1)) {
                var direction = delta / length;
                var visibleFrom = traveled + t0 * length;
                var visibleTo = traveled + t1 * length;

                var k = MathF.Ceiling((visibleFrom - ArrowSpacing * 0.5f) / ArrowSpacing);
                for (var at = ArrowSpacing * 0.5f + MathF.Max(k, 0.0f) * ArrowSpacing; at < visibleTo; at += ArrowSpacing) {
                    DrawArrow(drawList, start + direction * (at - traveled), direction, size, color, outline);
                    if (++drawn >= MaxArrowsPerLine) return;
                }
            }

            traveled += length;
        }
    }

    public static void DrawArrow(ImDrawListPtr drawList, Vector2 center, Vector2 direction, float size, uint color, uint outline)
    {
        var normal = new Vector2(-direction.Y, direction.X);

        if (outline != 0) {
            var outer = size + 1.5f;
            drawList.AddTriangleFilled(center + direction * (outer + 0.5f), center - direction * outer * 0.75f + normal * outer * 0.8f, center - direction * outer * 0.75f - normal * outer * 0.8f, outline);
        }

        drawList.AddTriangleFilled(center + direction * size, center - direction * size * 0.7f + normal * size * 0.7f, center - direction * size * 0.7f - normal * size * 0.7f, color);
    }

    public static void DrawRing(ImDrawListPtr drawList, Vector2 center, float radius, uint color, uint outline, float thickness)
    {
        if (outline != 0) {
            drawList.AddCircle(center, radius, outline, 0, thickness + 2.0f);
        }

        drawList.AddCircle(center, radius, color, 0, thickness);
    }

    public static void DrawDot(ImDrawListPtr drawList, Vector2 center, float radius, uint color, uint outline)
    {
        if (outline != 0) {
            drawList.AddCircleFilled(center, radius + 1.5f, outline);
        }

        drawList.AddCircleFilled(center, radius, color);
    }

    public static void DrawNumber(ImDrawListPtr drawList, Vector2 position, int number, uint color, uint shadow)
    {
        if (shadow != 0) {
            drawList.AddText(position + Vector2.One, shadow, $"{number}");
        }

        drawList.AddText(position, color, $"{number}");
    }

    public static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (!(lengthSquared > 0.0f)) return Vector2.Distance(point, a);

        var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f);
        return Vector2.Distance(point, a + ab * t);
    }

    /// <returns>到折線最近的距離；只有一個點時是到那個點的距離；沒有點時是正無限大。</returns>
    public static float DistanceToPolyline(ReadOnlySpan<Vector2> points, Vector2 point)
    {
        if (points.Length is 0) return float.PositiveInfinity;
        if (points.Length is 1) return Vector2.Distance(point, points[0]);

        var best = float.PositiveInfinity;
        for (var i = 1; i < points.Length; i++) {
            var distance = DistanceToSegment(point, points[i - 1], points[i]);
            if (distance < best) best = distance;
        }

        return best;
    }

    public static bool InRect(Vector2 point, Vector2 min, Vector2 max)
        => point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    /// <summary>Liang–Barsky：線段 start→start+delta 落在矩形內的參數區間。</summary>
    public static bool ClipSegment(Vector2 start, Vector2 delta, Vector2 min, Vector2 max, out float t0, out float t1)
    {
        t0 = 0.0f;
        t1 = 1.0f;

        return ClipAxis(-delta.X, start.X - min.X, ref t0, ref t1) &&
               ClipAxis(delta.X, max.X - start.X, ref t0, ref t1) &&
               ClipAxis(-delta.Y, start.Y - min.Y, ref t0, ref t1) &&
               ClipAxis(delta.Y, max.Y - start.Y, ref t0, ref t1);
    }

    private static bool ClipAxis(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0.0f) return q >= 0.0f;

        var r = q / p;
        if (p < 0.0f) {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }

        return true;
    }
}
