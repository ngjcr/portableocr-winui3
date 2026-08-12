using PortableOCR.WinUI3.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PortableOCR.WinUI3.Services;

public sealed class OcrEngine
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".gif" };
    private static readonly Dictionary<string, Profile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fast"] = new(220, [3], false),
        ["balanced"] = new(300, [3], true),
        ["best"] = new(400, [3, 6, 11], true)
    };
    private const int MergeBatch = 64;

    private readonly ProcessRunner _runner = new();
    private readonly string _root;
    private readonly string _tesseractDir;
    private readonly string _tesseract;
    private readonly string _poppler;
    private readonly string _pdfInfo;
    private readonly string _pdfToPpm;
    private readonly string _pdfUnite;
    private readonly string _pdfSeparate;
    private readonly string _pdfToText;
    private readonly string _preprocess;
    private readonly string _cache;
    private readonly Dictionary<string, string> _models;
    private readonly HashSet<string> _reservedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _outputGate = new();
    private int _ompThreads = Math.Clamp(Environment.ProcessorCount, 1, 4);

    public OcrEngine()
    {
        _root = System.IO.Path.Combine(AppContext.BaseDirectory, "runtime");
        _tesseractDir = System.IO.Path.Combine(_root, "engines", "tesseract");
        _tesseract = System.IO.Path.Combine(_tesseractDir, "tesseract.exe");
        _poppler = System.IO.Path.Combine(_root, "engines", "poppler", "bin");
        _pdfInfo = System.IO.Path.Combine(_poppler, "pdfinfo.exe");
        _pdfToPpm = System.IO.Path.Combine(_poppler, "pdftoppm.exe");
        _pdfUnite = System.IO.Path.Combine(_poppler, "pdfunite.exe");
        _pdfSeparate = System.IO.Path.Combine(_poppler, "pdfseparate.exe");
        _pdfToText = System.IO.Path.Combine(_poppler, "pdftotext.exe");
        _preprocess = System.IO.Path.Combine(_root, "engines", "preprocess", "preprocess.exe");
        _cache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortableOCR", "preview-cache");
        _models = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = System.IO.Path.Combine(_tesseractDir, "models", "fast"),
            ["balanced"] = System.IO.Path.Combine(_tesseractDir, "models", "balanced"),
            ["best"] = System.IO.Path.Combine(_tesseractDir, "models", "best")
        };
    }

    public static bool IsSupported(string path) => Supported.Contains(System.IO.Path.GetExtension(path));

    public void BeginBatch(int workers)
    {
        lock (_outputGate) _reservedOutputs.Clear();
        _ompThreads = Math.Clamp(Math.Max(1, Environment.ProcessorCount / Math.Max(1, workers)), 1, 4);
    }

    public void VerifyRuntime()
    {
        foreach (var file in new[] { _tesseract, _pdfInfo, _pdfToPpm, _pdfUnite, _pdfSeparate, _pdfToText, _preprocess })
            if (!File.Exists(file)) throw new FileNotFoundException($"Missing runtime file: {System.IO.Path.GetRelativePath(_root, file)}", file);
        foreach (var quality in Profiles.Keys)
        {
            var model = System.IO.Path.Combine(_models[quality], "eng.traineddata");
            if (!File.Exists(model)) throw new FileNotFoundException($"Missing OCR model for {quality}.", model);
        }
        Directory.CreateDirectory(_cache);
    }

    public async Task<FileInspection> InspectAsync(string file, CancellationToken ct = default)
    {
        if (!IsSupported(file)) throw new InvalidOperationException("Unsupported file type.");
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("File not found.", file);
        var ext = info.Extension.ToLowerInvariant();
        var pages = ext == ".pdf" ? await GetPdfPagesAsync(file, ct) : 1;
        return new(file, info.Name, ext, info.Length, FormatBytes(info.Length), pages);
    }

    public async Task<int> GetPdfPagesAsync(string file, CancellationToken ct)
    {
        var r = await _runner.RunAsync(_pdfInfo, [file], ct);
        var match = Regex.Match(r.StdOut, @"^Pages:\s+(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Could not determine PDF page count.");
        return Math.Max(1, int.Parse(match.Groups[1].Value));
    }

    public async Task<string> GetPreviewAsync(string file, int page, bool processed, string quality, int rotation, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cache);
        var sig = Signature(file);
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        var source = file;
        if (ext == ".pdf")
        {
            var key = Hash($"{sig}|{page}|preview-v3");
            var basePath = System.IO.Path.Combine(_cache, $"{key}-p{page}");
            var png = basePath + ".png";
            if (!File.Exists(png))
                await _runner.RunAsync(_pdfToPpm, ["-f", page.ToString(), "-l", page.ToString(), "-singlefile", "-r", "135", "-png", file, basePath], ct);
            source = png;
        }
        else if (ext is ".webp" or ".tif" or ".tiff")
        {
            var key = Hash($"{sig}|native-preview-v1");
            var converted = System.IO.Path.Combine(_cache, key + ".png");
            if (!File.Exists(converted))
                await _runner.RunAsync(_preprocess, ["--mode", "balanced", "--rotate", "0", file, converted], ct);
            source = converted;
        }

        if (processed && !quality.Equals("fast", StringComparison.OrdinalIgnoreCase))
        {
            var key = Hash($"{sig}|{source}|{quality}|{rotation}|processed-v3");
            var output = System.IO.Path.Combine(_cache, key + ".png");
            if (!File.Exists(output))
                await _runner.RunAsync(_preprocess, ["--mode", quality, "--rotate", rotation.ToString(), source, output], ct);
            source = output;
        }
        return source;
    }

    public void Cancel() => _runner.CancelAll();

    public async Task<OcrResult> ProcessFileAsync(OcrDocument item, OcrSettings settings, string desktop, IProgress<OcrProgress>? progress, CancellationToken ct)
    {
        VerifyRuntime();
        var sw = Stopwatch.StartNew();
        var file = item.Path;
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        var baseName = SafeBase(file);
        var quality = item.Quality is "fast" or "balanced" or "best" ? item.Quality : settings.Quality;
        var profile = Profiles.TryGetValue(quality, out var p) ? p : Profiles["balanced"];
        var rotation = ((item.Rotation % 360) + 360) % 360;
        if (!settings.Text && !settings.Pdf) throw new InvalidOperationException("Choose at least one output type.");
        var workDir = Directory.CreateTempSubdirectory("PortableOCR-").FullName;
        var pages = Math.Max(1, item.Pages);
        var texts = new List<string>(pages);
        var pagePdfs = new List<string>(pages);
        var overlays = new List<OcrOverlay>(pages);
        var allEmbedded = ext == ".pdf" && rotation == 0;

        try
        {
            for (var page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var source = file;
                if (ext == ".pdf" && rotation == 0)
                {
                    progress?.Report(new("inspect", page, pages, Math.Round((page - 1d) / pages * 100), $"Checking page {page} of {pages}"));
                    var embedded = await TryEmbeddedPdfPageAsync(file, page, workDir, settings.Pdf, ct);
                    if (embedded is not null)
                    {
                        texts.Add(embedded.Value.Text.TrimEnd());
                        if (embedded.Value.PdfPath is not null) pagePdfs.Add(embedded.Value.PdfPath);
                        overlays.Add(embedded.Value.Overlay);
                        progress?.Report(new("native", page, pages, Math.Round(page / (double)pages * 100), $"Preserved searchable page {page} of {pages}"));
                        continue;
                    }
                }

                if (ext == ".pdf")
                {
                    allEmbedded = false;
                    var renderBase = System.IO.Path.Combine(workDir, $"page-{page:0000}");
                    progress?.Report(new("render", page, pages, Math.Round((page - 1d) / pages * 100), $"Rendering page {page} of {pages}"));
                    await _runner.RunAsync(_pdfToPpm, ["-f", page.ToString(), "-l", page.ToString(), "-singlefile", "-r", profile.Dpi.ToString(), "-png", file, renderBase], ct);
                    source = renderBase + ".png";
                }

                progress?.Report(new("ocr", page, pages, Math.Round((page - .68d) / pages * 100), $"Reading page {page} of {pages}"));
                var pageResult = await OcrPageAsync(source, workDir, quality, rotation, page, settings.Pdf, ct);
                texts.Add(pageResult.Text.TrimEnd());
                if (pageResult.PdfPath is not null) pagePdfs.Add(pageResult.PdfPath);
                overlays.Add(pageResult.Overlay);
                progress?.Report(new("ocr", page, pages, Math.Round(page / (double)pages * 100), $"Finished page {page} of {pages}"));
            }

            string? textOut = null, pdfOut = null;
            if (settings.Text)
            {
                textOut = UniqueOutput(desktop, baseName, "_OCR", ".txt");
                await File.WriteAllTextAsync(textOut, string.Join("\r\n\r\n\f\r\n\r\n", texts), new UTF8Encoding(false), ct);
            }
            if (settings.Pdf)
            {
                pdfOut = UniqueOutput(desktop, baseName, "_Searchable", ".pdf");
                if (allEmbedded && ext == ".pdf" && rotation == 0) File.Copy(file, pdfOut);
                else await MergePdfsAsync(pagePdfs, pdfOut, workDir, ct);
                if (new FileInfo(pdfOut).Length < 500) throw new InvalidOperationException("Searchable PDF validation failed.");
                var samplePages = Math.Min(pages, 3);
                var recognized = string.Join(" ", texts.Take(samplePages)).Trim();
                if (!string.IsNullOrWhiteSpace(recognized))
                {
                    var probe = await _runner.RunAsync(_pdfToText, ["-f", "1", "-l", samplePages.ToString(), pdfOut, "-"], ct);
                    if (string.IsNullOrWhiteSpace(probe.StdOut)) throw new InvalidOperationException("Searchable PDF validation failed: no searchable text layer was found.");
                }
            }
            sw.Stop();
            return new(textOut, pdfOut, pages, sw.ElapsedMilliseconds, quality, overlays);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private async Task<PageResult> OcrPageAsync(string source, string workDir, string quality, int rotation, int pageIndex, bool makePdf, CancellationToken ct)
    {
        var profile = Profiles[quality];
        var modelDir = _models[quality];

        if (quality == "balanced" && makePdf && rotation == 0)
        {
            var originalBase = System.IO.Path.Combine(workDir, $"ocr-{pageIndex:0000}-original");
            var original = await TessAttemptAsync(source, originalBase, modelDir, 3, true, ct);
            if (IsStrongBalanced(original)) return new(original.Text, original.Overlay, original.PdfPath);
            var prepared = await PrepareImageAsync(source, workDir, quality, rotation, pageIndex, ct);
            if (PathsEqual(prepared, source)) return new(original.Text, original.Overlay, original.PdfPath);
            var preparedBase = System.IO.Path.Combine(workDir, $"ocr-{pageIndex:0000}-prepared");
            var improved = await TessAttemptAsync(prepared, preparedBase, modelDir, 3, false, ct);
            var best = improved.Score > original.Score ? improved : original;
            return new(best.Text, best.Overlay, original.PdfPath);
        }

        var preparedImage = await PrepareImageAsync(source, workDir, quality, rotation, pageIndex, ct);
        var canReusePdf = makePdf && (PathsEqual(preparedImage, source) || rotation != 0);
        Attempt? bestAttempt = null;
        var attemptNo = 0;
        foreach (var psm in profile.Psms)
        {
            attemptNo++;
            var workBase = System.IO.Path.Combine(workDir, $"ocr-{pageIndex:0000}-{attemptNo}");
            var attempt = await TessAttemptAsync(preparedImage, workBase, modelDir, psm, canReusePdf, ct);
            if (bestAttempt is null || attempt.Score > bestAttempt.Score) bestAttempt = attempt;
            if (quality == "best" && attemptNo == 1 && IsStrongBest(attempt)) break;
            if (quality == "best" && attemptNo == 2 && bestAttempt.Overlay.Confidence >= 94 && bestAttempt.Overlay.LowConfidenceRatio <= .04 && bestAttempt.Overlay.WordCount >= 12) break;
        }
        var best = bestAttempt ?? throw new InvalidOperationException("OCR did not produce a result.");
        var pdfPath = best.PdfPath;
        if (makePdf && pdfPath is null)
        {
            var workBase = System.IO.Path.Combine(workDir, $"search-{pageIndex:0000}");
            var pdfSource = rotation != 0 ? preparedImage : source;
            await RunTesseractAsync(pdfSource, workBase, modelDir, best.Psm, ["pdf", "quiet"], ct);
            pdfPath = workBase + ".pdf";
        }
        return new(best.Text, best.Overlay, pdfPath);
    }

    private async Task<string> PrepareImageAsync(string source, string workDir, string quality, int rotation, int index, CancellationToken ct)
    {
        if ((quality == "fast" && rotation == 0) || !File.Exists(_preprocess)) return source;
        var ext = System.IO.Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif")) return source;
        var output = System.IO.Path.Combine(workDir, $"prepared-{index:0000}.png");
        try
        {
            await _runner.RunAsync(_preprocess, ["--mode", quality == "fast" ? "balanced" : quality, "--rotate", rotation.ToString(), source, output], ct);
            return output;
        }
        catch { return source; }
    }

    private async Task<Attempt> TessAttemptAsync(string image, string workBase, string modelDir, int psm, bool makePdf, CancellationToken ct)
    {
        var configs = new List<string> { "txt", "tsv" };
        if (makePdf) configs.Add("pdf");
        configs.Add("quiet");
        await RunTesseractAsync(image, workBase, modelDir, psm, configs, ct);
        var text = File.Exists(workBase + ".txt") ? await File.ReadAllTextAsync(workBase + ".txt", ct) : string.Empty;
        var tsv = File.Exists(workBase + ".tsv") ? await File.ReadAllTextAsync(workBase + ".tsv", ct) : string.Empty;
        var overlay = ParseTsv(tsv);
        var score = ScoreAttempt(overlay, text);
        return new(text, overlay, psm, score, makePdf ? workBase + ".pdf" : null);
    }

    private async Task RunTesseractAsync(string image, string workBase, string modelDir, int psm, IEnumerable<string> configs, CancellationToken ct)
    {
        var args = new List<string> { image, workBase, "--tessdata-dir", modelDir, "-l", "eng", "--oem", "1", "--psm", psm.ToString() };
        args.AddRange(configs);
        await _runner.RunAsync(_tesseract, args, ct, environment: new Dictionary<string, string?> { ["TESSDATA_PREFIX"] = modelDir, ["OMP_THREAD_LIMIT"] = _ompThreads.ToString() });
    }

    private async Task<(string Text, string? PdfPath, OcrOverlay Overlay)?> TryEmbeddedPdfPageAsync(string file, int page, string workDir, bool makePdf, CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync(_pdfToText, ["-f", page.ToString(), "-l", page.ToString(), "-layout", file, "-"], ct);
            var text = r.StdOut.TrimEnd('\f', '\r', '\n');
            if (!EmbeddedTextIsUsable(text)) return null;
            string? pdfPath = null;
            if (makePdf)
            {
                var pattern = System.IO.Path.Combine(workDir, $"native-{page:0000}-%d.pdf");
                await _runner.RunAsync(_pdfSeparate, ["-f", page.ToString(), "-l", page.ToString(), file, pattern], ct);
                var candidate = pattern.Replace("%d", page.ToString());
                pdfPath = File.Exists(candidate) ? candidate : Directory.EnumerateFiles(workDir, $"native-{page:0000}-*.pdf").FirstOrDefault();
                if (pdfPath is null) return null;
            }
            var compact = Regex.Replace(text, @"\s", string.Empty);
            var wordCount = Regex.Split(text.Trim(), @"\s+").Count(s => s.Length > 0);
            return (text, pdfPath, new OcrOverlay(100, 0, 0, [], wordCount, 0, compact.Length));
        }
        catch { return null; }
    }

    private async Task MergePdfsAsync(IReadOnlyList<string> pagePdfs, string output, string workDir, CancellationToken ct)
    {
        if (pagePdfs.Count == 0) throw new InvalidOperationException("No PDF pages were produced.");
        if (pagePdfs.Count == 1) { File.Copy(pagePdfs[0], output); return; }
        var current = pagePdfs.ToList();
        var round = 0;
        while (current.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            round++;
            var next = new List<string>();
            for (var i = 0; i < current.Count; i += MergeBatch)
            {
                var group = current.Skip(i).Take(MergeBatch).ToList();
                if (group.Count == 1) { next.Add(group[0]); continue; }
                var part = current.Count <= MergeBatch && i == 0 ? output : System.IO.Path.Combine(workDir, $"merge-{round}-{i / MergeBatch:0000}.pdf");
                var args = new List<string>(group) { part };
                await _runner.RunAsync(_pdfUnite, args, ct);
                next.Add(part);
            }
            current = next;
        }
        if (!PathsEqual(current[0], output)) File.Copy(current[0], output);
    }

    private string UniqueOutput(string desktop, string baseName, string suffix, string ext)
    {
        lock (_outputGate)
        {
            for (var n = 1; ; n++)
            {
                var tail = n == 1 ? string.Empty : $" ({n})";
                var path = System.IO.Path.Combine(desktop, $"{baseName}{suffix}{tail}{ext}");
                if (_reservedOutputs.Add(path) && !File.Exists(path)) return path;
            }
        }
    }

    private static OcrOverlay ParseTsv(string tsv)
    {
        var words = new List<OcrWord>();
        double weighted = 0, weight = 0;
        var low = 0; var count = 0; var width = 0; var height = 0; var charCount = 0;
        var lines = tsv.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) continue;
            var c = lines[i].Split('\t');
            if (c.Length < 12) continue;
            _ = int.TryParse(c[0], out var level); _ = int.TryParse(c[6], out var left); _ = int.TryParse(c[7], out var top);
            _ = int.TryParse(c[8], out var w); _ = int.TryParse(c[9], out var h); _ = double.TryParse(c[10], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var conf);
            var text = string.Join("\t", c.Skip(11)).Trim();
            if (level == 1) { width = w; height = h; }
            if (level == 5 && text.Length > 0)
            {
                var chars = Math.Max(1, Regex.Replace(text, @"\s", string.Empty).Length); charCount += chars; count++;
                if (conf >= 0) { weighted += conf * chars; weight += chars; if (conf < 50) low++; }
                words.Add(new(text, left, top, w, h));
            }
        }
        return new(weight > 0 ? weighted / weight : 0, width, height, words, count, count > 0 ? low / (double)count : 1, charCount);
    }

    private static double ScoreAttempt(OcrOverlay o, string text)
    {
        var chars = Math.Max(o.CharacterCount, Regex.Replace(text, @"\s", string.Empty).Length);
        var textReward = Math.Min(7, Math.Log10(Math.Max(1, chars)) * 1.8);
        return o.Confidence + textReward - o.LowConfidenceRatio * 12;
    }
    private static bool IsStrongBest(Attempt a) => a.Overlay.Confidence >= 92 && a.Overlay.WordCount >= 10 && a.Overlay.LowConfidenceRatio <= .06 && a.Overlay.CharacterCount >= 50;
    private static bool IsStrongBalanced(Attempt a) => a.Overlay.Confidence >= 88 && a.Overlay.WordCount >= 8 && a.Overlay.LowConfidenceRatio <= .12 && a.Overlay.CharacterCount >= 35;
    private static bool EmbeddedTextIsUsable(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = Regex.Replace(text, @"\s", string.Empty); if (compact.Length < 80) return false;
        var words = Regex.Split(text.Trim(), @"\s+").Where(s => s.Length > 0).ToArray(); if (words.Length < 12) return false;
        var printable = compact.Count(ch => ch >= 32 && ch != 127); var replacement = compact.Count(ch => ch == '�');
        return printable / (double)Math.Max(1, compact.Length) > .94 && replacement / (double)Math.Max(1, compact.Length) < .02;
    }
    private static string Signature(string file) { var i = new FileInfo(file); return $"{i.FullName.ToLowerInvariant()}|{i.Length}|{i.LastWriteTimeUtc.Ticks}"; }
    private static string Hash(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    private static string SafeBase(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "document" : name.Trim();
    }
    private static bool PathsEqual(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string FormatBytes(long n) => n < 1024 ? $"{n} B" : n < 1024 * 1024 ? $"{n / 1024d:0.0} KB" : n < 1024L * 1024 * 1024 ? $"{n / 1024d / 1024:0.0} MB" : $"{n / 1024d / 1024 / 1024:0.0} GB";

    private sealed record Profile(int Dpi, int[] Psms, bool Preprocess);
    private sealed record Attempt(string Text, OcrOverlay Overlay, int Psm, double Score, string? PdfPath);
    private sealed record PageResult(string Text, OcrOverlay Overlay, string? PdfPath);
}
