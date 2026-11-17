# Solumanir Sheet Designer Studio

Desktop app for putting together species reference sheets (rows of model images, a details row and a lore column written in Markdown) and exporting them as very large PNGs. Written in C# with .NET 8 and Avalonia.

## Building

Needs the .NET 8 SDK, and fontconfig on Linux.

```sh
dotnet build -c Release
```

The finished app is copied to `bin/build/Release/` and a zip of it to `bin/package/`.

## Using it

Start `SolumanirSheetDesignerStudio`, optionally with a `.speciesproj` file to open.

- Drop images on a slot, or between slots to add new ones. Ctrl+V pastes one.
- Click any text on the sheet to edit it.
- Drag the bottom edge of a row, the corner of an image or the line in a split slot to resize them. Ctrl+Shift+wheel and `+` / `-` do the same in steps, finer with Alt.
- Shift+drag swaps two images, Ctrl+drag copies one.
- Ctrl+E exports, F5 previews.

Pages can also be exported without opening the window:

```sh
SolumanirSheetDesignerStudio --export sheet.speciesproj --out ./png
```

`--help` lists the options. A 30000 px page takes a few seconds.

To open project files by double-click, see `packaging/`.

## Credits

Avalonia, SkiaSharp, HarfBuzzSharp, Markdig and CommunityToolkit.Mvvm. The fonts are Cinzel, Alegreya, Alegreya Sans and Cascadia Mono, under the SIL Open Font License (see `assets/fonts`).
