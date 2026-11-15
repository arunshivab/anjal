# Packages in Anjal's own feed

Anjal restores these packages from this folder, not from nuget.org (`nuget.config` maps `LiPicons.*` and
`Chuvadi.*` here). Each one is listed with its SHA-256 in `ALL-SHA256SUMS.txt`; `tools/check_packages.py`
checks the folder against that list before every restore in CI.

| Package | Version | Licence | Used by |
|---|---|---|---|
| LiPicons.Blazor | 1.1.0 | © 2026 imagiQa Healthcare Services Pvt Ltd (no licence element in its `.nuspec`) | `Anjal.Webmail` |
| Chuvadi.Pdf (meta-package) and its parts: Chuvadi.Cryptography, Chuvadi.Pdf.Annotations, .Authoring, .Color, .Content, .Documents, .Encryption, .Filters, .Fonts, .Fonts.Bundled, .Fonts.Rendering, .Fonts.Woff2, .Forms, .Graphics, .IO, .Images, .Objects, .Operations, .PdfA, .Primitives, .Reader, .Redaction, .Rendering, .Rendering.DisplayList, .Rendering.Raster, .Rendering.Walking, .Signatures, .Svg, .Text, .Text.Shaping, .Watermark, .Xfa | 3.33.0 | Apache-2.0 (parts derived from HarfBuzz and FreeType, with their notices in the library) | `Anjal.Viewer` only |
| Chuvadi.Docs | 2.2.0 | MIT | `Anjal.Viewer` only |
| Chuvadi.Sheets | 2.1.0 | MIT | `Anjal.Viewer` only |
| Chuvadi.Zip | 1.0.0 | MIT | `Anjal.Viewer` only (every ZIP read in the viewer, AC-20) |
| Chuvadi.Fonts (LiPi Sans) | 1.1.0 | MIT for the code; SIL Open Font License 1.1 for the fonts | `Anjal.Viewer` only (through chuvadi-pdf) |
| Chuvadi.Fonts.Liberation | 1.0.0 | MIT for the code; SIL Open Font License 1.1 for the Liberation fonts (Reserved Font Name "Liberation") | `Anjal.Viewer` only (through chuvadi-pdf) |

**Where they came from (rc.29, DES-22 §11).** The Chuvadi packages are the verified set the Anjal coordinator
checked on 11 Nov 2026 (`anjal-chuvadi-packages-verified.zip`, 38 packages, every SHA-256 matching its list).
`Chuvadi.Pdf.Rendering.Wpf` from that set is not copied here: it draws on Windows' WPF only, the `Chuvadi.Pdf`
meta-package does not pull it in, and Anjal does not use it.

**Chuvadi.Zip 1.0.0 (AC-20, 12 Nov 2026).** The released package (tag `zip-v1.0.0`, 51,674 bytes), verified by the Anjal
coordinator and again on arrival here: SHA-256 `e019f9b61d48d705e9b8e81e3ca8bcbb3114056b2127f4e9ea2af0aa51110104`. It holds the
DLL, its XML docs, the README and the `.nuspec` only; no dependencies.

**What Anjal does with them.** Only `Anjal.Viewer` (the separate `anjal-viewer` process) references the Chuvadi
libraries; the webmail references none of them and talks to the viewer over its socket. No Chuvadi source is copied
into Anjal: the libraries are used as packages only (owner, 11 Nov 2026).

Each package carries its licence in its own `.nuspec` (`<license>`) and, for the fonts, the full OFL text inside the
package (`FontPack.LicenseText`, `LiberationPack.LicenseText`).
