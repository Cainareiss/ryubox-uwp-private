# Pesquisa Ryubing: trabalho repetido nos bindings D3D12

## Referencia lida

`artifacts/reference-ryubing-canary-1.3.351-20260929/src/Ryujinx.Graphics.Vulkan/DescriptorSetUpdater.cs`,
metodo UpdateAndBindDescriptorSets: retorna quando nao ha dirty flags,
atualiza separadamente uniform/storage/texture/image e limpa as flags.
`ARMeilleure/Translation/Translator.cs`: tabela de despacho de funcoes.

Isso indica duas frentes diferentes: diminuir trabalho de descritores no
host e diminuir consultas no dispatcher. Nao prova que uma delas explique
todo o baixo FPS. Vulkan e D3D12 nao possuem contratos intercambiaveis.

## Mudanca implementada nesta rodada

O marshal de bindings em Draw e Dispatch alocava um array nativo gerenciado
a cada chamada. Agora usa stackalloc para ate 16 registros (1664 bytes),
mantendo array para programas maiores. Cada registro e inicializado inteiro,
incluindo campos opcionais. Nenhum ponteiro e guardado apos a chamada:
o backend nativo consome os registros sincronamente e copia as entradas
necessarias ao cache; recursos continuam retidos pelo mecanismo existente.

Nao e um transplante do cache Vulkan: e uma adaptacao pequena para retirar
alocacao temporaria do caminho quente. Nao pula validacao de recursos,
barriers, atualizacao de conteudo nem sincronizacao. Nao adiciona cache
persistente de referencias a texturas.

## Medida reproduzivel

Teste BindingPacketTests, 128 dispatches apos aquecimento por quantidade de
bindings. GC.GetAllocatedBytesForCurrentThread mede apenas o trecho Dispatch.
Baseline recompilado com limite stack zero; restaurado para 16 ao terminar.

| Bindings | Array baseline | Stack limitado |
| --- | --- | --- |
| 0 | 0 bytes | 0 bytes |
| 1 | 16.384 bytes | 0 bytes |
| 16 | 216.064 bytes | 0 bytes |
| 17 | 229.376 bytes | 229.376 bytes |
| 64 | 855.040 bytes | 855.040 bytes |

PASS WARP e GPU fisica: resultado de compute conferido por readback em
0/1/16/17/64 bindings; zero erros da debug layer. Passou tambem o teste de
draw com quadrantes e quatro sinais de viewport na GPU fisica.
Isso comprova menos alocacao nesse trecho, nao aumento de FPS do jogo.

Logs em artifacts: `binding-packets-heap-baseline-20260930.log`,
`binding-packets-warp-20260930.log`, `binding-packets-gpu-20260930.log`,
`binding-packets-graphics-gpu-20260930.log`.

## Falha de queries encontrada e corrigida na suite

DescriptorRingTests esperava samples/primitives, mas o runner so habilitava
contadores guest no caminho `--stencil-only`. Como o renderer inicializa essa
opcao estaticamente, `--descriptors-only` precisava configurar a variavel
antes de criar o renderer. Corrigido em `eng/D3D12InteropRegression/Program.cs`.
A suite inteira agora passa em WARP e GPU Intel, inclusive o draw grafico
com queries e page switch. O backend de queries nao precisou de mudanca.
Logs: `artifacts/descriptor-queries-fixed-{warp,gpu}-20260930.log`.

## Proximos passos

1. Medir a mesma cena e caches aquecidos no CoreHost antes/depois, com
   frametime e alocacoes/GC. Nao inferir FPS de dispatches ou microbenchmarks.
2. Antes de dirty flags gerenciadas, cobrir mudancas de arrays, views,
   buffers renomeados, programas, command lists e heaps. Cache nativo
   existente nao dispensa esses contratos.
3. MSIX ainda adiado: esta rodada nao comprova ganho de FPS em jogos/Xbox.

## Sessao de Pokemon Lets Go com o CoreHost atual

O CoreHost NativeAOT atualizado foi publicado em
`artifacts/pokemon-runtime-20260930`, com DLL nativa D3D12 e SPIRV-Cross
pareadas. `artifacts/Start-PokemonLetsGo-Ryubox-Current-20260930.ps1`
iniciou a NSP em janela visivel, teclado, audio, contador de FPS e sem
timeout, usando cota de 5 GiB e o perfil JIT padrao. O launcher permanece
ativo para validacao visual pelo usuario.

O `session.log` registrou inicializacao bem-sucedida do CoreHost e loop FIFO.
Ate 21:06 UTC, o jogo manteve `processed=1; presented=0` e nenhuma captura
do primeiro frame foi criada. `d3d12-performance.log` apontou zero draws e
zero PSOs. Portanto esta sessao ainda nao valida FPS, orientacao ou tela do
Professor. Repete o comportamento do perfil pinado documentado no teste de
29/09. O perfil desktop com epoch que antes avancou foi preparado em
`artifacts/Start-PokemonLetsGo-Ryubox-Epoch-20260930.ps1`; aguarda o fim da
sessao atual para nao executar dois jogos de varios GiB ao mesmo tempo.
