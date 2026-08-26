using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;

namespace Mappy.Controllers;

/// <summary>
/// Mappy 對 Lifestream 的單向消費端。
///
/// 只有在使用者親手點擊地圖上的乙太網標記時才會呼叫，沒有任何自動化或事件驅動的呼叫鏈。
///
/// 🔴 下面這些字串是跨外掛的行為契約，對應 Lifestream/Lifestream/IPC/IPCProvider.cs：
///   Lifestream.AethernetTeleportById(uint aetheryteSheetRowId) -> bool
///   Lifestream.AethernetTeleportByPlaceNameId(uint placeNameRowId) -> bool
///   Lifestream.GetActiveAetheryte() -> uint
///   Lifestream.GetActiveResidentialAetheryte() -> uint
///   Lifestream.GetActiveCustomAetheryte() -> uint
///   Lifestream.IsBusy() -> bool
/// 改名字要兩邊一起改，否則失敗形式是「靜默退回內建傳送」而不是報錯。
/// </summary>
public class LifestreamIpc
{
    private const string LifestreamInternalName = "Lifestream";

    private readonly ICallGateSubscriber<uint, bool> aethernetTeleportById;
    private readonly ICallGateSubscriber<uint, bool> aethernetTeleportByPlaceNameId;
    private readonly ICallGateSubscriber<uint> getActiveAetheryte;
    private readonly ICallGateSubscriber<uint> getActiveResidentialAetheryte;
    private readonly ICallGateSubscriber<uint> getActiveCustomAetheryte;
    private readonly ICallGateSubscriber<bool> isBusy;

    /// <summary>
    /// 乙太網地名 ID（<see cref="Aetheryte.AethernetName"/>）對應到 Aetheryte 表的列號。
    /// 只在點擊時查，查完記起來；不用 KamiLib 的 Cache 是因為它第一次一定回預設值，
    /// 會讓「第一次點擊」靜默走到退路去。
    /// </summary>
    private readonly Dictionary<uint, uint> aethernetShardLookup = [];

    public LifestreamIpc()
    {
        aethernetTeleportById = Service.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
        aethernetTeleportByPlaceNameId = Service.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId");
        getActiveAetheryte = Service.PluginInterface.GetIpcSubscriber<uint>("Lifestream.GetActiveAetheryte");
        getActiveResidentialAetheryte = Service.PluginInterface.GetIpcSubscriber<uint>("Lifestream.GetActiveResidentialAetheryte");
        getActiveCustomAetheryte = Service.PluginInterface.GetIpcSubscriber<uint>("Lifestream.GetActiveCustomAetheryte");
        isBusy = Service.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    }

    /// <summary>
    /// Lifestream 是否已安裝且載入。沒載入時連 IPC 都不要呼叫，直接讓呼叫端走內建傳送。
    /// </summary>
    public static bool IsAvailable
        => Service.PluginInterface.InstalledPlugins.Any(plugin => plugin is { InternalName: LifestreamInternalName, IsLoaded: true });

    /// <summary>
    /// 處理點擊乙太網標記（MapMarkerInfo.DataType == 4，DataKey 是乙太網的地名 ID）。
    /// </summary>
    /// <returns>true 代表已經交給 Lifestream 處理，呼叫端不要再做別的事；
    /// false 代表沒處理，呼叫端應退回原本的 Telepo 傳送（＝傳到母乙太之光）。</returns>
    public bool TryHandleAethernetClick(uint aethernetPlaceNameId)
    {
        if (aethernetPlaceNameId is 0) return false;
        if (!IsAvailable) return false;

        try {
            if (!TryBeginTravel(out var handled)) return handled;

            // 先用 Aetheryte 表的列號呼叫：Lifestream 那邊會套用它自己的名稱覆寫表，
            // 比單純拿地名字串去比對穩。
            var shardRowId = ResolveAethernetShardAetheryteId(aethernetPlaceNameId);
            if (shardRowId is not 0 && aethernetTeleportById.InvokeFunc(shardRowId)) return true;

            // 住宅區與自訂乙太網的目的地不在 Aetheryte 表裡，改用地名 ID 讓 Lifestream 自己比對。
            return aethernetTeleportByPlaceNameId.InvokeFunc(aethernetPlaceNameId);
        }
        catch (Exception exception) {
            Service.Log.Information(exception, "[Mappy] 呼叫 Lifestream 乙太網傳送失敗，改用內建傳送。");
            return false;
        }
    }

    /// <summary>
    /// 處理點擊乙太之光標記（DataType == 3），但那一列其實是乙太網分店（IsAetheryte = false）的情況。
    /// 這種標記用 Telepo 傳送不過去，只能走乙太網。
    /// </summary>
    /// <returns>語意同 <see cref="TryHandleAethernetClick"/>。</returns>
    public bool TryHandleAethernetShardClick(uint aetheryteRowId)
    {
        if (aetheryteRowId is 0) return false;
        if (!IsAvailable) return false;

        try {
            if (!TryBeginTravel(out var handled)) return handled;

            return aethernetTeleportById.InvokeFunc(aetheryteRowId);
        }
        catch (Exception exception) {
            Service.Log.Information(exception, "[Mappy] 呼叫 Lifestream 乙太網傳送失敗，改用內建傳送。");
            return false;
        }
    }

    /// <summary>
    /// 共用的前置條件檢查。
    /// </summary>
    /// <param name="handled">回傳 false 時，這個值就是要交給呼叫端的「已處理」結果。</param>
    /// <returns>true 代表可以繼續呼叫 Lifestream。</returns>
    private bool TryBeginTravel(out bool handled)
    {
        handled = false;

        // 人不在任何乙太之光／乙太網水晶旁邊時，Lifestream 會在聊天視窗印一行紅字錯誤。
        // 這種情況直接讓呼叫端退回原本的 Telepo 傳送，行為與加這個整合之前一樣。
        if (getActiveAetheryte.InvokeFunc() is 0 &&
            getActiveResidentialAetheryte.InvokeFunc() is 0 &&
            getActiveCustomAetheryte.InvokeFunc() is 0) {
            return false;
        }

        // Lifestream 正在跑別的行程時不要插隊，也不要退回 Telepo 去跟它搶。
        if (isBusy.InvokeFunc()) {
            Service.Log.Information("[Mappy] Lifestream 忙碌中，略過這次乙太網傳送。");
            handled = true;
            return false;
        }

        return true;
    }

    private uint ResolveAethernetShardAetheryteId(uint aethernetPlaceNameId)
    {
        if (aethernetShardLookup.TryGetValue(aethernetPlaceNameId, out var cached)) return cached;

        var resolved = 0u;

        foreach (var aetheryte in Service.DataManager.GetExcelSheet<Aetheryte>()) {
            if (aetheryte.AethernetName.RowId != aethernetPlaceNameId) continue;

            resolved = aetheryte.RowId;
            break;
        }

        aethernetShardLookup[aethernetPlaceNameId] = resolved;
        return resolved;
    }
}
