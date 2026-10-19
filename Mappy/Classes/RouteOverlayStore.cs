using System;
using System.Collections.Generic;
using System.Numerics;

namespace Mappy.Classes;

public enum RouteOverlayKind : byte
{
    Path,
    Points,
}

/// <summary>Mappy.Overlay.SetPath／SetPoints 的 flags。低 2 位是狀態，其餘未列出的位元保留（收到時忽略）。</summary>
public static class RouteOverlayFlags
{
    public const int StateMask = 0x03;
    public const int StateConfirmed = 0;
    public const int StatePossible = 1;
    public const int StateDone = 2;

    /// <summary>沿線畫方向箭頭；只對 SetPath 有效。</summary>
    public const int Arrows = 0x10;

    /// <summary>點旁標序號（從 1 開始）。</summary>
    public const int Numbers = 0x20;
}

/// <summary>一筆 IPC 送來的路線或節點。建立後不再變動；點與標籤都是 Mappy 自己的複本。</summary>
public sealed class RouteOverlayEntry
{
    public required string Source { get; init; }
    public required string Key { get; init; }
    public required uint TerritoryId { get; init; }
    public required RouteOverlayKind Kind { get; init; }
    public required Vector3[] Points { get; init; }

    /// <summary>0xRRGGBB（最高位元組已清掉）。</summary>
    public required uint Rgb { get; init; }

    public required int Flags { get; init; }

    /// <summary>null，或長度等於點數；每個元素非 null、最多 <see cref="RouteOverlayStore.MaxLabelLength"/> 字。</summary>
    public string[]? Labels { get; init; }

    public required long ExpiresAtMs { get; init; }

    public int State => Flags & RouteOverlayFlags.StateMask;

    public bool HasArrows => (Flags & RouteOverlayFlags.Arrows) != 0;

    public bool HasNumbers => (Flags & RouteOverlayFlags.Numbers) != 0;
}

/// <summary>
/// 路線疊加層 IPC 的資料。執行緒：Set／Remove／ClearSource 跑在呼叫端的執行緒，其餘在框架執行緒；
/// 全部經過 syncRoot，鎖內只碰記憶體（不呼叫 ImGui、不做 I/O、不呼叫回呼）。
/// 公開方法絕不擲例外：驗證失敗或內部錯誤一律回 false。
/// </summary>
public sealed class RouteOverlayStore
{
    public const int ApiVersion = 1;

    public const int MaxPoints = 2048;
    public const int MaxLabelLength = 128;
    public const int MinTtlMs = 500;
    public const int MaxTtlMs = 600_000;
    public const int MaxSources = 32;
    public const int MaxSourceLength = 64;
    public const int MaxKeyLength = 128;
    public const int MaxEntriesPerSource = 256;
    public const int MaxPointsPerSource = 65_536;

    private const long RejectLogIntervalMs = 60_000;
    private const int MaxRejectLogKeys = 256;

    private readonly object syncRoot = new();
    private readonly Dictionary<string, SourceState> sources = new(StringComparer.Ordinal);

    private readonly object rejectLogLock = new();
    private readonly Dictionary<string, RejectLogState> rejectLog = new(StringComparer.Ordinal);

    private readonly Func<long> clock;
    private readonly Action<string>? log;
    private readonly Action<string>? sourceRegistered;

    /// <param name="clock">毫秒，單調遞增。</param>
    /// <param name="log">Information 等級的 log；可能在任何執行緒上被呼叫。</param>
    /// <param name="sourceRegistered">新來源第一次被接受時呼叫（呼叫端的執行緒、鎖外）。</param>
    public RouteOverlayStore(Func<long> clock, Action<string>? log, Action<string>? sourceRegistered)
    {
        this.clock = clock;
        this.log = log;
        this.sourceRegistered = sourceRegistered;
    }

    public bool Set(RouteOverlayKind kind, string? source, string? key, uint territoryId, Vector3[]? points, uint rgb, int flags, string[]? labels, int ttlMs)
    {
        try {
            return SetCore(kind, source, key, territoryId, points, rgb, flags, labels, ttlMs);
        }
        catch (Exception exception) {
            LogProblem(source, "內部錯誤", exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    public bool Remove(string? source, string? key)
    {
        try {
            if (string.IsNullOrWhiteSpace(source) || key is null) return false;

            var trimmedSource = source.Trim();
            lock (syncRoot) {
                if (!sources.TryGetValue(trimmedSource, out var state)) return false;
                if (!state.Entries.Remove(key, out var removed)) return false;

                state.PointTotal -= removed.Points.Length;
                return true;
            }
        }
        catch (Exception exception) {
            LogProblem(source, "內部錯誤", exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    /// <returns>這個來源本次有送過東西就回 true（清空後來源仍保留，設定頁照樣列出）。</returns>
    public bool ClearSource(string? source)
    {
        try {
            if (string.IsNullOrWhiteSpace(source)) return false;

            var trimmedSource = source.Trim();
            lock (syncRoot) {
                if (!sources.TryGetValue(trimmedSource, out var state)) return false;

                state.Entries.Clear();
                state.PointTotal = 0;
                return true;
            }
        }
        catch (Exception exception) {
            LogProblem(source, "內部錯誤", exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    /// <summary>某區域內還沒過期的條目，放進呼叫端的清單（先清空）。不看來源開關，那由呼叫端在鎖外判斷。</summary>
    public void CopyVisible(uint territoryId, long nowMs, List<RouteOverlayEntry> result)
    {
        result.Clear();

        lock (syncRoot) {
            foreach (var (_, state) in sources) {
                foreach (var (_, entry) in state.Entries) {
                    if (entry.TerritoryId != territoryId || entry.ExpiresAtMs <= nowMs) continue;

                    result.Add(entry);
                }
            }
        }
    }

    /// <summary>本次送過東西的來源與各自還沒過期的筆數，放進呼叫端的清單（先清空）。</summary>
    public void CopySummary(long nowMs, List<(string Source, int Count)> result)
    {
        result.Clear();

        lock (syncRoot) {
            foreach (var (source, state) in sources) {
                var count = 0;
                foreach (var (_, entry) in state.Entries) {
                    if (entry.ExpiresAtMs > nowMs) count++;
                }

                result.Add((source, count));
            }
        }
    }

    /// <summary>清掉過期的條目。只在框架執行緒呼叫。</summary>
    public void SweepExpired(long nowMs)
    {
        lock (syncRoot) {
            foreach (var (_, state) in sources) {
                // .NET Core 3.0 起 Dictionary.Remove 不會讓列舉中的列舉器失效。
                foreach (var (key, entry) in state.Entries) {
                    if (entry.ExpiresAtMs > nowMs) continue;

                    state.Entries.Remove(key);
                    state.PointTotal -= entry.Points.Length;
                }
            }
        }
    }

    public void Clear()
    {
        lock (syncRoot) {
            sources.Clear();
        }
    }

    private bool SetCore(RouteOverlayKind kind, string? source, string? key, uint territoryId, Vector3[]? points, uint rgb, int flags, string[]? labels, int ttlMs)
    {
        if (string.IsNullOrWhiteSpace(source)) return Reject(source, "source 是空的");

        var trimmedSource = source.Trim();
        if (trimmedSource.Length > MaxSourceLength) return Reject(source, $"source 超過 {MaxSourceLength} 字");
        if (key is null) return Reject(trimmedSource, "key 是 null");
        if (key.Length > MaxKeyLength) return Reject(trimmedSource, $"key 超過 {MaxKeyLength} 字");
        if (territoryId is 0) return Reject(trimmedSource, "territoryId 是 0");
        if (points is null) return Reject(trimmedSource, "points 是 null");
        if (points.Length is 0) return Reject(trimmedSource, "points 是空的（要刪掉請呼叫 Remove）");
        if (points.Length > MaxPoints) return Reject(trimmedSource, $"點數超過上限 {MaxPoints}", $"{points.Length} 點");
        if ((flags & RouteOverlayFlags.StateMask) is 3) return Reject(trimmedSource, "flags 低 2 位的狀態 3 未定義");

        // 型別相同時 CallGate 直接把呼叫端的陣列參考交過來：先複製，之後只驗、只存複本。
        var pointCopy = points.AsSpan().ToArray();
        foreach (var point in pointCopy) {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return Reject(trimmedSource, "points 含 NaN 或無限大");
        }

        string[]? labelCopy = null;
        if (labels is not null) {
            labelCopy = labels.AsSpan().ToArray();
            if (labelCopy.Length != pointCopy.Length) return Reject(trimmedSource, "labels 長度與點數不同", $"labels {labelCopy.Length} 個、點數 {pointCopy.Length}");

            for (var index = 0; index < labelCopy.Length; index++) {
                labelCopy[index] = TruncateLabel(labelCopy[index]);
            }
        }

        var entry = new RouteOverlayEntry
        {
            Source = trimmedSource,
            Key = key,
            TerritoryId = territoryId,
            Kind = kind,
            Points = pointCopy,
            Rgb = rgb & 0x00FF_FFFFu,
            Flags = flags,
            Labels = labelCopy,
            ExpiresAtMs = clock() + Math.Clamp(ttlMs, MinTtlMs, MaxTtlMs),
        };

        string? limitReason = null;
        var isNewSource = false;

        lock (syncRoot) {
            if (!sources.TryGetValue(trimmedSource, out var state)) {
                if (sources.Count >= MaxSources) {
                    limitReason = $"來源數已達上限 {MaxSources}";
                }
                else {
                    state = new SourceState();
                    sources[trimmedSource] = state;
                    isNewSource = true;
                }
            }

            if (state is not null) {
                state.Entries.TryGetValue(key, out var previous);
                var previousPoints = previous?.Points.Length ?? 0;

                if (previous is null && state.Entries.Count >= MaxEntriesPerSource) {
                    limitReason = $"這個來源的筆數已達上限 {MaxEntriesPerSource}";
                }
                else if (state.PointTotal - previousPoints + pointCopy.Length > MaxPointsPerSource) {
                    limitReason = $"這個來源的總點數會超過上限 {MaxPointsPerSource}";
                }
                else {
                    state.Entries[key] = entry;
                    state.PointTotal += pointCopy.Length - previousPoints;
                }
            }
        }

        if (isNewSource) {
            NotifySourceRegistered(trimmedSource);
        }

        return limitReason is null || Reject(trimmedSource, limitReason);
    }

    // 條目已經收下：回呼出錯只記 log，不影響這次的回傳值。
    private void NotifySourceRegistered(string source)
    {
        try {
            sourceRegistered?.Invoke(source);
        }
        catch (Exception exception) {
            LogProblem(source, "新來源回呼失敗", exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static string TruncateLabel(string? label)
    {
        if (label is null) return string.Empty;
        if (label.Length <= MaxLabelLength) return label;

        // 不要從代理字組中間切開。
        var length = char.IsHighSurrogate(label[MaxLabelLength - 1]) ? MaxLabelLength - 1 : MaxLabelLength;
        return label[..length];
    }

    /// <param name="reason">節流用的固定文字；會變的數字放 <paramref name="detail"/>。</param>
    private bool Reject(string? source, string reason, string? detail = null)
    {
        LogProblem(source, "拒絕：" + reason, detail);
        return false;
    }

    /// <summary>同一個「來源＋原因」每 60 秒最多記一行，期間被擋下的次數併進下一行。</summary>
    private void LogProblem(string? source, string reason, string? detail)
    {
        if (log is null) return;

        var safeSource = source is null ? "(null)" : source.Length > MaxSourceLength ? source[..MaxSourceLength] : source;
        var throttleKey = safeSource + "|" + reason;
        var now = clock();
        int suppressed;

        lock (rejectLogLock) {
            if (rejectLog.TryGetValue(throttleKey, out var previous) && now - previous.LastLoggedAtMs < RejectLogIntervalMs) {
                previous.Suppressed++;
                return;
            }

            if (previous is null && rejectLog.Count >= MaxRejectLogKeys) {
                rejectLog.Clear();
            }

            suppressed = previous?.Suppressed ?? 0;
            rejectLog[throttleKey] = new RejectLogState { LastLoggedAtMs = now };
        }

        var suffix = suppressed > 0 ? $"（前 60 秒內另有 {suppressed} 次同樣原因，未逐筆記錄）" : string.Empty;
        var detailText = detail is null ? string.Empty : $"：{detail}";
        log($"[Mappy] 路線疊加層 IPC（來源「{safeSource}」）{reason}{detailText}{suffix}");
    }

    private sealed class SourceState
    {
        public readonly Dictionary<string, RouteOverlayEntry> Entries = new(StringComparer.Ordinal);
        public int PointTotal;
    }

    private sealed class RejectLogState
    {
        public long LastLoggedAtMs;
        public int Suppressed;
    }
}
