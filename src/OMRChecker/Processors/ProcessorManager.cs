using System.Text.Json.Nodes;
using OMRChecker.Core;
using OMRChecker.Logging;
using OMRChecker.Utils;

namespace OMRChecker.Processors;

/// <summary>
/// 预处理器注册表（单例），对应 Python 版通过反射扫描包的 ProcessorManager。
/// C# 版改为显式注册；新增预处理器时在 <see cref="Factories"/> 中登记，
/// 并同步更新 Schemas/template.schema.json 中 preProcessors.name 的枚举。
/// </summary>
public static class ProcessorManager
{
    private delegate ImagePreprocessor Factory(JsonObject options, string relativeDir, ImageInstanceOps ops);

    private static readonly Dictionary<string, Factory> Factories = new()
    {
        [nameof(CropOnMarkers)] = (o, d, ops) => new CropOnMarkers(o, d, ops),
        [nameof(CropPage)] = (o, d, ops) => new CropPage(o, d, ops),
        [nameof(FeatureBasedAlignment)] = (o, d, ops) => new FeatureBasedAlignment(o, d, ops),
        [nameof(GaussianBlur)] = (o, d, ops) => new GaussianBlur(o, d, ops),
        [nameof(Levels)] = (o, d, ops) => new Levels(o, d, ops),
        [nameof(MedianBlur)] = (o, d, ops) => new MedianBlur(o, d, ops),
    };

    private static readonly Lazy<bool> Loaded = new(() =>
    {
        Logger.Info($"Loading processors from \"{typeof(ProcessorManager).Namespace}\"...");
        Logger.Info($"Loaded processors: {PyFormat.Repr(Factories.Keys.ToList())}");
        return true;
    });

    /// <summary>已注册的预处理器名称。</summary>
    public static IReadOnlyCollection<string> Names => Factories.Keys;

    /// <summary>
    /// 按名称创建预处理器实例。边界：名称未注册时抛出异常（正常情况下 schema 校验已拦截）。
    /// </summary>
    public static ImagePreprocessor Create(string name, JsonObject options, string relativeDir, ImageInstanceOps ops)
    {
        _ = Loaded.Value;
        if (!Factories.TryGetValue(name, out var factory))
        {
            throw new OmrCheckerException($"Unknown pre-processor: '{name}'");
        }

        return factory(options, relativeDir, ops);
    }
}
