using Avalonia.Input;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>What travels when an image is dragged from the library panel onto the sheet: the asset's id.</summary>
public static class AssetDrag
{
    public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat("solumanir-sheet-asset-id");
}
