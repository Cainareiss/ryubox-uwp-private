# Astra6 — correções CPU/JIT após a auditoria Logs (6)

Data: 2026-09-09. Alterações posteriores ao pacote 0.2.0.117 instalado.

## Resultado e limites

Foram corrigidos custos e uma corrida de interrupção reproduzíveis no código. O CoreHost compila e as regressões locais abaixo passam. **Ainda não foi medida a recuperação de FPS dos jogos no Xbox, nem comprovado que Terraria/New Super Mario Bros. U Deluxe passaram a produzir frames.** Não tratar esta rodada como fechamento do Gate 2.

Nenhum jogo ou frontend foi aberto, nenhum MSIX novo foi gerado e a instalação existente não foi substituída. A compilação gerenciada do CoreHost não equivale a uma nova publicação NativeAOT/MSIX.

## 1. Compilação fria não bloqueia buscas já aquecidas

Antes, `AcquireFunction` mantinha `_functionPublishLock` durante todo `GetOrTranslate`, incluindo decodificação, compilação e mapeamento de código. Outros threads guest não podiam sequer adquirir uma tradução pronta enquanto uma compilação segurava esse lock.

Agora a compilação fria usa um lock separado. Consulta/publicação, pin e invalidação continuam atômicos sob o lock de publicação; código ativo continua protegido contra liberação. Um teste pausa deliberadamente o decoder de um thread e verifica que outro consegue adquirir uma função aquecida sem aguardar a liberação do decoder.

Uma geração de invalidação acompanha a compilação fora do lock. Se o guest invalidar código durante a compilação, o resultado é descartado e recompilado; ele não é publicado nem persistido no PTC com um hash de fonte nova. O teste altera uma instrução ARM64 durante essa pausa e comprova a execução da instrução atualizada, não da antiga.

Arquivo: `src/src/ARMeilleure/Translation/Translator.cs`.

## 2. Descarte JIT condicionado ao espaço realmente necessário

O código anterior elevava o alvo de espaço livre para 224 MiB em uma região de 256 MiB quando o processo entrava em pressão crítica. Podia descartar até 1.024 funções com apenas 250 ms sem uso, voltando a pesquisar candidatos a cada pequeno lote de traduções. O log de Wonder chega a 657.014 evictions.

`JitCache.Unmap` recicla intervalos; `ReservedRegion` não descompromete as páginas nesse caminho. Assim, esse descarte não reduz diretamente o private commit, mas pode provocar recompilação de código que o jogo voltará a usar.

Nova manutenção:

- Inicia abaixo de 64 MiB livres e busca recuperar aproximadamente 96 MiB, verificando em lotes de 16 liberações.
- No máximo uma consulta de manutenção por segundo, lote de até 512 funções e idade mínima de cinco segundos.
- Não aumenta a agressividade apenas porque memória de outras categorias elevou a pressão do processo.
- Falha real de admissão continua tendo recuperação imediata, sem cooldown, tentando acomodar a função já compilada.
- Pins de funções em execução, teto de regiões JIT e controle global de memória permanecem ativos.

Esses valores são uma política inicial conservadora de reutilização, não um orçamento de FPS. Manter mais código útil pode ocupar mais slots JIT que a política anterior sob pressão; é necessário medir commit e recompilação no Xbox. Não foi aumentado o teto rígido de regiões.

Arquivos: `Translator.cs` e `src/src/ARMeilleure/Translation/Cache/JitReclaimPolicy.cs`.

## 3. Interrupções e caminho quente do dispatcher

Antes, o dispatcher gerenciado chamava `CheckInterrupt` a cada retorno, mesmo sem interrupção. Isso adquiria o lock de lifecycle e renovava o contador de sincronização. Uma nova interrupção recebida durante o callback também podia ter seu contador zero sobrescrito pela renovação final.

Agora:

- `CheckPendingInterrupt` retorna imediatamente quando não há solicitação; a checagem emitida no código nativo continua renovando o orçamento quando necessário.
- Consumo da solicitação e atualização do contador são protegidos; o callback do scheduler continua fora do lock, pois pode aguardar.
- Uma segunda solicitação ou Stop durante o callback conserva contador zero.
- No dispatcher gerenciado com pins, liberar a função anterior e adquirir a seguinte usa uma única entrada no lock de publicação. O dispatcher nativo e a proteção por epochs mantêm seus boundaries próprios.

Testes cobrem interrupção durante callback, Stop, Dispose, retorno de SVC que invalida o próprio código e 10.000 transições combinadas de pin.

Arquivos: `src/src/ARMeilleure/State/ExecutionContext.cs` e `Translator.cs`.

## 4. Métricas para o próximo lote Xbox

O watchdog inclui novos contadores cumulativos:

- `jitColdWaitMs`: espera para entrar no lock de compilação fria.
- `jitCompileMs`: tempo das chamadas de tradução, incluindo trabalho/esperas internas; não é tempo exclusivo de CPU.
- `jitInvalidatedCompilations`: resultados descartados por invalidação concorrente.
- `jitReclaimPasses`, `jitReclaimMs`, `jitReclaimedFunctions`: custo e volume da manutenção JIT.

Tempos acumulados de diferentes threads podem se sobrepor; não somá-los como categorias exclusivas de tempo de frame. O custo de reclaim pode estar incluído no tempo de tradução.

## Validação

| Verificação | Resultado |
| --- | --- |
| JitCacheRegression | 43 verificações aprovadas |
| Repetição de JitCacheRegression | Cinco execuções adicionais completas, todas aprovadas |
| PtcRegression | 15 verificações aprovadas |
| ArenaLifecycleRegression | 13 verificações aprovadas |
| CoreHost Release, `dotnet build --no-restore` | Zero erros; 15 avisos de campos/trim/AOT no CoreHost |

O teste de pressão de arenas estava usando o argumento antigo de `Sample` para simular pressão. O controlador atual mede o processo real e não usa esse argumento como injetor. A fixture foi corrigida para diminuir o orçamento **somente dentro do processo de teste**, confirmar pressão Emergency e restaurar o orçamento em `finally`. O allocator de produção não foi alterado para fazer o teste passar.

Microbenchmark de 500.000 aquisições quentes, nesta máquina: último par medido 141,27 ms com boundaries separados e 114,05 ms com boundary combinado (~19% menos tempo nesse trecho). O primeiro par sofreu aquecimento e não foi usado como estimativa. **Isso não representa ganho de 19% de FPS nem previsão de 60 FPS no Xbox.**

Evidências:

- `artifacts/logs6-jit-regression.log`
- `artifacts/logs6-jit-stress-1.log` até `logs6-jit-stress-5.log`
- `artifacts/logs6-PtcRegression.log`
- `artifacts/logs6-ArenaLifecycleRegression.log`
- `artifacts/logs6-corehost-build.log`

Próxima validação de dispositivo: Sonic Mania/FireRed com cache aquecido; Wonder/Dead Cells para volume de evictions, commit e recompilação; Terraria/New Super Mario Bros. U Deluxe para produção de frames e esperas guest. Referência: `docs/ASTRA6_LOGS6_AUDITORIA.md`.
