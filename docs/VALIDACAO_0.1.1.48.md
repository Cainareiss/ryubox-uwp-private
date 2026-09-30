# Validacao do MSIX 0.1.1.48

- Compilacao gerenciada: 0 erros e 2 avisos antigos de eventos nao utilizados.
- Publicacao NativeAOT: concluida.
- Compilacao XAML/UWP/MSIX: 0 erros e 4 avisos conhecidos de versoes de assemblies.
- Assinatura: valida, `CN=Ryujinx Xbox Dev`.
- Identidade: `Ryujinx.Xbox.UWP` versao `0.1.1.48`.
- Nome visivel: `Ryujinx Xbox Audio Safe`.
- CoreHost empacotado corresponde ao build NativeAOT final: sim.
- Host D3D11 e SPIRV-Cross presentes: sim.
- Smoke test do host nativo: `D3D11_PROBE=1`.
- Consulta do device antes da inicializacao: `0x80004005`, resultado esperado.
- DLLs Vulkan/Mesa/Dozen no pacote D3D11-only: nenhuma.
- Assets obrigatorios: cinco presentes.
- SHA-256 do MSIX: `5E95BCB57FD1810E493D2D8CB6532880D84501E166C246FB51AB47F5C1D9029F`.
