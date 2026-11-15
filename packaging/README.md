# File association

Makes `.speciesproj` files open in the app by double-click.

## Linux

```sh
dotnet publish src/SolumanirSheetDesignerStudio.App -c Release -r linux-x64 --self-contained -o ~/.local/opt/solumanir-sheet-designer-studio
ln -s ~/.local/opt/solumanir-sheet-designer-studio/SolumanirSheetDesignerStudio ~/.local/bin/SolumanirSheetDesignerStudio
xdg-mime install --novendor packaging/linux/solumanir-sheet-designer-studio-mime.xml
xdg-icon-resource install --novendor --size 512 packaging/linux/solumanir-sheet-designer-studio.png solumanir-sheet-designer-studio
desktop-file-install --dir ~/.local/share/applications packaging/linux/solumanir-sheet-designer-studio.desktop
xdg-mime default solumanir-sheet-designer-studio.desktop application/x-speciesproj
```

## Windows

```bat
reg add HKCU\Software\Classes\.speciesproj /ve /d SolumanirSheetDesignerStudio.Project /f
reg add HKCU\Software\Classes\SolumanirSheetDesignerStudio.Project\shell\open\command /ve /d "\"C:\Path\To\SolumanirSheetDesignerStudio.exe\" \"%1\"" /f
```
