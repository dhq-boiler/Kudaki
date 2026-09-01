using System;
using System.Collections.Generic;

namespace Kudaki.App.Models;

public sealed class WbsDocument
{
    // フォーマット破壊的変更時にインクリメント。読込時のマイグレーション判断に使う。
    // v1 → v2 (2026-09-01): actualHours + progressPercent を廃止、
    //   毎日更新する remainingHours に一本化。実績/進捗は派生計算に。
    // v2 → v3 (2026-09-01): predecessorIds (level-local な兄弟間の先行タスク) を追加。
    //   古いバージョンで開くと依存情報が silent に落ちるので version bump で拒否させる。
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    public string? Title { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public List<TaskNode> Tasks { get; set; } = new();
}
