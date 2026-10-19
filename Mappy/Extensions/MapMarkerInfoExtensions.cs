using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using Mappy.Classes;
using Map = Lumina.Excel.Sheets.Map;
using MarkerInfo = Mappy.Classes.MarkerInfo;

namespace Mappy.Extensions;

public static class MapMarkerInfoExtensions
{
    // 契約：只在繪製執行緒呼叫。DrawMapMarker 當場用完 MarkerInfo 與它的委派、不保留，
    // 所以共用一個實例，委派讀 current（這次 Draw 的標記複本）；副文字按原始位元組快取，位元組一變就重解。
    private static SubtextCache[] subtexts = [];
    private static MapMarkerInfo current;
    private static SubtextCache currentSubtext = new();
    private static readonly MarkerInfo Scratch = new() { Position = Vector2.Zero, Offset = Vector2.Zero, Scale = 1.0f };
    private static readonly Func<string?> PrimaryTooltip = () => GetMarkerPrimaryTooltip(ref current, currentSubtext);
    private static readonly Func<string?> SecondaryTooltip = () => GetTooltip(ref current);
    private static readonly global::System.Action LeftClicked = () => OnMarkerClicked(ref current);

    /// <param name="slot">這個標記在 AgentMap.MapMarkers 裡的索引，副文字快取以它分格。</param>
    public static void Draw(this MapMarkerInfo marker, int slot, Vector2 offset, float scale)
    {
        var subtext = GetSubtextCache(slot);
        subtext.Refresh(marker.MapMarker.Subtext);
        current = marker;
        currentSubtext = subtext;

        var markerInfo = Scratch;
        markerInfo.ResetForReuse(
            // Divide by 16, as it seems they use a fixed scalar
            // Add 1024 * scale, to offset from top-left, to center-based coordinate
            // Add offset for drawing relative to map when it's moved around
            new Vector2(marker.MapMarker.X, marker.MapMarker.Y) / 16.0f * scale + DrawHelpers.GetMapCenterOffsetVector() * scale,
            offset,
            scale);
        markerInfo.Radius = marker.MapMarker.Scale;
        markerInfo.RadiusColor = KnownColor.MediumPurple.Vector();
        markerInfo.IconId = marker.MapMarker.IconId;
        markerInfo.PrimaryText = PrimaryTooltip;
        markerInfo.OnLeftClicked = LeftClicked;
        markerInfo.SecondaryText = SecondaryTooltip;

        if (marker.MapMarker.IconId is 0 && marker.MapMarker.Index is not 0) {
            TryDrawText(marker, markerInfo, subtext);
        }
        else {
            DrawHelpers.DrawMapMarker(markerInfo);
        }
    }

    private static SubtextCache GetSubtextCache(int slot)
    {
        if (slot >= subtexts.Length) {
            Array.Resize(ref subtexts, Math.Max(slot + 1, subtexts.Length * 2));
        }

        return subtexts[slot] ??= new SubtextCache();
    }

    /// <summary>
    /// 一格標記的副文字。Text 等於 Subtext.AsDalamudSeString().TextValue：
    /// 同樣的位元組解出同樣的字串，所以只在位元組變了才重解，而且等到真的要用（畫文字、滑鼠移上去）才解。
    /// </summary>
    private sealed class SubtextCache
    {
        private byte[] raw = [];
        private string? text;

        public void Refresh(CStringPointer subtext)
        {
            var bytes = subtext.AsSpan();
            if (bytes.SequenceEqual(raw)) return;

            raw = bytes.ToArray();
            text = null;
        }

        public string Text => text ??= SeString.Parse(raw).TextValue;
    }

    private static void TryDrawText(MapMarkerInfo marker, MarkerInfo markerInfo, SubtextCache subtext)
    {
        if (!System.SystemConfig.ShowTextLabels) return;

        var textTypeScalar = marker.MapMarker.SubtextStyle switch
        {
            1 => System.SystemConfig.LargeAreaTextScale,
            _ => System.SystemConfig.SmallAreaTextScale,
        };

        if (System.SystemConfig.ScaleTextWithZoom) {
            markerInfo.Scale *= textTypeScalar * 0.33f;
        }
        else {
            markerInfo.Scale = textTypeScalar * 0.33f;
        }

        DrawHelpers.DrawText(markerInfo, subtext.Text);
    }

    private static void OnMarkerClicked(ref MapMarkerInfo marker)
    {
        switch (marker.DataType) {
            case 1: // MapLinkMarker
                OnMapLinkMarkerClicked(ref marker);
                break;

            case 2: // InstanceLink
                OnInstanceLinkClicked(ref marker);
                break;

            case 3: // Aetheryte
                OnAetheryteClicked(ref marker);
                break;

            case 4: // Aethernet
                OnAethernetClicked(ref marker);
                break;
        }
    }

    private static void OnMapLinkMarkerClicked(ref MapMarkerInfo marker)
    {
        if (marker.DataKey is 0) return;
        if (DrawHelpers.IsDisallowedIcon(marker.MapMarker.IconId)) return;

        System.IntegrationsController.OpenMap(marker.DataKey);
    }

    private static void OnInstanceLinkClicked(ref MapMarkerInfo _)
    {
        // Might consider opening contents finder to this duty, maybe
    }

    private static void OnAetheryteClicked(ref MapMarkerInfo marker)
    {
        if (marker.DataKey is 0) return;

        // 這一列其實是乙太網分店時，Telepo 傳送不過去（它只認母乙太之光），交給 Lifestream 走乙太網。
        if (Service.DataManager.GetExcelSheet<Aetheryte>().GetRowOrDefault(marker.DataKey) is { IsAetheryte: false }) {
            if (System.LifestreamIpc.TryHandleAethernetShardClick(marker.DataKey)) return;
        }

        System.Teleporter.Teleport(marker.DataKey);
    }

    private static void OnAethernetClicked(ref MapMarkerInfo marker)
    {
        if (marker.DataKey is 0) return;

        // 優先讓 Lifestream 走真正的乙太網移動：它認得分店、住宅區與自訂乙太網，
        // 內建的 Telepo 只會傳到母乙太之光。Lifestream 沒裝／不在水晶旁／呼叫失敗時退回原行為。
        if (System.LifestreamIpc.TryHandleAethernetClick(marker.DataKey)) return;

        var aetheryte = GetAetheryteForAethernet(marker.DataKey);
        if (aetheryte is null) return;
        if (aetheryte.Value.RowId is 0) return;

        System.Teleporter.Teleport(aetheryte.Value.RowId);
    }

    private static string GetTooltip(ref MapMarkerInfo marker)
    {
        switch (marker.DataType)
        {
            case 1: // MapLinkMarker
                return GetMapLinkTooltip(ref marker);

            case 2: // InstanceLink
                return GetInstanceLinkTooltip(ref marker);

            case 3: // Aetheryte
                return GetAetheryteTooltip(ref marker);

            case 4: // Aethernet
                return GetAethernetTooltip(ref marker);
        }

        return string.Empty;
    }

    private static string GetMapLinkTooltip(ref MapMarkerInfo marker)
    {
        if (marker.DataKey is 0) return string.Empty;
        if (DrawHelpers.IsDisallowedIcon(marker.MapMarker.IconId)) return string.Empty;

        var map = Service.DataManager.GetExcelSheet<Map>().GetRow(marker.DataKey);
        var mapPlaceName = map.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

        return $"開啟地圖：{mapPlaceName}";
    }

    private static string GetInstanceLinkTooltip(ref MapMarkerInfo marker)
    {
        return $"副本連結 {marker.DataKey}";
    }

    private static string GetAetheryteTooltip(ref MapMarkerInfo marker)
    {
        if (marker.DataKey is 0) return string.Empty;

        var aetheryteTeleportCost = GetAetheryteTeleportGilCost(marker.DataKey);
        if (aetheryteTeleportCost is null) return "尚未交感此乙太之光";

        var aetheryte = Service.DataManager.GetExcelSheet<Aetheryte>().GetRow(marker.DataKey);
        var aetherytePlaceName = aetheryte.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var aetheryteCost = GetAetheryteTeleportCost(marker.DataKey);

        return $"傳送至 {aetherytePlaceName} {aetheryteCost}";
    }

    private static string GetAethernetTooltip(ref MapMarkerInfo marker)
    {
        if (marker.DataKey is 0) return string.Empty;

        var aetheryte = GetAetheryteForAethernet(marker.DataKey);
        if (aetheryte is null) return string.Empty;
        if (aetheryte.Value.RowId is 0) return string.Empty;

        var aetherytePlaceName = aetheryte.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

        return $"傳送至 {aetherytePlaceName} {GetAetheryteTeleportCost(aetheryte.Value.RowId)}";
    }

    private static Aetheryte? GetAetheryteForAethernet(uint aethernetKey) => System.AetheryteAethernetCache.GetValue(aethernetKey);

    private static uint? GetAetheryteTeleportGilCost(uint aethernetKey) => Service.AetheryteList.FirstOrDefault(entry => entry.AetheryteId == aethernetKey)?.GilCost;

    private static string GetAetheryteTeleportCost(uint targetDataKey) => $"({GetAetheryteTeleportGilCost(targetDataKey) ?? 0:n0} {SeIconChar.Gil.ToIconChar()})";

    private static string GetMarkerPrimaryTooltip(ref MapMarkerInfo marker, SubtextCache subtext)
    {
        if (DrawHelpers.IsDisallowedIcon(marker.MapMarker.IconId)) return string.Empty;
        if (!System.SystemConfig.ShowMiscTooltips) return string.Empty;
        if (!subtext.Text.IsNullOrEmpty()) return subtext.Text;

        return marker.DataType switch
        {
            4 => Service.DataManager.GetExcelSheet<PlaceName>().GetRow(marker.DataKey).Name.ExtractText(),
            _ => System.TooltipCache.GetValue(marker.MapMarker.IconId) ?? string.Empty,
        };
    }
}
