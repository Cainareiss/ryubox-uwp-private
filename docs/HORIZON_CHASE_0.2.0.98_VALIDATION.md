# Horizon Chase Turbo — validação da saída do splash

## Resultado observado em 4 de setembro de 2026

O CoreHost D3D11 chegou à tela `PRESS ANY BUTTON` e depois ao menu
`WORLD TOUR`, usando o NSP base `01009EA00B714000`, sem alterar o jogo.
Capturas reais: `artifacts/horizon-native-three-minutes.png` e
`artifacts/horizon-native-menu-after-wait.png`.

Repetição com os binários finais, sem `-PerformanceMode`, usando o NSP original
no disco externo: início às 23:16:13 UTC, título capturado às 23:20:32 UTC e
menu `WORLD TOUR` às 23:20:48 UTC. O log confirmou a seleção automática do
perfil Horizon, `backgroundThreads=1` e `jitCacheMaxRegions=2`.
Capturas: `artifacts/horizon-final-title.png` e `artifacts/horizon-final-menu.png`.
O host foi fechado intencionalmente após a captura do menu, não por crash.

## Diagnóstico e alterações

- O perfil anterior usava dispatch gerenciado LCQ, sem High-CQ/rejit.
  No teste em SSD foram registrados 294.232.755 dispatch lookups e 20.106
  traduções, ainda na Aquiris após aproximadamente três minutos. A CPU
  continuava trabalhando; não era uma exceção D3D11 nem um framebuffer parado.
- O teste comparativo com dispatch nativo e High-CQ chegou ao título em
  aproximadamente três minutos e permitiu abrir o menu. Esse perfil fica
  restrito ao Horizon Chase Turbo. Mantém uma thread de tradução em background
  e duas regiões JIT de 256 MiB; não habilita High-CQ para todos os jogos.
- A thread `UnityPreload` estava fora do antigo limite de 12 threads do
  diagnóstico. Os snapshots agora incluem até 64 threads e seus nomes.
- A versão de diagnóstico confirmou mensagens do próprio jogo: perfil de
  usuário pronto, provider de leaderboard pronto e abertura do save bem-sucedida.
- O disco externo agravava bastante o carregamento. O host abria NSPs com
  `FILE_FLAG_RANDOM_ACCESS`, que desativa read-ahead no Windows. Foi removida
  essa indicação; o sistema pode detectar leituras sequenciais sem proibir seeks.
  Referência: https://devblogs.microsoft.com/oldnewthing/20120120-00/?p=8493
- Leituras de storage acima de 100 ms passam a registrar offset, bytes, duração
  e resultado com amostragem limitada, para não gerar um log por leitura lenta.
- O parser de configurações foi substituído por leitura explícita de JSON,
  preservando valores padrão quando campos são omitidos. Em NativeAOT foi
  reproduzida a perda de `guestInternetAccess=true` e do padrão de `vsync` no
  caminho antigo; no novo caminho os valores aplicados foram confirmados.
- O log de viewport D3D11 foi limitado por amostragem e só é montado quando
  o diagnóstico está habilitado.

## Limites da validação

Os testes acima foram feitos no PC. Não comprovam FPS estável no Xbox nem
ausência de falhas durante uma corrida longa. A execução gerenciada ainda
avançava internamente: não foi demonstrado um deadlock permanente. O ganho
vem de reduzir a demora de inicialização; não de pular o splash artificialmente.

A cópia temporária em SSD foi usada para separar I/O de execução da CPU.
O NSP original, as keys e os saves não foram substituídos ou apagados.
O MSIX não deve incluir essa cópia de jogo nem quaisquer keys/firmware.
