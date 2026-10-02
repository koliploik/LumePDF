# Third-party notices

Lieve ships with, or depends on, the following components.

| Component | Author | License | Source |
|---|---|---|---|
| PDFium | Google / The PDFium Authors | BSD-3-Clause, Apache-2.0 | https://pdfium.googlesource.com/pdfium/ |
| PDFium prebuilt binaries | Benoît Blanchon | Apache-2.0 (build scripts) | https://github.com/bblanchon/pdfium-binaries |
| Windows App SDK / WinUI 3 | Microsoft | MIT | https://github.com/microsoft/WindowsAppSDK |
| .NET runtime (compiled in via Native AOT) | Microsoft / .NET Foundation | MIT | https://github.com/dotnet/runtime |
| C#/WinRT | Microsoft | MIT | https://github.com/microsoft/CsWinRT |

`pdfium.dll` also contains third-party code bundled by PDFium itself (FreeType, libjpeg-turbo,
OpenJPEG, libpng, zlib, lcms2, Abseil and others), each under its own permissive license.
The full list is in the PDFium source tree under `third_party/`.
