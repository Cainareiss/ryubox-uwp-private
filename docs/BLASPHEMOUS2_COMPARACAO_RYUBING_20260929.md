# Blasphemous 2 — investigação comparativa do loading

## Escopo

Investigar Blasphemous 2 e comparar com New Super Mario Bros. U Deluxe no
Ryubing desktop original/Vulkan, sem substituir o D3D12 do Ryubox.
Estado em 29/09/2026: **sem correção funcional comprovada**.

## Reproduções CoreHost D3D12

- Blasphemous 2, título `010040801A4C8000`, versão 1.0.0.
- Três sessões limitadas, encerradas pelo harness: 09:40:54Z,
  09:48:37Z e 14:08:25Z. Não confundir teardown solicitado com crash.
- Capturas nativas mostram fundo preto e indicador de loading animado no
  canto inferior direito. Isso não é a tela inicial e não satisfaz o aceite.
- No primeiro teste, 3.630 presents e 9.576 frames processados não foram
  suficientes para chegar ao menu. A contagem não comprova progresso guest.
- No teste instrumentado, a fila GPFIFO estava vazia nos pontos amostrados,
  com dispatches concluídos; MainThread, workers e Loading.PreloadManager
  acumularam tempo de CPU. Uma thread pausada num snapshot não prova deadlock.
- Não se reproduziu esgotamento de JIT/memória, falha de compilação de shader
  ou device removal nestas sessões. Não extrapolar isso para todos os jogos.
- Um pulso A foi entregue pelo harness no segundo teste sem sair do loading.

## Instrumentação adicionada

`RYUJINX_UWP_SESSION_WATCHDOG=1`, com janela Win32 local e DeepTrace, mantém
o watchdog depois do primeiro present. O comportamento normal de produção
permanece inalterado. NativeAOT recompilado com sucesso no runtime isolado
`artifacts/blasphemous2-watchdog-runtime-20260929`.

Atenção: o watchdog existente consulta o status nativo **D3D11** mesmo numa
sessão D3D12. Seu `deviceStatus=0x80004005` não é evidência de perda do device
D3D12. Usar os registros próprios do renderer para esse diagnóstico.

## Controle desktop original

- Referência limpa: Ryubing Canary 1.3.351,
  commit `475615f0431b4995b03c930c5089d20f6f3570e0`.
- Diretório: `artifacts/reference-ryubing-canary-1.3.351-20260929`.
- Perfil separado: `artifacts/ryubing-original-comparison-data-20260929`.
  Keys/firmware copiados localmente; nenhum save do usuário modificado.
- Build direcionado ao disco E após a restauração NuGet esgotar o C.
  Apenas dependências recém-criadas por essa tentativa foram movidas ao E.
- Não reutilizar caches de shaders/PTC do Ryubox no controle original.

## Critério da comparação

### Resultado parcial do controle Vulkan

Build desktop concluído, árvore upstream permanece limpa. A execução elevada
foi recusada pela proteção original; iniciado posteriormente com o token da
sessão interativa do Explorer, sem alterar o emulador.

Log `blasphemous2-original-vulkan-limited-20260929.stdout.log` confirma Intel
HD Graphics 620, Vulkan 1.2.151, driver 27.20.100.8729, Blasphemous 2 v1.0.0.
A captura de tela `blasphemous2-original-vulkan-screen-20260929.png` mostra
o indicador de loading, não o menu. Aos 2m59s o original encerrou com
`ErrorOutOfDeviceMemory` em `Window.CreateSwapchain/RecreateSwapchain/Present`.
O usuário relatou que o PC crashou e que viu a tela inicial antes do fechamento;
essa transição não foi preservada pela captura. Não concluir que é o mesmo
defeito do D3D12 nem que a falha do PC foi causada pelo jogo. Sem novo teste
imediato após a instabilidade. Mario U ainda não executado no controle.

1. Mesmo NSP e firmware; registrar versão, GPU, backend, memória e threading.
2. Executar um jogo por vez; não medir FPS durante builds concorrentes.
3. Exigir captura real do menu, não apenas loading/presents.
4. Se o desktop passar, separar HLE/CPU/JIT/memória do renderer: desktop
   HostMappedUnsafe versus CoreHost SoftwarePageTable/pinned JIT não é A/B
   exclusivo de API gráfica.
5. Investigar Mario U separadamente antes de declarar uma causa comum.

## Evidência preservada

## Isolamento CPU/JIT — rodada adicional

Confirmação do usuário: o controle original chegou rapidamente à tela inicial;
o fechamento posterior coincidiu com instabilidade do PC. Eventos do Windows
registraram falha de `dwm.exe` e a exceção Vulkan; a relação causal entre elas
não foi estabelecida.

No watchdog pinned anterior, aos 416 s havia **334.444.539 dispatchLookups**,
36.191 traduções concluídas, cerca de 50.983 ms acumulados em compilação,
5.642 ms em espera por compilação fria e zero funções recuperadas por reclaim.
Esses tempos não medem o custo total do despacho, nem o tempo total de CPU.
A fila GPFIFO estava vazia e os 13.230 dispatches GPU tinham sido concluídos.

O código explica uma diferença estrutural:

- `InstEmitFlowHelper.EmitTableBranch`: em pinned mode retorna ao dispatcher
  em vez de continuar por chamada/tabela nativa.
- `NativeInterface.AcquirePinnedFunction` volta ao managed, verifica interrupção
  e chama `Translator.AcquireFunction`.
- `AcquireFunction` consulta/transfere os pins sob `_functionPublishLock`,
  compartilhado pelas threads guest. Até hits passam por esse caminho.
- `SafeJit` acrescenta sincronização na entrada de blocos e força LCQ.
- O CoreHost desativa High-CQ/rejit por padrão; o original permite otimização.

Isso identifica custo adicional real, mas **não prova sozinho a causa final
do loading**, nem autoriza remover proteção de código em voo.

### Experimentos locais, mesmo runtime D3D12

Nenhum padrão de produção foi alterado; sem PTC nos dois testes abaixo,
sem DeepTrace, sem build concorrente e mantendo a cota de 5 GiB.

| Sessão UTC | Mudança diagnóstica | Resultado |
| --- | --- | --- |
| 14:33:04 | `FAST_EPOCH_JIT=1`; LCQ ligado pela tabela; High-CQ desligado | Primeiro buffer guest em 58 s; amostras próximas de 58–61 presents aceitos/s no loading; não chegou ao menu em 240 s; teardown normal, deviceLost=False. |
| 14:37:42 | Mesmo epoch + `LOCAL_PERFORMANCE_TEST=1` e `ALLOW_BACKGROUND_HIGHCQ=1` | Um worker High-CQ confirmado; loading continuou; ao fim ~582 MB de JIT committed e ~4,05 GB de commit privado, pressão Critical; não chegou ao menu em 240 s; teardown normal. |

As contagens de presents não representam FPS de gameplay. O primeiro teste
pinned tinha PTC e, parcialmente, build concorrente: não anunciar um fator
de aceleração exato a partir dessa comparação. A opção epoch muda também
SafeJit, LCQ na tabela e proteção de profundidade; não é um ensaio isolado
somente do lock.

Próximo controle: desktop original com `--memory-manager-mode SoftwarePageTable`
e `--disable-ptc`, ainda Vulkan. O harness limita a duração e solicita fechamento
se o commit privado ultrapassar 4 GiB, se RAM livre ficar abaixo de 768 MiB ou
se a folga de commit global ficar abaixo de 1 GiB. Não elevar limites do Xbox.

Outra divergência a auditar, sem atribuir causalidade neste título: CoreHost
ativa `D3D12DisableGuestCounters`, e `D3D12Renderer.BeginCounters` pode não
iniciar queries reais. Counters guest não são apenas telemetria. Isso exige
teste próprio de resultados/consumidores, não uma remoção indiscriminada da
contenção contra falhas do driver Intel.

### Arquivos de evidência

Na raiz externa do workspace, pasta `artifacts`:

- `blasphemous2-before-20260929.*`;
- `blasphemous2-diagnostic-20260929*`;
- `blasphemous2-watchdog-20260929*`;
- wrappers `Run-Blasphemous2-*-20260929.ps1`;
- `ryubing-original-desktop-publish-disk-e-20260929.log`.
- `blasphemous2-epoch-diagnostic-20260929*`;
- `blasphemous2-highcq-diagnostic-20260929*`;
- `blasphemous2-original-software-vulkan-20260929*`.

Os logs detalhados do CoreHost estão em LocalState/Logs/Ryujinx e
LocalState/Logs/CoreHost. Não publicar keys, firmware ou imagens NSP.
