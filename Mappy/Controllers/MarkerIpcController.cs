using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Mappy.Data;

namespace Mappy.Controllers;

/// <summary>
/// 一筆由其他外掛透過 IPC 放上來的標記。建立之後不再變動。
/// </summary>
public class IpcMarker
{
    public required uint Handle { get; init; }
    public required string Source { get; init; }
    public required uint MapId { get; init; }
    public required Vector2 MapCoordinates { get; init; }
    public required uint IconId { get; init; }
    public required string Tooltip { get; init; }
}

/// <summary>
/// Mappy 的通用標記 IPC。讓其他外掛（狩獵列車、藏寶圖……）把標記畫到 Mappy 的地圖上，
/// 不必自己處理 SizeFactor／Offset 的換算。
/// 執行緒：呼叫端可能在任何執行緒上呼叫，繪製則在框架執行緒上，所以全部經過 syncRoot、繪製時取的是快照；鎖內只准改記憶體狀態，Save() 與 log 都在鎖外做。
/// </summary>
public class MarkerIpcController : IDisposable
{
    public const int IpcVersion = 1;

    private const int MaxSources = 32;
    private const int MaxMarkersPerSource = 512;
    private const int MaxTooltipLength = 256;

    private readonly object syncRoot = new();
    private readonly Dictionary<string, List<IpcMarker>> markersBySource = [];
    private uint nextHandle = 1;

    private readonly ICallGateProvider<int> getVersionProvider;
    private readonly ICallGateProvider<string, uint, Vector2, uint, string, uint> addMarkerProvider;
    private readonly ICallGateProvider<string, uint, bool> removeMarkerProvider;
    private readonly ICallGateProvider<string, bool> clearSourceProvider;

    public MarkerIpcController()
    {
        getVersionProvider = Service.PluginInterface.GetIpcProvider<int>("Mappy.GetVersion");
        addMarkerProvider = Service.PluginInterface.GetIpcProvider<string, uint, Vector2, uint, string, uint>("Mappy.AddMarker");
        removeMarkerProvider = Service.PluginInterface.GetIpcProvider<string, uint, bool>("Mappy.RemoveMarker");
        clearSourceProvider = Service.PluginInterface.GetIpcProvider<string, bool>("Mappy.ClearSource");

        getVersionProvider.RegisterFunc(GetVersion);
        addMarkerProvider.RegisterFunc(AddMarker);
        removeMarkerProvider.RegisterFunc(RemoveMarker);
        clearSourceProvider.RegisterFunc(ClearSource);
    }

    public void Dispose()
    {
        getVersionProvider.UnregisterFunc();
        addMarkerProvider.UnregisterFunc();
        removeMarkerProvider.UnregisterFunc();
        clearSourceProvider.UnregisterFunc();

        lock (syncRoot) {
            markersBySource.Clear();
        }
    }

    private static int GetVersion() => IpcVersion;

    private uint AddMarker(string source, uint mapId, Vector2 mapCoordinates, uint iconId, string tooltip)
    {
        if (string.IsNullOrWhiteSpace(source)) return 0;
        if (mapId is 0) return 0;

        // DrawMapMarker 對 IconId 0 直接 return，收下來只會變成看不見的幽靈標記。
        if (iconId is 0) return 0;

        if (!float.IsFinite(mapCoordinates.X) || !float.IsFinite(mapCoordinates.Y)) return 0;

        var trimmedSource = source.Trim();
        var safeTooltip = tooltip ?? string.Empty;
        if (safeTooltip.Length > MaxTooltipLength) {
            safeTooltip = safeTooltip[..MaxTooltipLength];
        }

        // 這把鎖繪製端每幀都要拿（GetMarkersForMap），而 IPC 端點是在呼叫端的執行緒上執行的，
        // 任何外掛隨時可能進來。所以鎖內只改狀態，把「要印什麼、要不要存檔」記進區域變數；
        // 磁碟寫入（SystemConfig.Save）與 log 一律等出鎖之後再做——在鎖內寫一次檔就等於讓畫面卡一次。
        uint handle = 0;
        var needsSave = false;
        string? newSourceLog = null;
        string? rejectedLog = null;

        lock (syncRoot) {
            if (!markersBySource.TryGetValue(trimmedSource, out var sourceMarkers)) {
                if (markersBySource.Count >= MaxSources) {
                    rejectedLog = $"[Mappy] 標記來源數量已達上限 {MaxSources}，拒絕新來源「{trimmedSource}」。";
                }
                else {
                    sourceMarkers = [];
                    markersBySource[trimmedSource] = sourceMarkers;

                    // 來源數量有上限，這裡的寫檔次數是有界的，不會像逐 icon 記錄那樣洗檔。
                    needsSave = System.SystemConfig.IpcSourceEnabled.TryAdd(trimmedSource, true);

                    newSourceLog = $"[Mappy] 新的標記來源「{trimmedSource}」已註冊。";
                }
            }

            // sourceMarkers 是 null 只有一種可能：上面因為來源數量上限而拒絕了。
            // 這一段等同於原本那兩個 return 0：被拒絕就不會走到下面配識別碼。
            if (sourceMarkers is not null) {
                if (sourceMarkers.Count >= MaxMarkersPerSource) {
                    rejectedLog = $"[Mappy] 來源「{trimmedSource}」的標記數已達上限 {MaxMarkersPerSource}，拒絕新標記。";
                }
                else {
                    handle = nextHandle++;
                    if (nextHandle is 0) nextHandle = 1;

                    sourceMarkers.Add(new IpcMarker {
                        Handle = handle,
                        Source = trimmedSource,
                        MapId = mapId,
                        MapCoordinates = mapCoordinates,
                        IconId = iconId,
                        Tooltip = safeTooltip,
                    });
                }
            }
        }

        if (needsSave) {
            SystemConfig.Save();
        }

        if (newSourceLog is not null) {
            Service.Log.Information(newSourceLog);
        }

        if (rejectedLog is not null) {
            Service.Log.Information(rejectedLog);
        }

        return handle;
    }

    private bool RemoveMarker(string source, uint handle)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (handle is 0) return false;

        lock (syncRoot) {
            if (!markersBySource.TryGetValue(source.Trim(), out var sourceMarkers)) return false;

            return sourceMarkers.RemoveAll(marker => marker.Handle == handle) > 0;
        }
    }

    private bool ClearSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        lock (syncRoot) {
            return markersBySource.Remove(source.Trim());
        }
    }

    /// <summary>
    /// 取出某張地圖上、來源沒有被使用者關掉的標記快照。
    /// </summary>
    public List<IpcMarker> GetMarkersForMap(uint mapId)
    {
        var result = new List<IpcMarker>();

        lock (syncRoot) {
            foreach (var (source, sourceMarkers) in markersBySource) {
                if (System.SystemConfig.IpcSourceEnabled.TryGetValue(source, out var enabled) && !enabled) continue;

                foreach (var marker in sourceMarkers) {
                    if (marker.MapId != mapId) continue;

                    result.Add(marker);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 設定頁用：目前有哪些來源、各有幾筆標記。
    /// </summary>
    public List<(string Source, int Count)> GetSourceSummary()
    {
        var result = new List<(string, int)>();

        lock (syncRoot) {
            foreach (var (source, sourceMarkers) in markersBySource) {
                result.Add((source, sourceMarkers.Count));
            }
        }

        return result;
    }
}
