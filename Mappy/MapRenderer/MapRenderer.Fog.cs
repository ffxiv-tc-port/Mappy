using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiLib.Classes;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer
{
    private delegate void ImmediateContextProcessCommands(ImmediateContext* commands, RenderCommandBufferGroup* bufferGroup, uint a3);

    [Signature("E8 ?? ?? ?? ?? 48 8B 4B 30 FF 15 ?? ?? ?? ??", DetourName = nameof(OnImmediateContextProcessCommands))]
    private readonly Hook<ImmediateContextProcessCommands>? immediateContextProcessCommandsHook = null;

    // AddonAreaMap 與 AtkComponentMap 在本 pin 的 FFXIVClientStructs 裡**都沒有具名成員**
    // （兩者都是空結構，只有 Size：0x7E0 與 0x420），所以下面兩個位移只能寫數字。
    // 位移推導見這次整理註解的 commit 訊息，遊戲改版時照著重驗一次。
    private const int MapComponentOffset = 0x430;
    private const int MaskTextureOffset = 0x270;

    // 下界擋「假 null」（會被當成 NullReference 攔下來的低位址），上界擋 non-canonical 位址。
    // 解參考之前這兩個檢查是唯一擋得住 AccessViolation 的東西 —— try/catch 擋不到它。
    private const ulong MinimumPlausiblePointer = 0x1_0000;
    private const ulong MaximumUserSpacePointer = 0x0000_7FFF_FFFF_FFFF;

    // volatile：寫入端是繪製（框架）執行緒，讀取端是 ProcessCommands hook（遊戲的 RenderThread）。
    private volatile bool requestUpdatedMaskingTexture;

    // LoadFogTexture 是丟到執行緒集區上跑的（Task.Run），那裡不准解 AgentMap 這類原生指標。
    // 所以背景貼圖路徑改成「趁還在繪製（框架）執行緒上先抄成字串」，背景只拿這份純資料快照。
    // volatile：寫入端是繪製執行緒，讀取端是 ProcessCommands hook。
    private volatile string? pendingFogBgPath;

    // 遮罩貼圖的來源。框架執行緒解析完 addon 指標鏈之後，對 ID3D11Texture2D 加一次 COM 參考
    // 才發佈出去 ⇒ hook 拿到的是「我們自己持有的資源」，不是「遊戲隨時可以回收的 addon 指標」。
    // 這樣即使 AreaMap 在發佈與消費之間被關掉，那顆貼圖也不會在我們手上被釋放。
    private FogMaskSource? fogMaskSource;

    // 解析失敗的原因，給 hook 那行 Information 用，免得「霧一直不更新」查不出是卡在哪一關。
    private volatile string? fogMaskUnavailableReason;

    // 背景工作建立、繪製執行緒每幀讀來畫：交棒走 Interlocked，換下來的舊 wrap 進佇列，
    // 由繪製執行緒釋放 —— 背景執行緒不可以釋放這一幀可能正在被畫的那一顆。
    private IDalamudTextureWrap? fogTexture;
    private readonly ConcurrentQueue<IDalamudTextureWrap> retiredFogTextures = new();

    // 慢的那一輪不准蓋掉快的那一輪：背景工作發佈結果之前先比世代。
    private long fogGeneration;

    // Stopwatch 不是執行緒安全的，而這個等待窗是「繪製執行緒開始、hook 判到期」。
    // 改成單一 long 時間戳，兩端都只做原子讀寫。
    private long maskRequestTimestamp;

    private int lastKnownDiscoveryFlags;

    private static int CurrentDiscoveryFlags => AtkStage.Instance()->GetNumberArrayData(NumberArrayType.AreaMap2)->IntArray[2];

    private void LoadFogHooks()
    {
        Service.Hooker.InitializeFromAttributes(this);
        immediateContextProcessCommandsHook?.Enable();
    }

    private void UnloadFogHooks()
    {
        immediateContextProcessCommandsHook?.Dispose();

        // hook 停掉之後才把還沒被消費的那份參考還掉。Interlocked 保證同一份快照只會被其中
        // 一邊取到，所以這裡與 hook 不會重複 Release。
        var leftover = Interlocked.Exchange(ref fogMaskSource, null);

        // 遊戲收攤的時候整個 D3D device 都在拆，這時候再去碰 COM 參考沒有好處；
        // 行程本來就要結束了，漏一個參考無害。
        if (leftover is not null && !Service.Framework.IsFrameworkUnloading) {
            ComRelease(leftover.D3D11Texture2D);
        }

        // 換世代讓還在跑的背景工作不要再發佈貼圖。視窗系統在這之前就已經拆掉，
        // 所以這一刻不會有人在畫這些貼圖。
        Interlocked.Increment(ref fogGeneration);

        if (!Service.Framework.IsFrameworkUnloading) {
            SwapFogTexture(null);
            DisposeRetiredFogTextures();
        }
    }

    // 🔴 這支跑在**遊戲自己的 "RenderThread" 上，不是 Dalamud 的框架執行緒**。
    //    ⇒ 所以這裡**一律不解 addon／AgentMap 指標**，只消費框架執行緒發佈的快照。
    //      CopyResource／MapSubresource 仍然留在命令處理點（進 Original 之前、
    //      RenderThread 獨佔 immediate context 的那一刻），時序沒有改。
    private void OnImmediateContextProcessCommands(ImmediateContext* commands, RenderCommandBufferGroup* bufferGroup, uint a3)
    {
        // 我們自己的工作即使整段失敗，原函式也一定要被叫下去，否則遊戲這一幀不會被畫出來。
        HookSafety.ExecuteSafe(UpdateMaskingTextureIfRequested, Service.Log, "Exception during OnImmediateContextProcessCommands");

        immediateContextProcessCommandsHook!.Original(commands, bufferGroup, a3);
    }

    private void UpdateMaskingTextureIfRequested()
    {
        // Delay by a certain number of frames because the game hasn't loaded the new texture yet.
        if (!requestUpdatedMaskingTexture) return;
        if (Stopwatch.GetElapsedTime(Volatile.Read(ref maskRequestTimestamp)).TotalMilliseconds <= 200) return;

        requestUpdatedMaskingTexture = false;

        // 原子取走：取到就由這裡負責 Release；沒取到就是已經被 UnloadFogHooks 收走了。
        var maskSource = Interlocked.Exchange(ref fogMaskSource, null);

        // 路徑一定要用繪製執行緒抄好的那一份，不可以在 Task.Run 裡面重新去讀 AgentMap。
        var pendingBgPath = pendingFogBgPath;
        var generation = Volatile.Read(ref fogGeneration);

        try {
            if (maskSource is null) {
                Service.Log.Information($"[Mappy] 霧貼圖：主執行緒這一輪沒有交出可用的探索遮罩貼圖（{fogMaskUnavailableReason ?? "原因未記錄"}），這次略過更新。");
                return;
            }

            if (string.IsNullOrEmpty(pendingBgPath)) {
                Service.Log.Information("[Mappy] 霧貼圖：尚未從主執行緒取得地圖材質路徑，這次略過更新。");
                return;
            }

            var maskBytes = ReadMaskTextureBytes(maskSource.D3D11Texture2D);

            if (maskBytes is null) {
                Service.Log.Information("[Mappy] 霧貼圖：讀回探索遮罩貼圖失敗，這次略過更新。");
                return;
            }

            // 遮罩位元組以參數交棒而不放欄位：欄位版本會被下一輪在背景讀到一半時設成 null。
            Task.Run(() => LoadFogTexture(pendingBgPath, maskBytes, generation));
        }
        finally {
            if (maskSource is not null) ComRelease(maskSource.D3D11Texture2D);
        }
    }

    private void DrawFogOfWar()
    {
        // 換下來的貼圖只能在繪製執行緒上釋放，所以這一行要在所有提早 return 之前。
        DisposeRetiredFogTextures();

        if (!System.SystemConfig.ShowFogOfWar) return;
        if (CurrentDiscoveryFlags == AgentMap.Instance()->SelectedMapDiscoveryFlag) return;
        if (CurrentDiscoveryFlags == -1) return;

        var flagsChanged = lastKnownDiscoveryFlags != CurrentDiscoveryFlags;
        lastKnownDiscoveryFlags = CurrentDiscoveryFlags;

        if (flagsChanged) {
            Service.Log.Debug("[Fog of War] Discovery Bits Changed, updating fog texture.");

            // 世代與時間戳都要在旗標之前發佈：hook 看到旗標為真時才保證讀得到本輪的值。
            Interlocked.Increment(ref fogGeneration);
            Volatile.Write(ref maskRequestTimestamp, Stopwatch.GetTimestamp());
            requestUpdatedMaskingTexture = true;
            SwapFogTexture(null);
        }

        // 這裡是繪製（框架）執行緒，讀 AgentMap 是合法的。等待期間每幀更新一次，
        // 讓 hook 真正送出背景工作時拿到的是最新一幀的路徑（等同原本在背景讀到的值）。
        if (requestUpdatedMaskingTexture) {
            var fogAgent = AgentMap.Instance();
            if (fogAgent is not null) {
                pendingFogBgPath = $"{fogAgent->SelectedMapBgPath.ToString()}.tex";
            }

            // addon 指標鏈只在這裡走 —— 這裡確定是框架執行緒。
            // 等待的那 200 毫秒內每幀重新發佈一次，hook 拿到的就是最新一幀的貼圖。
            PublishFogMaskSource();
        }

        var currentFogTexture = Volatile.Read(ref fogTexture);

        if (currentFogTexture is not null) {
            ImGui.SetCursorPos(DrawPosition);
            ImGui.Image(currentFogTexture.Handle, currentFogTexture.Size * Scale);
        }
        else {
            var defaultBackgroundTexture = Service.TextureProvider.GetFromGame($"{AgentMap.Instance()->SelectedMapBgPath.ToString()}.tex").GetWrapOrEmpty();

            ImGui.SetCursorPos(DrawPosition);
            ImGui.Image(defaultBackgroundTexture.Handle, defaultBackgroundTexture.Size * Scale);
        }
    }

    private void LoadFogTexture(string vanillaBgPath, byte[] maskBytes, long generation)
    {
        // 卸載期不要再碰 Dalamud 的貼圖服務。這支整支都在執行緒集區上跑，
        // 路徑與遮罩位元組都是呼叫端交棒的快照，所以這裡不再有任何原生指標可解。
        if (Service.Framework.IsFrameworkUnloading) return;

        var bgFile = GetTexFile(vanillaBgPath);

        if (bgFile is null) {
            Service.Log.Warning("Failed to load map textures");
            return;
        }

        // Load non-transparent background texture
        var backgroundBytes = bgFile.GetRgbaImageData();

        var timer = Stopwatch.StartNew();

        // Make background texture fully invisible
        for (var index = 0; index < 2048 * 2048; index++) {
            backgroundBytes[index * 4 + 3] = 0;
        }

        // Make non-transparent any section that the player has not-already explored
        for (var x = 0; x < 128; x++)
        for (var y = 0; y < 128; y++) {
            var pixelIndex = (x + y * 128) * 4;
            var targetPixel = (x + 2048 * y) * 4;

            var redAmount = maskBytes[pixelIndex + 0] / 255.0f;
            var greenAmount = maskBytes[pixelIndex + 1] / 255.0f;
            var blueAmount = maskBytes[pixelIndex + 2] / 255.0f;

            var maxAlpha = Math.Max(redAmount, Math.Max(greenAmount, blueAmount));
            var alphaSum = (byte)(maxAlpha * 255);

            if (alphaSum is not 0) {
                const int scaleFactor = 16;
                foreach (var xScalar in Enumerable.Range(0, scaleFactor))
                foreach (var yScalar in Enumerable.Range(0, scaleFactor)) {
                    var scalingPixelTarget = targetPixel * scaleFactor + xScalar * 4 + yScalar * 2048 * 4;
                    backgroundBytes[scalingPixelTarget + 3] = alphaSum;
                }
            }
        }

        Service.Log.Debug($"Fog of War Calculated in {timer.ElapsedMilliseconds} ms");

        if (Volatile.Read(ref fogGeneration) != generation) return;

        SwapFogTexture(Service.TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(2048, 2048), backgroundBytes));

        // 位元組陣列也以參數交棒：模糊那一輪就地改寫它，所以持有者必須只有一個。
        Task.Run(() => CleanupFogTexture(backgroundBytes, generation));
    }

    /// <summary>
    /// 在框架（繪製）執行緒上把探索遮罩貼圖解析好，加一次 COM 參考之後發佈給 ProcessCommands hook。
    /// </summary>
    private void PublishFogMaskSource()
    {
        var resolved = ResolveFogMaskSource();

        var previous = Interlocked.Exchange(ref fogMaskSource, resolved);
        if (previous is not null) ComRelease(previous.D3D11Texture2D);
    }

    /// <summary>
    /// 走 AreaMap -&gt; AtkComponentMap -&gt; Texture -&gt; ID3D11Texture2D 這條鏈，每一步都先驗證再解參考。
    /// 任何一關不過就整輪放棄（fail-closed），並把原因記下來給 hook 那行 log 用。
    /// </summary>
    private FogMaskSource? ResolveFogMaskSource()
    {
        var addon = Service.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon is null) return FogMaskUnavailable("AreaMap 沒有開著");

        // ULD 還在載的時候下面那些欄位還沒被填好，+0x430 拿到的不保證是有效指標。
        if (addon->UldManager.LoadedState is not AtkLoadState.Loaded) return FogMaskUnavailable($"AreaMap 的 UldManager 還沒載完（LoadedState={addon->UldManager.LoadedState}）");
        if (addon->UldManager.NodeListCount is 0) return FogMaskUnavailable("AreaMap 的節點列表是空的");

        var componentAddress = *(nint*)((byte*)addon + MapComponentOffset);
        if (!IsPlausiblePointer(componentAddress)) return FogMaskUnavailable("地圖元件指標不像有效位址");

        var mapComponent = (AtkComponentMap*)componentAddress;

        // 型別檢查：照遊戲自己那支取元件函式（0x140697B80）的判準再走一次，擋掉
        // 「這一格其實掛著別的元件樣板」那整類問題 —— 那是位移對不上時最常見的失敗形狀。
        var ownerNode = mapComponent->OwnerNode;
        if (ownerNode is null) return FogMaskUnavailable("地圖元件沒有 OwnerNode");
        if ((nint)ownerNode->Component != componentAddress) return FogMaskUnavailable("地圖元件與 OwnerNode 對不起來");
        if (mapComponent->UldManager.LoadedState is not AtkLoadState.Loaded) return FogMaskUnavailable($"地圖元件的 UldManager 還沒載完（LoadedState={mapComponent->UldManager.LoadedState}）");

        var objectInfo = (AtkUldComponentInfo*)mapComponent->UldManager.Objects;
        if (objectInfo is null) return FogMaskUnavailable("地圖元件沒有 ULD 物件資訊");
        if (objectInfo->ComponentType is not ComponentType.Map) return FogMaskUnavailable($"+0x430 掛的不是地圖元件（ComponentType={objectInfo->ComponentType}）");

        var textureAddress = *(nint*)((byte*)mapComponent + MaskTextureOffset);
        if (!IsPlausiblePointer(textureAddress)) return FogMaskUnavailable("遮罩貼圖指標不像有效位址（地圖元件可能還沒建好貼圖）");

        var maskTexture = (Texture*)textureAddress;
        var d3D11Texture2D = (nint)maskTexture->D3D11Texture2D;
        if (!IsPlausiblePointer(d3D11Texture2D)) return FogMaskUnavailable("遮罩貼圖還沒有對應的 ID3D11Texture2D");

        // 加一次參考之後這顆貼圖就不會在我們手上被釋放，hook 那邊可以放心用。
        ComAddRef(d3D11Texture2D);
        fogMaskUnavailableReason = null;

        return new FogMaskSource(d3D11Texture2D);
    }

    private FogMaskSource? FogMaskUnavailable(string reason)
    {
        fogMaskUnavailableReason = reason;
        return null;
    }

    /// <summary>
    /// 下界擋假 null、上界擋 non-canonical 位址。這是解參考之前唯一擋得住 AccessViolation 的檢查
    /// —— AccessViolationException 在 .NET Core 是 corrupted-state exception，try/catch 攔不到。
    /// </summary>
    private static bool IsPlausiblePointer(nint value)
        => (ulong)value >= MinimumPlausiblePointer && (ulong)value <= MaximumUserSpacePointer;

    /// <summary>
    /// 把遊戲的遮罩貼圖複製到一張 staging 貼圖再讀回 CPU。呼叫點刻意留在 ProcessCommands 的
    /// detour 裡（進 Original 之前），那一刻 RenderThread 獨佔 immediate context。
    /// </summary>
    private static byte[]? ReadMaskTextureBytes(nint d3D11Texture2D)
    {
        var deviceHandle = Service.PluginInterface.UiBuilder.DeviceHandle;
        if (deviceHandle == nint.Zero) return null;

        var device = CppObject.FromPointer<Device>(deviceHandle);
        var texture = CppObject.FromPointer<Texture2D>(d3D11Texture2D);

        var sourceDescription = texture.Description;
        var desc = new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            Format = sourceDescription.Format,
            Height = sourceDescription.Height,
            Width = sourceDescription.Width,
            MipLevels = 1,
            OptionFlags = sourceDescription.OptionFlags,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        };

        using var stagingTexture = new Texture2D(device, desc);
        var context = device.ImmediateContext;

        context.CopyResource(texture, stagingTexture);
        context.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None, out var dataStream);

        try {
            using var pixelDataStream = new MemoryStream();
            dataStream.CopyTo(pixelDataStream);

            return pixelDataStream.ToArray();
        }
        finally {
            // 原本少了這一步：staging 貼圖會在還被 map 著的狀態下被釋放。
            context.UnmapSubresource(stagingTexture, 0);
        }
    }

    // IUnknown 的 vtable 前三格固定是 QueryInterface／AddRef／Release。這裡直接走 vtable 而不用
    // Marshal.AddRef／Marshal.Release，是為了不依賴執行期的內建 COM 互通開關。
    private static void ComAddRef(nint pUnknown)
    {
        var vtable = *(nint**)pUnknown;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[1])(pUnknown);
    }

    private static void ComRelease(nint pUnknown)
    {
        var vtable = *(nint**)pUnknown;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(pUnknown);
    }

    /// <summary>
    /// 已經加過一次 COM 參考的遮罩貼圖。持有者負責在用完之後 <see cref="ComRelease"/> 一次。
    /// </summary>
    private sealed class FogMaskSource(nint d3D11Texture2D)
    {
        public nint D3D11Texture2D { get; } = d3D11Texture2D;
    }

    /// <summary>
    /// 交棒 fogTexture。舊的那顆只推進佇列、不在這裡釋放 —— 呼叫端可能是背景執行緒。
    /// </summary>
    private void SwapFogTexture(IDalamudTextureWrap? next)
    {
        var previous = Interlocked.Exchange(ref fogTexture, next);
        if (previous is not null) retiredFogTextures.Enqueue(previous);
    }

    /// <summary>
    /// 只能從繪製執行緒呼叫：排在這裡的貼圖最後一次被畫是在前一幀，那一幀已經送出去了。
    /// </summary>
    private void DisposeRetiredFogTextures()
    {
        while (retiredFogTextures.TryDequeue(out var retired)) {
            retired.Dispose();
        }
    }

    private void CleanupFogTexture(byte[] fogBytes, long generation)
    {
        if (Service.Framework.IsFrameworkUnloading) return;

        var timer = Stopwatch.StartNew();

        // Because we had to scale a 128x128 texture mapping onto a 2048x2048, it'll look very blurry, lets blend the alpha channel
        const int blurRadius = 8;

        for (var x = 0; x < 2048; x++)
        for (var y = 0; y < 2048; y++) {
            var pixelIndex = (x + y * 2048) * 4;

            var alphaAverage = 0.0f;
            var numAveraged = 0;

            if (fogBytes[pixelIndex + 3] == 255) continue;

            for (var xBlur = -blurRadius; xBlur < -blurRadius + blurRadius * 2; ++xBlur) {
                var currentX = x + xBlur;
                if (currentX is < 0 or >= 2048) continue;
                var currentPixelIndex = (currentX + y * 2048) * 4;

                alphaAverage += fogBytes[currentPixelIndex + 3];
                numAveraged++;
            }

            for (var yBlur = -blurRadius; yBlur < -blurRadius + blurRadius * 2; ++yBlur) {
                var currentY = y + yBlur;

                if (currentY is < 0 or >= 2048) continue;
                var currentPixelIndex = (x + currentY * 2048) * 4;

                alphaAverage += fogBytes[currentPixelIndex + 3];
                numAveraged++;
            }

            var newAlpha = (byte)(alphaAverage / numAveraged);
            fogBytes[pixelIndex + 3] = newAlpha;
        }

        Service.Log.Debug($"Texture Cleanup completed in {timer.ElapsedMilliseconds} ms");

        if (Volatile.Read(ref fogGeneration) != generation) return;

        SwapFogTexture(Service.TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(2048, 2048), fogBytes));
    }
}