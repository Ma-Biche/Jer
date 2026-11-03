using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Jer.Core;
using Jer.Interop;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

public sealed class ShelfItemViewModel : ObservableObject
{
    public ShelfItemViewModel(ShelfItemModel model)
    {
        Model = model;
        if (model.Kind == ShelfItemKind.Text)
        {
            var text = (model.Text ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            Name = text.Length > 60 ? text[..60] + "…" : text;
            Detail = "Click to copy";
            Icon = Ui.Icon("Text");
        }
        else
        {
            var path = model.Path ?? "";
            var isFolder = Directory.Exists(path);
            Name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(Name))
            {
                Name = path;
            }

            Detail = isFolder ? "Folder" : FormatSize(path);
            Icon = Ui.Icon(isFolder ? "Folder" : "File");
        }
    }

    public ShelfItemModel Model { get; }
    public bool IsText => Model.Kind == ShelfItemKind.Text;
    public string Name { get; }
    public string Detail { get; }
    public Geometry Icon { get; }
    public ImageSource? Thumbnail { get; set => SetProperty(ref field, value); }
    public bool IsSelected { get; set => SetProperty(ref field, value); }

    public bool Exists => IsText || File.Exists(Model.Path) || Directory.Exists(Model.Path);

    private static string FormatSize(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }
        catch
        {
            return "File";
        }
    }
}

/// <summary>Files and text kept close at hand. Jer remembers where files live; it never copies them.</summary>
public sealed class ShelfViewModel : ObservableObject
{
    private const string FileName = "shelf.json";
    private const int MaxItems = 200;
    private readonly ShelfData _data;
    private readonly NotchEvents _events;
    private readonly bool _demo;
    private int _anchor = -1;

    public ShelfViewModel(NotchEvents events, bool demo)
    {
        _events = events;
        _demo = demo;
        _data = demo ? new ShelfData() : JsonStore.Load<ShelfData>(FileName);
        _data.Items.RemoveAll(i => i.Kind == ShelfItemKind.File && !File.Exists(i.Path) && !Directory.Exists(i.Path));
        foreach (var model in _data.Items)
        {
            Add(new ShelfItemViewModel(model), persist: false);
        }

        RemoveCommand = new RelayCommand(p => Remove(p as ShelfItemViewModel));
        ClearCommand = new RelayCommand(Clear, () => Items.Count > 0);
        UpdateSummary();
    }

    public ObservableCollection<ShelfItemViewModel> Items { get; } = new();
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public string CountText { get; private set => SetProperty(ref field, value); } = "";
    public bool IsEmpty { get; private set => SetProperty(ref field, value); } = true;
    public bool HasSelection { get; private set => SetProperty(ref field, value); }

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || Items.Any(i => !i.IsText && string.Equals(i.Model.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            var model = new ShelfItemModel { Kind = ShelfItemKind.File, Path = path };
            _data.Items.Add(model);
            Add(new ShelfItemViewModel(model), persist: false);
        }

        Trim();
        Save();
        UpdateSummary();
    }

    public void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var model = new ShelfItemModel { Kind = ShelfItemKind.Text, Text = text };
        _data.Items.Add(model);
        Add(new ShelfItemViewModel(model), persist: false);
        Trim();
        Save();
        UpdateSummary();
    }

    /// <summary>Opens a file with its default app, or copies a text card.</summary>
    public void Open(ShelfItemViewModel item)
    {
        try
        {
            if (item.IsText)
            {
                Clipboard.SetText(item.Model.Text ?? "");
                _events.ShowHud(new HudInfo("copy", Ui.Icon("Check"), "Copied", null, null, Ui.Green));
                return;
            }

            if (!item.Exists)
            {
                _events.ShowHud(new HudInfo("missing", Ui.Icon("File"), "File not found", item.Name, null, Ui.Orange));
                Remove(item);
                return;
            }

            Process.Start(new ProcessStartInfo(item.Model.Path!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {item.Name}", ex);
        }
    }

    /// <summary>Click / Ctrl+click / Shift+click selection.</summary>
    public void Select(ShelfItemViewModel item, bool toggle, bool range)
    {
        var index = Items.IndexOf(item);
        if (range && _anchor >= 0)
        {
            var (from, to) = _anchor < index ? (_anchor, index) : (index, _anchor);
            for (var i = 0; i < Items.Count; i++)
            {
                Items[i].IsSelected = i >= from && i <= to;
            }
        }
        else if (toggle)
        {
            item.IsSelected = !item.IsSelected;
            _anchor = index;
        }
        else
        {
            foreach (var other in Items)
            {
                other.IsSelected = ReferenceEquals(other, item);
            }

            _anchor = index;
        }

        HasSelection = Items.Any(i => i.IsSelected);
    }

    public void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        _anchor = -1;
        HasSelection = false;
    }

    /// <summary>The items a drag should carry: the selection if the dragged item is part of it, otherwise just that item.</summary>
    public IReadOnlyList<ShelfItemViewModel> DragSet(ShelfItemViewModel item) =>
        item.IsSelected ? Items.Where(i => i.IsSelected).ToList() : new[] { item };

    public void Remove(ShelfItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var targets = item.IsSelected ? Items.Where(i => i.IsSelected).ToList() : new List<ShelfItemViewModel> { item };
        foreach (var target in targets)
        {
            _data.Items.Remove(target.Model);
            Items.Remove(target);
        }

        HasSelection = Items.Any(i => i.IsSelected);
        Save();
        UpdateSummary();
    }

    private void Clear()
    {
        _data.Items.Clear();
        Items.Clear();
        HasSelection = false;
        Save();
        UpdateSummary();
    }

    private void Add(ShelfItemViewModel item, bool persist)
    {
        Items.Add(item);
        if (!item.IsText && item.Model.Path is { } path)
        {
            var dispatcher = Application.Current.Dispatcher;
            ShellThumbnails.Request(path, 96, image =>
            {
                if (image is not null)
                {
                    dispatcher.BeginInvoke(() => item.Thumbnail = image);
                }
            });
        }

        if (persist)
        {
            Save();
        }
    }

    private void Trim()
    {
        while (Items.Count > MaxItems)
        {
            _data.Items.Remove(Items[0].Model);
            Items.RemoveAt(0);
        }
    }

    private void Save()
    {
        if (!_demo)
        {
            JsonStore.Save(FileName, _data);
        }
    }

    private void UpdateSummary()
    {
        IsEmpty = Items.Count == 0;
        CountText = Items.Count switch
        {
            0 => "",
            1 => "1 item",
            var n => $"{n} items",
        };
        ClearCommand.NotifyCanExecuteChanged();
    }
}
