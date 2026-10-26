namespace OMRChecker.Utils;

/// <summary>
/// .NET Framework 4.8 缺失的 BCL API 补齐（.NET Core 2.0+ 才内置），保持调用处写法与 .NET 8 版一致。
/// </summary>
internal static class NetFxCompat
{
    /// <summary>支持 <c>foreach (var (key, value) in dict)</c> 解构写法。</summary>
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    /// <summary>等价于 .NET 7 的 Enumerable.Order（稳定升序）。</summary>
    public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T>? comparer = null) =>
        source.OrderBy(x => x, comparer ?? Comparer<T>.Default);

    /// <summary>等价于 .NET Core 2.0 的 CollectionExtensions.GetValueOrDefault。</summary>
    public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
        dictionary.TryGetValue(key, out var value) ? value : default;

    /// <inheritdoc cref="GetValueOrDefault{TKey,TValue}(IReadOnlyDictionary{TKey,TValue},TKey)"/>
    public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key,
        TValue defaultValue) =>
        dictionary.TryGetValue(key, out var value) ? value : defaultValue;

    /// <summary>
    /// 等价于 .NET Core 2.0 的 Path.GetRelativePath。
    /// 两者相同时返回 "."；仅处理 <paramref name="path"/> 位于 <paramref name="relativeTo"/> 之下或同盘的情况。
    /// </summary>
    public static string GetRelativePath(string relativeTo, string path)
    {
        var from = AppendSeparator(Path.GetFullPath(relativeTo));
        var to = Path.GetFullPath(path);
        if (string.Equals(AppendSeparator(to), from, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (to.StartsWith(from, StringComparison.OrdinalIgnoreCase))
        {
            return to.Substring(from.Length);
        }

        // 非子目录（含 ..）的一般情况交给 Uri 计算
        var relative = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(to)).ToString());
        return relative.Length == 0 ? "." : relative.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
}
