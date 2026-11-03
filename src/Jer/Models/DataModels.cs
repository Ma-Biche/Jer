using System;
using System.Collections.Generic;

namespace Jer.Models;

public sealed class TodoItemModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public int Color { get; set; }
    public DateTime? DueAt { get; set; }
    public bool Reminded { get; set; }
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}

public sealed class TodoData
{
    public List<TodoItemModel> Items { get; set; } = new();
}

public enum ShelfItemKind
{
    File,
    Text,
}

public sealed class ShelfItemModel
{
    public ShelfItemKind Kind { get; set; } = ShelfItemKind.File;

    /// <summary>Full path of the file or folder. Jer remembers where files live; it never copies them.</summary>
    public string? Path { get; set; }

    public string? Text { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

public sealed class ShelfData
{
    public List<ShelfItemModel> Items { get; set; } = new();
}

public sealed class WaterEntry
{
    public DateTime Time { get; set; }
    public int Ml { get; set; }
}

public sealed class WaterData
{
    public List<WaterEntry> Entries { get; set; } = new();
}
