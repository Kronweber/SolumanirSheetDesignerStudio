using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SolumanirSheetDesignerStudio.App.Dialogs;
using SolumanirSheetDesignerStudio.App.ViewModels;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>An image found on the clipboard: either a file on disk or raw encoded bytes.</summary>
public sealed record ClipboardImage(byte[]? Data, string? FilePath, string Name);

/// <summary>Everything modal the view models need, so they never touch windows directly.</summary>
public interface IDialogService
{
    Task<string?> OpenProjectAsync(string? title = null);
    Task<string?> SaveProjectAsync(string suggestedName, string? startFolder);
    Task<IReadOnlyList<string>> OpenImagesAsync(bool multiple);
    Task<string?> PickFolderAsync(string title, string? start);
    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);
    /// <summary>Returns the index of the chosen button; the last button is the cancel choice (also returned as -1 when closed).</summary>
    Task<int> AskAsync(string title, string message, params string[] buttons);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);
    Task MessageAsync(string title, string message);
    Task<string?> PromptAsync(string title, string label, string initial);
    Task<ExportOptions?> ShowExportAsync(ExportDialogViewModel vm);
    Task<bool> ShowSettingsAsync(SettingsDialogViewModel vm);
    Task<bool> ShowImportPagesAsync(ImportPagesViewModel vm);
    Task<ClipboardImage?> ReadClipboardImageAsync();
}

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    private static FilePickerFileType ProjectType => new(Loc.T("Species project")) { Patterns = new[] { "*" + AppInfo.ProjectExtension } };

    private static FilePickerFileType ImageType => new(Loc.T("Images"))
    {
        Patterns = EditorViewModel.ImageExtensions.Select(e => "*" + e).ToArray(),
        MimeTypes = new[] { "image/png", "image/jpeg", "image/webp", "image/bmp", "image/gif" },
    };

    private async Task<IStorageFolder?> FolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        try { return await _owner.StorageProvider.TryGetFolderFromPathAsync(path); }
        catch (Exception) { return null; }
    }

    public async Task<string?> OpenProjectAsync(string? title = null)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title ?? Loc.T("Open project"),
            AllowMultiple = false,
            FileTypeFilter = new[] { ProjectType, FilePickerFileTypes.All },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveProjectAsync(string suggestedName, string? startFolder)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Save project"),
            SuggestedFileName = suggestedName,
            DefaultExtension = AppInfo.ProjectExtension.TrimStart('.'),
            FileTypeChoices = new[] { ProjectType },
            SuggestedStartLocation = await FolderAsync(startFolder),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> OpenImagesAsync(bool multiple)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = multiple ? Loc.T("Choose images") : Loc.T("Choose an image"),
            AllowMultiple = multiple,
            FileTypeFilter = new[] { ImageType, FilePickerFileTypes.All },
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }

    public async Task<string?> PickFolderAsync(string title, string? start)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderAsync(start),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    // ---------- small dialogs built in code ----------

    private Window NewDialog(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        MinWidth = 380,
        MaxWidth = 620,
    };

    public async Task<int> AskAsync(string title, string message, params string[] buttons)
    {
        var dialog = NewDialog(title);
        int result = -1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            if (i == 0) button.Classes.Add("primary");
            button.Click += (_, _) => { result = index; dialog.Close(); };
            row.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 18),
            Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 540 }, row },
        };
        await dialog.ShowDialog(_owner);
        return result;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        await AskAsync(title, message, confirmLabel, Loc.T("Cancel")) == 0;

    public async Task MessageAsync(string title, string message) => await AskAsync(title, message, Loc.T("OK"));

    public async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var dialog = NewDialog(title);
        string? result = null;
        var box = new TextBox { Text = initial, MinWidth = 340 };
        var ok = new Button { Content = Loc.T("OK"), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) => { result = box.Text ?? ""; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } },
            },
        };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(_owner);
        return result;
    }

    // ---------- XAML dialogs ----------

    public async Task<ExportOptions?> ShowExportAsync(ExportDialogViewModel vm)
    {
        var dialog = new ExportDialog { DataContext = vm };
        return await dialog.ShowDialog<bool>(_owner) ? vm.BuildOptions() : null;
    }

    public async Task<bool> ShowSettingsAsync(SettingsDialogViewModel vm) =>
        await new SettingsDialog { DataContext = vm }.ShowDialog<bool>(_owner);

    public async Task<bool> ShowImportPagesAsync(ImportPagesViewModel vm) =>
        await new ImportPagesDialog { DataContext = vm }.ShowDialog<bool>(_owner);

    // ---------- clipboard ----------

    public async Task<ClipboardImage?> ReadClipboardImageAsync()
    {
        var clipboard = _owner.Clipboard;
        if (clipboard == null) return null;
        try
        {
            // 1) Image files copied in a file manager.
            if (await clipboard.TryGetFilesAsync() is { } items)
                foreach (var item in items)
                    if (item.TryGetLocalPath() is { } path && EditorViewModel.IsImageFile(path) && File.Exists(path))
                        return new ClipboardImage(null, path, Path.GetFileName(path));

            // 2) Encoded image data under its MIME type (browsers and paint programs on Linux; the original bytes are kept).
            string stamp = "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (string mime in ImageFormats.MimeTypes)
            {
                byte[]? bytes = await clipboard.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(mime));
                if (bytes is { Length: > 0 }) return new ClipboardImage(bytes, null, stamp + ImageFormats.ExtensionFor(mime));
            }

            // 3) A bitmap in the platform's own clipboard format (Windows, macOS): Avalonia converts it.
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png);
                    if (png.Length > 0) return new ClipboardImage(png.ToArray(), null, stamp + ".png");
                }
            }

            // 4) A path or file:// address as text.
            string? text = (await clipboard.TryGetTextAsync())?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Length < 2000 && !text.Contains('\n'))
            {
                string candidate = text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.LocalPath : text.Trim('"');
                if (EditorViewModel.IsImageFile(candidate) && File.Exists(candidate)) return new ClipboardImage(null, candidate, Path.GetFileName(candidate));
            }
        }
        catch (Exception)
        {
            // Clipboard access fails in platform-specific ways (owner gone, format refused, timeout): all mean "no image".
        }
        return null;
    }
}
