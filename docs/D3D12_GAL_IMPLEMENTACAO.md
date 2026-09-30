# D3D12: integração GAL e programas — estado verificável

Estado da validação local de Rivals em 15/09/2026:
[falhas de ownership, correções e resultados](D3D12_RIVALS_CACHE_20260915.md).
As seções abaixo são históricas; não representam uma certificação atual de
compatibilidade ou desempenho dos jogos.

Revisão das otimizações propostas a partir do Eden em 15/09/2026:
[correções, testes WARP/Intel e limitações DXIL](D3D12_EDEN_REVISAO_20260915.md).

Atualizado em 2026-09-11. Trabalho referente aos itens 1 e 2 de `D3D12_NATIVE_BACKEND.md`.

## Situação

Implementação parcial substancial, **não conclusão integral dos dois itens**.
Todas as interfaces solicitadas têm classes concretas. Os testes executam
trabalho na GPU através da GAL; não são testes que apenas instanciam stubs.
Entretanto, diversas operações ainda geram `NotSupportedException`, e o caminho
de jogos não foi habilitado. Não existe validação de ROM ou de Xbox nesta rodada.
D3D11 permanece disponível e continua sendo a escolha do CoreHost.

**Escopo vigente:** por instrução posterior do usuário, trabalhar somente no
item 1 (contratos GAL). Trabalho adicional do item 2 pausado; partes 3 a 5
suspensas até nova solicitação. A meta solicitada de 80% não foi convertida em
declaração de cobertura sem uma matriz verificável. Guia de conclusão:
[`D3D12_PARTE1_GUIA_CONCLUSAO.md`](D3D12_PARTE1_GUIA_CONCLUSAO.md).
Não habilitar o backend
no frontend/CoreHost nem gerar pacote anunciando suporte a jogos nesta etapa.

## Implementado e exercitado

| Área | Execução real verificada |
| --- | --- |
| `IRenderer` | Buffers, programas, texturas, capabilities conservadoras, sync, captura, counters |
| `IPipeline` | Draw/indexed/instanced; indirect e indirect-count; compute; bindings; viewport/scissor; barreiras; rasterizer discard preservando escritas do VS |
| `ITexture` | RGBA8 2D, upload, sub-região via staging, cópia GPU, readback, vista idêntica com referência compartilhada |
| `IWindow` | Apresentação GPU, escala nearest/bilinear, crop, flip X/Y, aspecto, resize, VSync on/off |
| `IProgram` | HLSL SM5.1, compilação síncrona, reflexão, root signature; cache portátil de fontes com versão |
| Arrays/samplers | Binding de arrays de SRV/sampler e arrays de UAV de textura em draws/dispatches |
| Counters | Queries D3D12 de samples-passed/pipeline-statistics/SO-statistics, acumulação através de submissões, callback/flush |
| SPIR-V | SPIRV-Cross HLSL 5.1 separado do D3D11; store de SSBO com set=4/binding=5 executado na GPU |
| Geometry/tessellation | GS SPIR-V com EmitVertex/EndPrimitive; HS/DS SPIR-V ligados, interfaces por vértice/patch, CBV, especialização do tamanho de patch, cache e pixels conferidos; HS limitado a invocações puras independentes |
| Blend/alpha | Fatores constantes, dupla saída e máscaras do draw; alpha test especializado no shader, mudança de referência e recarga do cache |
| Transform feedback | Xfb de variáveis e membros SPIR-V refletido em layout SO; posições e varying em dois buffers, offsets/strides/gaps, append entre submissões, overflow e counters conferidos |
| Builtins compute | NumWorkgroups remapeado para constantes root exclusivas do compilador; dimensões atualizadas a cada dispatch, incluindo cache |

Os testes de apresentação da base nativa verificam padrão assimétrico 7×5,
padding, crop, flip, escala, resize, descarte antecipado e 90 frames. A bateria
expandida inclui bindings em spaces diferentes, array de buffers, CBV com offset
17 e tamanho 4 (snapshot GPU alinhado), UAV ordering e argumentos indiretos.

O teste C# passa pelas interfaces da GAL, desenha em textura, mantém uma vista
após liberar o handle original, lê/copia o resultado, apresenta e captura. Há
verificação de 64 amostras e uma primitiva em um draw 8×8. Outro teste emite
1.600 draws sem apresentar, exige 102.400 amostras e 1.600 primitivas, exercitando
renovação dos descritores e acumulação das queries.

O padrão assimétrico da GAL também verifica flip X/Y, resize 8×8 → 16×8,
upload de um pixel e blit sobre a própria textura com inversão das linhas.
Esse último usa snapshot GPU para não ler um render target simultaneamente.

O teste de rasterizer discard exige três escritas do vertex shader em UAV e
nenhuma alteração da imagem. Depois reativa a rasterização e exige a imagem
vermelha. Não equivale a suprimir o draw. O teste de alpha usa SPIR-V de
comparação/kill e confere quais pixels sobrevivem para referências 0,5 e 0,25,
inclusive após `IProgram.GetBinary`/`IRenderer.LoadProgramBinary`.

## Decisões importantes

- Cada range refletido preserva stage/register/space/count. Root tables agrupam
  ranges por estágio e por heap de sampler versus CBV/SRV/UAV. Bindings ausentes
  ou incompatíveis são erros explícitos, não descritores de memória aleatória.
- Os PSOs gráficos são cacheados por programa, layout de entrada, topologia,
  rasterizer/discard e blend. O cache limita-se a 256 variantes/programa; a expulsão
  mantém referências das submissões ainda em voo. Não é um cache persistente
  de blobs PSO do driver; o cache portátil de programa armazena HLSL versionado.
- Constantes de blend são estado dinâmico de OM, fora da chave do PSO. RGB com
  fator alpha constante usa splat; misturar simultaneamente color e alpha
  constantes distintos no RGB ainda é rejeitado. Não presume suporte às
  extensões opcionais recentes de blending do runtime.
- Alpha test não é função fixa do D3D12. `GpuAccessor` e
  `DiskCacheGpuAccessor` agora pedem especialização ao `EmitterContext` para
  esse backend. A GAL aceita o estado com programas especializados; HLSL cru
  do dispositivo não recebe essa garantia e continua rejeitado nesse caso.
  O cache portátil mudou para **D2P2**, invalidando fontes anteriores à correção.
  A seleção de alpha test do D3D11 e do OpenGL não foi modificada.
- Atualização subsequente: cache **D2P3** preserva também o layout de stream
  output com cada shader. Fontes antigas sem esse metadado são rejeitadas.
- Captura SO usa quatro slots com contador de bytes próprio. A API valida
  semânticas contra a reflexão do último estágio de geometria e rejeita
  sobreposição SO/IA/UAV. PSOs distinguem captura habilitada/desabilitada;
  recursos de saída e contadores ficam retidos pelas fences existentes.
- `SupportsTransformFeedback` foi mantido **false** até a retomada da parte 2.
  Após validar decorações SPIR-V diretas/agrupadas, counters e overflow em WARP
  e GPU física, ele passou a ser anunciado como verdadeiro nesta rodada.
- A dimensão NumWorkgroups ocupa `b1,space2147483647`, com três root constants;
  nenhum binding guest é utilizado como valor substituto. O teste grava apenas
  pela invocação global zero, evitando corrida de escrita no SSBO.
- Buffers guardam tamanho lógico separado do padding físico de alinhamento.
  Upload/readback, copy/clear, bindings de shader, IA, indirect e SO validam
  ranges lógicos antes de usar o recurso nativo.
- PSOs/root signatures/recursos ficam retidos até a fence; referências de
  programa por submissão são deduplicadas. Descritores não são sobrescritos em
  voo. A GAL submete antes de esgotar slots, terminando os segmentos de queries.
- O auxiliar `SPIRV_Cross_VertexInfo` ocupa `b0,space2147483647`, com dois root
  constants. Draws diretos informam bases; indirect com esse auxiliar monta
  registros `[constantes,draw]` por cópias GPU, sem ler argumentos pela CPU.
- Layout de vértice usa somente entradas efetivamente consumidas, obtidas por
  reflexão. Atributos não usados não exigem buffers. Atributos `IsZero` usam
  float4/uint4/int4 de stride zero e todos os componentes zero, inclusive W.
- Quads, quad strips, fans, line loops, índices de 8 bits e alguns casos de
  restart têm expansão de índices. **Esse fallback faz readback síncrono** quando
  precisa de índices/argumentos guest; não está otimizado. O caminho comum de
  índices 16/32 bits e indirect suportado continua inteiramente na GPU.
- CBVs desalinhados/curtos recebem snapshot alinhado, cópia GPU e padding zero.
  Isso corrige semântica; precisa de pooling adicional antes de alegar desempenho.
- `TargetApi.Direct3D12` foi acrescentado sem alterar valores anteriores. O
  tradutor preserva descriptor sets e a convenção DirectX de transform-disabled.
  A seleção da linguagem exige SPIR-V para D3D12; o cache D3D12 continua usando
  seu próprio `IProgram.GetBinary`, não bytes de outro formato de cache.
- Nenhuma operação foi tratada como sucesso de jogo. Retornos `false` de
  host mapping/conditional rendering são negociação prevista pela GAL para
  acionar fallback, não emulação de suporte. Estados desativados e operações de
  tamanho zero são identidades; modos ativos não implementados geram erro.

## Pendências para fechar os itens 1 e 2

1. Ampliar tessellation além do caminho puro já executado: barreiras/fases,
   dependências entre outputs, escritas externas/atomics e estado privado por
   invocação. O replay atual rejeita esses casos, não os descarta. Completar
   builtins de clip/cull e casos de geometry. Domínios quad/isoline e
   particionamento inteiro/fractional-even/fractional-odd já passaram em pixels
   (ver revisão de interfaces abaixo). Especializações de
   hull que introduzam bindings ausentes no layout inicial exigem relink;
   atualmente são rejeitadas antes do draw. Não há fallback genérico via compute.
2. Ampliar transform feedback: grupos de decorations, agregados complexos,
   builtins restantes e streams de GS; capturas escalares/vetoriais de 32 bits
   já existem, mas a capability global continua desabilitada. Completar
   máscaras de clip,
   pontos largos/sprites e casos ainda rejeitados pelo pipeline.
   Restart de patches (agrupamento por control points) e line-strip-adjacency
   foi adicionado ao fallback e teve os índices conferidos por testes; esse
   teste de patches não equivale a executar tessellation SPIR-V.
   Restart de triangle-strip-adjacency também foi implementado e comparado
   com a montagem nativa na GPU (revisão de interfaces abaixo).
3. Completar formatos/vistas: atualmente somente RGBA8 2D, 1 mip/layer/sample.
   Buffer textures/images tipados, mips, arrays físicos de textura, MSAA,
   depth/stencil, MRT e reinterpret/swizzle permanecem pendentes (sobreposição
   com o item 3 do plano original). Arrays de *bindings* não são texture arrays.
4. Completar sampler address modes sem equivalente direto, blending avançado,
   mistura de fatores constantes distintos no RGB, logic ops com consulta de suporte,
   viewport swizzle/múltiplos viewports. Clear parcial de canais RGBA8 foi
   implementado e testado na revisão exclusiva da parte 1. Não trocar rejeições
   por no-ops para fazer uma ROM parecer inicializar.
5. Exercitar shaders guest, render-scale, cache frio/quente, concorrência de
   compilação/cleanup e device removal em sessões reais. Os testes atuais são
   workloads determinísticos, não garantia de compatibilidade/performance.
6. Reduzir readbacks dos fallbacks, pooling de CBV snapshots e argumentos
   indiretos ampliados. A interface permite testes; ainda não há medição de FPS
   de emulação nem conclusão de que D3D12 será mais rápido no Series S.

Além disso, seleção de backend no CoreHost/UI, ownership com ImGui, NVDEC,
suspensão UWP e validação no console continuam nas etapas posteriores do plano.
Nenhum MSIX foi gerado/instalado nesta rodada.

### Extensão solicitada: partes 3 a 5

**Suspensa por instrução posterior: não iniciar estas partes por enquanto.**

Recebida em 11/09: completar as partes 1/2 e depois iniciar formatos/vistas,
subresources/descritores/alocação e queries/readback/NVDEC. **As partes 1/2
ainda não estão integralmente concluídas; as partes 3–5 não são declaradas
concluídas nem foram habilitadas no CoreHost.** Há sobreposição entre a GAL
de texturas do item 1 e os formatos do item 3; a tabela acima delimita o que
já existe, sem converter suporte parcial em capability geral.

HS/DS SPIR-V puros com interfaces por patch/vértice e stream output com dados e
counters conferidos passaram nos testes descritos abaixo. Os próximos marcos
são tessellation com sincronização/efeitos colaterais e eliminação dos estados
ainda rejeitados. Depois: matriz de formatos/subresources e
resolve, gerenciamento paginado/fences de vida útil, readback assíncrono e
integração de decode. Nenhum desses marcos será validado somente por build.
Os marcos posteriores aos itens 1/2 ficam apenas como registro do plano,
sem autorização atual para iniciá-los.

## Validação integrada de 11/09

- DLL nativa completa compilada, mantendo D3D11 e D3D12.
- Teste D3D12 da DLL integrada com swapchain HWND invisível: **115 submissões
  concluídas, zero erros de debug**, Intel HD 620. Sem janela de jogo aberta.
- Bateria GAL/SPIR-V no hardware: pixels de geometry, alpha especializado,
  cache, blending, cópias, queries e descarte passaram na DLL de validação.
- Bateria completa GAL/SPIR-V na DLL integrada também passou no WARP,
  incluindo geometry, rasterizer discard, alpha especializado e cache D2P2.
- `RyuboxNativePresentationSmoke.exe` com a DLL integrada: **PASS**, inclusive
  blits D3D11, isolamento do frontend, resize e duas sessões.
- CoreHost Release: build concluído sem erros. Build incremental final com
  zero avisos; uma recompilação anterior reportou 15 avisos preexistentes.
  Não é publish NativeAOT, teste de ROM, teste Xbox ou medição de FPS.

Artefato: `artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`.
SHA-256: `A2C2B53A8543097B28B65BC33069B7124A2A90F11D37EC856CC1B912425D670A`.

### Validação posterior — somente itens 1/2

- Stream output passou com posição em membro de bloco SPIR-V e varying em
  variável separada, dois buffers, offsets/strides/gaps diferentes, append
  através de submissões, saturação de capacidade e counter de primitivas
  efetivamente gravadas. Layout preservado na recarga do cache **D2P3**.
- Passaram os testes de NumWorkgroups com dois conjuntos de dimensões e
  reload; verificações de bounds lógicos impediram acessar padding dos CBVs.
- Um fixture com ID bound incorreto provocou falha no SPIRV-Cross. O fixture
  foi corrigido e a checagem de instruções/IDs refletidos passou a ocorrer
  antes do parser nativo. Regressão exige rejeição gerenciada desse módulo.
  Essa proteção é limitada: não substitui um validador completo de SPIR-V.
- Testes de fallback verificaram line-strip-adjacency, agrupamento de patches
  após restart e descarte de primitivas incompletas, sem alegar execução HS/DS.
- DLL integrada recompilada. Regressão D3D11 novamente **PASS**. Testes
  ampliados de GAL/SPIR-V passaram nas rodadas Intel e WARP.
- Build CoreHost sem erros; avisos preexistentes de campos/serialização/AOT
  continuam quando esses fontes são recompilados. Nenhum MSIX ou jogo iniciado.

DLL integrada desta revisão, SHA-256:
`E3D05C94380D859B83D38ED8DE772961FCCF1DA2CF02703F5DEBD705DE1A304D`.
O hash da seção anterior identifica a revisão anterior, não esta DLL.

Essa revisão antecede a implementação HS/DS SPIR-V descrita na seção seguinte.

### Tessellation SPIR-V — revisão seguinte de 11/09

Implementado e verificado nesta rodada:

- Domain shaders: `OutputPatch`, coordenadas baricêntricas, `PatchVertices`,
  `TessLevelOuter`, assinatura de patch e varying de patch. O tamanho do patch
  vem do HS ligado, não do tamanho conservador de 32 posições no SPIR-V.
- Hull shaders: metadados do DS ligados independentemente da ordem dos estágios,
  fatores/outputs por patch e por ponto de controle. O caminho puro reexecuta
  invocações para reconstruir as duas assinaturas HLSL; barreiras, leituras de
  outputs, stores externos/atomics e globals privados são rejeitados. Isso é
  um fallback restrito, não suporte completo a qualquer TCS guest.
- Novo teste com blocos builtin encontrou `gl_in[i].gl_Position` indevido em
  HLSL. O acesso foi corrigido para os arrays HLSL, preservando o índice e a
  classe de armazenamento input/output. A extensão exige opt-in do D3D12;
  o comportamento padrão do compilador compartilhado não foi alterado nesse
  mapeamento.
- Assinatura de entrada HS especializada por tamanho do patch da IA. Cache
  de até 32 variantes por programa, incluído no cache de PSOs. Verificação de
  compatibilidade dos bindings refletidos antes de utilizar cada variante.
- `SetPatchParameters` armazena os defaults enviados pela GAL sem rejeitar a
  atualização. Quando há HS, os fatores escritos pelo shader prevalecem.
  Um draw que precise sintetizar HS usando os defaults continua rejeitado.

Validação real com a DLL integrada e o compilador abaixo: **PASS no WARP e na
Intel HD 620**, incluindo pixels, CBV do HS em `space2/b6`, input/output em
variável simples e bloco builtin, patches IA 3 → 4 → 3, ligação de outputs HS
3/4 com DS, defaults guest diferentes dos fatores escritos pelo shader e
recarga do cache. Testes negativos exigem rejeição de barreiras e dependências
de outputs. A bateria anterior GAL/compute/geometry/SO/alpha também passou.

- DLL nativa integrada compilada; regressão de apresentação D3D11: **PASS**.
- Smoke nativo WARP: 115 submissões/conclusões, zero erros de debug.
- Build da regressão gerenciada: zero erros e zero avisos.
- Build CoreHost Release: zero erros, 15 avisos preexistentes. Não é publish
  NativeAOT nem validação de jogos/Xbox. Não foi gerado/instalado MSIX.

Artefatos desta revisão (não confundir com os hashes históricos acima):

- `artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`:
  `6B19D7800DCF40639DBC48EF5C5E06EBF8678572F12E4731AA3AA6063880ECB8`.
- `artifacts/d3d12-spirvcross-validation/spirv-cross-c-shared.dll`:
  `C2710C70A481ABDBC0797C5ECF5F5E47B894CB2E931B08334F4D5FD4EFEE7EE6`.

O compilador de validação foi gerado em diretório separado; o DLL previamente
usado pelo D3D11/pacote não foi sobrescrito. A tradução tessellation usa a opção
privada `HLSL_TESSELLATION_INPUT_VERTICES` e exige esse compilador atualizado.

**Conclusão de escopo:** partes 3–5 não iniciadas. Houve avanço verificável dos
itens 1/2, mas ainda não estão 100% concluídos. As pendências enumeradas acima
continuam abertas; suporte a HS/DS puro não significa tessellation geral nem
compatibilidade com jogos.

### Interfaces HS/DS, invocações GS e adjacência — revisão seguinte de 11/09

Correções implementadas e verificadas:

- **Ligação HS/DS:** a falha de PSO em quad/isoline vinha de assinaturas de
  control points diferentes quando o DS não lia os outputs do HS. O novo
  `D3D12TessellationLinker` mantém ordem/tipos/arrays/semânticas do produtor,
  preserva os nomes usados pelo consumidor e inclui campos não utilizados.
  Entradas sem produtor e tipos incompatíveis são rejeitados antes do PSO.
  Não substitui leituras por zero nem modifica instruções do shader.
- **Quad/isoline:** seis combinações de domínio e particionamento
  inteiro/fractional-even/fractional-odd passaram em pixels, inclusive DS que
  lê somente dados de patch. O fixture de linhas usa deslocamento de meio
  pixel para não coincidir com empates de rasterização; não é uma alteração
  da orientação ou das coordenadas de jogos no backend.
- **Instancing de GS:** o tradutor ignorava `ExecutionModeInvocations`. Um
  teste de três invocações produzia 64 amostras em vez de 192. Agora emite
  `[instance(N)]` e recebe `SV_GSInstanceID` como argumento direto, não como
  campo por vértice. Testados 1/3/32, com e sem leitura do ID, conferindo
  contagem real de amostras e cor da última invocação. Counts 0/33 rejeitados.
- **GS sem entrada por vértice:** o wrapper declara estrutura vazia com o
  qualificador da primitiva, sem inventar binding do estágio anterior.
  Helpers não recebem uma estrutura de entrada inexistente. As combinações
  acima também passam sem leitura de posição/atributos de entrada.
- **Separação compute/graphics:** a checagem de patch sem HS estava em
  `DispatchCompute`. Movida para o draw. O teste executa compute mantendo
  patches como última topologia gráfica e confere o SSBO; draw de patches
  sem HS é rejeitado antes de enviar comandos inválidos.
- **Restart triangle-strip-adjacency:** expansão preserva os seis vértices
  recebidos pelo GS, incluindo vizinhos de arestas, paridade e leading vertex.
  Teste HLSL grava esses seis índices num UAV e compara GPU nativa, expansão
  CPU e draw pela GAL com fallback: comprimentos 0..17, trechos incompletos,
  múltiplos reinícios e índices não sequenciais. PASS na Intel e no WARP.
  Esse fallback ainda usa o readback síncrono existente; não há ganho de
  desempenho alegado.
- **Cache D2P4:** invalida HLSL anterior às correções de linkage/GS. O teste
  recarrega o cache novo e exige rejeição do D2P3, sem apagar arquivos do usuário.
- **Diagnóstico nativo:** falha de criação de PSO propaga mensagens da debug
  layer até a exceção gerenciada. Foi assim que a incompatibilidade HS/DS
  foi identificada, em vez de tratar apenas o HRESULT genérico.

Validação final desta revisão:

- Bateria GAL/SPIR-V completa: **PASS Intel HD 620 e WARP**, com a DLL
  integrada, sem erros de debug reportados pelos testes.
- Smoke nativo integrado: **115 submissões/115 conclusões, zero erros de
  debug**, em ambos os adaptadores.
- Regressão de apresentação D3D11: **PASS**, incluindo duas sessões, resize
  e isolamento do frontend.
- Testes negativos de linkage: campos ausentes, tipos/arrays incompatíveis,
  colisões e publicação parcial rejeitados; linkage idempotente conferido.
- Compilação CoreHost Release sem erros; avisos preexistentes de campos,
  serialização e NativeAOT continuam. Build comum não equivale a publish AOT.
  A recompilação completa de CoreHost teve 15 avisos preexistentes; a última
  incremental, após o fallback de adjacência, teve zero erros e zero avisos.

Artefatos atuais (os hashes nas seções anteriores são históricos):

- `artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`:
  `E2238C34170568E415C8F92AF2DB25FA60840DA49E55EB178EA09B722C5E9A18`.
- `artifacts/d3d12-spirvcross-validation/spirv-cross-c-shared.dll`:
  `82873DD808AA7C10F3D105D11FB47BB5B9A54D244D5DB42DCB3BB1BC015DBF76`.

Referências de implementação: [instancing GS no HLSL](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/overviews-direct3d-11-hlsl-gs-instance),
[topologias DirectX](https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/primitive-topologies)
e tabela 10.2 da [especificação OpenGL 4.6](https://registry.khronos.org/OpenGL/specs/gl/glspec46.core.pdf).

**Não é conclusão de 100%:** continuam pendentes tessellation geral com
barreiras/dependências/efeitos colaterais, cobertura restante de GS/SO,
estados GAL ainda rejeitados e validação de shaders guest. Partes 3–5
continuam suspensas. D3D11 permanece selecionado; nenhum jogo, MSIX ou
desligamento nesta rodada.

### Rodada exclusiva da parte 1 — contratos e ciclo de vida GAL

- `ClearRenderTargetColor` aceita todas as 16 máscaras RGBA no alvo 2D atual.
  Máscara completa usa clear nativo; parcial usa draw utilitário sem blending,
  com write mask; zero é identidade. O draw não modifica estado guest nem
  incrementa queries guest. Camadas/depth/stencil/MRT continuam pendentes.
- Texturas e todas as vistas registradas no renderer; `Release` remove cada
  handle do registro. Um teste cria/libera 200 pares e exige contagem zero.
  Vistas continuam válidas após liberar a textura original, mas são invalidadas
  no encerramento do renderer mesmo se o chamador esquecer de liberá-las.
- Cópia textura → buffer valida o destino inteiro antes do readback/upload.
  Range inválido não pode modificar a primeira linha antes de falhar na próxima.
- Handles de buffer GAL não reutilizam IDs nativos locais ao device: dois
  renderers não podem confundir buffers com o mesmo número nativo.
- Cleanup tenta liberar todos os recursos mesmo após uma falha de callback,
  compilação concorrente de programas é serializada com a liberação do
  compilador, e exceções de cleanup são preservadas em `AggregateException`.
  Isso não é prova de resistência a qualquer device removal ou concorrência
  arbitrária de gravação de comandos; estes testes continuam necessários.
- Samplers validam enums, bias/LOD/anisotropia finitos e intervalos válidos.
  Filtros sem mip selecionam nível zero mesmo se o range informado começar
  acima dele. Modos de endereçamento que exigem emulação continuam rejeitados.
- `GetCurrentSync` agora avança somente após a conclusão da fence nativa.
  Antes devolvia o último ID enviado. IDs guest são separados de fences,
  waits de IDs já concluídos funcionam e registros concluídos são retirados.
  Teste determinístico cobre 10.000 sincronizações e não depende da velocidade
  da GPU. A gravação continua tendo um único proprietário de thread.

Testes novos: `GalContractTests.cs` e `SyncTrackerTests.cs`, no projeto
`eng/D3D12InteropRegression`. Testes já existentes da parte 2 são executados
como regressão, sem desenvolver novas traduções/shaders guest nesta rodada.
Resultado: bateria completa **PASS Intel HD 620 e WARP**; regressão de
apresentação D3D11 **PASS**. CoreHost Release compilou sem erros (15 avisos
preexistentes, inclusive no último build desta rodada).
As DLLs nativas/compilador não foram alteradas nesta revisão gerenciada;
os hashes da revisão anterior permanecem válidos.

**Status:** progresso implementado, mas 80%/100% da parte 1 não foi comprovado.
As estimativas percentuais anteriores eram aproximadas, não uma medição de
cobertura. O guia separa cada área pendente e os testes de aceitação para
concluir sem transformar rejeições em no-ops ou anunciar capabilities falsas.

### Início controlado da parte 2 — SPIR-V/HLSL e PSO

Rodada de 13/09/2026, mantendo D3D12 fora do CoreHost e sem executar jogos:

- `FragmentOutputDepth` foi coberto por regressão real: SPIR-V `FragDepth`
  traduzido para HLSL `SV_Depth`, PSO com alvo D32, escrita de cor e profundidade
  conferidas por readback de pixels/texels, incluindo recarga do cache.
- Transform feedback deixou de rejeitar `OpDecorationGroup`, `OpGroupDecorate`
  e `OpGroupMemberDecorate` quando usados para metadados Xfb. O leitor agora
  expande os grupos antes de montar o layout SO, validando conflitos e IDs.
- A regressão executa os dois caminhos, decoração direta e agrupada, capturando
  os mesmos dados em dois buffers com offsets/strides/padding diferentes,
  append entre submissões, query de primitivas escritas e overflow.
- O cache de shader D3D12 ficou explícito no `ShaderCache`: programas D3D12
  persistem o `IProgram.GetBinary()` próprio do backend, em vez de depender do
  caminho implícito usado por APIs que empacotam fontes.
- `supportsVertexStoreAndAtomics` foi habilitado para D3D12 somente após teste
  real de `RWByteAddressBuffer.InterlockedAdd` em vertex e geometry shader.
  Isso remove uma queda conservadora para conversão VTG→compute que prejudicava
  desempenho, sem anunciar uma capability não validada.
- `SupportsTransformFeedback` agora é anunciado como verdadeiro. A validação
  cobre stream output nativo, counters, overflow e as decorações SPIR-V diretas
  e agrupadas, então o translator não precisa reservar o caminho de emulação
  quando o jogo ativa transform feedback.

Validação:

- `dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore --nologo -v:q`: PASS, 0 avisos, 0 erros.
- Bateria `D3D12InteropRegression` com a DLL integrada: PASS no WARP e PASS na
  GPU física, incluindo `SPIR-V FragDepth`, `SPIR-V direct/grouped Xfb` e
  stores/atomics em estágios gráficos; repetida após ativar
  `SupportsTransformFeedback=true`.
- `dotnet build src/src/Ryujinx.Graphics.D3D12/Ryujinx.Graphics.D3D12.csproj -c Release --no-restore --nologo -v:q`: PASS, 0 avisos, 0 erros.
- `dotnet build src/src/Ryujinx.Graphics.Gpu/Ryujinx.Graphics.Gpu.csproj -c Release --no-restore --nologo -v:q`: PASS, 0 avisos, 0 erros.
- `dotnet build src/Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release --no-restore --nologo -v:q`: PASS, 0 avisos, 0 erros.

Isso é avanço real da parte 2, mas ainda não habilita testes de jogos por D3D12:
faltam shaders guest em sessão CoreHost, seleção de backend segura, cobertura de
formatos/subresources e validação de apresentação/input no caminho UWP.

## Reprodução

```powershell
.\eng\Test-RyuboxD3D12.ps1 -DebugLayer
.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Warp

.\eng\Build-SpirvCrossUwp.ps1 `
  -OutputRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation' `
  -IntermediateRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-obj'

dotnet run --project .\eng\D3D12InteropRegression\D3D12InteropRegression.csproj -c Release -- `
  'E:\ports\ryujinx uwp\artifacts\d3d12-native-validation\Ryujinx.D3D12.Validation.dll' `
  'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll'

.\eng\Build-RyujinxUwpNative.ps1 -Configuration Release `
  -OutputRoot 'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native'

# A bateria gerenciada também aceita --warp depois do caminho SPIRV-Cross.
dotnet run --project .\eng\D3D12InteropRegression\D3D12InteropRegression.csproj -c Release -- `
  'E:\ports\ryujinx uwp\artifacts\d3d12-integrated-native\Release\Ryujinx.Uwp.Native.dll' `
  'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll' --warp
```

Não misturar uma DLL anterior com as novas estruturas de draw: os exports novos
fazem parte deste desenvolvimento e não foram publicados em MSIX. Os scripts
recompilam os testes nativos contra o header atual.

## Caminho Texture3D nativo — 17/09/2026

Foi implementado o caminho D3D12 para texturas 3D de cor não comprimidas, sem
fallback D3D11:

- recursos `D3D12_RESOURCE_DIMENSION_TEXTURE3D`, mips e vistas por faixa de
  mip e de fatias W, mantendo o armazenamento pai vivo;
- upload, readback e cópia por subresource com `rowPitch`, `depthPitch`, XYZ e
  extensão 3D;
- descritores `TEXTURE3D` para SRV e UAV e RTVs com `FirstWSlice/WSize`;
- transições agrupadas pelo recurso físico quando várias vistas apontam para o
  mesmo volume;
- clears RGBA de intervalos de fatias e clears com máscara parcial. Estes
  últimos usam uma RTV de uma fatia por draw para preservar os canais não
  selecionados;
- o caso observado em Pokémon Legends Z-A, `R16Float 64x64x6`, possui teste
  explícito de criação, upload e readback.

`Texture3DTests` confere bytes/pixels para três mips, upload parcial, cópia Z,
vistas, amostragem SRV, escrita UAV, RTV de uma fatia, clear de duas fatias,
clear mascarado e o caso da fatia zero. Resultado: **PASS no WARP e na GPU
física**, com build gerenciado em 0 erros e 0 avisos. O publish NativeAOT também
foi concluído; os avisos de trimming/AOT exibidos são preexistentes.

No primeiro reteste real, o jogo ultrapassou o erro original de criação da
Texture3D e só parou depois em `layered render-target clear`, o que confirmou
que o caminho de recurso 3D foi exercitado. Esse segundo bloqueio foi então
implementado e coberto pelos testes acima. As três tentativas posteriores de
reteste do jogo não chegaram à GPU: o Windows encerrou o processo em
`VirtualAlloc(MEM_COMMIT)` com erro 1455, devido ao limite de commit/pagefile do
PC. Portanto, há prova automatizada do caminho 3D e da correção do clear, mas
não há captura final do jogo desta rodada.

Runtime AOT de validação:
`artifacts/direct-d3d12-host-pokemon-legends-za-3d-layered/`.

- `Ryujinx.Uwp.CoreHost.dll` SHA-256:
  `171D7FB76438AE76AC1AB201C671CE5BBA5658CDB4ABA7FCAFCC1E2338610C1D`
- `Ryujinx.Uwp.Native.dll` SHA-256:
  `A397ADD5E21B514A769B1122E2FD13CCF0C0ABB8F3F859AD37BC39F64A7CC503`
- `Ryujinx.Graphics.D3D12.dll` SHA-256:
  `2683DA5BE55590F706C1CA397FCD4E2B125AAAC512B7DAC7FB00B3D8006C7BA1`

O script `Test-D3D12FrontendCoreHost.ps1` agora aceita
`-MemoryLimitGiB 1..5` para diagnóstico local; o padrão de produção continua
em 5 GiB. Compressão 3D permanece anunciada como não suportada, coerente com
`supports3DTextureCompression=false`, e não foi substituída por conversão ou
fallback silencioso.
