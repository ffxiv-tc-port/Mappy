using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Mappy.Classes.Caches;

/// <summary>
/// 與 KamiLib 的 Cache 行為相同（第一次查回預設值、背景載入、之後回載入值），
/// 只差在命中時不配置：KamiLib 那支在方法開頭就建立捕獲 key 的閉包，每次查快取都付一次。
/// KamiLib 是子模組（別的外掛也釘它），所以在本外掛內另寫。
/// </summary>
public abstract class MappyCache<T, TU> where T : notnull
{
    private readonly ConcurrentDictionary<T, TU?> internalCache = [];

    public TU? GetValue(T key)
    {
        if (internalCache.TryGetValue(key, out var result)) {
            return result;
        }

        return AddAndLoad(key);
    }

    private TU? AddAndLoad(T key)
    {
        if (!internalCache.TryAdd(key, default)) {
            return default;
        }

        Task.Run(() => internalCache[key] = LoadValue(key));
        return internalCache[key];
    }

    protected abstract TU? LoadValue(T key);
}
