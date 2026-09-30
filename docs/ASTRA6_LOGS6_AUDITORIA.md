# Astra6 — auditoria de Logs (6) e D3D11ShaderCache (1)

Data: 2026-09-09. Escopo: comparar o lote informado como Parte 1 com o código candidato 0.2.0.117 da Parte 2, incluindo desempenho. Esta auditoria não executou jogos nem modificou o renderer, configurações ou caches instalados.

## Conclusão

**Não há evidência de que todos os problemas deste lote estejam resolvidos na Parte 2.** Existem melhorias D3D11 e testes locais aprovados, mas a política de execução Safe-JIT e de descarte de traduções ainda exige investigação de desempenho. O Gate 2 continua aberto.

Os logs não fornecem uma identificação inequívoca do pacote que permita atribuir estes resultados à 0.2.0.117. São os resultados do lote enviado, não um benchmark da nova versão. Também não há neste material uma medição anterior equivalente que quantifique a regressão de 60 FPS.

## Material e método

- Entradas: `C:\Users\Cainã\Downloads\Logs (6).zip` e `C:\Users\Cainã\Downloads\D3D11ShaderCache (1).zip`.
- Logs extraídos isoladamente em `artifacts/logs6-audit-20260909`, sem substituir os dados do aplicativo.
- Foram identificadas 18 tentativas de inicialização, de 14 títulos, delimitadas por horário de `CoreHost_Start`: `SessionId=1` se repete entre processos e não serve sozinho para separar execuções.
- Script: `eng/Audit-RyuboxLogs6.ps1`; resultados: `artifacts/logs6-audit-20260909/audit-summary.json`.
- FPS abaixo é a mediana de `callbacks` do log de apresentação após o primeiro callback, incluindo amostras posteriores com zero. Não mede uma fase específica nem comprova que o conteúdo apresentado é correto. `processed` conta trabalho distinto e não foi tratado como FPS.
- Tempo para primeiro frame usa a primeira amostra periódica com callbacks acumulados positivos; é aproximado, não tempo exato da primeira imagem visível.

## Resultado por sessão

Horários UTC de 2026-09-09. “Sem frame” significa ausência de callback de apresentação na janela observada.

| Início | Jogo | Primeiro frame aprox. | Mediana de FPS | Compilações HLSL |
| --- | --- | ---: | ---: | ---: |
| 13:45:57 | Mario Wonder | Sem frame | — | 0 |
| 13:47:05 | STEINS;GATE 0 | 12 s | 21,3 | 12 |
| 13:49:49 | CHAOS;CHILD | 14 s | 0 | 4 |
| 13:51:50 | Mario 3D World | 111 s | 60 | 21 |
| 13:55:47 | New Super Mario Bros. U Deluxe | Sem frame | — | 4 |
| 14:01:29 | Mario Wonder | 74 s | 2,8 | 204 |
| 14:21:01 | GTA III | 133 s | 0 | 0 |
| 14:25:18 | Cuphead | 71 s | 0 | 28 |
| 14:30:55 | Pokémon FireRed | 30 s | 5,5 | 2 |
| 14:32:14 | Pokémon LeafGreen | 30 s | 5 | 0 |
| 14:33:15 | Sonic Mania | 12 s | 17 | 2 |
| 14:34:41 | SEGA AGES Sonic 2 | 20 s | 13 | 3 |
| 14:36:29 | Terraria | Sem frame | — | 0 |
| 16:55:29 | Sonic Mania, cache reutilizado | 12 s | 13 | 0 |
| 16:59:35 | STEINS;GATE 0, cache reutilizado | 12 s | 15 | 0 |
| 17:01:34 | Super Mario Bros. 35 | Sem frame | — | 0 |
| 17:06:09 | Pokémon FireRed, cache reutilizado | 29 s | 5 | 0 |
| 17:08:51 | Dead Cells | 50 s | 1 | 3 |

Uma mediana zero não prova crash: inclui sessões que apresentam alguns frames e depois deixam de apresentar. Menus e cenas estáticas também precisam de validação visual. Há registros de suspensão solicitada pelo Windows; não se deve classificar todo encerramento como falha fatal. O arquivo principal Ryujinx deste lote está vazio, limitando a investigação de exceções/HLE.

## O que o cache esclareceu

Os 283 arquivos DXBC do ZIP passaram na validação estrutural de assinatura, tamanho declarado e limites da tabela de chunks. São 128 vertex shaders e 155 fragment shaders, totalizando 2.758.504 bytes. Todos os 283 nomes únicos registrados como `HLSL bytecode persisted` nos logs estão presentes: nenhum ausente.

Isso não certifica o resultado gráfico nem a compatibilidade do driver; não houve execução desses binários nesta auditoria. A associação de nomes e a estrutura válida descartam a hipótese simples de um ZIP sem os shaders que o log registrou.

Sonic Mania e FireRed continuam lentos em sessões com cache hits e **zero novas compilações HLSL**. Logo, compilação fria de shaders não explica sozinha essas sessões. Cache D3D11 contém shaders da GPU; PTC contém traduções de código da CPU emulada. Um não substitui o outro. Não há justificativa nesta análise para apagar o cache.

## Evidências de gargalos ainda não encerrados

### CPU/JIT e configuração aplicada

O lote registra `safeJit=True`, `unmanagedDispatch=False`, `allowLcqInTable=False`, `backgroundThreads=0`, `highCqEnabled=False`, `jitMode=baseline-managed`, PTC desativado e `BackendThreading=Off`.

O código atual em `src/Ryujinx.Uwp.CoreHost/CoreHostExports.cs` ainda mantém o caminho seguro e aplica PTC/GPU threading conforme as configurações. As alterações gráficas da Parte 2 não ativam automaticamente traduções otimizadas nem recuperam throughput da CPU. Suporte a PTC preservado não significa PTC ligado nesses testes.

Em Sonic Mania e FireRed, `nativeUpload=0` e apresentações diretas bem-sucedidas coexistem com FPS baixo. Isso afasta o readback de apresentação como explicação universal, mas não elimina outros custos D3D11 ou espera da GPU. CPU/dispatch/scheduler são hipóteses prioritárias a medir, não uma causa única já comprovada.

### Descarte excessivo de traduções

- Mario Wonder: contador de eviction chega a 657.014; pico de private commit de aproximadamente 4.280 MiB.
- GTA III: 196.608 evictions; Cuphead: 41.728; Dead Cells: 15.104.
- Sonic Mania e FireRed: zero evictions nas sessões observadas. Portanto, descarte JIT não explica todos os casos.

`src/src/ARMeilleure/Translation/Translator.cs`, método `RetireColdTranslations`, ainda utiliza limiares de memória livre, idade mínima de 250 ms em pressão crítica e lotes de até 1.024 funções. A seleção ordena candidatos sob `_functionPublishLock`. A proteção de funções em execução evita liberação insegura, mas não demonstra ausência de custo alto de seleção, descarte e recompilação. O volume observado exige medir esse ciclo antes de declarar o desempenho corrigido.

### Jogos sem progresso de apresentação

- Terraria: cerca de 80 s, um despacho concluído, fila vazia e nenhum buffer adquirido/frame enfileirado.
- Super Mario Bros. 35: cerca de 253 s, também sem frame enfileirado.
- New Super Mario Bros. U Deluxe: mais de 320 s, milhares de trabalhos processados, mas nenhum frame apresentado.
- Cuphead: apresentou 504 frames acumulados e depois deixou de avançar, já utilizando apresentação direta.

É preciso rastrear produção de frames, execução guest e esperas de serviços/scheduler nestes casos. Apenas trocar o caminho de apresentação ou retirar um perfil de compatibilidade não demonstra a resolução dessas paralisações.

## Cobertura da Parte 2 versus pendências

| Área | Situação no candidato 0.2.0.117 | O que ainda falta provar |
| --- | --- | --- |
| Cópias escaladas/hazard snapshots | Implementação ampliada e regressões locais | Ganho por jogo e variantes restantes; o lote não fornece comparação suficiente de bytes de hazards |
| Shaders/half3/bindings | Correções e testes locais descritos no relatório da Parte 2 | Resultado visual e comportamento no driver Xbox |
| Apresentação/orientação | Contrato centralizado e testes de pixels locais | Regressão visual dos jogos; logs de callbacks não certificam orientação |
| Stalls | Instrumentação adicional de FIFO/esperas/compilação | Captura atual distinguindo espera guest, compilação, query e trabalho real |
| Safe-JIT/PTC/dispatch | Segurança preservada; configurações continuam relevantes | Medição e recuperação de desempenho, sem simplesmente desativar proteções |
| Política de descarte JIT | Continua presente | Evitar descarte/recompilação excessivos com memória limitada |

## Ordem proposta de validação

1. Capturar Sonic Mania e FireRed na versão atual, com cache aquecido e mesma cena, medindo custo de dispatch, locks, scheduler e apresentação. São casos lentos sem compilação HLSL nova nem evictions.
2. Instrumentar custo/volume/recompilação dos descartes em Wonder e Dead Cells, preservando a proteção de código em execução.
3. Capturar a sequência de produção de frames e as esperas guest em Terraria e New Super Mario Bros. U Deluxe; não confundir fila GPU vazia com GPU travada.
4. Comparar as métricas novas de hazards e stalls antes/depois no Xbox e repetir regressão visual. Só então decidir se o Gate 2 e o desempenho estão aprovados.

Referência de implementação e limitações: `docs/ASTRA6_PARTE2_IMPLEMENTACAO.md`. Nenhum novo MSIX foi gerado, instalado ou executado como parte desta auditoria.
