using System;
using System.Collections.Generic;

namespace Kudaki.App.Models;

public sealed class WbsDocument
{
    // フォーマット破壊的変更時にインクリメント。読込時のマイグレーション判断に使う。
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string? Title { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public List<TaskNode> Tasks { get; set; } = new();
}
