using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Classes;
using Mappy.Data;

namespace Mappy.Controllers;

public unsafe class FlagController : IDisposable
{
    private readonly Hook<AgentMap.Delegates.SetFlagMapMarker>? setFlagMapMarkerHook;

    public FlagController()
    {
        // 位址沒解出時不建 hook（HookFromAddress 對 0 位址會擲，插件建構子就中斷）：只少旗標紀錄。
        var setFlagMapMarker = AgentMap.MemberFunctionPointers.SetFlagMapMarker;
        if (setFlagMapMarker is null) {
            Service.Log.Warning("[Mappy] 找不到 AgentMap.SetFlagMapMarker 的位址（遊戲改版後特徵碼對不上），旗標紀錄停用，其餘功能照常。");
        }
        else {
            setFlagMapMarkerHook ??= Service.Hooker.HookFromAddress<AgentMap.Delegates.SetFlagMapMarker>(setFlagMapMarker, OnSetFlagMapMarker);
        }

        if (!IntegrationsController.IsPvPBlocked()) {
            EnableIntegrations();
        }
    }

    public void Dispose()
    {
        setFlagMapMarkerHook?.Dispose();
    }

    public void EnableIntegrations()
    {
        setFlagMapMarkerHook?.Enable();
    }

    public void DisableIntegrations()
    {
        setFlagMapMarkerHook?.Disable();
    }

    private void OnSetFlagMapMarker(AgentMap* thisPtr, uint territoryId, uint mapId, float x, float y, uint iconId) =>
        HookSafety.ExecuteSafe(() =>
        {
            var newFlagData = new Flag(territoryId, mapId, x, y, iconId);
            var dataFile = System.FlagConfig;

            if (!dataFile.FlagHistory.Contains(newFlagData)) {
                dataFile.FlagHistory.AddFirst(new Flag(territoryId, mapId, x, y, iconId));

                if (dataFile.FlagHistory.Count > dataFile.HistoryLimit) {
                    dataFile.FlagHistory.RemoveLast();
                }

                dataFile.Save();
            }

            setFlagMapMarkerHook!.Original.Invoke(thisPtr, territoryId, mapId, x, y, iconId);
        }, Service.Log, "Exception during OnSetFlagMapMarker");
}