# PortableOCR WinUI 3

Native WinUI 3 port of PortableOCR. The application project is in `PortableOCR.WinUI3/`.

- `PortableOCR.WinUI3/README.md` — build and publish instructions.
- `PORTING-NOTES.md` — compatibility and UI migration notes.
- `Import-Runtime.ps1` — imports the OCR runtime from an extracted copy of the original PortableOCR Studio package when using the source-only bundle.
- `.github/workflows/windows-build.yml` — Windows x64 CI publish workflow when the runtime is committed to the repository.
