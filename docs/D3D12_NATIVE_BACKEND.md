# Backend D3D12 nativo — implementação e limites

Data: 2026-09-10.

## Retomada da parte 2 — 2026-09-13

O primeiro jogo reservado para smoke test do backend D3D12 será
`E:\jogos para testar ryuboxz\Sonic Mania (v0).nsp`. Esse teste deve começar
somente depois que a ponte CoreHost conseguir selecionar D3D12 sem remover o
caminho D3D11 e com apresentação/input/fallbacks básicos validados. Até lá,
Sonic Mania serve como alvo de aceitação, não como substituto das regressões
unitárias.

## Estado real

Existe agora uma base nativa D3D12 compilável e executável, adicionada à DLL
`Ryujinx.Uwp.Native.dll` ao lado do backend D3D11 existente. A API nova tem
prefixo `RyuboxD3D12_`, contexto por instância e ABI versão 1. Não depende de
D3D11On12, Vulkan ou Mesa/Dozen. Há uma interface C# em
`src/src/Ryujinx.Graphics.D3D12` para consumir essa API com `SafeHandle`.

**Não é ainda um renderizador completo de jogos do Switch.** Agora existem
implementações concretas das interfaces da GAL, com execução de workloads
verificada, mas com limites explícitos de formatos/estados e tradução.
Os itens 1 e 2 abaixo ainda NÃO estão integralmente concluídos. Consulte
`D3D12_GAL_IMPLEMENTACAO.md` para testes e pendências exatas.
O CoreHost continua iniciando jogos pelo caminho existente. Adicionar a referência
ao projeto C# não seleciona D3D12 automaticamente. Não há opção habilitada no menu
que finja executar jogos por D3D12. Os testes abaixo verificam workloads gráficos
e compute reais, não emulação de um jogo.

## Implementado

- Device D3D12 com escolha de adaptador compatível e consulta do feature level
  e resource binding tier. Feature level 11_0 mínimo, sem presumir recursos
  Ultimate. WARP só é solicitado explicitamente em testes.
- Fila DIRECT, command list e três command allocators independentes, protegidos
  por fences. A apresentação normal não espera a GPU ao final de cada frame:
  espera somente quando o slot de comandos vai ser reutilizado.
- Swapchain para HWND e `SwapChainPanel`; alvo offscreen para testes. Resize
  espera as submissões antigas e recria os alvos. Escala de composição separada
  do tamanho físico do buffer.
- Estados explícitos dos recursos e tratamento do decay de buffers para COMMON
  nas fronteiras de `ExecuteCommandLists`.
- Uploads em páginas mapeadas reutilizáveis, com alinhamento de row pitch e
  footprints fornecidos pelo D3D12. Capacidade total de upload limitada a 192 MiB.
- Buffers DEFAULT com upload parcial e cópia GPU–GPU. Texturas RGBA8 2D com uma
  camada/mipmap; pitch de entrada pode conter padding.
- Retenção de recursos, PSOs e descritores até a conclusão da submissão. Uma
  referência por recurso por submissão evita ampliar a lista a cada draw.
- Heap de SRVs com 1024 entradas por slot. Uma entrada não é sobrescrita antes
  de sua fence. Excesso é um erro explícito, não reutilização de descritor ativo.
- Root signature e PSO de apresentação com HLSL SM5.1, escala por shader,
  crop normalizado, sampler point/clamp e uma única inversão vertical explícita.
- Compute pipelines HLSL SM5.1 com root SRV t0, root UAV u0 e 16 DWORDs de
  constantes b0. Dispatch com barreira UAV e testes de dependências RAW/WAR.
- Limite inicial de 512 MiB de alocações DEFAULT controladas pela base, incluindo
  recursos cujo handle foi removido mas que ainda estão em uso. Não representa
  todo o consumo de memória do driver; swapchain/PSOs/descriptors são adicionais.
- Readback separado para diagnóstico/captura. A apresentação não faz readback.
- HRESULT na ABI, detecção de device removal, espera com timeout, contagem de
  erros da camada de debug e descarte/recriação de instância.
- Se a espera de destruição expirar com o device ainda ativo, a ABI retorna erro
  e retém o contexto para permitir retry, em vez de liberar recursos usados pela
  GPU. A integração futura do ciclo de sessão precisa tratar esse retorno; uma
  falha na liberação final do SafeHandle conserva a alocação até o processo
  terminar. Timeout não é contabilizado como cleanup completo.
- C# com spans de entrada copiados durante a chamada, IDs associados ao device,
  proteção de vida útil por SafeHandle e descarte de recursos após o device.
- GAL concreta: renderer, pipeline, programa, textura RGBA8, janela, arrays,
  samplers, contadores de amostras/primitivas, sync e captura. Falhas de recursos
  não implementados são explícitas; CoreHost ainda não seleciona esta GAL.
- Novo caminho de programas com reflexão SM5.1, register spaces, descriptor
  arrays, root signatures por estágio/tipo, PSOs gráficos limitados por cache,
  compute genérico, vertex/index buffers, instancing e ExecuteIndirect/count.
- Geometry e hull/domain executados também a partir de SPIR-V, com pixels e
  cache conferidos. Hull usa fallback puro restrito: barreiras, dependências
  entre invocações e efeitos colaterais ainda não são suportados. Não considerar
  esses testes como validação geral dos shaders guest.
- Revisão seguinte: interfaces HS/DS ligadas, quad/isoline e particionamentos
  conferidos, GS com 1/3/32 invocações e sem inputs por vértice, cache D2P4 e
  isolamento do estado compute/patch. Restart de triangle-strip-adjacency
  comparado com os índices realmente recebidos pelo GS nativo. PASS Intel/WARP;
  regressão D3D11 preservada. Detalhes e limites no relatório GAL.
- SPIRV-Cross separado do D3D11: HLSL 5.1, preservação dos descriptor sets,
  auxiliar base vertex/instance explícito e teste de store SPIR-V real na GPU.

## Reaproveitamento e convivência

O build nativo, Windows SDK, COM/WRL, D3DCompiler, DXGI e a integração com
`SwapChainPanel` seguem a infraestrutura já disponível. O novo módulo fica em
arquivos próprios para não alterar os exports nem o estado do D3D11.

CPU/JIT, HLE, filesystem, firmware, keys, input, áudio, decodificação de vídeo e
o frontend ImGui continuam reaproveitáveis. A GAL, o tradutor de shaders do Switch
e a conversão de formatos de textura também devem ser compartilhados. O
`D3D11SpirvCrossCompiler` contém uma ponte SPIR-V/HLSL que pode ser extraída para
uma camada comum; suas correções específicas de bindings/orientação precisam ser
auditadas antes de usar no D3D12. Não se deve copiar os hazards implícitos,
device-context calls ou caches de recursos D3D11 para o novo backend.

## O que falta para jogos

**Escopo atual (última instrução do usuário): trabalhar somente na parte 1,
visando 80% e deixando guia de conclusão até 100%. Parte 2 pausada; não iniciar
as partes 3 a 5. A porcentagem ainda não é cobertura comprovada.**
Guia: [`D3D12_PARTE1_GUIA_CONCLUSAO.md`](D3D12_PARTE1_GUIA_CONCLUSAO.md).

Atualização 11/09: a validação ampliada está em
[`D3D12_GAL_IMPLEMENTACAO.md`](D3D12_GAL_IMPLEMENTACAO.md). Geometry via SPIR-V,
alpha especializado, blending constante/dual-source e rasterizer discard
passaram em testes de GPU. **Isso ainda não conclui os itens 1/2.** A extensão
solicitada para os itens 3–5 foi registrada, mantendo a ordem pedida e sem
anunciar compatibilidade de jogos ou ativar D3D12 no CoreHost prematuramente.

1. Implementar `IRenderer`, `IPipeline`, `ITexture`, `IWindow`, `IProgram`, arrays,
   samplers e counters da GAL com o novo device. Não preencher métodos com no-op
   nem anunciar capabilities que não estejam implementadas.
   **Em andamento:** interfaces e operações testadas existem; estados/formatos
   ainda rejeitados estão enumerados no relatório da GAL. A presença de todas
   as interfaces não equivale à conclusão integral deste item.
2. Integrar SPIR-V/HLSL com reflexão de bindings, registradores/espaços, root
   signatures e cache de PSOs. O layout compute atual é deliberadamente restrito;
   ainda não atende todos os shaders de jogos. Vertex/index buffers e draws
   genéricos, instancing/indirect, geometry/tessellation e fallback adequado
   ainda precisam de implementação.
   **Em andamento:** já há execução dos draws/compute, reflexão, cache e
   fallbacks para índices/topologias. A retomada controlada da parte 2 adicionou
   `FragDepth`, transform feedback com decorações SPIR-V agrupadas,
   `SupportsTransformFeedback=true`, cache D3D12 explícito e validação de
   stores/atomics em vertex/geometry. Ainda falta fechar tradução de todos os
   estágios guest e eliminar/revisar limites explicitados no relatório da GAL
   antes de usar jogos como critério principal.
3. Completar formatos e vistas: BCn, depth/stencil, arrays, mips, MSAA/resolve,
   swizzle, UAVs de textura, render targets múltiplos, blend/write masks,
   viewport/scissor e orientação da convenção guest para DirectX.
4. Ampliar o controle de estados para subresources, UAV/aliasing barriers,
   descritores dinâmicos paginados e heap/suballocation. A base atual usa
   committed resources, uma única fila e um estado por recurso.
5. Implementar queries/fences guest, readback assíncrono e caminhos NVDEC,
   mantendo contadores FPS baseados em apresentações reais.
6. Acrescentar seleção persistida de backend ao snapshot/UI/CoreHost e passagem
   de ownership entre frontend e jogo. Hoje o snapshot força D3D11; remover isso
   antes de ter uma GAL D3D12 funcional produziria uma seleção enganosa.
7. Integrar ImGui D3D12 ou uma transferência explícita entre sua superfície
   D3D11 e a nova swapchain, bloqueando input/present concorrentes. Testar troca
   de sessão, fatal, device removal, timeout e suspensão UWP.
8. Validar no Xbox Dev Mode real, primeiro um jogo 2D e depois jogos 3D. Só então
   comparar cold start, FPS, tempos de frame e memória com D3D11. O teste no PC
   não garante desempenho nem compatibilidade no Xbox.

## Testes reproduzíveis

No diretório raiz:

```powershell
# DLL isolada, hardware real, debug layer e pixel checks offscreen
.\eng\Test-RyuboxD3D12.ps1 -DebugLayer

# Swapchain HWND, janela invisível
.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Window

# Referência software explícita para diferenciar problemas do driver
.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Warp

# DLL completa com D3D11 e D3D12
.\eng\Build-RyujinxUwpNative.ps1 -Configuration Release `
  -OutputRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native'

.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Window `
  -NativeDll 'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll'

dotnet run --project .\eng\D3D12InteropRegression\D3D12InteropRegression.csproj -c Release -- `
  'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll'
```

O teste nativo compara pixels de uma textura assimétrica 7×5 com padding,
confere buffer copy e compute na GPU, executa 90 frames, testa flip/crop/resize,
remove recursos/PSOs antes da execução e recria o contexto. Não usa ROMs,
keys ou firmware. Os 90 frames são um teste de correção, não uma medição de FPS
de jogo.

## Referências de arquitetura

- [Microsoft: resource barriers e regras de promoção/decay](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12)
- [Microsoft: execução e sincronização das command lists](https://learn.microsoft.com/en-us/windows/win32/direct3d12/executing-and-synchronizing-command-lists)
- [Microsoft: swapchains D3D12 associadas à command queue](https://learn.microsoft.com/en-us/windows/win32/direct3d12/swap-chains)
- [Microsoft: integração DirectX/XAML UWP](https://learn.microsoft.com/en-us/windows/uwp/gaming/directx-and-xaml-interop)

## Resultados desta implementação

A validação isolada e a validação da DLL integrada no Intel HD 620 (vendor 8086,
device 5916) passaram com feature level 12_1 e binding tier 3, incluindo
compute/UAV. Foram 103 submissões com fences concluídas, 12 MiB de páginas de
upload e zero erros da camada de debug. A contagem de esperas varia com a
execução. O teste isolado também passou no WARP (vendor 1414, device 008c).

A interface C# ampliada passou usando a DLL integrada: marshaling, buffers,
texturas, compute/UAV, captura e ordem de descarte. O teste existente
`RyuboxNativePresentationSmoke.exe` passou com a mesma DLL: inicialização
D3D11, blits legacy/V2, crop, flip, presents, isolamento do frontend, resize e
duas sessões. A DLL D3D12 isolada importa D3D12/DXGI/D3DCompiler e bibliotecas
do Windows; não importa D3D11On12 nem Vulkan.

Artefato de integração:
`artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`.
Nenhum pacote instalado foi substituído e não foi gerado MSIX anunciando
D3D12 jogável. Não foi executado jogo pelo novo backend.

`dotnet build src/Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release
--no-restore` concluiu com zero erros. Permanecem 15 avisos no código existente
do CoreHost, incluindo serialização/trimming/AOT; não se tratou de um publish
NativeAOT nem de validação do pacote no console.

O script de build nativo também recebeu um filtro explícito por extensão de
header. O Windows PowerShell estava ignorando `-Include` com `-LiteralPath`
nessa enumeração e arquivos cpp/obj podiam invalidar todos os objetos.
