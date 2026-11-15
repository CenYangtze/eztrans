# Third-party software notices

SnapTrans uses the following packages as binaries through NuGet. Upstream source files were not copied into SnapTrans's OCR implementation. License identifiers below were checked against the actual package metadata and, for file-based declarations, the package's license text on 2026-11-08. Full notices are retained under `licenses/` and should accompany redistributed release artifacts.

| Component | Version | License | Authoritative source |
| --- | --- | --- | --- |
| SkiaSharp / native assets | 3.119.1 | MIT | [NuGet](https://www.nuget.org/packages/SkiaSharp/3.119.1), package `LICENSE.txt`; [source](https://github.com/mono/SkiaSharp) |
| Microsoft.Extensions.* | 10.0.0 | MIT | [Hosting package](https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.0); [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT | [NuGet](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.0), package `LICENSE.TXT` |
| Serilog | 4.2.0 | Apache-2.0 | [NuGet](https://www.nuget.org/packages/Serilog/4.2.0), [versioned license](https://github.com/serilog/serilog/blob/v4.2.0/LICENSE) |
| Serilog.Extensions.Hosting / Logging | 9.0.0 | Apache-2.0 | [Hosting](https://www.nuget.org/packages/Serilog.Extensions.Hosting/9.0.0), [Logging](https://www.nuget.org/packages/Serilog.Extensions.Logging/9.0.0) package declarations |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | [NuGet](https://www.nuget.org/packages/Serilog.Sinks.File/7.0.0) package declaration |
| Velopack | 1.2.161 | MIT | [NuGet](https://www.nuget.org/packages/Velopack/1.2.161), [versioned license](https://github.com/velopack/velopack/blob/1.2.161/LICENSE) |
| RapidOcrNet (optional Paddle component) | 4.2.0 | Apache-2.0 | [NuGet](https://www.nuget.org/packages/RapidOcrNet/4.2.0), [fixed source/license](https://github.com/BobLd/RapidOcrNet/blob/708cae2fcb88720e1d891a81b5ee3e8b2bcc139e/LICENSE.txt) |
| PaddleOCR models | PP-OCRv5 / PP-OCRv6, fixed model manifest | Apache-2.0 | [PaddleOCR license](https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE), [RapidOCR model distribution](https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml) |
| RapidOCR model distribution / dictionaries | Publisher revision in `OcrModelCatalog` | Apache-2.0 | [RapidOCR license](https://github.com/RapidAI/RapidOCR/blob/main/LICENSE); upstream copyright notice retained |
| Microsoft.ML.OnnxRuntime / Managed (optional) | 1.29.0 | MIT | [NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.29.0), package `LICENSE` / `LICENSE.txt`; package `ThirdPartyNotices.txt` retained unmodified |
| Clipper2 (optional, via RapidOcrNet) | 2.0.0 | BSL-1.0 | [NuGet](https://www.nuget.org/packages/Clipper2/2.0.0), package `License.txt` explicitly states Boost Software License 1.0 |
| System.Numerics.Tensors (optional, via ONNX Runtime) | 9.0.0 | MIT | [NuGet](https://www.nuget.org/packages/System.Numerics.Tensors/9.0.0) package declaration |

RapidOcrNet's README credits RapidAI/RapidOCR, PdfPig and the PContour/PContourNet work. Its Apache-2.0 license and upstream notices remain applicable to its packaged binary; this project adds an adapter rather than copying their implementations. ONNX Runtime's own third-party notice file covers dependencies embedded in its native binary. `SkiaSharp.Native.ThirdPartyNotices.txt`, retained from the native asset package, covers Skia and the third-party libraries embedded with it; the wrapper's MIT license does not replace those notices.

Windows SDK targeting/runtime components and the .NET Desktop Runtime are governed by their respective Microsoft license terms; they are not automatically relabeled as MIT by this table. `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 links [Windows SDK terms](https://aka.ms/WinSDKLicenseURL) rather than declaring an SPDX expression. Self-contained distributions also retain the runtime's supplied notices.

Development/test-only packages (xUnit, Microsoft.NET.Test.Sdk, Coverlet) are not included in the product executable. Their license metadata remains available in the locked NuGet dependency graph.
