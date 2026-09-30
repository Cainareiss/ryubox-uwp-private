# Validacao do MSIX 0.1.1.47

- Compilacao gerenciada: 0 erros.
- Publicacao NativeAOT: concluida.
- Compilacao UWP/MSIX: 0 erros e 4 avisos conhecidos de versoes de assemblies.
- Assinatura: valida, `CN=Ryujinx Xbox Dev`.
- Identidade: `Ryujinx.Xbox.UWP` versao `0.1.1.47`.
- Nome visivel: `Ryujinx Xbox D3D11 Safe JIT`.
- CoreHost empacotado corresponde ao build final: sim.
- Host nativo empacotado corresponde ao build final: sim.
- `Ryujinx.Uwp.Native.dll`: x64, AppContainer e sem CRT desktop.
- `spirv-cross-c-shared.dll`: x64, AppContainer e sem CRT desktop.
- Smoke test de carregamento do host nativo: `D3D11Probe=1`.
- Consulta do device antes da inicializacao: `0x80004005`, resultado esperado.
- DLLs Vulkan/Mesa/Dozen no pacote D3D11-only: nenhuma.
- Assets obrigatorios: cinco presentes.
- SHA-256 do MSIX: `211CF042AE1C626B410AA27073CCAE41EB4B34051A74C9B554F3A2F7288E6996`.
