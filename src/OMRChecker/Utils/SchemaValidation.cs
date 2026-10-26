using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using OMRChecker.Logging;

namespace OMRChecker.Utils;

/// <summary>
/// 基于 JSON Schema (draft 2020-12) 的配置校验，对应 Python 版 src/utils/validations.py。
/// schema 文件以嵌入资源形式随程序发布（Schemas/*.schema.json）。
/// </summary>
public static class SchemaValidation
{
    private static readonly Lazy<JsonSchema> ConfigSchema = new(() => LoadSchema("config"));
    private static readonly Lazy<JsonSchema> TemplateSchema = new(() => LoadSchema("template"));
    private static readonly Lazy<JsonSchema> EvaluationSchema = new(() => LoadSchema("evaluation"));

    /// <summary>校验 evaluation.json，不合法时打印错误表并抛出异常。</summary>
    public static void ValidateEvaluationJson(JsonNode jsonData, string evaluationPath)
    {
        Logger.Info($"Loading evaluation.json: {evaluationPath}");
        Validate(EvaluationSchema.Value, jsonData, kind: "evaluation",
            requiredHint: ". Make sure the spelling of the key is correct",
            failMessage: $"Provided Evaluation JSON is Invalid: '{evaluationPath}'");
    }

    /// <summary>校验 template.json，不合法时打印错误表并抛出异常。</summary>
    public static void ValidateTemplateJson(JsonNode jsonData, string templatePath)
    {
        Logger.Info($"Loading template.json: {templatePath}");
        Validate(TemplateSchema.Value, jsonData, kind: "template",
            requiredHint: ". Check for spelling errors and make sure it is in camelCase",
            failMessage: $"Provided Template JSON is Invalid: '{templatePath}'");
    }

    /// <summary>校验 config.json，不合法时打印错误表并抛出异常。</summary>
    public static void ValidateConfigJson(JsonNode jsonData, string configPath)
    {
        Logger.Info($"Loading config.json: {configPath}");
        Validate(ConfigSchema.Value, jsonData, kind: "config",
            requiredHint: ". Check for spelling errors and make sure it is in camelCase",
            failMessage: $"Provided config JSON is Invalid: '{configPath}'");
    }

    /// <summary>
    /// 执行校验并汇总错误：每条错误取实例路径第一段作为 Key（根对象为 $root）。
    /// required 错误追加缺失的属性名；模板 preProcessors 下的错误显示为 preProcessors.名称.键。
    /// </summary>
    private static void Validate(JsonSchema schema, JsonNode jsonData, string kind, string requiredHint, string failMessage)
    {
        var results = schema.Evaluate(jsonData, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return;
        }

        var table = new ConsoleTable(showLines: true);
        table.AddColumn("Key");
        table.AddColumn("Error");

        var details = results.Details
            .Where(d => d.HasErrors && d.Errors is not null)
            .OrderBy(d => d.InstanceLocation.ToString(), StringComparer.Ordinal);
        foreach (var detail in details)
        {
            var segments = detail.InstanceLocation.ToString()
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Replace("~1", "/").Replace("~0", "~"))
                .ToArray();
            var key = segments.Length > 0 ? segments[0] : "$root";

            foreach (var (keyword, message) in detail.Errors!)
            {
                if (kind == "template" && key == "preProcessors" && segments.Length >= 3
                    && int.TryParse(segments[1], out var index))
                {
                    var name = jsonData["preProcessors"]?[index]?["name"]?.ToString();
                    table.AddRow($"{key}.{name}.{segments[2]}", message);
                }
                else if (keyword == "required")
                {
                    var match = Regex.Match(message, "[\"'](.*?)[\"']");
                    var property = match.Success ? match.Groups[1].Value : string.Empty;
                    table.AddRow($"{key}.{property}", message + requiredHint);
                }
                else
                {
                    table.AddRow(key, message);
                }
            }
        }

        table.Print();
        throw new OmrCheckerException(failMessage);
    }

    /// <summary>从嵌入资源加载指定名称的 schema。</summary>
    private static JsonSchema LoadSchema(string name)
    {
        var resource = $"Schemas.{name}.schema.json";
        using var stream = typeof(SchemaValidation).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"未找到嵌入的 schema 资源: {resource}");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
