namespace SolumanirSheetDesignerStudio.App.Services;

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes / 1024.0;
        if (v < 1024) return v.ToString("0.#") + " KB";
        v /= 1024;
        if (v < 1024) return v.ToString("0.#") + " MB";
        return (v / 1024).ToString("0.##") + " GB";
    }

    public static string Pixels(int w, int h) => $"{w:N0} × {h:N0} px";
}
