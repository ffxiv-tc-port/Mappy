using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Data.Files;
using Mappy.Classes;

namespace Mappy.MapRenderer;

public unsafe partial class MapRenderer : IDisposable
{
    public static float Scale
    {
        get => System.SystemConfig.MapScale;
        set => System.SystemConfig.MapScale = value;
    }

    public Vector2 DrawOffset { get; set; }
    public Vector2 DrawPosition { get; private set; }

    // 混色底圖在執行緒池上解碼（每次約 32 MB／25 ms）。blendedTexture／blendedPath／blendTargetPath／blendLoadStarted
    // 只在繪製執行緒讀寫；背景只經 completedBlend 交棒，結果的世代與 blendGeneration 不符就丟掉。
    private IDalamudTextureWrap? blendedTexture;
    private string blendedPath = string.Empty;
    private string blendTargetPath = string.Empty;
    private bool blendLoadStarted;
    private long blendGeneration;
    private BlendResult? completedBlend;
    private uint lastMissingTextureMapId;

    // 視窗連續顯示不到這麼久就不解碼：換角登入後原生地圖會閃開一幀就關，那種開窗看不到底圖。
    // 用時間不用幀數：高更新率下一閃也有好幾幀。只畫一幀的開窗經過時間是 0，一定略過。
    private const double BlendLoadMinVisibleMilliseconds = 50;
    private int lastBackgroundDrawFrame = int.MinValue;
    private int visibleFrames;
    private long visibleSinceTimestamp;

    // 換圖解碼的每小時彙總（要使用者回報的診斷，Information）。後三個由背景執行緒累加。
    private long blendSummarySinceTimestamp = Stopwatch.GetTimestamp();
    private int skippedBlendLoads;
    private int blendLoads;
    private long blendLoadMicroseconds;
    private long blendLoadBytes;

    // 每幀都要拿地圖路徑字串，位元組沒變就沿用上一幀的字串。
    private readonly Utf8PathCache mapPathCache = new();
    private readonly Utf8PathCache mapBgPathCache = new();

    public MapRenderer()
    {
        updateMaskingTextureAction = UpdateMaskingTextureIfRequested;
        LoadFogHooks();
    }

    public void Dispose()
    {
        UnloadFogHooks();

        // 換世代讓還在解碼的背景工作不再交棒。視窗系統已先拆掉，這一刻沒有人在畫底圖。
        Interlocked.Increment(ref blendGeneration);
        if (!Service.Framework.IsFrameworkUnloading) {
            Interlocked.Exchange(ref completedBlend, null)?.Texture?.Dispose();
            blendedTexture?.Dispose();
            blendedTexture = null;
        }
    }

    public void CenterOnGameObject(IGameObject obj) => CenterOnCoordinate(new Vector2(obj.Position.X, obj.Position.Z));

    public void CenterOnCoordinate(Vector2 coord) => DrawOffset = -coord * DrawHelpers.GetMapScaleFactor() + DrawHelpers.GetMapOffsetVector();

    public void DrawBaseTexture()
    {
        UpdateScaleLimits();
        UpdateDrawOffset();

        DrawBackgroundTexture();
    }

    public void DrawDynamicElements()
    {
        DrawFogOfWar();
        DrawMapMarkers();
    }

    private void UpdateScaleLimits() => Scale = Math.Clamp(Scale, 0.05f, 20.0f);

    private void UpdateDrawOffset()
    {
        var childCenterOffset = ImGui.GetContentRegionAvail() / 2.0f;
        var mapCenterOffset = new Vector2(1024.0f, 1024.0f) * Scale;

        DrawPosition = childCenterOffset - mapCenterOffset + DrawOffset * Scale;
    }

    private void DrawBackgroundTexture()
    {
        var agent = AgentMap.Instance();
        if (agent is null) return;

        // 開放 PvP 之後會畫到上游沒假設過的區域，這些地圖不保證有材質路徑。
        // 取不到就不畫，並印一行 Information 級診斷（每張地圖只印一次）。
        if (agent->SelectedMapPath.Length is 0 && agent->SelectedMapBgPath.Length is 0) {
            if (lastMissingTextureMapId != agent->SelectedMapId) {
                lastMissingTextureMapId = agent->SelectedMapId;
                Service.Log.Information($"[Mappy] MapId {agent->SelectedMapId} 沒有可用的地圖材質路徑，略過底圖繪製。");
            }

            return;
        }

        if (AgentMap.Instance()->SelectedMapBgPath.Length is 0) {
            var texture = Service.TextureProvider.GetFromGame(mapPathCache.Update(AgentMap.Instance()->SelectedMapPath.AsSpan()).TexPath).GetWrapOrEmpty();

            ImGui.SetCursorPos(DrawPosition);
            ImGui.Image(texture.Handle, texture.Size * Scale);
        }
        else {
            var bgPath = mapBgPathCache.Update(AgentMap.Instance()->SelectedMapBgPath.AsSpan()).Text;
            TrackVisibleFrames();
            AdoptCompletedBlend();
            LogBlendSummaryIfDue();

            if (blendTargetPath != bgPath) {
                // 換圖：霧貼圖屬於舊圖、在途的解碼作廢。舊底圖留到新的一張交棒才釋放，換回原圖不必重解。
                SwapFogTexture(null);
                Interlocked.Increment(ref blendGeneration);
                blendTargetPath = bgPath;
                blendLoadStarted = false;
            }

            if (blendedPath != bgPath && !blendLoadStarted && Stopwatch.GetElapsedTime(visibleSinceTimestamp).TotalMilliseconds >= BlendLoadMinVisibleMilliseconds) {
                StartBlendLoad(bgPath, agent);
            }

            // 解碼完成前不畫底圖（約 50 ms＋解碼時間）。刻意不先畫遊戲的共用貼圖墊檔：那會讓一閃即關的開窗
            // 也去讀同兩張檔（約 8 MB），省下的又花回去。
            if (blendedPath == bgPath && blendedTexture is not null) {
                ImGui.SetCursorPos(DrawPosition);
                ImGui.Image(blendedTexture.Handle, blendedTexture.Size * Scale);
            }
        }
    }

    private void TrackVisibleFrames()
    {
        var frame = ImGui.GetFrameCount();
        if (frame == lastBackgroundDrawFrame) return;

        if (frame != lastBackgroundDrawFrame + 1) {
            // 上一段連續顯示結束時還缺底圖、也沒開始解碼＝被「顯示不夠久」擋掉的一次。
            if (lastBackgroundDrawFrame != int.MinValue && blendedPath != blendTargetPath && !blendLoadStarted) skippedBlendLoads++;

            visibleFrames = 0;
            visibleSinceTimestamp = Stopwatch.GetTimestamp();
        }

        visibleFrames++;
        lastBackgroundDrawFrame = frame;
    }

    private void StartBlendLoad(string bgPath, AgentMap* agent)
    {
        blendLoadStarted = true;

        // 路徑在繪製執行緒上抄成字串；背景只拿這份快照，不讀 AgentMap。
        var request = new BlendRequest(
            Volatile.Read(ref blendGeneration),
            bgPath,
            mapBgPathCache.TexPath,
            mapPathCache.Update(agent->SelectedMapPath.AsSpan()).TexPath,
            visibleFrames,
            Stopwatch.GetElapsedTime(visibleSinceTimestamp).TotalMilliseconds);

        QueueBlendLoad(request);
    }

    // 閉包放在這裡而不是呼叫端：捕獲呼叫端區域變數的閉包會在方法一進來就配置。
    private void QueueBlendLoad(BlendRequest request)
        => Task.Run(() => LoadBlendedTexture(request));

    private void LoadBlendedTexture(BlendRequest request)
    {
        if (Service.Framework.IsFrameworkUnloading || Volatile.Read(ref blendGeneration) != request.Generation) return;

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        IDalamudTextureWrap? texture = null;
        try {
            texture = LoadTexture(request.BgTexPath, request.FgTexPath);
        }
        catch (Exception exception) {
            Service.Log.Warning(exception, $"[Mappy] 底圖混色：{request.Path} 載入失敗，這張圖不顯示底圖。");
        }

        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Interlocked.Increment(ref blendLoads);
        Interlocked.Add(ref blendLoadMicroseconds, (long)(elapsed.TotalMilliseconds * 1000));
        Interlocked.Add(ref blendLoadBytes, allocated);

        var current = Volatile.Read(ref blendGeneration) == request.Generation && !Service.Framework.IsFrameworkUnloading;
        Service.Log.Information($"[Mappy] 底圖混色：{request.Path} 背景解碼 {elapsed.TotalMilliseconds:F0} ms、配置 {allocated / 1048576.0:F1} MB（開窗第 {request.VisibleFrames} 幀、已顯示 {request.VisibleMilliseconds:F0} ms 時開始）{(current ? string.Empty : "；完成時已換圖或卸載，丟棄")}");

        if (!current) {
            if (!Service.Framework.IsFrameworkUnloading) texture?.Dispose();
            return;
        }

        // 被擠掉的那份從沒被畫過，在這裡釋放安全（Dalamud 的 Dispose 本身延到幀尾，任何執行緒可呼叫）。
        var result = new BlendResult(request.Generation, request.Path, texture);
        Interlocked.Exchange(ref completedBlend, result)?.Texture?.Dispose();

        // Dispose() 可能剛好在交棒前後換了世代：沒人會再來取，自己收回。
        if (Volatile.Read(ref blendGeneration) != request.Generation && Interlocked.CompareExchange(ref completedBlend, null, result) == result) {
            if (!Service.Framework.IsFrameworkUnloading) texture?.Dispose();
        }
    }

    private void AdoptCompletedBlend()
    {
        if (Volatile.Read(ref completedBlend) is null) return;

        var done = Interlocked.Exchange(ref completedBlend, null);
        if (done is null) return;

        if (done.Generation != Volatile.Read(ref blendGeneration) || done.Path != blendTargetPath) {
            done.Texture?.Dispose();
            return;
        }

        // 失敗（null）也交棒：同一張圖不重試，與同步載入時一樣。
        blendedTexture?.Dispose();
        blendedTexture = done.Texture;
        blendedPath = done.Path;
    }

    private void LogBlendSummaryIfDue()
    {
        var elapsed = Stopwatch.GetElapsedTime(blendSummarySinceTimestamp);
        if (elapsed.TotalHours < 1) return;

        blendSummarySinceTimestamp = Stopwatch.GetTimestamp();
        var skipped = skippedBlendLoads;
        skippedBlendLoads = 0;
        var loads = Interlocked.Exchange(ref blendLoads, 0);
        var micros = Interlocked.Exchange(ref blendLoadMicroseconds, 0);
        var bytes = Interlocked.Exchange(ref blendLoadBytes, 0);
        if (skipped is 0 && loads is 0) return;

        Service.Log.Information($"[Mappy] 底圖混色（過去 {elapsed.TotalMinutes:F0} 分鐘）：背景解碼 {loads} 次、共 {micros / 1000.0:F0} ms、配置 {bytes / 1048576.0:F0} MB；開窗未滿 {BlendLoadMinVisibleMilliseconds:F0} ms 就關、略過解碼 {skipped} 次。");
    }

    private sealed record BlendRequest(long Generation, string Path, string BgTexPath, string FgTexPath, int VisibleFrames, double VisibleMilliseconds);

    private sealed record BlendResult(long Generation, string Path, IDalamudTextureWrap? Texture);

    /// <summary>
    /// 執行緒池上呼叫：只用呼叫端抄好的路徑字串，不碰 AgentMap 與 ImGui。
    /// </summary>
    private static IDalamudTextureWrap? LoadTexture(string vanillaBgPath, string vanillaFgPath)
    {
        var bgFile = GetTexFile(vanillaBgPath);
        var fgFile = GetTexFile(vanillaFgPath);

        if (bgFile is null || fgFile is null) {
            Service.Log.Warning("Failed to load map textures");
            return null;
        }

        // 遊戲內的地圖都是 2048 BC1：直接解成混好的 RGBA，省掉整張解碼的中間陣列（每次換圖約 130 MB → 16 MB）。
        var blended = MapTextureBlend.TryBlendBc1(bgFile, fgFile);
        if (blended is not null) {
            return Service.TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(MapTextureBlend.Size, MapTextureBlend.Size), blended);
        }

        var backgroundBytes = bgFile.GetRgbaImageData();
        var foregroundBytes = fgFile.GetRgbaImageData();

        // Blend textures together
        Parallel.For(0, 2048 * 2048, i =>
        {
            var index = i * 4;

            // Blend, R, G, B, skip A.
            backgroundBytes[index + 0] = (byte)(backgroundBytes[index + 0] * foregroundBytes[index + 0] / 255);
            backgroundBytes[index + 1] = (byte)(backgroundBytes[index + 1] * foregroundBytes[index + 1] / 255);
            backgroundBytes[index + 2] = (byte)(backgroundBytes[index + 2] * foregroundBytes[index + 2] / 255);
        });

        return Service.TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(2048, 2048), backgroundBytes);
    }

    private static TexFile? GetTexFile(string rawPath)
    {
        var path = Service.TextureSubstitutionProvider.GetSubstitutedPath(rawPath);

        if (Path.IsPathRooted(path)) {
            return Service.DataManager.GameData.GetFileFromDisk<TexFile>(path);
        }

        return Service.DataManager.GetFile<TexFile>(path);
    }

    private void DrawMapMarkers()
    {
        DrawRouteOverlayLines();
        DrawStaticMapMarkers();
        DrawMiniMapMarkers();
        DrawDynamicMarkers();
        DrawSocialMarkers();
        DrawGameObjects();
        DrawGroupMembers();
        DrawTemporaryMarkers();
        DrawGatheringMarkers();
        DrawFieldMarkers();
        DrawRouteOverlayPoints();
        DrawPlayer();
        DrawStaticTextMarkers();
        DrawFlag();
        DrawIpcMarkers();
    }
}