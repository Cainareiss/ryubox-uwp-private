# Parte 1 D3D12 — estado atual e guia para concluir a GAL

Atualizado em 13/09/2026. Este é um roteiro de implementação e aceitação,
não um certificado de conclusão ou compatibilidade com jogos.

## Autorização de dependências — 13/09/2026

O usuário autorizou implementar as dependências das partes 2 e 3 necessárias
para encerrar a parte 1. O restante dessas etapas continua pausado. Essa
autorização substitui as restrições históricas abaixo relativas a formatos,
vistas e emulação em shaders necessários aos contratos GAL.
Continuam fora desta rodada: ativação D3D12 no CoreHost, jogos, MSIX,
instalação e desligamento. D3D11 deve permanecer disponível e sem regressão.

Primeiro bloco dessa autorização: 13 formatos de cor 2D, layouts de
upload/readback por formato e cache de PSOs por formato de render target.
Veja [implementação e testes](D3D12_PARTE1_DEPENDENCIAS_AUTORIZADAS.md).
Segundo bloco: D16/D32 como depth attachment/SRV, clear/copy/readback/upload
controlado, draw depth-only, PSO com DSV e comparação de profundidade validada
em WARP e Intel.
Stencil e formatos depth/stencil combinados continuam pendentes.
A tabela histórica abaixo descreve o ponto de partida, anterior a essa ampliação.

## Revisão posterior ao relatório Gemini — 11/09/2026

Foram corrigidos suspensão de janela, cancelamento de ações GPU pendentes,
culling conforme a saída GS/tessellator, validação atômica de estados e corridas
de cleanup/counters. Os testes foram reforçados com pixels, conteúdo dos arrays,
concorrência e falha nativa controlada por orçamento.
Consulte [resultados e pendências atuais](D3D12_PARTE1_REVISAO_GEMINI.md).
As tabelas e tarefas abaixo são o roteiro original: algumas foram exercitadas
nessa revisão, mas as dependências de formatos/shaders permanecem pendentes.
Não há base para certificar 95% ou 100% da parte 1 inteira.

## Escopo e critério de 100%

A parte 1 abrange `IRenderer`, `IPipeline`, `ITexture`, `IWindow`, `IProgram`,
arrays, samplers e counters. Todas têm implementação concreta, mas isso não
significa que todos os seus contratos e modos estejam cobertos.

Instrução vigente: focar na parte 1 e em suas dependências autorizadas nas
partes 2/3, sem iniciar trabalhos independentes dessas etapas. Não habilitar D3D12
no CoreHost, iniciar jogos, gerar MSIX ou desligar o PC nesta etapa.

Há sobreposição real com as partes 2 e 3: formatos completos pertencem à
parte 3, mas limitam `ITexture`; modos que dependem de emulação em shaders
limitam `IPipeline`/samplers. Não retirar essas lacunas do denominador para
anunciar 100%. Registrar as dependências implementadas; a autorização de
13/09/2026 cobre as necessárias aos contratos da parte 1.

As estimativas de 60%/70%/65% dadas anteriormente não vieram de uma contagem
ponderada. Não é possível concluir que esta rodada atingiu exatamente 80%.
Para medir a próxima rodada, congele uma matriz de operações e variantes,
separe obrigatório/opcional negociável/dependência, e só conte como aprovado
um caso com teste de resultado e evidência preservada. Quantidade de métodos
ou linhas de código não mede completude.

## O que está entregue

| Área | Trabalho exercitado | Ainda impede conclusão integral |
| --- | --- | --- |
| Renderer/buffers | Upload/readback/copy, ranges lógicos, handles separados por renderer, cleanup com falha de callback | Stress de concorrência, falhas nativas e política de `BufferAccess` |
| Pipeline | Draw/indexed/instancing/indirect, bindings, fallbacks de topologia, clear RGBA com todas as máscaras | Modos raster/depth/stencil/MRT/blend/viewport ainda rejeitados |
| Textura | RGBA8 2D, upload parcial, blit, vistas idênticas, liberação/rastreamento, cópia para buffer com pré-validação | Demais formatos, mip/layer/sample, buffer texture, reinterpret/swizzle |
| Janela | Crop/flip/resize/aspecto, nearest/bilinear, VSync simples | AA, filtros restantes, VSync customizado, color-space passthrough |
| Programa | Criação/reflexão/cache existentes, vida útil e lock compilação/dispose | Stress de cancelamento/falha/concorrência; parte 2 continua parcial |
| Arrays/samplers | Arrays de bindings SRV/sampler/UAV, enums/ranges validados, filtros sem mip | Casos de arrays separados/ausentes e endereçamentos emulados |
| Counters/sync | Queries reais, callbacks/flush, IDs guest só concluídos após fence, descarte de registros | Corridas com dispose/dispatcher, perda de device, waits interrompidos |

Testes da nova rodada: 200 ciclos de textura/vista; falha de callback durante
dispose; buffer estrangeiro; cópia inválida sem modificação parcial; máscaras
0..15 com conferência de cada byte; preservação do estado e de queries guest;
samplers inválidos; linha do tempo de 10.000 syncs.

Evidência desta rodada: bateria completa aprovada no WARP e na Intel HD 620,
regressão de apresentação D3D11 aprovada, build CoreHost Release sem erros.
Não houve teste de jogo, publish NativeAOT, MSIX ou validação no Xbox.

## 1. Auditar os contratos antes de modificar

Ler as interfaces em `src/src/Ryujinx.Graphics.GAL/` e as implementações em
`src/src/Ryujinx.Graphics.D3D12/`. A fronteira nativa fica em
`src/Ryujinx.Uwp.Native/D3D12Backend.{h,cpp}`, `D3D12Gal.inl` e
`D3D12Program.inl`.

```powershell
rg -n 'NotSupportedException|Unsupported\(|throw |=> false' src/src/Ryujinx.Graphics.D3D12
rg -n '^\s*(public |void |bool |ITexture |IProgram |BufferHandle )' src/src/Ryujinx.Graphics.GAL/IRenderer.cs src/src/Ryujinx.Graphics.GAL/IPipeline.cs src/src/Ryujinx.Graphics.GAL/ITexture.cs src/src/Ryujinx.Graphics.GAL/IWindow.cs
```

Para cada operação registrar: entrada válida, resultado esperado, estado
modificado, sincronização, ownership, exceções, fallback negociado e teste.
Comparar os consumidores da GAL, não apenas a implementação D3D11.

`PrepareHostMapping=false`, sparse capability desabilitada e tentativa de
conditional rendering retornando false podem acionar fallbacks previstos.
Não transformar métodos obrigatórios em sucesso vazio. Também não implementar
host import como uma cópia pontual: o contrato requer coerência de memória.

## 2. Fechar ciclo de vida e sincronização

Arquivos: `D3D12Renderer.cs`, `D3D12SyncTracker.cs`, `D3D12CounterEvent.cs`,
`D3D12GalTexture.cs`, `D3D12NativeDevice.cs`.

1. Exercitar criação de programas em background concorrente com dispose.
   O lock deve garantir conclusão ou erro previsível sem publicar objeto órfão.
2. Injetar falhas em criação, upload, submissão, leitura de query e callback.
   Verificar que a última falha não pula a liberação do device ou de recursos.
3. Validar `RunOnGpuThread`: callback entregue, rejeitado, atrasado e após
   encerramento. Um timeout não deve permitir comandos tardios em nova sessão.
4. Exercitar `ReportCounter`, `Invalid`, `ReserveForHostAccess`, `Flush`,
   `ResetCounter`, descarte do evento e callback exatamente uma vez.
5. Testar leitura de sync numa thread diferente enquanto a thread GPU cria
   novos IDs. Não reportar uma fence enviada como concluída.
6. Medir recursos vivos após ciclos de sessão. O teste atual da lista de vistas
   não substitui medição de allocations nativas e memória após device removal.

Aceitação: sem use-after-free, deadlock, callback duplicado, perda silenciosa
de erro ou crescimento persistente de recursos. Dispose de comandos continua
obedecendo à thread proprietária; não presumir segurança multithread universal.

## 3. Completar os estados de pipeline sem falsificar draws

Arquivo principal: `D3D12Pipeline.cs`; conversões/PSOs em `D3D12Program.inl`.

1. Inventariar culling frente+trás, polygon mode assimétrico, wide lines,
   point sprites, depth bias por modo, clip masks e blend/logic ops.
2. Para suporte nativo, traduzir o estado, incluí-lo na chave do PSO quando
   necessário e consultar suporte do device antes de anunciar capability.
3. Para emulação necessária em shaders, registrar dependência da parte 2;
   não iniciar silenciosamente nem trocar rejeição por ignorar o estado.
4. Não tratar culling de ambas as faces como pular o draw: VS/GS podem ter
   efeitos colaterais. Testar escritas em UAV/SO mesmo sem pixels visíveis.
5. Testar clear parcial intercalado com draws e queries abertas, além de
   render targets descartados antes da submissão. O clear parcial atual é
   de canais RGBA8, não depth/stencil, múltiplas camadas ou novos formatos.
6. Validar combinações e transições: enable→disable→enable, programa trocado,
   alvo trocado, cache de PSO e submit automático por limite de descritores.

Aceitação: pixels e efeitos laterais corretos, zero mensagens de erro na debug
layer e estado guest preservado após operações internas.

## 4. Texturas e vistas: registrar a dependência da parte 3

Arquivos: `D3D12GalTexture.cs`, descrições nativas de recursos e conversões de
formato. As dependências necessárias à parte 1 foram autorizadas em 13/09/2026.

Implementar e validar dentro dessa autorização:

1. Criar tabela explícita GAL→DXGI, bloco comprimido, bytes, planos, flags e
   suporte a SRV/UAV/RTV/DSV. Não reinterpretar todos os formatos como RGBA8.
2. Levar layer/mip/plane/sample por toda a ABI e manter estado por subresource.
3. Criar vistas com formato/range validado e ownership compartilhado real.
4. Implementar buffer textures e `SetStorage` com ranges/alinhamento e vistas
   tipadas; não confundir arrays de bindings com arrays físicos de textura.
5. Cobrir upload/readback com row pitch, padding, recorte, BCn e resolve MSAA.
6. Validar todos os formatos anunciados com round-trip e amostragem na GPU.

Aceitação: nenhuma capability de formato anunciada sem testar suas operações
relevantes. Enquanto isso permanecer parcial, `ITexture` não está 100% geral.

## 5. Arrays e samplers

Arquivos: `D3D12Sampler.cs`, `D3D12Pipeline.Bindings`, `D3D12Program.inl`.

1. Ampliar testes de arrays com offset não zero, múltiplos elementos e sets
   separados. Usar cores distintas para detectar binding trocado.
2. Definir tratamento de slots nulos e recursos liberados conforme os
   consumidores GAL: erro previsível ou descritor nulo real, nunca ID aleatório.
3. Conferir ausência de mutação parcial em setters inválidos.
4. Testar filtros, bordas, comparação e anisotropia por resultado amostrado,
   não apenas pelo valor numérico do descriptor.
5. `Clamp`, `MirrorClamp` e `MirrorClampToBorder` continuam dependentes de
   emulação. Não substituir por `ClampToEdge` indiscriminadamente.
6. Cubemap/seamless e LOD de mips dependem da matriz de texturas ainda pendente.

Referência: [D3D12_SAMPLER_DESC](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_sampler_desc)
e [limites oficiais D3D12](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/direct3d12/constants.md).

## 6. Concluir a janela

Arquivo: `D3D12Window.cs`; blit em `D3D12Renderer.cs`.

1. Mapear todos os valores dos enums de AA/scaling/VSync aceitos pela UI.
2. Reaproveitar algoritmos de filtros existentes, portando somente a operação
   de apresentação. Não ativar enum cujo processamento não existe.
3. Especificar VSync customizado com sua fonte de frequência/tempo; o método
   atual recebe apenas o modo. Não inventar um limite de 30/60 FPS sem parâmetro.
4. Tratar minimização, tamanho zero transitório, resize repetido, DPI e
   aspect ratio sem recorte acidental; manter padrão assimétrico na captura.
5. Color-space passthrough depende do formato/swapchain: exigir suporte real.
6. Callback de apresentação deve ocorrer na ordem documentada. Contadores de
   FPS futuros não podem contar apenas chamadas ao método como frames visíveis.

## 7. Programa: concluir o contrato, pausar desenvolvimento da parte 2

Arquivos: `D3D12Program.cs`, `D3D12Renderer.cs`.

1. Testar criação e descarte repetidos, cache truncado/incompatível, programa
   descartado ainda em voo e erro nativo propagado com diagnóstico.
2. Auditar `ProgramCount` durante compilação, descarte e cleanup.
3. Preservar D2P4 e não aceitar fontes antigas defeituosas para evitar recompilar.
4. `CheckProgramLink` síncrono pode retornar sucesso porque a criação terminou;
   não anunciar compilação assíncrona sem implementar estado/cancelamento.
5. Limitações de tradução HS/DS/GS continuam na parte 2. Reexecutar seus testes
   como regressão não significa avançar seu escopo nesta rodada.

## 8. Como compilar e validar

Executar na raiz `E:\ports\ryujinx uwp`. Não rodar jogos para esses testes.
Manter compilador de validação separado daquele usado pelo pacote D3D11.

```powershell
# Necessário se alterar a fronteira C++/ABI; não reutilizar DLL antiga nesse caso.
.\eng\Build-RyujinxUwpNative.ps1 -Configuration Release `
  -OutputRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native'

# Só reconstruir SPIRV-Cross se o código do compilador tiver mudado.
.\eng\Build-SpirvCrossUwp.ps1 `
  -OutputRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation' `
  -IntermediateRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-obj'

dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore

dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll `
  'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll' `
  'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll' --warp

# Repetir sem --warp para a GPU física.
dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll `
  'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll' `
  'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll'

dotnet build src/Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release --no-restore

.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Warp `
  -NativeDll 'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll'

& 'E:\ports\ryujinx uwp\artifacts\presentation-smoke\RyuboxNativePresentationSmoke.exe' `
  'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll'
```

Critério por alteração: primeiro reproduzir, implementar, conferir resultado,
verificar regressões e registrar comando/adapter/resultado. Não desabilitar
debug layer ou remover teste que falha para obter PASS. Build CoreHost comum
não é publish NativeAOT, instalação UWP ou teste no Xbox.

## 9. Checklist de encerramento

- [ ] Matriz de operações/variantes congelada e toda lacuna obrigatória resolvida.
- [ ] Dependências com partes 2/3 explicitamente autorizadas ou ainda marcadas
  como pendentes; não anunciar 100% global se foram apenas adiadas.
- [ ] Fallbacks opcionais recusados corretamente e testados nos consumidores.
- [ ] Zero stubs/no-ops para operações com efeito observável.
- [ ] Capabilities compatíveis com o comportamento real e limites do device.
- [ ] Testes de pixels, buffers, counters, erros e vida útil Intel/WARP aprovados.
- [ ] Stress de concorrência, cleanup e falhas nativas concluído.
- [ ] Regressão D3D11 aprovada; CoreHost/frontend mantidos sem ativação prematura.
- [ ] Evidência de UWP/NativeAOT e Xbox antes de alegar funcionamento no console.

Somente após fechar esses critérios revisar o status de conclusão da parte 1
e retomar a parte 2 com autorização do usuário.
