# Ryujinx Xbox UWP 0.1.1.47

Build D3D11-only focada no bloqueio observado durante a inicializacao de
Shovel Knight no Xbox.

## Correcao principal

- ativa `Ryujinx.Uwp.SafeJit` antes da criacao do Switch;
- usa o dispatch gerenciado do ARMeilleure no Xbox;
- mantem as funcoes no nivel baseline durante toda a sessao;
- desativa a fila de promocao High-CQ/rejit;
- impede atualizacoes concorrentes da function table durante a inicializacao;
- remove os contadores de rejit das funcoes baseline no modo seguro;
- reinicia os contadores de telemetria a cada sessao;
- identifica o modo no watchdog como `jitMode=baseline-managed`.

O perfil portatil, o limite de 5 GiB, o guest de 4 GiB, o host D3D11 nativo,
o heartbeat e o watchdog permanecem ativos.

## Motivacao

Os logs da versao 0.1.1.46 mostraram o D3D11, a fila GPU e o heartbeat vivos,
enquanto milhares de funcoes baseline eram promovidas para High-CQ. A execucao
deixava de produzir trabalho grafico durante essa transicao. Esta versao remove
essa variavel para priorizar compatibilidade e previsibilidade no Xbox Dev Mode.

O modo seguro pode ter desempenho inferior ao dispatch nativo com High-CQ. Ele
deve ser avaliado primeiro pela capacidade de atravessar o carregamento e
apresentar o primeiro frame.
