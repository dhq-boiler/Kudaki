using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kudaki.App.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;
using YamlDotNet.Serialization.NamingConventions;

namespace Kudaki.App.Services;

public sealed class YamlStorageService
{
    // 主拡張子。読込時は .yaml / .yml も受け付ける。
    public const string PrimaryExtension = ".wbs.yaml";

    // 保存ダイアログ用のフィルタ文字列 (SaveFileDialog.Filter にそのまま流せる)。
    public const string SaveFilter = "Kudaki WBS ファイル (*.wbs.yaml)|*.wbs.yaml|YAML (*.yaml)|*.yaml";

    public const string OpenFilter =
        "Kudaki WBS ファイル (*.wbs.yaml;*.yaml;*.yml)|*.wbs.yaml;*.yaml;*.yml|すべてのファイル (*.*)|*.*";

    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public YamlStorageService()
    {
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new DateOnlyConverter(
                System.Globalization.CultureInfo.InvariantCulture,
                doubleQuotes: false,
                formats: new[] { "yyyy-MM-dd" }))
            .ConfigureDefaultValuesHandling(
                DefaultValuesHandling.OmitNull |
                DefaultValuesHandling.OmitEmptyCollections)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new DateOnlyConverter(
                System.Globalization.CultureInfo.InvariantCulture,
                doubleQuotes: false,
                formats: new[] { "yyyy-MM-dd" }))
            .IgnoreUnmatchedProperties()
            .Build();
    }

    // in-memory シリアライズ。MCP の get_document から現在の Document スナップショットを
    // 取り出すときにも使うので public に。ファイル保存とは違い ModifiedAt を触らない。
    public string SerializeToString(WbsDocument document)
    {
        return _serializer.Serialize(document);
    }

    public async Task SaveAsync(WbsDocument document, string path, CancellationToken ct = default)
    {
        document.ModifiedAt = DateTime.UtcNow;
        var yaml = SerializeToString(document);

        // UTF-8 without BOM が YAML の慣例。
        await File.WriteAllTextAsync(path, yaml, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    // in-memory デシリアライズ。MCP propose_changes で AI から投入された
    // YAML 文字列をパースするのに使う。ファイル I/O とバージョンチェックの共通部品。
    public WbsDocument DeserializeFromString(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new WbsLoadException("YAML が空っすね。");
        }

        WbsDocument? doc;
        try
        {
            doc = _deserializer.Deserialize<WbsDocument>(yaml);
        }
        catch (YamlException ex)
        {
            throw new WbsLoadException($"YAML の解釈に失敗したっす: {ex.Message}", ex);
        }

        if (doc is null)
        {
            throw new WbsLoadException("YAML から WbsDocument を復元できなかったっす。");
        }

        if (doc.Version > WbsDocument.CurrentVersion)
        {
            throw new WbsLoadException(
                $"このファイルのフォーマットバージョン ({doc.Version}) は新しすぎるっす。" +
                $"Kudaki 側の対応は v{WbsDocument.CurrentVersion} まで。");
        }

        // 将来 v0 → v1 みたいなマイグレーションが要ればここに書く。

        return doc;
    }

    public async Task<WbsDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WbsLoadException($"ファイルの読込に失敗したっす: {ex.Message}", ex);
        }

        return DeserializeFromString(yaml);
    }
}

public sealed class WbsLoadException : Exception
{
    public WbsLoadException(string message) : base(message) { }
    public WbsLoadException(string message, Exception inner) : base(message, inner) { }
}
