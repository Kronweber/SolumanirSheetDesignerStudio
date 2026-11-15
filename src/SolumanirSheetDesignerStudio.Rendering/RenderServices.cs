using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>The rendering objects shared by the whole application.</summary>
public sealed class RenderServices : IDisposable
{
    public RenderServices()
    {
        Fonts = new FontLibrary();
        Markdown = new MarkdigParser();
        Painter = new ScenePainter(Fonts);
        Probe = new SkiaImageProbe();
    }

    public FontLibrary Fonts { get; }
    public MarkdigParser Markdown { get; }
    public ScenePainter Painter { get; }
    public SkiaImageProbe Probe { get; }

    public SheetLayoutEngine CreateEngine(AssetLibrary assets) => new(Fonts, Markdown, assets.SizeOf);

    public void Dispose() => Fonts.Dispose();
}
