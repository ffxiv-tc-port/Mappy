using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Mappy.Classes;

/// <summary>
/// detour 裡插件邏輯擲出的受管理例外的回報（範本 detour-guard 的 BossMod 形：只進 log、每個站點每 60 秒最多一行、之間累計次數）。
/// 呼叫端契約：插件邏輯包在 try 裡、catch 呼叫 <see cref="Report"/>；Original 留在 try 外，每條路徑恰好呼叫一次（刻意不呼叫的分支除外）。
/// 攔不到 AccessViolation；裸指標仍要靠判空。
/// </summary>
internal static class DetourGuard
{
    private const long ThrottleMilliseconds = 60_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SiteState> Sites = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Report(string site, Exception exception)
    {
        try {
            long total;
            lock (Gate) {
                if (!Sites.TryGetValue(site, out var state)) {
                    state = new SiteState();
                    Sites[site] = state;
                }

                state.Total++;
                var now = Environment.TickCount64;
                if (state.Logged && now - state.LastLoggedTick < ThrottleMilliseconds) return;

                state.Logged = true;
                state.LastLoggedTick = now;
                total = state.Total;
            }

            // 沿用既有的 Error 等級與 "Exception during <站點>" 開頭，舊的 log 搜尋字串照樣找得到。
            Service.Log.Error(exception, $"Exception during {site}（累計 {total} 次；遊戲原本的處理照常，同一處 60 秒內不重複記錄）");
        }
        catch {
            // 刻意吞掉：回報本身擲出的例外會穿進沒有處理常式的原生呼叫框架。
        }
    }

    private sealed class SiteState
    {
        public long Total;
        public bool Logged;
        public long LastLoggedTick;
    }
}
