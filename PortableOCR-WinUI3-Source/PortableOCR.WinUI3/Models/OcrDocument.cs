using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PortableOCR.WinUI3.Models;

public sealed class OcrDocument : INotifyPropertyChanged
{
    private string _status = "Ready";
    private string _message = "Ready";
    private double _progress;
    private string _quality = "inherit";
    private int _rotation;
    private string? _thumbnailPath;
    private BitmapImage? _thumbnailSource;
    private OcrResult? _result;
    private string? _error;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public long Size { get; init; }
    public required string SizeLabel { get; init; }
    public int Pages { get; init; } = 1;

    public string Status { get => _status; set { if (Set(ref _status, value)) RaiseDerived(); } }
    public string Message { get => _message; set { if (Set(ref _message, value)) RaiseDerived(); } }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Quality { get => _quality; set { if (Set(ref _quality, value)) RaiseDerived(); } }
    public int Rotation { get => _rotation; set => Set(ref _rotation, value); }
    public string? ThumbnailPath { get => _thumbnailPath; set => Set(ref _thumbnailPath, value); }
    public BitmapImage? ThumbnailSource { get => _thumbnailSource; set => Set(ref _thumbnailSource, value); }
    public OcrResult? Result { get => _result; set { if (Set(ref _result, value)) RaiseDerived(); } }
    public string? Error { get => _error; set { if (Set(ref _error, value)) RaiseDerived(); } }

    public string Meta => $"{SizeLabel}{(Pages > 1 ? $" · {Pages} pages" : string.Empty)}{(Result is not null ? $" · {FormatElapsed(Result.ElapsedMs)}" : string.Empty)}";
    public string Substatus => Status switch
    {
        "Processing" => Message,
        "Done" => "Complete · saved to Desktop",
        "Error" => Error ?? "OCR error",
        _ => "Ready"
    };
    public string StatusGlyph => Status switch { "Done" => "\uE73E", "Error" => "\uEA39", "Processing" => "\uE895", _ => "\uE73E" };
    public string FileGlyph => Extension.ToLowerInvariant() switch { ".pdf" => "\uEA90", ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".webp" or ".gif" => "\uEB9F", _ => "\uE8A5" };
    public string QualityLabel => Quality == "inherit" ? string.Empty : char.ToUpperInvariant(Quality[0]) + Quality[1..];
    public Visibility QualityVisibility => Quality == "inherit" ? Visibility.Collapsed : Visibility.Visible;

    private static string FormatElapsed(long ms) => ms < 1000 ? $"{ms} ms" : ms < 60000 ? $"{ms / 1000d:0.#} sec" : $"{ms / 60000} min {(ms % 60000) / 1000} sec";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    private void RaiseDerived()
    {
        foreach (var name in new[] { nameof(Meta), nameof(Substatus), nameof(StatusGlyph), nameof(QualityLabel), nameof(QualityVisibility) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record OcrWord(string Text, int Left, int Top, int Width, int Height);
public sealed record OcrOverlay(double Confidence, int Width, int Height, IReadOnlyList<OcrWord> Words, int WordCount, double LowConfidenceRatio, int CharacterCount);
public sealed record OcrResult(string? TextPath, string? PdfPath, int Pages, long ElapsedMs, string Quality, IReadOnlyList<OcrOverlay> Overlays);
public sealed record OcrProgress(string Stage, int Page, int Pages, double Percent, string Message);
public sealed record FileInspection(string Path, string Name, string Extension, long Size, string SizeLabel, int Pages);
public sealed record OcrSettings(string Quality, bool Text, bool Pdf);
