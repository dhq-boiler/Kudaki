using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kudaki.App.ViewModels;

namespace Kudaki.App.Services.Mcp;

// v0.3 のマルチドキュメント + MCP スキーマ変更 (sec-mcp-schema) 用ブリッジ。
//
// 責務:
//   - MainViewModel.Documents (現在開いてる DocumentViewModel 一覧) を MCP tool 側からアクセス可能にする
//   - documentId (= 絶対パス) → DocumentViewModel の解決
//   - list_documents 応答の生成
//
// 設計:
//   - シングルトン。Kudaki プロセスに MainViewModel は常に 1 個なので Bind() で紐付ける
//   - documentId は絶対パス (Windows なので大文字小文字無視)
//   - 未保存 doc (CurrentFilePath == null) は register 対象外 — ID がないので AI から触れない
//   - 現在の実装は Bind 時に MainViewModel を保持するだけ、都度 Documents をスキャンして応答
//     (Documents CollectionChanged / CurrentFilePath 変化を購読する必要がない)
public sealed class DocumentRegistry
{
    private static readonly Lazy<DocumentRegistry> _instance = new(() => new DocumentRegistry());
    public static DocumentRegistry Instance => _instance.Value;

    private MainViewModel? _owner;

    private DocumentRegistry() { }

    // MainViewModel の ctor から self-register。プロセス寿命中 1 回。
    public void Bind(MainViewModel owner) => _owner = owner;

    // 現在開いている doc の情報一覧をスナップショットで返す (未保存 doc は含めない)。
    // UI thread で呼ぶ前提 (Documents / ActiveDocument への読み出し安全性を確保)。
    public IReadOnlyList<DocumentInfo> ListDocuments()
    {
        var owner = _owner;
        if (owner is null) return Array.Empty<DocumentInfo>();

        var active = owner.ActiveDocument.Value;
        return owner.Documents
            .Where(d => d.CurrentFilePath is not null)
            .Select(d => new DocumentInfo(
                DocumentId: NormalizeId(d.CurrentFilePath!),
                FilePath: d.CurrentFilePath!,
                // ウィンドウタイトル ("Kudaki - <名前>") ではなくファイル名を返す。
                // 前者だと AI 側にも全 doc が Kudaki のファイルに見える。
                Title: d.DocumentName.Value,
                IsActive: ReferenceEquals(d, active),
                IsDirty: d.IsDirty.Value,
                Revision: d.GetRevision(),
                // 「AI 待機中」= この doc に対して未完了の wait_for_request が 1 本以上ある状態。
                // これ以外に接続状態の真実を知る手段は stateless transport には無い。
                AgentWaiting: d.AgentRequests.WaiterCount.Value > 0,
                PendingRequests: d.AgentRequests.Queue.Count))
            .ToList();
    }

    // documentId (絶対パス) から DocumentViewModel を解決。未 open なら null。
    public DocumentViewModel? Resolve(string documentId)
    {
        var owner = _owner;
        if (owner is null || string.IsNullOrWhiteSpace(documentId)) return null;

        string normalized;
        try
        {
            normalized = NormalizeId(documentId);
        }
        catch
        {
            return null;
        }

        return owner.Documents.FirstOrDefault(d =>
            d.CurrentFilePath is not null &&
            string.Equals(NormalizeId(d.CurrentFilePath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeId(string path) => Path.GetFullPath(path);
}

public sealed record DocumentInfo(
    string DocumentId,
    string FilePath,
    string Title,
    bool IsActive,
    bool IsDirty,
    string Revision,
    bool AgentWaiting,
    int PendingRequests);
