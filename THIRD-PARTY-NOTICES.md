# Third-party notices

Meetus Privatus source is MIT, attributed to Meetus Privatus contributors. These components retain upstream terms and copyright notices. Include this file and licenses/ when redistributing binaries/models.

| Component | Version / asset | Terms and included notice |
| --- | --- | --- |
| NAudio.Wasapi / NAudio.Core | 3.1.0 | MIT; licenses/NAudio.txt; Mark Heath |
| Cockos WDL resampler included in NAudio | NAudio's C# port | WDL permissive notice; licenses/WDL.txt; Cockos Incorporated, ported by Mark Heath with Justin Frankel's permission |
| Whisper.net / CPU runtimes | 1.9.1 | MIT; licenses/Whisper.net.txt; sandrohanea |
| whisper.cpp / ggml native libraries | whisper.cpp 1.8.5 integration | MIT; licenses/whisper.cpp.txt; ggml authors |
| Whisper base.en model | ggml-base.en-q5_1.bin / ggml-base.en.bin | MIT; licenses/Whisper.txt; OpenAI |
| Silero VAD | ggml-silero-v5.1.2.bin | MIT; licenses/Silero.txt; Silero Team |
| System.Numerics.Tensors | 9.0.0 | MIT; licenses/System.Numerics.Tensors.txt; .NET Foundation and contributors |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | MIT; licenses/Microsoft.Extensions.txt; .NET Foundation and Microsoft contributors |

Whisper.net is used only for local inference. Its downloadable-model utilities and cloud-facing abstractions are not invoked. Windows x64 CPU libraries, including baseline fallback, are the shipped native assets; GPU backends are excluded from selection.

Sources: [NAudio 3.1.0](https://github.com/naudio/NAudio/tree/v3.1.0), [Whisper.net 1.9.1](https://github.com/sandrohanea/whisper.net/tree/1.9.1), [whisper.cpp 1.8.5](https://github.com/ggml-org/whisper.cpp/tree/v1.8.5), [Whisper](https://github.com/openai/whisper), [Silero VAD](https://github.com/snakers4/silero-vad/tree/v5.1), [.NET extensions 10.2.0](https://github.com/dotnet/extensions/tree/v10.2.0).

Model conversion sources/revisions and hashes are pinned in scripts/download-models.ps1. Assets are fetched only by explicit setup or supplied manually. Conversion/quantization does not remove attribution requirements.

The build copies the actual self-contained .NET runtime's license and third-party notices to licenses/dotnet-runtime.txt and licenses/dotnet-runtime-third-party.txt. Preserve them too. The Visual C++ redistributable is installed separately under Microsoft's terms and is not bundled here.

Development-only xUnit, its Visual Studio adapter, and Microsoft.NET.Test.Sdk are pinned in the test project and not application dependencies. The public JFK test fixture distributed in whisper.cpp samples is downloaded into ignored development artifacts and is not redistributed in source/publication.
