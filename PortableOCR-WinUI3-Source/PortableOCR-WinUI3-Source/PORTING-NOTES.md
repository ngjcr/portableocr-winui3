# PortableOCR WinUI 3 porting notes

This project replaces the Electron/Chromium presentation layer with a native WinUI 3 application while preserving the existing offline OCR runtime.

## Preserved behavior

- PDF, PNG, JPG/JPEG, TIFF, BMP, WEBP, and GIF input.
- Fast (220 DPI), Balanced (300 DPI), and Best (400 DPI) profiles.
- Embedded-text PDF fast path before OCR.
- Adaptive OCR layout scoring for Best mode (PSM 3/6/11).
- Tesseract text and TSV output, including overlay geometry.
- Searchable PDF output and Poppler-based PDF merging/validation.
- Desktop output naming: `_OCR.txt` and `_Searchable.pdf`.
- Adaptive 1–3 document workers with bounded Tesseract OpenMP threads.
- Cancellation and temporary-work cleanup.

## Native WinUI 3 UI

- Mica backdrop and Fluent resource styling.
- Custom title bar.
- Drag/drop plus native multi-file picker.
- File queue with thumbnails, page counts, status, and per-file quality override.
- Live page preview, processed preview, OCR bounding-box overlay, rotate, and zoom.
- Light/dark/system theme setting.
- Ctrl+O, Ctrl+Enter, and Delete shortcuts.

## Deliberate simplification from the Electron shell

The new shell focuses on the document/OCR workflow. Electron-only chrome and secondary UI such as the command palette, renderer notification system, and recovery/history presentation are not carried over as separate subsystems. The OCR engine, output contract, profiles, and preview workflow remain the compatibility priorities.

## Build validation note

The source and XAML are structurally validated in the delivery environment. A WinUI 3 binary must be compiled on Windows with the Windows App SDK toolchain; the delivery environment is Linux and cannot perform that native Windows build.
