# Marvel: admissao de commit guest

O log de 22/09/2026 registra erro Windows 1455 em KernelContext.CommitMemory,
durante MapPhysicalMemory. Isso e alocacao de RAM guest; nao prova falha do JIT
ou do D3D12. Guest=untracked era uma lacuna de diagnostico, nao a causa provada.

Alteracoes:
- preflight de commit explicito guest no modo UWP LazyGuestCommit, usando uso
  privado/working set atual e margem de 128 MiB sob a cota do processo;
- contagem de paginas explicitamente comprometidas pelo kernel, independente
  dos bits de reutilizacao/zero-fill, atualizada somente apos commit bem sucedido;
- captura de OOM e erros Windows 1455/8/14 no despacho SVC, interrompendo a
  sessao pelo GuestFaultHandler antes de retornar a fronteira nativa do JIT;
- diagnostico inclui SVC, PC guest, thread, offset fisico e bytes solicitados.

Validacao: GuestCommitRegression verifica limite exato, recusa acima do limite,
overflow, entradas invalidas, commit zero e mensagem de recusa.

Limites: o preflight nao reserva commit global do Windows e nao evita corridas
com outros processos. O contador Guest cobre commits explicitos do kernel,
nao os commits do handler nativo lazy. A recusa encerra a sessao; nao concede
memoria ficticia ao jogo. Esta protecao nao comprova que Marvel chega ao menu
nem resolve por si so seu consumo de memoria. Captura real continua pendente.

## Reteste NativeAOT de 22/09, 16:40 UTC

Execucao direta D3D12 invisivel, PID 8012, limite 5 GiB, sem frontend.
O contador Guest passou a registrar os commits explicitos. Houve somente
tres apresentacoes, sem captura de conteudo valido. O processo chegou a
4.86 GB de commit privado e o JIT registrou uma falha de alocacao.
O Windows registrou excecao 0xC0000005 no endereco zero, e nao uma nova
excecao gerenciada 1455. Nao houve encerramento controlado comprovado.

A inspecao encontrou dois stubs que faziam tailcall do retorno de
GetFunctionAddress sem verificar zero, embora o callback retorne zero em
caso de falha de compilacao. Ambos agora retornam ao dispatcher quando
a resolucao falha; entrada zero na tabela rapida passa pela resolucao lenta.
Esta e uma correcao verificavel no codigo, mas a associacao causal com o
crash requer reteste NativeAOT. Nao representa validacao de gameplay.

## Regressao de despacho e reutilizacao

O teste JitCacheRegression executa agora uma traducao com falha injetada no
dispatcher nativo sem pins: retorna ao gerenciado, informa a falha e libera
o JIT sem chamar endereco zero.

Outro teste reproduziu a recusa incorreta de reutilizacao de paginas ja
comprometidas sob pressao. O controle de admissao passou a cobrar somente
a expansao real da regiao, arredondada pela granularidade de commit. Reuso
nao consome novo commit; crescimento continua sujeito ao mesmo limite.
Evidencias: artifacts/jit-commit-reuse-before.log (falha reproduzida) e
artifacts/jit-commit-reuse-after.log (155 verificacoes aprovadas).

O segundo reteste NativeAOT encerrou de forma controlada em 4.834.107.392
bytes de commit privado. A recusa informou `additionalCommit=65536`: o reuso
funcionou, mas a proxima pagina realmente exigia commit. A telemetria mostrou
4096 funcoes aposentadas pendentes e somente 3 liberadas. Novas varreduras
agora ficam suspensas enquanto existir um lote epoch pendente, evitando
retirar mais entradas publicas e recompilar duplicatas.

O watchdog correlacionado tambem inicia no D3D12 quando DeepTrace e solicitado;
antes a condicao o restringia ao compilador D3D11. Durante a sessao anterior,
depois dos tres buffers iniciais, o backend registrou zero draws e zero render
passes: a tela preta ocorre antes da submissao de uma cena ao D3D12.
