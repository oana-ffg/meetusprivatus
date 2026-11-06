# Third-party notices

Meetus Privatus source is MIT, attributed to Meetus Privatus contributors. These components retain upstream terms and copyright notices. Include this file and licenses/ when redistributing binaries/models.

| Component | Version / asset | Terms and included notice |
| --- | --- | --- |
| NAudio.Wasapi / NAudio.Core | 3.1.0 | MIT; licenses/NAudio.txt; Mark Heath |
| Cockos WDL resampler included in NAudio | NAudio's C# port | WDL permissive notice; licenses/WDL.txt; Cockos Incorporated, ported by Mark Heath with Justin Frankel's permission |
| Whisper.net / CPU runtimes | 1.9.1 | MIT; licenses/Whisper.net.txt; sandrohanea |
| whisper.cpp / ggml native libraries | whisper.cpp 1.8.5 integration | MIT; licenses/whisper.cpp.txt; ggml authors |
| Whisper base.en model | ggml-base.en-q5_1.bin / ggml-base.en.bin | MIT; licenses/Whisper.txt; OpenAI |
| Whisper tiny.en and small.en models (optional downloads) | ggml-tiny.en-q5_1.bin / ggml-small.en-q5_1.bin | MIT; licenses/Whisper.txt; OpenAI |
| ONNX Runtime | Microsoft.ML.OnnxRuntime 1.28.0 | MIT; licenses/ONNXRuntime.txt and licenses/ONNXRuntime-third-party.txt; Microsoft Corporation |
| Parakeet TDT 0.6 v3 model (optional download) | int8 ONNX conversion by istupakov of nvidia/parakeet-tdt-0.6b-v3 | CC-BY-4.0; licenses/Parakeet.txt; NVIDIA |
| Silero VAD | ggml-silero-v5.1.2.bin | MIT; licenses/Silero.txt; Silero Team |
| LLamaSharp / LLamaSharp.Backend.Cpu / LLamaSharp.Backend.Vulkan.Windows | 0.30.0 | MIT; licenses/LLamaSharp.txt; SciSharp STACK |
| llama.cpp / ggml native libraries | commit 815a2a59 | MIT; licenses/llama.cpp.txt; ggml authors |
| Velopack | 1.2.161 | MIT; licenses/Velopack.txt; Caelan Sayler, Velopack Ltd. |
| Qwen3.5-4B model (optional download) | Qwen3.5-4B-Q4_K_M.gguf, unsloth GGUF conversion | Apache-2.0; licenses/Qwen.txt; Alibaba Cloud (Qwen team) |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT; licenses/CommunityToolkit.txt; .NET Foundation and Contributors |
| System.Linq.Async / System.Interactive.Async | 7.0.1 | MIT; licenses/System.Linq.Async.txt; .NET Foundation and Contributors |
| System.Numerics.Tensors | 10.0.13 | MIT; licenses/System.Numerics.Tensors.txt; .NET Foundation and contributors |
| Microsoft.Extensions.AI.Abstractions, DependencyInjection.Abstractions, Logging.Abstractions; Microsoft.Bcl.AsyncInterfaces, Microsoft.Bcl.Memory | 10.11.0 / 10.0.13 | MIT; licenses/Microsoft.Extensions.txt; .NET Foundation and Microsoft contributors |

Whisper.net is used only for local inference. Its downloadable-model utilities and cloud-facing abstractions are not invoked. Windows x64 CPU libraries, including baseline fallback, are the shipped native assets; GPU backends are excluded from selection. LLamaSharp likewise runs local inference only, on its CPU backend or, when the user turns on GPU use, its Vulkan backend. Velopack is used only when the user asks to check for or apply an update. ONNX Runtime runs local inference on its CPU provider only, with its telemetry events turned off before any session is created.

Sources: [NAudio 3.1.0](https://github.com/naudio/NAudio/tree/v3.1.0), [Whisper.net 1.9.1](https://github.com/sandrohanea/whisper.net/tree/1.9.1), [whisper.cpp 1.8.5](https://github.com/ggml-org/whisper.cpp/tree/v1.8.5), [Whisper](https://github.com/openai/whisper), [Silero VAD](https://github.com/snakers4/silero-vad/tree/v5.1), [LLamaSharp 0.30.0](https://github.com/SciSharp/LLamaSharp/tree/v0.30.0), [Velopack](https://github.com/velopack/velopack/tree/92d6a1c91716729d449034df5c50307dcce39493), [llama.cpp](https://github.com/ggml-org/llama.cpp/tree/815a2a5915f22ce6a760c676389c5dfe8535c08f), [Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B), [ONNX Runtime 1.28.0](https://github.com/microsoft/onnxruntime/tree/v1.28.0), [Parakeet TDT 0.6 v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3), [its ONNX conversion](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/tree/8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce), [Community Toolkit](https://github.com/CommunityToolkit/dotnet/tree/35dfe7b12da0e64384bfaec7ebc0883196a89f9f), [dotnet/reactive](https://github.com/dotnet/reactive/tree/96eed184a84471105d5d701a99e0b98629703d3f).

Model conversion sources/revisions and hashes are pinned in scripts/model-assets.psd1 and src/Meetu/SpeechModels.cs. Assets are fetched only by explicit setup or supplied manually. Conversion/quantization does not remove attribution requirements.

The build copies the actual self-contained .NET runtime's license and third-party notices into licenses/ as dotnet-runtime.txt and dotnet-runtime-third-party.txt, and the Windows Desktop runtime's license as windowsdesktop-runtime.txt. That package ships no third-party file, so WPF's notices are kept in licenses/dotnet-wpf-third-party.txt. Preserve them too. The Visual C++ redistributable is installed separately under Microsoft's terms and is not bundled here.

Development-only xUnit, its Visual Studio adapter, and Microsoft.NET.Test.Sdk are pinned in the test project and not application dependencies. The public JFK test fixture distributed in whisper.cpp samples is downloaded into ignored development artifacts and is not redistributed in source/publication.
