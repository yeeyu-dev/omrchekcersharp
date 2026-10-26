using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OMRChecker.Logging;

namespace OMRChecker.Utils;

/// <summary>JSON 读取、合并与取值辅助方法。</summary>
public static class JsonUtils
{
    /// <summary>
    /// 读取 JSON 文件。解析失败时记录 critical 日志并抛出 <see cref="OmrCheckerException"/>
    /// （Python 版此处直接 exit(1)）。
    /// </summary>
    public static JsonNode LoadJson(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))
                   ?? throw new JsonException("文件内容为 null");
        }
        catch (JsonException error)
        {
            Logger.Critical($"Error when loading json file at: '{path}'\n{error.Message}");
            throw new OmrCheckerException($"Error when loading json file at: '{path}'");
        }
    }

    /// <summary>
    /// 深度合并：对象与对象递归合并，其余类型（含数组）由 source 覆盖 target。
    /// 对应 Python 版 deepmerge 的 OVERRIDE_MERGER。会直接修改 target 并返回它。
    /// </summary>
    public static JsonObject DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObj && target[key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }

        return target;
    }

    /// <summary>读取数值节点为 double；节点为 null 时返回默认值。</summary>
    public static double GetDouble(JsonNode? node, double defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        // 通过 JSON 文本解析，兼容解析得到的节点与代码中创建的 int/long/double 节点
        var value = node.AsValue();
        var text = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>读取数值节点并按 Python int() 语义截断为整数。</summary>
    public static int GetInt(JsonNode? node, int defaultValue) =>
        node is null ? defaultValue : (int)Math.Truncate(GetDouble(node, defaultValue));

    /// <summary>读取布尔节点；节点为 null 时返回默认值。</summary>
    public static bool GetBool(JsonNode? node, bool defaultValue) => node?.GetValue<bool>() ?? defaultValue;

    /// <summary>读取字符串节点；节点为 null 时返回默认值。</summary>
    public static string? GetString(JsonNode? node, string? defaultValue = null) =>
        node is null ? defaultValue : node.GetValue<string>();

    /// <summary>读取字符串数组节点。</summary>
    public static List<string> GetStringList(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToList() : new List<string>();

    /// <summary>读取数值数组节点，并逐个按 Python int() 截断。</summary>
    public static int[] GetIntArray(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => GetInt(n, 0)).ToArray() : Array.Empty<int>();

    /// <summary>
    /// 判断 JSON 数值是否为整数字面量（对应 Python json 解析出的 int 而非 float），
    /// 例如 "2" 为整数，"2.0" 不是。
    /// </summary>
    public static bool IsIntegerLiteral(JsonNode node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        var raw = value.ToJsonString();
        return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E');
    }

    /// <summary>
    /// 将 JSON 节点转换为 Python 风格的对象树：string / long / double / bool / null / List&lt;object?&gt;。
    /// 用于答案项（answers_in_order），以便与 CSV 中 Python 字面量解析结果走同一套逻辑。
    /// </summary>
    public static object? ToPlainObject(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
                return array.Select(ToPlainObject).ToList();
            case JsonObject obj:
                return obj.ToDictionary(kv => kv.Key, kv => ToPlainObject(kv.Value));
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        return value.GetValue<string>();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Number:
                        // 必须显式装箱：否则三元表达式会把 long 提升为 double
                        return IsIntegerLiteral(value) ? (object)value.GetValue<long>() : (object)value.GetValue<double>();
                    default:
                        return null;
                }

            default:
                return null;
        }
    }
}
