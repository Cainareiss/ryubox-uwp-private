# Ryubing: comparacao de JIT e experimento de desempenho

Referencia local: `artifacts/reference-ryubing-canary-1.3.351-20260929`.
Ryubing usa function table e traducoes High-CQ em background. Ryubox usa
dispatch pinned, High-CQ desativado e backend D3D12 sem worker GAL adicional.
As politicas nao podem ser transplantadas sem considerar reclaim e cota UWP.

## Implementacao experimental

Cache de ate 512 consultas de funcao no Translator, protegido pela mesma
trava de publicacao. Pin permanece obrigatorio; invalidacao/reclaim remove
a entrada antes de liberar codigo; fim de sessao limpa o cache. Nao altera
limite de memoria nem autoriza chamadas diretas a codigo nao pinado.

Opt-in: `RYUJINX_UWP_PINNED_LOOKUP_CACHE=1`. Desativado por padrao enquanto
nao houver comparacao NativeAOT e gameplay. O teste interno pode alternar
EnablePinnedLookupCache sem modificar o perfil dos outros testes.

## Medidas

Consulta alternada isolada, 500 mil iteracoes: 170,53/185,83 ms sem cache;
113,92/147,92 ms com cache. Nao sao FPS.

Loop ARM64 real com 300 mil chamadas, mesmo dispatcher nativo e instrucoes:
primeira rodada com tiering do .NET apresentou resultados mistos, logo nao
constitui evidencia de ganho consistente.

Repeticao com DOTNET_TieredCompilation=0, ordem A-B-B-A-A-B-B-A:

| Sem cache (ms) | Com cache (ms) |
| --- | --- |
| 319,35 | 274,79 |
| 319,49 | 277,19 |
| 304,77 | 267,37 |
| 295,12 | 288,78 |

Media 309,68 contra 277,03 ms: aproximadamente 10,5% menos tempo neste
microbenchmark de execucao. Sem tiering nao equivale a NativeAOT/Xbox.
170 verificacoes passaram, incluindo invalidacao, pins concorrentes,
codigo atualizado, falhas de traducao, descarte entre sessoes e admissao.

Logs: `artifacts/ryubing-arm64-cache-ab-20260930.log` e
`artifacts/ryubing-arm64-cache-ab-stable-20260930.log`.

## Proximo aceite

Comparar NativeAOT cache ligado/desligado e mesma cena Horizon/Pokemon,
mesmo perfil pinned e caches de shaders igualmente aquecidos. Medir tempo
de frame, nao contadores FIFO. Nao prometer 10,5% de FPS ou ganho Xbox.
MSIX permanece adiado conforme solicitado pelo usuario.

## Validacao NativeAOT posterior

Publicado o harness da raiz `eng/JitCacheRegression` em NativeAOT win-x64,
otimizacao Speed. Este e o harness que contem o A/B lookupCache; a copia
antiga em `src/eng` nao serve para medir essa mudanca.
Corrigida a reflexao de `CacheMemoryAllocator.Allocate` no teste: chamada
tipada evita que o trimmer remova o metodo acessado via GetType().

Duas execucoes completas passaram as 170 verificacoes. Medias do loop
ARM64 de 300 mil chamadas, ordem A-B-B-A-A-B-B-A:

| Rodada | Cache desligado | Cache ligado | Reducao de tempo medio |
| --- | --- | --- | --- |
| 1 | 240,22 ms | 221,75 ms | 7,69% |
| 2 | 236,49 ms | 231,48 ms | 2,12% |

Uma amostra com cache em cada rodada foi mais lenta que as amostras sem
cache. Nao descartar essas amostras nem tratar a melhor como ganho garantido.
Opt-in permanece: nao houve A/B de gameplay nem validacao Xbox.
Logs: `artifacts/jit-lookup-nativeaot-{publish,run,repeat}-20260930.log`.
