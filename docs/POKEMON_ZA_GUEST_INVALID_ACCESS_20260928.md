# Pokémon Legends: Z-A — investigação do acesso guest inválido

Atualizado em 28/09/2026. A correção investigada é a seleção de threads
acordadas por `KAddressArbiter`; não é uma correção geral de memória guest nem
uma certificação de compatibilidade do jogo.

## Hipótese e alteração

As filas do arbiter eram mantidas em ordem de `DynamicPriority` no momento da
inserção. Essa prioridade pode mudar enquanto a thread espera, por herança de
prioridade. A fila então ficava desatualizada e `WakeThreads` acordava elementos
do início sem recalcular a ordem. Em inicialização concorrente isso podia
alterar a sequência de publicação/consumo de estado guest.

`KAddressArbiter` agora preserva a ordem de entrada nas filas e escolhe quem
acordar no momento do sinal, ordenando pela prioridade atual. A ordenação é
estável, portanto waiters de mesma prioridade mantêm FIFO. A seleção foi extraída
para um helper coberto por testes que alteram a prioridade depois da inserção.

Não alteramos limites de memória, não relaxamos validação de endereços guest e
não mascaramos exceções.

## Evidência de runtime

Runtime do CoreHost D3D12 compilado em
`artifacts/direct-d3d12-za-arbiter-fix-20260928` e executado sem janela visível
por 15 minutos, com JIT seguro padrão, teto de memória de 5 GiB e sem entrada
automática de controle. Evidências preservadas em
`artifacts/direct-d3d12-za-arbiter-fix-20260928/probe-20260928-135505`.

- `errors.log` e `fatal.log`: vazios; não foi registrado acesso guest inválido.
- `session.log`: teardown normal, `deviceLost=False`, `DeviceReleaseRequired=False`;
  1.698 operações processadas e 962 presents antes do teardown.
- A cadeia de inicialização publicou `E413780=0xC0BBE00`, marcou
  `E413880=1` e a chamada em `0x8770B40` recebeu `x0=0xE413780` em vez do
  ponteiro nulo observado nas reproduções anteriores.
- `pokemon-frame.png` foi capturado no present 600: é uma imagem real do jogo,
  mostrando a seleção de idioma. Ela ainda está invertida; a captura prova
  apresentação e chegada à tela, não correção de orientação nem jogabilidade.
- O pico de `privateCommit` no log foi 5.124.964.352 bytes, abaixo da cota de
  5.368.709.120, mas a pressão permaneceu `Emergency`. O uso alto requer
  investigação separada; não se aumentou o limite.

Este resultado é promissor, mas é uma única sessão. Ainda não prova que a causa
raiz está definitivamente eliminada nem que o jogo chega à gameplay.

## Validação automatizada e pendências

O teste NUnit `KAddressArbiterTests` foi adicionado para verificar (1) prioridade
alterada enquanto o waiter está na fila, (2) desempate FIFO estável e (3) a
semântica de `count=0`.

Não foi possível executar `dotnet test ... --no-restore`: o ambiente não tem o
pacote NuGet `FSharp.Core 8.0.400` restaurado (`NETSDK1064`). Uma compilação
posterior de `Ryujinx.HLE` também parou em erros preexistentes em
`Ryujinx.HLE/Loaders/Processes/ProcessLoader.cs` (tipo `Result` não resolvido;
arquivo local não rastreado), antes de validar a última extração do helper.
O runtime testado foi compilado com a mesma ordenação estável ainda inline,
antes dessa extração sem mudança semântica.

Próxima validação: restaurar dependências, executar os dois testes NUnit,
publicar novamente o CoreHost com o helper final e repetir o boot. Depois
corrigir/testar a orientação separadamente e testar navegação além da seleção
de idioma. Não declarar o bug definitivamente resolvido antes da repetição.

## Reteste de orientação e navegação — 28/09/2026

O apresentador D3D12 complementava `crop.FlipY` antes de criar as coordenadas
de amostragem. Isso invertia verticalmente o caso normal `FlipY=false`. Agora
usa o bit GAL diretamente. A regressão foi corrigida para exigir a orientação
top-down normal e passou em WARP e na GPU física: 8 casos de crop/FlipX/FlipY,
mais a verificação de depth proxy; nenhum erro da debug layer. CoreHost
NativeAOT foi recompilado com o intervalo de captura diagnóstica configurável.

O reteste do jogo ainda **não foi aprovado**. As sessões após a mudança de
orientação encerraram antes do primeiro present, sempre com leitura guest em
`0x00000000000000D0` no PC `0x8770B40`, LR `0xAF99AC4`; `E413780` continuava
nulo. Uma sessão anterior, sem a nova orientação, chegou à tela de idioma e
publicou esse singleton, portanto a correção de ordenação do arbiter é apenas
promissora e ainda não eliminou a falha intermitente.

O primeiro disparo automático de A ocorreu antes de haver prova visual de que
a tela de idioma estava pronta; a sessão terminou antes de capturar um frame.
Não foi repetido às cegas. O runner agora suporta pedidos individuais por
arquivo (`input-a.request`), e a captura seriada pode ser configurada a cada
30 presents. Nas tentativas seguintes sem envio de A, o mesmo null singleton
voltou a causar a exceção; não houve PNG novo nem tela inicial do jogo.

Evidências dessas tentativas estão nas pastas `probe-20260928-181118`,
`probe-20260928-182643`, `probe-20260928-183500` e
`probe-20260928-184312` sob o diretório de artefatos citado acima. Conclusão:
orientação corrigida e validada em teste GPU; falha guest do Z-A permanece
intermitente, e chegar à tela inicial com A continua pendente.

## Reteste sem sondas e PTC Canary — 28/09/2026

Uma execução sem guest-dispatch probes, mas com watchpoints limitados para
`E413780/E413880`, reproduziu a leitura nula em `PC=0x8770B40`,
`LR=0xAF99AC4`, endereço `0xD0`, após aproximadamente 6m50s. Os watchpoints
confirmaram o mapeamento da página, mas não observaram publicação do ponteiro;
o evento de página suja inicial não prova escrita nesses campos. Nenhum frame
foi apresentado. Evidência: `probe-20260928-190247`.

Em seguida habilitei PTC explicitamente no runner, mantendo D3D12, Safe-JIT
fixado, limite de 5 GiB e deep-call protection desativada no modo pinned.
`ptcEnabled=True` foi confirmado no log e o PTC salvou perfis; mesmo assim,
após 8 presents a sessão caiu no mesmo PC/endereço inválido, em cerca de
6m26s. Não houve captura de jogo nem envio de A. Evidência:
`artifacts/direct-d3d12-za-canary183-ptc-20260928/probe-20260928-192658`.

Conclusão atualizada: a exceção não é causada pelas sondas nem é resolvida
somente por PTC. O limite de memória continua em 5 GiB; a reserva inicial de
4 MiB recusada também ocorre na execução anterior que avançou, portanto não é
causa suficiente. A primeira divergência de inicialização guest permanece
localizada no fluxo que deveria publicar o singleton, não no D3D12/present.
