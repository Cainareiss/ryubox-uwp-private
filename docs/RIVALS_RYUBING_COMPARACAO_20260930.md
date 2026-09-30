# Rivals of Aether no Ryubing original — comparação local

Data: 30/09/2026. Referência: Ryubing Canary 1.3.351, Vulkan, NSP do SSD
`C:\Users\Cainã\Downloads\Rivals of Aether [0100D2700DE7C000][v0].nsp`.
Dados do Ryubing isolados em `artifacts/ryubing-original-comparison-data-20260929`.
Nenhuma sessão deste dia produziu um frame confirmado; não há FPS comparável.

## Inicialização sem administrador

O ambiente de desenvolvimento estava elevado; o Ryubing recusou rodar
(`Ryujinx is not intended to be run as administrator`). Uma tarefa agendada
com nível limitado passou dessa verificação, mas parou em `CreateRenderer`
antes de ler o jogo. O dump de diagnóstico confirmou esse ponto. A tarefa foi
removida e o processo de teste encerrado.

O launcher `artifacts/Start-Rivals-Ryubing-Limited-20260930.ps1` duplicou o
token do Explorer da sessão interativa e iniciou o teste no desktop normal.
Nessa configuração o Ryubing carregou a NSP e mostrou o título Rivals na
janela. O script preserva um limite de memória e fecha a sessão quando ele
é atingido. Não use este método como mecanismo de lançamento UWP/Xbox.

## Duas políticas de memória

| Política Ryubing | Progresso observado | Resultado |
| --- | --- | --- |
| SoftwarePageTable | Carregou NSP, Vulkan Intel HD 620, serviços NV/HID e chegou ao jogo | Em 31 s, memória privada 5.130.092.544 bytes; limitador interrompeu sem primeiro frame |
| HostMappedUnsafe | Carregou NSP, Vulkan e iniciou serviços guest | `SetHeapSize64` falhou em `MemoryManagementWindows.Commit`: pagefile insuficiente; guard registrou 2.794.196.992 bytes privados e encerrou a sessão |

O segundo valor é uma amostra do guard após o erro, não um teto do uso
necessário. Não aumentar pagefile/cota só para forçar um FPS no PC. O
Ryubing chegou mais longe que o reteste Ryubox D3D12 de 26/09, cujo log
parou em `ProcessFrame=3`, zero draws e zero presents após cerca de cinco
minutos; contudo, são builds, backends e políticas de memória diferentes.
O resultado não demonstra que Rivals funcione ou rode mais rápido no Ryubing
nesta máquina.

## Evidência

- `artifacts/rivals-original-ryubing-token-20260930.{stdout,harness}.log`;
- `artifacts/rivals-original-ryubing-hostmappedunsafe-20260930.{stdout,harness}.log`;
- `artifacts/rivals-original-dump-stacks-20260930.log` para a tentativa
  agendada que não chegou à NSP;
- `docs/PLANO_CRITICO_CORRECAO_LOGS24_D3D12_20260926.md` para o reteste
  Ryubox no SSD.

## Próxima comparação válida

Executar o mesmo trecho de gameplay em ambos os emuladores, com memória
física/commit suficientes e captura de primeiro `Present`, e medir frametimes
por 60 segundos. Este PC não sustentou o Ryubing até esse ponto nesta rodada.
