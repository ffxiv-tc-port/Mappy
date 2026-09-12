using System;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Mappy.Controllers;

/// <summary>
/// Mappy 對 vnavmesh 的消費端：只用來「問有沒有在走」與「叫它停」。
/// </summary>
public class VnavmeshIpc : IDisposable
{
    private const string VnavmeshInternalName = "vnavmesh";

    /// <summary>補送停止的窗口長度。窗口內每 100ms 補送一次，確認停了就提早收工。</summary>
    private static readonly TimeSpan EnforceWindow = TimeSpan.FromSeconds(3);

    /// <remarks>
    /// 🔴 為什麼需要：窗口到期時只要 vnavmesh 還在算路徑就會延展，若 IPC 端點因故永遠卡在
    /// true，看門狗就會永久補送。這條上限保證它一定會收工。
    /// </remarks>
    private static readonly TimeSpan AbsoluteCap = TimeSpan.FromSeconds(30);

    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;

    private DateTime enforceUntil = DateTime.MinValue;
    private DateTime enforceStartedAt = DateTime.MinValue;
    private DateTime lastResendAt = DateTime.MinValue;
    private bool watchdogSubscribed;

    public VnavmeshIpc()
    {
        pathIsRunning = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathStop = Service.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        pathfindInProgress = Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
    }

    /// <summary>
    /// vnavmesh 是否已安裝且載入。<b>與導航網格載完了沒有無關</b>——網格沒好是 Lifestream
    /// 那邊自己等的事，Mappy 不重複判斷。
    /// </summary>
    public static bool IsAvailable
        => Service.PluginInterface.InstalledPlugins.Any(plugin => plugin is { InternalName: VnavmeshInternalName, IsLoaded: true });

    /// <summary>目前是不是還在確保「真的停下來了」。</summary>
    public bool IsEnforcingStop => enforceUntil != DateTime.MinValue;

    /// <summary>vnavmesh 目前是不是正在沿路徑移動。</summary>
    /// <remarks>📌 未安裝或 IPC 出錯時回 false——沒有那個外掛就不可能有我們發起的移動在跑。</remarks>
    public bool IsPathRunning()
    {
        if (!IsAvailable) return false;

        try {
            return pathIsRunning.InvokeFunc();
        }
        catch (Exception exception) {
            Service.Log.Information(exception, "[Mappy] 呼叫 vnavmesh.Path.IsRunning 失敗。");
            return false;
        }
    }

    /// <summary>vnavmesh 是不是正在<b>計算</b>路徑（還沒開始走）。</summary>
    /// <remarks>🔴 這段期間按停止是攔不住的，所以 <see cref="RequestStop"/> 要一直補送到它回 false。</remarks>
    public bool IsPathfindInProgress()
    {
        if (!IsAvailable) return false;

        try {
            return pathfindInProgress.InvokeFunc();
        }
        catch (Exception exception) {
            Service.Log.Information(exception, "[Mappy] 呼叫 vnavmesh.SimpleMove.PathfindInProgress 失敗。");
            return false;
        }
    }

    /// <summary>
    /// 立刻要求停止，並開啟補送窗口。
    /// </summary>
    /// <remarks>📌 對「本來就沒在移動」是安全的無操作，呼叫端不必先檢查。</remarks>
    public void RequestStop()
    {
        SendStop();

        if (!IsAvailable) return;

        var now = DateTime.UtcNow;

        // 絕對上限自「這一輪的第一次要求停止」起算：連按停止不會把上限一直往後推。
        if (enforceStartedAt == DateTime.MinValue) enforceStartedAt = now;

        enforceUntil = now + EnforceWindow;
        lastResendAt = now;

        if (watchdogSubscribed) return;

        Service.Framework.Update += OnFrameworkUpdate;
        watchdogSubscribed = true;
    }

    private void SendStop()
    {
        if (!IsAvailable) return;

        try {
            pathStop.InvokeAction();
        }
        catch (Exception exception) {
            Service.Log.Information(exception, "[Mappy] 呼叫 vnavmesh.Path.Stop 失敗。");
        }
    }

    /// <summary>沒開補送窗口時，這裡就只是一行比較後直接返回（而且窗口一關就退訂了）。</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (enforceUntil == DateTime.MinValue) {
            CloseWindow();
            return;
        }

        var now = DateTime.UtcNow;

        if (now >= enforceUntil) {
            // 🔴 到期不能無條件放棄：路徑計算超過窗口長度（遠距離目標很常見）時，vnavmesh
            //    算完照樣把路徑交給 FollowPath 開走——那正是這段程式要修掉的原樣復發。
            if (IsPathfindInProgress() && now - enforceStartedAt < AbsoluteCap) {
                enforceUntil = now + EnforceWindow;
                return;
            }

            if (now - enforceStartedAt >= AbsoluteCap) {
                Service.Log.Information(
                    $"[Mappy] 補送停止已達絕對上限 {AbsoluteCap.TotalSeconds:0} 秒（vnavmesh 仍回報正在計算路徑），停止補送。");
            }

            CloseWindow();
            return;
        }

        if (now - lastResendAt < TimeSpan.FromMilliseconds(100)) return;
        lastResendAt = now;

        var pathfinding = IsPathfindInProgress();
        var running = IsPathRunning();

        if (running) SendStop();

        // 既沒在算路徑也沒在走＝真的停了，提早收工。
        if (!pathfinding && !running) CloseWindow();
    }

    private void CloseWindow()
    {
        enforceUntil = DateTime.MinValue;
        enforceStartedAt = DateTime.MinValue;
        lastResendAt = DateTime.MinValue;

        if (!watchdogSubscribed) return;

        Service.Framework.Update -= OnFrameworkUpdate;
        watchdogSubscribed = false;
    }

    /// <summary>
    /// 🔴 外掛卸載時一定要退訂——絕對不能留一個指向本組件的 <c>Framework.Update</c> 訂閱。
    /// </summary>
    public void Dispose() => CloseWindow();
}
