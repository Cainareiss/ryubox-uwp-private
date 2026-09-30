# Deep Trace 0.1.1.49

Esta build usa `sample=N` para correlacionar o estado de todos os subsistemas a
cada 2,5 segundos durante a inicialização do primeiro frame.

## Arquivos

- `CoreHost/clock.log`: compara `Environment.TickCount64`, QPC/Stopwatch,
  `PerformanceCounter` e o contador virtual ARM do Switch.
- `CoreHost/guest-dispatch.log`: função guest atualmente executada por thread,
  tempo dentro da função, retornos e último SVC.
- `CoreHost/guest-threads.log`: PC, estado do scheduler, core, prioridade,
  esperas de sincronização, arbitragem, mutex e solicitação de término.
- `CoreHost/compositor.log`: ciclos do SurfaceFlinger, VSync, buffers adquiridos
  e frames entregues à fila da GPU.
- `CoreHost/audio-trace.log`: sinais, renders concluídos, waits e timeouts do DSP.
- `CoreHost/resource-trace.log`: RAM, GC, loop da GPU, frames e estado D3D11.
- `CoreHost/stall-analysis.log`: somente bloqueios confirmados, reunindo em uma
  linha os estados relevantes dos arquivos anteriores.
- `CoreHost/watchdog.log`: visão consolidada para leitura rápida.

## Leitura

Cruze linhas com o mesmo `sample=N`. O primeiro contador que deixa de avançar
indica o lado do bloqueio; os demais mostram se o processo, relógio, áudio,
compositor e device D3D11 continuaram vivos naquele instante.

O watchdog encerra a coleta após o primeiro frame apresentado. Isso limita o
custo de I/O durante o jogo e mantém a instrumentação concentrada no problema de
inicialização.
