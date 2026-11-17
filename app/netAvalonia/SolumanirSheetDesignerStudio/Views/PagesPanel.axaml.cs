using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.App.ViewModels;

namespace SolumanirSheetDesignerStudio.App.Views;

public partial class PagesPanel : UserControl
{
    private EditorViewModel? _editor;
    private bool _syncing;
    private PageItemViewModel? _pressed;
    private PageItemViewModel? _collapseOnRelease; // pressed inside a multi-selection: it only becomes the sole selection if no drag follows
    private Point _pressPoint;
    private bool _dragging;
    private int _dropIndex = -1;
    private readonly DispatcherTimer _edgeScroll;
    private double _edgeScrollStep;

    public PagesPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as EditorViewModel);
        List.SelectionChanged += OnSelectionChanged;
        List.DoubleTapped += OnDoubleTapped;
        List.KeyDown += OnListKeyDown;
        _edgeScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => ScrollAtEdge());
        // The ListBox handles presses itself (selection); observe them as well for drag-reordering and the context menu.
        List.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        List.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        List.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Attach(EditorViewModel? editor)
    {
        if (ReferenceEquals(_editor, editor)) return;
        if (_editor != null) _editor.PropertyChanged -= OnEditorChanged;
        _editor = editor;
        if (_editor == null) return;
        _editor.PropertyChanged += OnEditorChanged;
        SyncSelection();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.CurrentPage)) SyncSelection();
    }

    /// <summary>
    /// Makes the list show what the editor holds: the remembered multi-selection if it still contains the page on
    /// the canvas, otherwise just that page (e.g. after undo jumped elsewhere). Runs after every change to the
    /// document, because reordering the list makes the ListBox drop moved items from its selection.
    /// </summary>
    private void SyncSelection()
    {
        if (_editor is not { } editor) return;
        string currentId = editor.Session.CurrentPageId;
        var wanted = editor.SelectedPageIds;
        wanted.IntersectWith(editor.Pages.Select(p => p.Id));
        if (!wanted.Contains(currentId))
        {
            wanted.Clear();
            wanted.Add(currentId);
        }

        _syncing = true;
        try
        {
            var selection = List.Selection;
            bool same = selection.Count == wanted.Count && selection.SelectedItems.OfType<PageItemViewModel>().All(i => wanted.Contains(i.Id));
            if (same) return;
            selection.BeginBatchUpdate();
            try
            {
                selection.Clear();
                for (int i = 0; i < editor.Pages.Count; i++)
                    if (wanted.Contains(editor.Pages[i].Id)) selection.Select(i);
            }
            finally { selection.EndBatchUpdate(); }
        }
        finally { _syncing = false; }
    }

    private void UpdateSelectedIds()
    {
        if (_editor is not { } editor) return;
        editor.SelectedPageIds.Clear();
        foreach (var item in List.SelectedItems?.OfType<PageItemViewModel>() ?? Enumerable.Empty<PageItemViewModel>())
            editor.SelectedPageIds.Add(item.Id);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only the user's own clicks and keys count; changes caused by rebuilding or reordering the list do not.
        if (_syncing || _editor is not { } editor || editor.IsSyncingPages) return;
        UpdateSelectedIds();
        // The page clicked last becomes the one shown on the canvas.
        if (e.AddedItems.OfType<PageItemViewModel>().LastOrDefault() is { } added) editor.Session.SelectPage(added.Id);
        else if (!editor.SelectedPageIds.Contains(editor.Session.CurrentPageId) && List.SelectedItems?.OfType<PageItemViewModel>().FirstOrDefault() is { } first)
            editor.Session.SelectPage(first.Id);
    }

    /// <summary>The pages the panel's own commands act on: the selection, or just the page on the canvas.</summary>
    private IReadOnlyCollection<string> SelectedOrCurrent(EditorViewModel editor) =>
        editor.SelectedPageIds.Count > 0 ? editor.SelectedPageIds.ToList() : new[] { editor.Session.CurrentPageId };

    /// <summary>With the keyboard focus in this list, Delete, F2 and Ctrl+D mean pages, not whatever is selected on the sheet.</summary>
    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is not { } editor || e.Handled) return;
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        try
        {
            if (e.KeyModifiers == KeyModifiers.None && (e.Key == Key.Delete || (e.Key == Key.Back && OperatingSystem.IsMacOS())))
            {
                e.Handled = true;
                await editor.DeletePagesAsync(SelectedOrCurrent(editor));
            }
            else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F2)
            {
                e.Handled = true;
                await editor.RenamePageAsync(editor.Session.CurrentPageId);
            }
            else if (e.KeyModifiers == command && e.Key == Key.D)
            {
                e.Handled = true;
                editor.DuplicatePages(SelectedOrCurrent(editor));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            editor.Main.Toasts.Error(ex.Message);
        }
    }

    private PageItemViewModel? ItemAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PageItemViewModel;

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_editor is { } editor && ItemAt(e.Source) is { } item)
        {
            try { await editor.RenamePageAsync(item.Id); }
            catch (Exception ex) { editor.Main.Toasts.Error(ex.Message); }
        }
    }

    // ---------- drag to reorder ----------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_editor is not { } editor) return;
        var point = e.GetCurrentPoint(List);
        var item = ItemAt(e.Source);

        if (e.Route == RoutingStrategies.Tunnel)
        {
            // A plain press on one page of a multi-selection would normally collapse the selection at once, which
            // makes dragging several pages impossible. Keep the selection until we know whether this is a drag.
            _collapseOnRelease = null;
            if (item != null && point.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None &&
                editor.SelectedPageIds.Count > 1 && editor.SelectedPageIds.Contains(item.Id))
            {
                _collapseOnRelease = item;
                _pressed = item;
                _pressPoint = point.Position;
                _dragging = false;
                List.Focus();
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (item != null) ShowContextMenu(item);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || _collapseOnRelease != null) return;
        _pressed = item;
        _pressPoint = point.Position;
        _dragging = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Route != RoutingStrategies.Bubble || _pressed == null || _editor == null) return;
        var point = e.GetCurrentPoint(List);
        if (!point.Properties.IsLeftButtonPressed) { EndDrag(); return; }
        if (!_dragging && Math.Abs(point.Position.Y - _pressPoint.Y) < 8) return;
        _dragging = true;
        UpdateDropTarget(point.Position.Y);

        // Near the top or bottom edge the list scrolls, so a page can be dragged to a place that is not on screen.
        const double edge = 28;
        _edgeScrollStep = point.Position.Y < edge ? -14 : point.Position.Y > List.Bounds.Height - edge ? 14 : 0;
        _edgeScroll.IsEnabled = _edgeScrollStep != 0;
        _lastDragY = point.Position.Y;
    }

    private double _lastDragY;

    private void ScrollAtEdge()
    {
        if (!_dragging || _edgeScrollStep == 0 || List.FindDescendantOfType<ScrollViewer>() is not { } scroll)
        {
            _edgeScroll.IsEnabled = false;
            return;
        }
        double y = Math.Clamp(scroll.Offset.Y + _edgeScrollStep, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        if (Math.Abs(y - scroll.Offset.Y) < 0.5) return;
        scroll.Offset = scroll.Offset.WithY(y);
        Dispatcher.UIThread.Post(() => { if (_dragging) UpdateDropTarget(_lastDragY); }, DispatcherPriority.Background);
    }

    /// <summary>Insertion index under the pointer: before the first item whose middle is below it.</summary>
    private void UpdateDropTarget(double pointerY)
    {
        if (_editor is not { } editor) return;
        // The list is virtualised: only the items on screen have containers. Below the last of those the page goes
        // right after it, not to the end of the project.
        int count = editor.Pages.Count, index = -1, lastRealised = -1;
        double lineY = 0;
        for (int i = 0; i < count; i++)
        {
            if (List.ContainerFromIndex(i) is not { } container) continue;
            lastRealised = i;
            var top = container.TranslatePoint(new Point(0, 0), List) ?? default;
            double mid = top.Y + container.Bounds.Height / 2;
            lineY = top.Y + container.Bounds.Height + 1;
            if (pointerY < mid) { index = i; lineY = top.Y - 4; break; }
        }
        if (index < 0) index = lastRealised >= 0 ? lastRealised + 1 : count;
        _dropIndex = index;
        DropLine.Margin = new Thickness(0, Math.Clamp(lineY, 0, Math.Max(0, List.Bounds.Height - 3)), 0, 0);
        DropLine.IsVisible = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Route != RoutingStrategies.Bubble) return;
        if (_dragging && _pressed != null && _dropIndex >= 0 && _editor is { } editor)
        {
            // Dragging a selected page moves the whole selection; otherwise just the dragged page.
            IReadOnlyCollection<string> ids = editor.SelectedPageIds.Contains(_pressed.Id) ? editor.SelectedPageIds.ToList() : new[] { _pressed.Id };
            editor.MovePages(ids, _dropIndex);
        }
        else if (_collapseOnRelease is { } single)
        {
            List.SelectedItem = single; // it was a click after all: the usual "select only this page"
        }
        EndDrag();
    }

    private void EndDrag()
    {
        _pressed = null;
        _collapseOnRelease = null;
        _dragging = false;
        _dropIndex = -1;
        _edgeScrollStep = 0;
        _edgeScroll.IsEnabled = false;
        DropLine.IsVisible = false;
    }

    // ---------- context menu ----------

    private void ShowContextMenu(PageItemViewModel item)
    {
        if (_editor is not { } editor) return;
        // Act on the whole selection when the clicked page is part of it.
        IReadOnlyCollection<string> ids = editor.SelectedPageIds.Contains(item.Id) && editor.SelectedPageIds.Count > 1
            ? editor.SelectedPageIds.ToList()
            : new[] { item.Id };
        int index = editor.Pages.IndexOf(item);

        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true) =>
            menu.Items.Add(new MenuItem { Header = Loc.T(header), IsEnabled = enabled, Command = new RelayCommand(action) });
        void AddAsync(string header, Func<Task> action) =>
            menu.Items.Add(new MenuItem { Header = Loc.T(header), Command = new AsyncRelayCommand(action) });

        AddAsync("Rename…", () => editor.RenamePageAsync(item.Id));
        Add(ids.Count > 1 ? "Duplicate pages" : "Duplicate", () => editor.DuplicatePages(ids));
        menu.Items.Add(new Separator());
        Add("Move up", () => editor.MovePage(item.Id, -1), index > 0);
        Add("Move down", () => editor.MovePage(item.Id, 1), index < editor.Pages.Count - 1);
        menu.Items.Add(new Separator());
        AddAsync("Save as template…", () => editor.SavePageAsTemplateAsync(item.Id));
        Add("New page", () => editor.AddPageCommand.Execute(null));
        menu.Items.Add(new Separator());
        AddAsync(ids.Count > 1 ? "Delete pages…" : "Delete…", () => editor.DeletePagesAsync(ids));
        menu.Open(List);
    }
}
