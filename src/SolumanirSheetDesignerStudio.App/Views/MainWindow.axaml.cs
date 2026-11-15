using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.App.ViewModels;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Settings;

namespace SolumanirSheetDesignerStudio.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = null!;
    private bool _closeConfirmed;
    private double _leftWidth = 230, _rightWidth = 320;

    /// <summary>Design-time constructor.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm, string? fileArgument)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Dialogs = new DialogService(this);
        vm.GpuRenderer = GpuHost;

        RestoreWindow(vm.Settings.Window);
        ApplyUiScale();
        ApplyPanels();
        if (OperatingSystem.IsMacOS()) UseCommandKey();

        vm.PropertyChanged += OnViewModelChanged;
        vm.SettingsApplied += ApplyUiScale;
        // Posted: an exit requested while the window is still opening (a second instance that only had to wake the
        // first) must not close it from inside its own Opened event — the desktop lifetime is not running yet.
        vm.ExitRequested += () => Dispatcher.UIThread.Post(Close, DispatcherPriority.Background);
        vm.ActivateRequested += () => Dispatcher.UIThread.Post(() =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });

        FileMenu.SubmenuOpened += (_, _) => BuildRecentMenu();
        PageMenu.SubmenuOpened += (_, _) => BuildTemplateMenu();
        BuildRecentMenu();
        BuildTemplateMenu();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKeyDownBubbled, RoutingStrategies.Bubble);

        Opened += async (_, _) =>
        {
            try { await vm.StartupAsync(fileArgument); }
            catch (Exception ex) { vm.Toasts.Error(ex.Message); }
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            vm.Shutdown();
            // Safety net: make sure the process ends even if a dialog was still closing when this window went away.
            Dispatcher.UIThread.Post(() =>
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        };
    }

    // ---------- window and panel state ----------

    private void RestoreWindow(WindowSettings w)
    {
        Width = Math.Max(MinWidth, w.Width);
        Height = Math.Max(MinHeight, w.Height);
        // Restore the position only if it is still on a connected screen.
        if (w.X is { } x && w.Y is { } y && Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(x + 60, y + 40))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
        _leftWidth = Math.Clamp(w.LeftPanelWidth, 170, 520);
        _rightWidth = Math.Clamp(w.RightPanelWidth, 260, 600);
    }

    private void SaveWindow(WindowSettings w)
    {
        w.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            w.Width = Width;
            w.Height = Height;
            w.X = Position.X;
            w.Y = Position.Y;
        }
        var cols = EditorGrid.ColumnDefinitions;
        if (_vm.LeftPanelOpen && cols[0].ActualWidth > 0) _leftWidth = cols[0].ActualWidth;
        if (_vm.RightPanelOpen && cols[4].ActualWidth > 0) _rightWidth = cols[4].ActualWidth;
        w.LeftPanelWidth = _leftWidth;
        w.RightPanelWidth = _rightWidth;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LeftPanelOpen) or nameof(MainViewModel.RightPanelOpen)) ApplyPanels();
    }

    /// <summary>Collapses or restores the side panels (their widths are remembered).</summary>
    private void ApplyPanels()
    {
        var cols = EditorGrid.ColumnDefinitions;
        if (!_vm.LeftPanelOpen && cols[0].ActualWidth > 0) _leftWidth = cols[0].ActualWidth;
        if (!_vm.RightPanelOpen && cols[4].ActualWidth > 0) _rightWidth = cols[4].ActualWidth;

        cols[0].MinWidth = _vm.LeftPanelOpen ? 170 : 0;
        cols[0].Width = new GridLength(_vm.LeftPanelOpen ? _leftWidth : 0);
        cols[1].Width = new GridLength(_vm.LeftPanelOpen ? 4 : 0);
        LeftPanel.IsVisible = LeftSplitter.IsVisible = _vm.LeftPanelOpen;

        cols[4].MinWidth = _vm.RightPanelOpen ? 260 : 0;
        cols[4].Width = new GridLength(_vm.RightPanelOpen ? _rightWidth : 0);
        cols[3].Width = new GridLength(_vm.RightPanelOpen ? 4 : 0);
        RightPanel.IsVisible = RightSplitter.IsVisible = _vm.RightPanelOpen;
    }

    private void ApplyUiScale()
    {
        double s = Math.Clamp(_vm.Settings.UiScale, 0.75, 2.5);
        UiScale.LayoutTransform = Math.Abs(s - 1) < 0.001 ? null : new ScaleTransform(s, s);
    }

    /// <summary>macOS: every Ctrl shortcut becomes ⌘.</summary>
    private void UseCommandKey()
    {
        static KeyGesture Swap(KeyGesture g) => g.KeyModifiers.HasFlag(KeyModifiers.Control)
            ? new KeyGesture(g.Key, (g.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta)
            : g;

        foreach (var binding in KeyBindings)
            if (binding.Gesture != null) binding.Gesture = Swap(binding.Gesture);

        void Walk(ItemsControl parent)
        {
            foreach (object? item in parent.Items)
            {
                if (item is not MenuItem menuItem) continue;
                if (menuItem.InputGesture != null) menuItem.InputGesture = Swap(menuItem.InputGesture);
                Walk(menuItem);
            }
        }
        Walk(MainMenu);
    }

    // ---------- closing ----------

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _vm.Editor == null)
        {
            SaveWindow(_vm.Settings.Window); // while the window still exists: its position is gone once it has closed
            return;
        }
        e.Cancel = true; // ask first (Save / Don't Save / Cancel), then close for real
        try
        {
            if (!await _vm.TryCloseProjectAsync()) return;
        }
        catch (Exception ex)
        {
            _vm.Toasts.Error(ex.Message);
            return;
        }
        _closeConfirmed = true;
        // Posted, not called directly: the "Save changes?" dialog is still closing at this point, and closing its owner
        // underneath it leaves the app running with no windows.
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>
    /// Shortcuts that act on the sheet selection. They are handled here, on the way back up from the focused control,
    /// so a text box always gets first refusal — and while the caret is in a text box, or the focus is in the pages
    /// or image list, they do not reach the sheet at all (Delete must never remove a row while someone is typing).
    /// </summary>
    private void OnKeyDownBubbled(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm.Editor is not { } editor || editor.IsBusy) return;
        var focused = FocusManager?.GetFocusedElement() as Visual;
        bool inText = focused is TextBox;

        if (e.Key == Key.Escape)
        {
            // In a panel field Esc gives the keyboard back to the sheet; it does not also change the selection.
            if (inText && editor.EditingField == null) SheetArea.FocusCanvas();
            else editor.Escape();
            e.Handled = true;
            return;
        }
        if (inText || focused?.FindAncestorOfType<PagesPanel>(includeSelf: true) != null || focused?.FindAncestorOfType<LibraryPanel>(includeSelf: true) != null) return;

        var mods = e.KeyModifiers;
        var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        // "+" and "−" resize the selection in steps (Alt: fine steps). Shift is allowed: "+" needs it on many layouts.
        int grow = e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : 0;
        if (grow != 0 && (mods & ~(KeyModifiers.Shift | KeyModifiers.Alt)) == 0)
        {
            if (editor.NudgeSelectionSize(grow, fine: mods.HasFlag(KeyModifiers.Alt))) e.Handled = true;
            return;
        }

        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Z when mods == cmd => editor.UndoCommand,
            Key.Y when mods == cmd => editor.RedoCommand,
            Key.Z when mods == (cmd | KeyModifiers.Shift) => editor.RedoCommand,
            Key.D when mods == cmd => editor.DuplicateSelectionCommand,
            Key.V when mods == cmd => editor.PasteImageCommand,
            Key.Delete when mods == KeyModifiers.None => editor.DeleteSelectionCommand,
            Key.Back when mods == KeyModifiers.None && OperatingSystem.IsMacOS() => editor.DeleteSelectionCommand,
            Key.Up or Key.Left when mods == KeyModifiers.Alt => editor.MoveSelectionBackCommand,
            Key.Down or Key.Right when mods == KeyModifiers.Alt => editor.MoveSelectionForwardCommand,
            Key.F2 when mods == KeyModifiers.None => editor.RenameSelectionCommand,
            _ => null,
        };
        if (command == null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    // ---------- project files dropped on the window ----------

    private static string? ProjectFileIn(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && ProjectFile.LooksLikeProject(p));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled) return; // the sheet accepted image files
        e.DragEffects = ProjectFileIn(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || ProjectFileIn(e) is not { } path) return;
        e.Handled = true;
        try { await _vm.OpenPathAsync(path); }
        catch (Exception ex) { _vm.Toasts.Error(ex.Message); }
    }

    // ---------- dynamic menus ----------

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var recent in _vm.Recents.Ordered.Take(12))
        {
            string path = recent.Path;
            string title = string.IsNullOrWhiteSpace(recent.Title) ? Path.GetFileNameWithoutExtension(path) : recent.Title;
            RecentMenu.Items.Add(new MenuItem
            {
                // Underscores would be read as access keys.
                Header = (recent.Pinned ? "★ " : "") + title.Replace("_", "__") + "   —   " + path.Replace("_", "__"),
                Command = new AsyncRelayCommand(() => _vm.OpenPathAsync(path)),
            });
        }
        if (RecentMenu.Items.Count == 0)
            RecentMenu.Items.Add(new MenuItem { Header = Loc.T("No recent projects"), IsEnabled = false });
    }

    private void BuildTemplateMenu()
    {
        TemplateMenu.Items.Clear();
        foreach (string name in EditorViewModel.ListTemplates())
        {
            string template = name;
            TemplateMenu.Items.Add(new MenuItem
            {
                Header = template.Replace("_", "__"),
                Command = new RelayCommand(() => _vm.Editor?.AddPageFromTemplate(template)),
            });
        }
        if (TemplateMenu.Items.Count == 0)
            TemplateMenu.Items.Add(new MenuItem { Header = Loc.T("No templates yet — use \"Save Page as Template…\""), IsEnabled = false });
    }

    // ---------- Page menu ----------

    private IReadOnlyCollection<string> TargetPages()
    {
        if (_vm.Editor is not { } e) return Array.Empty<string>();
        return e.SelectedPageIds.Count > 1 && e.SelectedPageIds.Contains(e.Session.CurrentPageId)
            ? e.SelectedPageIds.ToList()
            : new[] { e.Session.CurrentPageId };
    }

    private void OnDuplicatePage(object? sender, RoutedEventArgs e) => _vm.Editor?.DuplicatePages(TargetPages());

    private async void OnDeletePage(object? sender, RoutedEventArgs e)
    {
        if (_vm.Editor is { } editor) await Guard(editor.DeletePagesAsync(TargetPages()));
    }

    private async void OnRenamePage(object? sender, RoutedEventArgs e)
    {
        if (_vm.Editor is { } editor) await Guard(editor.RenamePageAsync(editor.Session.CurrentPageId));
    }

    private void OnMovePageUp(object? sender, RoutedEventArgs e) => _vm.Editor?.MovePage(_vm.Editor.Session.CurrentPageId, -1);

    private void OnMovePageDown(object? sender, RoutedEventArgs e) => _vm.Editor?.MovePage(_vm.Editor.Session.CurrentPageId, 1);

    private async void OnSavePageTemplate(object? sender, RoutedEventArgs e)
    {
        if (_vm.Editor is { } editor) await Guard(editor.SavePageAsTemplateAsync(editor.Session.CurrentPageId));
    }

    private async Task Guard(Task task)
    {
        try { await task; }
        catch (Exception ex) { _vm.Toasts.Error(ex.Message); }
    }
}
