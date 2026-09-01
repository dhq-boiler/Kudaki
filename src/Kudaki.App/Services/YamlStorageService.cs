using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kudaki.App.Models;
using Kudaki.App.Properties;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;
using YamlDotNet.Serialization.NamingConventions;

namespace Kudaki.App.Services;

public sealed class YamlStorageService
{
    // 主拡張子。読込時は .yaml / .yml も受け付ける。
    public const string PrimaryExtension = ".wbs.yaml";

    // 保存 / 読込ダイアログ用のフィルタ (SaveFileDialog.Filter / OpenFileDialog.Filter に流す)。
    // resx 側で言語別に持つのでプロパティ (static getter) で解決。
    public static string SaveFilter => Strings.Storage_YamlSaveFilter;
    public static string OpenFilter => Strings.Storage_YamlOpenFilter;

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
            throw new WbsLoadException(Strings.Storage_Error_EmptyYaml);
        }

        WbsDocument? doc;
        try
        {
            doc = _deserializer.Deserialize<WbsDocument>(yaml);
        }
        catch (YamlException ex)
        {
            throw new WbsLoadException(string.Format(Strings.Storage_Error_YamlParse_Format, ex.Message), ex);
        }

        if (doc is null)
        {
            throw new WbsLoadException(Strings.Storage_Error_UnableToRestore);
        }

        if (doc.Version > WbsDocument.CurrentVersion)
        {
            throw new WbsLoadException(
                string.Format(Strings.Storage_Error_VersionTooNew_Format, doc.Version, WbsDocument.CurrentVersion));
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
            throw new WbsLoadException(string.Format(Strings.Storage_Error_LoadFailed_Format, ex.Message), ex);
        }

        return DeserializeFromString(yaml);
    }
}

public sealed class WbsLoadException : Exception
{
    public WbsLoadException(string message) : base(message) { }
    public WbsLoadException(string message, Exception inner) : base(message, inner) { }
}
