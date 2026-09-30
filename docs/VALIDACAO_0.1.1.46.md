# Validação do MSIX 0.1.1.46

- Compilação UWP: 0 erros, 4 avisos conhecidos de versões de assemblies.
- NativeAOT CoreHost: concluído; avisos conhecidos de trimming/AOT.
- Assinatura MSIX: válida (`CN=Ryujinx Xbox Dev`).
- SHA-256 do MSIX: `31291D0C174EC1FBEC125877B71B637D9E4EEEA86763BE8E37148CE18626136C`.
- CoreHost empacotado corresponde ao build final: sim.
- Host nativo empacotado corresponde ao build final: sim.
- `Ryujinx.Uwp.Native.dll`: AppContainer, CRT estático e exports D3D11 presentes.
- `spirv-cross-c-shared.dll`: AppContainer e sem CRT desktop.
- Smoke test do host nativo: `D3D11Probe=1`.
- Consulta do device antes da inicialização: `0x80004005`, resultado esperado.
- DLLs Vulkan/Mesa/Dozen no pacote D3D11-only: nenhuma.
- Manifesto: `Ryujinx.Xbox.UWP` versão `0.1.1.46`.
- Assets obrigatórios: cinco presentes.

O `Ryujinx.Uwp.CoreHost.dll` é uma biblioteca compartilhada NativeAOT e não recebe o bit PE AppContainer pelo linker do .NET; ele não depende do CRT desktop e é carregado como conteúdo pelo executável UWP, exatamente como nas versões executadas anteriormente no Xbox.
