# PortableOCR — WinUI 3 edition

Native WinUI 3 front end for PortableOCR, ported from the Electron Studio UI while retaining the bundled Tesseract 5.5.3, Poppler 26.02.0, and preprocessing runtime.

## What changed

- Native WinUI 3 / XAML shell with Mica, Fluent controls, custom title bar, theme support, drag and drop, keyboard shortcuts, queue, progress, and document preview.
- Calls Tesseract, Poppler, and `preprocess.exe` directly from C#; Electron is not used by the new UI.
- Preserves Fast / Balanced / Best OCR profiles, adaptive Best-mode layout attempts, the embedded-text PDF fast path, searchable PDF generation, text output, cancellation, per-file quality overrides, rotation, zoom, and OCR overlay boxes.
- Remains portable and network-independent. OCR runtime files are bundled under `runtime/` and copied next to the app at build/publish time.

## Requirements

- Windows 10 version 1809 or later; Windows 11 is recommended for the full Mica experience.
- Visual Studio 2026 with the **WinUI application development** workload, or .NET 10 SDK with WinUI templates installed.
- x64 Windows.

## Build

Open `PortableOCR.WinUI3.csproj` in Visual Studio 2026 and build x64 Release, or from a configured Windows terminal:

```powershell
dotnet restore
dotnet build -c Release -r win-x64
```

## Publish portable folder

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishSingleFile=false
```

The published folder contains the WinUI 3 app and the OCR runtime. Keep the folder together; the `runtime` subfolder is required.

## Output

OCR results are written to the user's Desktop using the same naming convention as the original app:

- `filename_OCR.txt`
- `filename_Searchable.pdf`

## Source migration note

The old Electron sources are not required at runtime. They were used only as the behavior reference for the WinUI 3 port.
