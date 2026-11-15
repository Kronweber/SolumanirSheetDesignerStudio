using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.App.ViewModels;

namespace SolumanirSheetDesignerStudio.App.Views;

public partial class LibraryPanel : UserControl
{
    private LibraryItemViewModel? _pressed;
    private Point _pressPoint;
    private bool _dragging;

    public LibraryPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Update(true);
        // Dragging an image out of the list places it on the sheet: onto a slot, or between slots as a new one.
        List.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        List.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = e.GetCurrentPoint(List).Properties.IsLeftButtonPressed
            ? (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LibraryItemViewModel
            : null;
        _pressPoint = e.GetPosition(List);
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } item || _dragging) return;
        var now = e.GetPosition(List);
        if (Math.Abs(now.X - _pressPoint.X) < 6 && Math.Abs(now.Y - _pressPoint.Y) < 6) return;
        _pressed = null;
        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(AssetDrag.Format, item.Id));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Update(false);
    }

    /// <summary>Thumbnails are decoded only while the panel is on screen.</summary>
    private void Update(bool visible)
    {
        if (DataContext is not LibraryViewModel vm) return;
        vm.IsVisible = visible && this.IsAttachedToVisualTree();
        if (vm.IsVisible) vm.LoadThumbnails();
    }
}
