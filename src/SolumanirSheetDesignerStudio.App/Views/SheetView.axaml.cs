using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SolumanirSheetDesignerStudio.App.Controls;
using SolumanirSheetDesignerStudio.App.ViewModels;
using SolumanirSheetDesignerStudio.Core.Layout;
using TextAlign = SolumanirSheetDesignerStudio.Core.Layout.TextAlign;

namespace SolumanirSheetDesignerStudio.App.Views;

public partial class SheetView : UserControl
{
    private EditorViewModel? _editor;
    private TextFieldRef? _shown;

    public SheetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as EditorViewModel);

        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) ApplyFit();
        };
        // Ctrl+wheel zooms around the pointer; tunnel so the ScrollViewer does not scroll first.
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        foreach (var box in new[] { LineEditor, BodyEditor })
        {
            var editorBox = box;
            box.TextChanged += OnEditorTextChanged;
            box.LostFocus += (_, _) =>
            {
                // The right-click menu (Cut / Copy / Paste) takes the focus while it is open; that is not the end of the edit.
                if (editorBox.ContextFlyout?.IsOpen == true || editorBox.ContextMenu?.IsOpen == true) return;
                _editor?.EndEdit();
            };
            box.KeyDown += OnEditorKeyDown;
        }
        BodyEditor.PropertyChanged += (_, e) =>
        {
            // The Markdown editor grows with its content; the sheet makes room for it.
            if (e.Property == BoundsProperty && BodyEditor.IsVisible) _editor?.SetEditingLoreHeight(BodyEditor.Bounds.Height);
        };
    }

    private void Attach(EditorViewModel? editor)
    {
        if (ReferenceEquals(_editor, editor)) return;
        if (_editor != null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
            _editor.CurrentPageChanged -= OnPageChanged;
        }
        _editor = editor;
        HideEditors();
        if (_editor == null) return;
        _editor.PropertyChanged += OnEditorChanged;
        _editor.CurrentPageChanged += OnPageChanged;
        UpdateOverlay();
        ApplyFit();
    }

    private void OnPageChanged() => Scroller.Offset = new Vector(Scroller.Offset.X, 0);

    /// <summary>Gives the keyboard focus to the sheet (leaving whatever field had it).</summary>
    public void FocusCanvas() => Sheet.Focus();

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.FitToWidth):
                ApplyFit();
                break;
            case nameof(EditorViewModel.Zoom):
                UpdateOverlay();
                break;
            case nameof(EditorViewModel.Layout):
                UpdateOverlay();
                PlaceEditor(focus: false);
                break;
            case nameof(EditorViewModel.EditingField):
                PlaceEditor(focus: true);
                break;
        }
    }

    // ---------- zoom ----------

    private void ApplyFit()
    {
        if (_editor is not { FitToWidth: true } editor) return;
        double available = Scroller.Bounds.Width - 2 * SheetCanvas.DeskMargin - 18; // leave room for the scroll bar
        if (available <= 0) return;
        editor.SetZoom(Math.Clamp(available / SheetLayoutEngine.SheetWidth, EditorViewModel.MinZoom, 1.0), keepFit: true);
    }

    private void UpdateOverlay()
    {
        if (_editor is not { } editor) return;
        Overlay.Height = editor.Layout.Height;
        if (OverlayScale.LayoutTransform is ScaleTransform st && Math.Abs(st.ScaleX - editor.Zoom) < 1e-9) return;
        OverlayScale.LayoutTransform = new ScaleTransform(editor.Zoom, editor.Zoom);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_editor is not { } editor) return;
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!command || e.Delta.Y == 0) return;
        e.Handled = true;

        double oldZoom = editor.Zoom;
        double newZoom = Math.Clamp(oldZoom * Math.Pow(1.1, e.Delta.Y), EditorViewModel.MinZoom, EditorViewModel.MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 1e-6) return;

        // Keep the sheet point under the pointer where it is.
        var inView = e.GetPosition(Scroller);
        var inSheet = e.GetPosition(Sheet);
        double sx = (inSheet.X - SheetCanvas.DeskMargin) / oldZoom, sy = (inSheet.Y - SheetCanvas.DeskMargin) / oldZoom;
        editor.SetZoom(newZoom);
        Dispatcher.UIThread.Post(() =>
        {
            double x = sx * newZoom + SheetCanvas.DeskMargin - inView.X, y = sy * newZoom + SheetCanvas.DeskMargin - inView.Y;
            // When the sheet is narrower than the view it is centred and cannot scroll horizontally.
            Scroller.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
        }, DispatcherPriority.Loaded);
    }

    // ---------- in-place editors ----------

    private void HideEditors()
    {
        _shown = null;
        LineEditor.IsVisible = false;
        BodyEditor.IsVisible = false;
    }

    private FontFamily FamilyFor(FontFamilyId id)
    {
        string key = id switch { FontFamilyId.Display => "DisplayFont", FontFamilyId.Body => "BodyFont", FontFamilyId.Label => "UiFont", _ => "MonoFont" };
        return this.TryFindResource(key, out object? value) && value is FontFamily family ? family : FontFamily.Default;
    }

    /// <summary>Shows, moves or hides the editor for the field being edited.</summary>
    private void PlaceEditor(bool focus)
    {
        if (_editor is not { EditingField: { } field } editor || !editor.Layout.Fields.TryGetValue(field, out var info))
        {
            HideEditors();
            return;
        }

        bool body = field.Kind == TextFieldKind.LoreBody;
        var box = body ? BodyEditor : LineEditor;
        (body ? LineEditor : BodyEditor).IsVisible = false;

        Canvas.SetLeft(box, info.Box.X);
        Canvas.SetTop(box, info.Box.Y);
        box.Width = info.Box.W;
        if (body)
        {
            box.MinHeight = 180;
            box.Height = double.NaN;
        }
        else
        {
            box.Height = info.Box.H;
        }

        if (_shown != field)
        {
            // Same face, size and spacing as the rendered text, so nothing jumps when editing starts.
            box.FontFamily = FamilyFor(info.Font.Family);
            box.FontSize = info.Font.Size;
            box.FontWeight = (FontWeight)info.Font.Weight;
            box.FontStyle = info.Font.Italic ? FontStyle.Italic : FontStyle.Normal;
            box.LetterSpacing = body ? 0 : info.Font.LetterSpacing;
            box.TextAlignment = info.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
            box.Foreground = new SolidColorBrush(Color.FromArgb(info.Color.A, info.Color.R, info.Color.G, info.Color.B));
            box.Watermark = info.Uppercase ? TextCase.Upper(info.Placeholder) : info.Placeholder;
            if (body) box.LineHeight = info.Font.Size * 1.5;
        }

        string text = editor.GetFieldText(field);
        if ((box.Text ?? "") != text) box.Text = text;

        bool first = _shown != field;
        _shown = field;
        box.IsVisible = true;
        if (first && focus)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!box.IsVisible) return;
                box.Focus();
                if (body) box.CaretIndex = box.Text?.Length ?? 0;
                else box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box || _editor is not { EditingField: { } field } editor) return;
        if (_shown != field) return;
        // The event arrives after the fact, also for text this view put into the box itself: only real edits count.
        string text = box.Text ?? "";
        if (text != editor.GetFieldText(field)) editor.SetFieldText(field, text);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor == null) return;
        // Enter confirms a single-line field; Esc leaves text editing (the text typed so far is kept).
        if (e.Key == Key.Escape || (e.Key == Key.Enter && ReferenceEquals(sender, LineEditor)))
        {
            e.Handled = true;
            _editor.EndEdit();
            Sheet.Focus();
        }
    }
}
