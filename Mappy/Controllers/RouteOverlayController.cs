using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mappy.Classes;
using Mappy.Data;
using Map = Lumina.Excel.Sheets.Map;

namespace Mappy.Controllers;

public enum VnavPathStatus
{
    NotInstalled,
    NotLoaded,

    /// <summary>沒有向 vnavmesh 讀取（地圖沒開、看的不是目前所在區域、或開關關著）＝不知道。</summary>
    NotPolling,

    ReadFailed,
    Ok,
}

/// <summary>
/// 路線疊加層的資料端：vnavmesh 目前路徑的輪詢、Mappy.Overlay.* IPC 端點、插件清單快取、地圖→區域對照。
/// 執行緒：IPC 端點跑在呼叫端的執行緒（只碰 <see cref="Store"/>）；ActivePluginsChanged 只設旗標；
/// 其餘全部只在框架執行緒（＝繪製執行緒）上用。
/// </summary>
public sealed unsafe class RouteOverlayController : IDisposable
{
    public const string VnavmeshInternalName = "vnavmesh";

    /// <summary>單一條線的點數上限（16 位元索引下一次 AddPolyline 的頂點數要壓在 65536 以內）。</summary>
    public const int MaxPathPoints = RouteOverlayStore.MaxPoints;

    private const long VnavPollIntervalMs = 250;
    private const long SweepIntervalMs = 1000;

    private readonly Dictionary<uint, uint> territoryByMap = [];
    private readonly Dictionary<string, IExposedPlugin> pluginsByName = new(StringComparer.OrdinalIgnoreCase);

    private IExposedPlugin? vnavmeshPlugin;
    private volatile bool pluginListDirty = true;

    private Vector3[] vnavPoints = new Vector3[64];
    private long nextVnavPollAt;
    private long nextSweepAt;

    // 這幾個端點名稱與型別是跨插件契約（BOCCHI 等推送端照它寫）：要改形狀就換新名字，不要同名改型別。
    private readonly ICallGateProvider<int> apiVersionProvider;
    private readonly ICallGateProvider<string, string, uint, Vector3[], uint, int, string[]?, int, bool> setPathProvider;
    private readonly ICallGateProvider<string, string, uint, Vector3[], uint, int, string[]?, int, bool> setPointsProvider;
    private readonly ICallGateProvider<string, string, bool> removeProvider;
    private readonly ICallGateProvider<string, bool> clearSourceProvider;

    public RouteOverlayController()
    {
        Store = new RouteOverlayStore(static () => Environment.TickCount64, static message => Service.Log.Information(message), OnSourceRegistered);

        Service.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;

        apiVersionProvider = Service.PluginInterface.GetIpcProvider<int>("Mappy.Overlay.ApiVersion");
        setPathProvider = Service.PluginInterface.GetIpcProvider<string, string, uint, Vector3[], uint, int, string[]?, int, bool>("Mappy.Overlay.SetPath");
        setPointsProvider = Service.PluginInterface.GetIpcProvider<string, string, uint, Vector3[], uint, int, string[]?, int, bool>("Mappy.Overlay.SetPoints");
        removeProvider = Service.PluginInterface.GetIpcProvider<string, string, bool>("Mappy.Overlay.Remove");
        clearSourceProvider = Service.PluginInterface.GetIpcProvider<string, bool>("Mappy.Overlay.ClearSource");

        apiVersionProvider.RegisterFunc(static () => RouteOverlayStore.ApiVersion);
        setPathProvider.RegisterFunc(SetPath);
        setPointsProvider.RegisterFunc(SetPoints);
        removeProvider.RegisterFunc(Store.Remove);
        clearSourceProvider.RegisterFunc(Store.ClearSource);
    }

    public void Dispose()
    {
        apiVersionProvider.UnregisterFunc();
        setPathProvider.UnregisterFunc();
        setPointsProvider.UnregisterFunc();
        removeProvider.UnregisterFunc();
        clearSourceProvider.UnregisterFunc();

        Service.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;

        Store.Clear();
    }

    public RouteOverlayStore Store { get; }

    private bool SetPath(string source, string key, uint territoryId, Vector3[] points, uint rgb, int flags, string[]? labels, int ttlMs)
        => Store.Set(RouteOverlayKind.Path, source, key, territoryId, points, rgb, flags, labels, ttlMs);

    private bool SetPoints(string source, string key, uint territoryId, Vector3[] points, uint rgb, int flags, string[]? labels, int ttlMs)
        => Store.Set(RouteOverlayKind.Points, source, key, territoryId, points, rgb, flags, labels, ttlMs);

    // 呼叫端的執行緒：兩個字典都是 ConcurrentDictionary，存檔只設旗標。
    private static void OnSourceRegistered(string source)
    {
        var config = System.SystemConfig;
        var added = config.IpcSourceEnabled.TryAdd(source, true);
        added |= config.RouteOverlaySources.TryAdd(source, true);

        if (added) {
            SystemConfig.RequestSave();
        }

        Service.Log.Information($"[Mappy] 新的路線疊加層來源「{source}」已註冊。");
    }

    /// <summary>以內部名稱（不分大小寫）找插件；設定頁用來判斷來源是不是沒裝或沒載入。</summary>
    public bool TryGetPlugin(string internalName, out IExposedPlugin plugin) => pluginsByName.TryGetValue(internalName, out plugin!);

    public VnavPathStatus VnavStatus { get; private set; } = VnavPathStatus.NotPolling;

    /// <summary>vnavmesh 回報的剩餘點數（可能大於實際畫出來的點數）。</summary>
    public int VnavRemainingCount { get; private set; }

    public int VnavPointCount { get; private set; }

    public ReadOnlySpan<Vector3> VnavPoints => vnavPoints.AsSpan(0, VnavPointCount);

    // 這個事件可能在載入插件的背景執行緒上觸發：這裡只設旗標，清單在框架執行緒重抓。
    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => pluginListDirty = true;

    public void Update()
    {
        if (pluginListDirty) {
            RefreshPluginList();
        }

        var now = Environment.TickCount64;

        if (now >= nextSweepAt) {
            nextSweepAt = now + SweepIntervalMs;
            Store.SweepExpired(now);
        }

        if (now < nextVnavPollAt) return;

        nextVnavPollAt = now + VnavPollIntervalMs;
        PollVnavmesh();
    }

    /// <summary>Map 表的 TerritoryType。同一個區域可以有好幾張地圖（例如新月島南征之章 1252 有 21 張）。</summary>
    /// <returns>查不到時回 0。</returns>
    public uint GetMapTerritory(uint mapId)
    {
        if (mapId is 0) return 0;
        if (territoryByMap.TryGetValue(mapId, out var cached)) return cached;

        var territory = Service.DataManager.GetExcelSheet<Map>().GetRowOrDefault(mapId)?.TerritoryType.RowId ?? 0;
        territoryByMap[mapId] = territory;
        return territory;
    }

    private void RefreshPluginList()
    {
        pluginListDirty = false;
        pluginsByName.Clear();

        // 同名的可能不只一個（開發版＋安裝版）：優先拿已載入的那個。
        foreach (var plugin in Service.PluginInterface.InstalledPlugins) {
            if (!pluginsByName.TryGetValue(plugin.InternalName, out var existing) || (!existing.IsLoaded && plugin.IsLoaded)) {
                pluginsByName[plugin.InternalName] = plugin;
            }
        }

        vnavmeshPlugin = pluginsByName.TryGetValue(VnavmeshInternalName, out var vnavmesh) && vnavmesh.InternalName == VnavmeshInternalName ? vnavmesh : null;
    }

    private void PollVnavmesh()
    {
        if (vnavmeshPlugin is null) {
            ClearVnav(VnavPathStatus.NotInstalled);
            return;
        }

        if (!vnavmeshPlugin.IsLoaded) {
            ClearVnav(VnavPathStatus.NotLoaded);
            return;
        }

        if (!ShouldPollVnavmesh()) {
            ClearVnav(VnavPathStatus.NotPolling);
            return;
        }

        if (System.VnavmeshIpc.ListWaypoints() is not { } waypoints) {
            ClearVnav(VnavPathStatus.ReadFailed);
            return;
        }

        CopyVnavWaypoints(waypoints);
        VnavStatus = VnavPathStatus.Ok;
    }

    private bool ShouldPollVnavmesh()
    {
        var config = System.SystemConfig;
        if (!config.ShowRouteOverlay || !config.ShowVnavmeshPath) return false;
        if (!System.MapWindow.IsOpen || !IntegrationsController.ShouldShowMap()) return false;

        var agent = AgentMap.Instance();
        if (agent is null) return false;

        return GetMapTerritory(agent->SelectedMapId) == Service.ClientState.TerritoryType;
    }

    private void CopyVnavWaypoints(List<Vector3> waypoints)
    {
        var count = Math.Min(waypoints.Count, MaxPathPoints);
        if (vnavPoints.Length < count) {
            vnavPoints = new Vector3[Math.Min(Math.Max(count, vnavPoints.Length * 2), MaxPathPoints)];
        }

        var written = 0;
        for (var index = 0; index < count; index++) {
            var point = waypoints[index];
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) continue;

            vnavPoints[written++] = point;
        }

        VnavPointCount = written;
        VnavRemainingCount = waypoints.Count;
    }

    private void ClearVnav(VnavPathStatus status)
    {
        VnavStatus = status;
        VnavPointCount = 0;
        VnavRemainingCount = 0;
    }
}
