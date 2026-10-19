using System;
using System.Drawing;
using System.Numerics;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.Interop;

namespace Mappy.Extensions;

public static unsafe class FateContextExtensions
{
    /// <summary>
    /// 依 FATE 剩餘時間算出顏色：剩餘 5 分鐘以內由黃漸變到紅，否則回傳白色。
    /// </summary>
    /// <param name="context">FATE 內容指標。只在呼叫當下解參，不保存跨幀。</param>
    /// <param name="alpha">只對「直接把回傳值交給 ImGui」的呼叫端有效；地圖上所有圓圈的透明度統一由使用者設定的「區域顏色」決定，所以預設值在地圖圓圈上永遠看不到效果。</param>
    public static Vector4 GetColor(this Pointer<FateContext> context, float alpha = 0.33f)
    {
        var timeRemaining = GetTimeRemaining(context);
        if (timeRemaining <= TimeSpan.FromSeconds(300) && timeRemaining.TotalSeconds > 0) {
            var hue = (float)(timeRemaining.TotalSeconds / 300.0f * 25.0f);

            var hsvColor = new ColorHelpers.HsvaColor(hue / 100.0f, 1.0f, 1.0f, alpha);
            return ColorHelpers.HsvToRgb(hsvColor);
        }

        return KnownColor.White.Vector();
    }

    public static TimeSpan GetTimeRemaining(this Pointer<FateContext> context)
    {
        if (context.Value->Duration is 0) return TimeSpan.Zero;

        return TimeSpan.FromSeconds(context.Value->StartTimeEpoch + context.Value->Duration - DateTimeOffset.Now.ToUnixTimeSeconds());
    }

    /// <summary>
    /// 緊急遭遇戰目前階段的剩餘時間，算法同遊戲自己的倒數：StartTimestamp 其實是階段截止時刻，
    /// 戰鬥中另以 SecondsLeft（為 0 時用 SecondsDuration）封頂。數值不合理時回 null，不要把它畫成 0。
    /// </summary>
    public static TimeSpan? GetTimeRemaining(this in DynamicEvent dynamicEvent, long unixNow)
    {
        var total = (long)dynamicEvent.SecondsDuration;
        if (total is 0 || dynamicEvent.StartTimestamp <= 0 || dynamicEvent.SecondsLeft > total) return null;

        var remaining = dynamicEvent.StartTimestamp - unixNow;
        if (remaining > total) return null;

        remaining = Math.Max(remaining, 0);
        if (dynamicEvent.State is DynamicEventState.Battle && dynamicEvent.SecondsLeft is not 0) {
            remaining = Math.Min(remaining, dynamicEvent.SecondsLeft);
        }

        return TimeSpan.FromSeconds(remaining);
    }
}