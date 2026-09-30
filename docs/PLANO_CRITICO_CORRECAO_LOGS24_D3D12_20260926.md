# Logs 24 — plano crítico de recuperação D3D12

Analisado em 26/09/2026 a partir de `E:\Logs (24).zip` (45 arquivos; nove
sessões de oito títulos). Este documento separa evidência do log, hipóteses e
testes de aceitação. Não afirma que os problemas estejam corrigidos nem que
qualquer jogo esteja sustentado a 60 FPS.

## Resumo executivo

O ZIP confirma dois encerramentos fatais por orçamento de memória — Pokémon
Legends: Z-A e Rivals of Aether —, além de pausas longas no caminho de
syncpoint/GPU, compilação de shaders/PSOs cara e baixa taxa de callbacks de
apresentação em vários títulos. Alien Hominid e Horizon Chase chegaram a
produzir atividade de apresentação, mas seus registros não comprovam pixels
corretos nem desempenho estável. A telemetria de apresentação nativa está
cruzada com D3D11, então parte dos zeros é inválida como medida de D3D12.

Não há evidência de falta de capacidade do descriptor ring: nas sessões
registradas `pressureDelta=0`, `ringWaitsDelta=0` e `failures=0`. Há, contudo,
churn de cópias de descritores. Aumentar o ring ou o teto de memória sem medir
não é uma correção sustentada pelos dados.

## Estado da Parte 1 — P0 de telemetria concluído em 26/09/2026

O problema de instrumentação identificado abaixo foi corrigido depois da coleta
do ZIP. O relatório dos jogos continua representando fielmente a build antiga;
os logs antigos não foram reescritos e os zeros de `nativePresent` neles seguem
sem valor diagnóstico para D3D12.

- O CoreHost consulta os contadores D3D12 quando a sessão usa D3D12 e só chama
  `Native_D3D11_GetPresentationTelemetry` em sessões D3D11.
- O ABI D3D12 de estatísticas foi atualizado para a versão 3: tentativas e
  HRESULTs bem-sucedidos de `IDXGISwapChain::Present`, último HRESULT, dimensão
  da surface, fence/QPC do último Present e tipo de alvo (offscreen, HWND ou
  painel de composição).
- O log `performance.log` preserva o campo histórico `callbacks` para não
  quebrar ferramentas existentes e acrescenta `presentAcceptedFps`. O log
  distingue `d3d12Present=sucessos/tentativas`, sequência, HRESULT, fence e
  surface. `FirstGuestPresentCallback` não é mais rotulado como frame visível.
- `HasFirstVisiblePresent` mantém o nome exportado por compatibilidade, mas
  para D3D12 consulta um Present DXGI bem-sucedido. Isso comprova aceitação pelo
  DXGI, não pixels não pretos nem visibilidade física; a captura continua sendo
  uma verificação separada.
- O contador visual do frontend D3D12 só avança para contexto com swapchain;
  callback GAL headless não é contado como apresentação.

**Validação executada:** build nativa Release concluída; build CoreHost Release
concluída; `D3D12InteropRegression --presentation-only --warp` aprovou quatro
Presents DXGI, resultado/HRESULT/fence/QPC/tipo de alvo/dimensões e pixels
assimétricos nas quatro combinações de flip com debug layer;
`D3D12FrontendRegression --warp` aprovou
três ciclos e confirmou que callback headless não cria contagem DXGI. O parser
PowerShell de `Measure-RyuboxSession.ps1` passou na análise sintática. Builds
gerenciadas emitiram avisos NU1900 por falha ao acessar o feed de auditoria
NuGet e um warning preexistente de evento não usado, sem erros de compilação.

**Limite desta etapa:** ainda não houve sessão real de jogo com esta build nem
teste no Xbox/UWP. `PresentAcceptedFps` é taxa de HRESULT não-falho do DXGI, não
GPU frame time nem prova de pixels mostrados; a validação de conteúdo precisa
da captura assimétrica e de screenshots reais nas próximas fases.

## P1 — implementação local e regressões concluídas em 26/09/2026

Auditoria da implementação existente confirmou que `MapPhysicalMemory` reserva
e conta somente `remainingSize` (trechos ainda `Unmapped`); um intervalo já
totalmente mapeado retorna sem alocar páginas novas. A memória guest realmente
committed pelo host continua medida separadamente da reserva física guest.
Acrescentei diagnóstico para pedidos grandes (a partir de 64 MiB) e todas as
recusas, com bytes virtuais solicitados, bytes previamente mapeados, páginas e
bytes físicos novos, uso físico antes/depois, commit guest do host, amostra de
uso do processo e limite do recurso. A decisão de quota e o limite de 5 GiB não
foram relaxados.

O caminho JIT já tinha reclaim frio e telemetria; as regressões executadas
confirmaram que readers/pins e épocas impedem liberar código ainda executável,
que o último pin libera código aposentado uma única vez e que há recuperação
por eviction antes de cancelar uma admissão. Não alterei teto, perfil de
dispatch, nem removi a proteção contra OOM.

**Validação local:** `JitCacheRegression` passou 162 verificações, incluindo
dispatch nativo/gerenciado, invalidação, reclaim/épocas, 600 MiB de mapeamento
reutilizados, pressão de memória e 100 sessões sequenciais;
`GuestCommitRegression` passou os limites, overflow, entradas inválidas e
recusa segura; `Ryujinx.HLE` Release compilou com 0 erros. Permaneceram avisos
NU1900 porque o feed privado de auditoria NuGet não estava acessível.

**Aceitação de jogo ainda pendente:** Pokémon Legends: Z-A foi retestado em
27/09; foi encontrada e removida a seleção automática de DRAM guest de 6 GiB
por título/nome de arquivo. A cota host de 5 GiB foi mantida. Consulte a
[correção de configuração e evidências do reteste](POKEMON_ZA_RETAIL_MEMORY_20260927.md).
Isso não certifica o aceite completo da P1. O reteste de Rivals desta build está registrado abaixo, mas não
chegou a desenhar; portanto, não demonstra correção nem satisfaz o aceite de
sessão real do P1.

### Reteste de Rivals a partir do SSD interno — 26/09/2026

O CoreHost D3D12 NativeAOT atual e a DLL nativa validada foram colocados em
runtime isolado `artifacts/direct-d3d12-rivals-ssd-p1-20260926/`. O NSP foi
aberto pelo caminho `C:\Users\Cainã\Downloads\Rivals of Aether
[0100D2700DE7C000][v0].nsp`; o `loader.log` confirmou esse caminho. O launcher
foi estendido com `-RuntimeDirectory` para evitar usar a cópia antiga do
pacote. Smoke de apresentação WARP no runtime isolado passou antes da sessão.

Sessão visível de aproximadamente cinco minutos, limite de 5 GiB, sem input
automático. Resultado: `ProcessFrame=3`, `draws=0`, `Present=0/0`, nenhuma
captura de primeiro frame. No último sample: commit privado
2.521.583.616 bytes (~2,35 GiB), working set 1.170.235.488 bytes (~1,09 GiB),
JIT committed 205.389.824 bytes, reclaim/eviction 13.824 e falhas de alocação
JIT zero. A sessão foi encerrada manualmente após cinco minutos sem progresso
de renderização; não foi um crash do jogo.

Esse reteste não demonstra ganho de SSD: não houve primeiro frame/FPS, nem A/B
com o mesmo runtime e a mesma build executados a partir do HD. O diagnóstico
objetivo desta tentativa é que a abertura do NSP no SSD ocorreu, mas o jogo
continuou sem desenhar. Logs completos permanecem em
`%LOCALAPPDATA%\Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\Logs\CoreHost\`;
a captura `rivals-first-frame.png` não foi criada.

## Escopo e inventário

O arquivo contém telemetria do CoreHost/D3D12, frontend, scheduler, memória,
áudio, DMA, shared memory e logs detalhados do Ryujinx. Foram encontrados
oito títulos únicos, nove sessões (Alien Hominid aparece duas vezes):

| Jogo | Title ID | Resultado que os logs sustentam |
| --- | --- | --- |
| Alien Hominid HD | `010056B019874000` | Duas sessões com callbacks; sem exceção fatal. O log não prova imagem correta ou FPS sustentado. |
| New Super Mario Bros. U Deluxe | `0100EA80032EA000` | Atividade de draw/callback, pausas de syncpoint e suspensão do app; sem exceção fatal registrada. |
| Pokémon Legends: Z-A | `0100F43008C44000` | Falha fatal: orçamento recusou um commit solicitado por `MapPhysicalMemory64`. |
| Pokémon Let’s Go Pikachu | `010003F003A34000` | Poucos callbacks e suspensão do app; sem exceção fatal no CoreHost. |
| Captain Toad: Treasure Tracker | `01009BF0072D4000` | Atividade de draw/callback e esperas; suspensão do app. Um log detalhado contém bytes NUL, limitando a busca textual simples. |
| Horizon Chase Turbo | `01009EA00B714000` | Apresentação/callbacks, taxa irregular e `Disconnect: BadValue`; sem exceção fatal registrada. |
| Rivals of Aether | `0100D2700DE7C000` | Falha fatal de JIT por pressão de commit; callbacks quase inexistentes antes da falha. |
| Cuphead | `0100A5C00D162000` | Compilação/submit muito caros e poucos callbacks na sessão capturada; suspensão do app, sem exceção fatal registrada. |

Os jogos usaram backend D3D12, surface UWP compartilhada, resolução reportada
de 1920×1080, escala interna 1.00× e `BackendThreading=Off`. Isso não demonstra
que a imagem visível tenha 1080p nem que não tenha sido aplicado recorte ou
transformação incorreta. O ZIP não contém captura PNG/JPG do jogo.

## Evidência e interpretação

### Problema de instrumentação — corrigir primeiro

Em `src/src/Ryujinx.Uwp.CoreHost/CoreHostExports.cs`,
`GetNativePresentationDelta()` consulta `Native_D3D11_GetPresentationTelemetry`
mesmo quando a sessão ativa é D3D12. Portanto `nativePresent=0/0` e os campos
associados não são evidência de que o D3D12 deixou de apresentar frames.

O log `d3d12-first-frame` registra chamadas/processamento de `ProcessFrame`;
`processed=N` não equivale a um frame visível no painel. Os callbacks de
apresentação gerenciados também não são uma captura de pixels. Corrigir a fonte
da métrica antes de comparar FPS, “primeiro frame” ou blank screen.

**Mudança/teste:** selecionar a telemetria conforme backend; para D3D12 registrar
tentativa e resultado do `Present`, código de retorno, sequência/fence, tamanho
da surface e primeiro frame efetivamente apresentado. Correlacionar isso com a
seleção/associação da camada UWP. Capturar uma imagem assimétrica conhecida e
uma captura real do jogo. Manter separado “guest processado”, “present aceito”
e “pixels capturados”.

### Memória: duas falhas diferentes

**Pokémon Legends: Z-A.** O erro é uma recusa explícita durante SVC
`MapPhysicalMemory64`: pediu 4 GiB, com uso reportado de 1,87 GiB, teto de 5 GiB
e folga de 128 MiB. O log sozinho não prova se os 4 GiB representam páginas
físicas novas ou um mapeamento/alias. Auditar a semântica e a contabilização
antes de alterar o teto: cobrar páginas físicas únicas que realmente exigem
commit; não cobrar duas vezes aliases; se forem páginas novas, manter a recusa
segura e implementar tratamento degradado explícito, não liberar 4 GiB às
cegas.

**Rivals of Aether.** O JIT falhou com `JitCacheFullException` e depois recusou
um commit de página de 64 KiB. O uso reportado chegou a 5.239.328.768 bytes
(~4,88 GiB) perante o limite de 5 GiB, com dispatch pinado. O log registra trim
de heap gerenciado, mas isso não libera código JIT ainda referenciado por
dispatch ativo. Investigar recuperação segura de código frio/despinado por
época; nunca desmapear código ativo. Não aumentar o teto Xbox nem remover a
proteção.

`memory-trim.log` mostra trims que reduzem substancialmente o heap gerenciado
em alguns eventos e outros que quase não o alteram. As categorias aparecem
parcialmente como `untracked`; por isso ainda não há reconciliação confiável
entre commit do processo, guest/JIT, heaps D3D12, uploads e caches.

### GPU/FIFO, custo de CPU e descritores

`gpu-stalls.log` registra 15 esperas `guest-syncpoint`, incluindo uma próxima
de 1 s com `signaled=false`; outros snapshots posteriores progridem. Isso é
evidência de pausas severas no caminho de sincronização, não prova de deadlock
permanente nem de espera no descriptor ring. Logs detalhados também registram
“GPU processing thread is too slow” e esperas de syncpoint acima de 1 s. Há
leituras de NSP lentas e `SurfaceFlinger Disconnect: BadValue` em Horizon e
Let’s Go; correlacionar com carregamento, seleção de camada e primeiro Present,
sem presumir causalidade.

O ring não reportou pressão, espera por fence ou falha em nenhum título. Ainda
assim, a taxa de cópias é alta em alguns caminhos: New Super Mario Bros. U teve
589.666 cópias para 115.275 draws (~5,12 cópias/draw); Alien Hominid ~2,26;
Captain Toad ~2,36; Horizon ~2,35. Primeiro corrigir imagem e sincronização;
depois medir reutilização de tabelas e copiar somente slots alterados.

Todos os jogos usaram `gpuThreading=False`, `BackendThreading=Off` e `ptc=False`.
Isso sugere compilação/preparação serial e ausência de cache persistente nessa
configuração, mas não autoriza ligar threading global. Cuphead registrou lote
inicial de ~76.980 draws, compilação de shader e submit de muitos segundos;
Horizon também teve intervalos de submit/present de vários segundos. Durações
acumuladas por operação não devem ser convertidas diretamente em FPS.

### Suspensão não é sinônimo de crash

Alien Hominid, NSMB U, Let’s Go, Captain Toad, Horizon e Cuphead encerram nos
logs após pedido de suspensão do app pelo Windows, seguido de parada ordenada.
Isso não é uma exceção fatal de CoreHost. Z-A e Rivals, em contraste, têm erro
fatal explícito. Para comparar sessões, distinguir suspensão/fechamento,
crash, timeout de syncpoint e ausência de imagem.

## Plano crítico de correção

### P0 — tornar a medição confiável

1. Corrigir a seleção da telemetria nativa: D3D12 não pode consultar a API
   D3D11.
2. Separar contadores de guest `ProcessFrame`, draws, `Present` aceito,
   callback, primeiro frame visível e captura de pixels.
3. Registrar resultado/HRESULT de Present, mudanças de surface/layer, sequência
   do frame, fence e timestamps monotônicos. Não tratar ausência de callback
   como “zero frame” sem evidência do backend.
4. Testar a telemetria em D3D11 e D3D12 e comparar contagem com captura
   assimétrica e screenshots reais.

**Aceite:** os contadores identificam corretamente backend e distinguem frame
processado de frame apresentado; nenhum campo D3D12 é alimentado por D3D11.

### P1 — corrigir os dois encerramentos por memória

1. Instrumentar `MapPhysicalMemory64`: bytes virtuais pedidos, páginas físicas
   únicas, páginas já respaldadas, aliases, bytes efetivamente committed e
   decisão da cota. Reproduzir Z-A em harness sem elevar o limite.
2. Ajustar a cobrança somente se houver dupla contagem ou se a solicitação for
   apenas mapeamento de páginas já respaldadas. Se os 4 GiB forem commit real,
   manter o limite e retornar erro guest controlado, com diagnóstico claro.
3. Em Rivals, registrar bytes JIT totais, ativos/pinados, elegíveis para
   reclaim, idade/último uso e motivo de pin. Implementar reclaim somente após
   garantia de que nenhum dispatch pode retornar ao código removido (epoch,
   fence ou mecanismo equivalente).
4. Reconciliar em uma linha temporal categorias JIT, guest commit, D3D12,
   uploads/readback, shader cache, managed heap e commit privado do processo.
5. Testar várias sessões sequenciais e pressão próxima ao teto de 5 GiB; não
   aumentar o limite e não usar swap como substituto.

**Aceite:** Z-A não sofre recusa incorreta de alias; alocações realmente acima
da cota continuam protegidas. Rivals recupera código frio seguro ou encerra
com erro controlado antes de falha de alocação do SO; zero use-after-free.

### P2 — imagem, camada e sincronização D3D12

1. Capturar a primeira imagem efetivamente apresentada em cada título e
   verificar orientação, crop, resolução, layer selecionada e lifecycle da
   `SwapChainPanel`.
2. Correlacionar `Disconnect: BadValue`, `Compose skipped`, seleção da camada,
   resize e resultado de Present por timestamp. Não adicionar retries que
   escondam erros sem registrar causa e resultado.
3. Para a primeira pausa de syncpoint, registrar produtor/consumidor, valor
   esperado/observado, queue depth, fence signal, submissão ativa e duração;
   garantir progresso/cancelamento no shutdown.
4. Executar debug layer e testes de textura/depth/resolve/barrier já existentes;
   então produzir capturas antes/depois para Horizon, NSMB U, Captain Toad e
   Let’s Go. O log atual não contém pixels para provar esses defeitos.

**Aceite:** frame visível confirmado, orientação e crop corretos; nenhuma
espera longa sem diagnóstico; sem mensagens de debug layer; shutdown não deixa
callback/Present tardio.

### P3 — reduzir stutter sem falsificar ganhos

1. Instrumentar por intervalo e percentis: tradução/cache de shader, criação
   de PSO, cópias/binds de descritor, gravação da command list, submit, fence,
   Present e leitura de assets.
2. Comparar shader cold/warm. Habilitar PTC/cache persistente apenas se
   compatível com o caminho D3D12, versionado e limitado por orçamento.
3. Reutilizar descriptor tables por chave e atualizar slots dirty; medir
   cópias por draw e trocas de heap. Só ampliar capacidade diante de pressão
   observada.
4. Só depois avaliar workers para tradução/preparação de shaders. Command list
   e submissão mantêm dono único; não ligar `gpuThreading` global neste passo.

**Aceite:** comparação A/B no mesmo trecho, resolução e estado de cache,
apresentando p50/p95/p99 de frametime e FPS de Present confirmado; sem regressão
de pixels ou aumento de memória além da cota.

## Ordem de reteste e regressões

1. Harness: telemetria, captura assimétrica, D3D12 debug layer e round-trip de
   memória/aliases.
2. Z-A: sem OOM falso; registrar pico e categorias de memória.
3. Rivals: monitorar reclaim/pinning e executar trecho que antes esgotava JIT.
4. Horizon, New Super Mario Bros. U, Captain Toad e Let’s Go: primeira imagem,
   orientação, cena 3D e syncpoint.
5. Cuphead: sessão fria e aquecida, p95/p99 e custo de shader/PSO.
6. Alien Hominid, Sonic Mania e Dead Cells: regressão de imagem, áudio e
   apresentação; comparar com a mesma build e configuração.

Para cada reteste guardar build/commit, adapter, backend, configuração,
timestamps, log bruto, screenshot e resultado. Sessões encerradas por suspensão
do Windows não contam como estabilidade de jogo; FPS de callbacks sem imagem
capturada não contam como validação visual.

## Conclusões que os dados não permitem

- Não provam que o ring de descritores esteja esgotado.
- Não provam que todos os jogos falharam por um único bug de textura, nem que
  nenhum frame chegou a ser mostrado.
- Não provam que Alien Hominid ou Horizon sustentaram 60 FPS; o máximo de
  callbacks em um intervalo não representa estabilidade.
- Não justificam aumentar o teto de 5 GiB, remover proteções de JIT ou ativar
  threading global.
- Não permitem validar orientação ou qualidade visual: falta screenshot no
  pacote e a telemetria nativa está no backend errado.

## Arquivos de evidência

ZIP original: `E:\Logs (24).zip`.

Extração preservada em `artifacts/logs24-analysis-20260926/`, incluindo logs
`CoreHost/errors.log`, `CoreHost/settings.log`, `CoreHost/graphics.log`,
`CoreHost/d3d12-first-frame.log`, `CoreHost/d3d12-performance.log`,
`CoreHost/d3d12-descriptors.log`, `CoreHost/memory.log`,
`CoreHost/scheduler.log`, `gpu-stalls.log`, `memory-trim.log` e os logs
detalhados do Ryujinx por sessão.
