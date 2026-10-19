namespace Mappy.Classes;

/// <summary>
/// 「改了就記著，等滑鼠放開才存一次」。拖曳或縮放視窗時每一幀都在變，逐幀存檔等於每幀寫一次磁碟。
/// 沒按著滑鼠時的改動（例如設定頁直接輸入座標）當幀就存。
/// </summary>
public sealed class DeferredSaveGate
{
    private bool dirty;

    public bool IsDirty => dirty;

    /// <returns>這一幀要不要存檔。</returns>
    public bool Observe(bool changedThisFrame, bool mouseHeld)
    {
        if (changedThisFrame) dirty = true;
        if (!dirty || mouseHeld) return false;

        dirty = false;
        return true;
    }
}
