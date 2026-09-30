# CoreHost: limite JIT por sessão — 21/09/2026

## Diagnóstico

Demon Slayer terminou com `JitCacheFullException`, duas regiões de 256 MiB
e pressão `Normal`. O perfil de dispatch nativo não executa a recuperação
reservada ao dispatch pinned. A mensagem anterior "after reclaim" era incorreta
nesse caminho. Somente Rivals recebia uma exceção de capacidade no CoreHost.

## Alteração

- CoreHost define explicitamente seis regiões máximas por sessão, para todos
  os títulos; não herda o limite do jogo anterior.
- Regiões continuam reservadas sob demanda e comprometidas conforme utilizadas.
- Expansões além de 512 MiB exigem amostra de uso válida e orçamento para uma
  região inteira mais 512 MiB de folga. Usa-se a amostra do monitor do host;
  não se chama Refresh sob o lock do JIT, evitando callbacks de trim nesse lock.
- Capacidade continua finita: 1,5 GiB. Não é cache ilimitado nem garantia contra
  falta de memória/paginação. Amostras não são reservas atômicas de memória.
- Nenhuma tradução em execução é descartada. Não muda shaders nem backend DX12.

## Testes

`eng/JitCacheRegression`: 72 verificações aprovadas. Incluem admissão e limites,
commit com falha, epochs/pins, concorrência e expansão real até a sexta região.
O teste de expansão ocupa ranges lógicos sem comprometer 1,5 GiB fisicamente;
apenas os pequenos mapeamentos executáveis são comprometidos/protegidos.
Sétima região rejeitada e mapeamentos liberados corretamente.

Evidência: `artifacts/jit-growth-regression-20260921.log`.

Teste de jogo solicitado: New Super Mario Bros U Deluxe, em CoreHost D3D12.
O teste de capacidade não comprova que Demon Slayer ou Mario chegam ao gameplay;
essa confirmação depende da sessão real.

## Publicação e sessão Mario

Publicação NativeAOT Release win-x64 concluída (exit 0), com avisos AOT/trim.
Runtime isolado: `artifacts/mario-deluxe-d3d12-jit-20260921`.
Lançamento visível às 14:04:18 UTC; `CoreHostStartSuccess` às 14:04:32 UTC;
primeiro processamento às 14:05:00 UTC. Às 14:05:25 UTC: 27 processamentos,
zero apresentações. Logs também registram leituras lentas de armazenamento
e waits de syncpoint; não atribuir essa demora ao limite JIT sem evidência.
Janela mantida aberta para o usuário. Não há confirmação de gameplay nesse
ponto nem reteste concluído de Demon Slayer.

Atualização: `FirstGuestFrameProduced` e `FirstVisibleGuestPresent` confirmados
às 14:05:44 UTC (aproximadamente 86 segundos após o lançamento).
Primeira apresentação não certifica fase jogável nem FPS sustentado.
