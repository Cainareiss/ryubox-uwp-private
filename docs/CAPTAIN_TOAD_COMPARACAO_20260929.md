# Captain Toad — controle original versus CoreHost

29/09/2026. Investigar a tela branca, sem declarar correção a partir de FPS,
janela aberta ou captura do desktop incorreta. Nenhum padrão de produção alterado.

## Controle confirmado

- NSP local: Captain Toad Treasure Tracker, título `01009BF0072D4000`, v1.0.0,
  programa **32-bit**.
- Ryubing Canary 1.3.351 original, commit
  `475615f0431b4995b03c930c5089d20f6f3570e0`, compilação desktop gerenciada,
  Vulkan na Intel HD 620, perfil separado dos dados UWP.
- Cena 3D com Toad, Toadette, cenário e estrela capturada em
  `artifacts/captain-toad-original-vulkan-confirm-20260929.png`.
- Log `artifacts/captain-toad-original-vulkan-20260929.stdout.log`:
  atividade de configuração/sensores do jogo por volta de 38 s. Isso não
  determina o instante exato em que a cena apareceu; a captura é posterior.
- Usuário confirmou que o Ryubox novo não chega a essa cena. O sucesso
  observado pertence ao **original/Vulkan**, não ao Ryubox/D3D12.

## Atenção às capturas

`PrintWindow` retornou sucesso e imagem branca mesmo com o original exibindo
a cena 3D. Essa captura não prova tela branca no jogo. A captura confirmada
foi feita da janela visível, temporariamente trazida à frente. Uma tentativa
anterior capturou outra janela que a cobria e deve ser descartada como evidência.
Para o D3D12 usar o readback nativo da apresentação.

## CoreHost D3D12 — primeiro teste

- Runtime isolado `artifacts/blasphemous2-watchdog-runtime-20260929`.
- Pinned/Safe-JIT padrão; sem PTC, sem DeepTrace, captura nativa em série,
  limite 180 segundos após Start retornar. Cota mantida em 5 GiB.
- Log detalhado: `Ryujinx_UWP_2026-09-29_18-11-02-024.log`.
- Buffers 1920x1080 criados por volta de 20 s. Inicialmente apenas um lote
  processado, sem apresentação; posteriormente áudio e draws passaram a ocorrer.
- Cerca do fim da janela de teste houve 22 presents. Não houve captura
  suficiente para afirmar que a cena correta foi renderizada.
- Em teardown solicitado, às 18:14:11Z, ocorreu
  `InvalidMemoryRegionException: Not mapped: va=0x312600, size=4`, em
  `KServerSession.Reply`, chamado pelo serviço de áudio.
  Tratar como falha de encerramento/vida útil de IPC a investigar; não como
  prova de que essa exceção causou o bloqueio de inicialização.

## Comparação seguinte

Wrapper `artifacts/Run-CaptainToad-Epoch-D3D12-20260929.ps1`: mesmo D3D12,
`FAST_EPOCH_JIT=1`, High-CQ desligado, sem PTC/DeepTrace, 240 s e capturas
em intervalos de 60 presents. É diagnóstico, não configuração aprovada no Xbox.

## Aceite

Exigir que o Ryubox D3D12 alcance a cena confirmada no controle, com captura
nativa e logs da mesma sessão. Separar progresso guest, produção de comandos,
present e conteúdo visual. Só depois tratar controles e desempenho.

## Resultado do diagnóstico epoch de 240 segundos

- Log detalhado `Ryujinx_UWP_2026-09-29_18-17-36-954.log`.
- Captura nativa `artifacts/captain-toad-epoch-d3d12-20260929-p00010.bmp`
  confirma **tela de título no Ryubox D3D12**, não a cena 3D do controle.
- A apresentação chegou a 2255 frames, mas parou de avançar na transição.
  Os valores próximos de 60 presents/s pertencem ao trecho anterior;
  não demonstram desempenho da cena 3D nem frames visualmente distintos.
- `ProcessFrame 5851` terminou somente no encerramento, após **94437 ms**.
  Na janela de telemetria de 96328 ms houve **92019319 us de compilação
  de shaders**, 24 misses, 14 hits e 30 criações de PSO (987581 us).
  Esse tempo vem do contador nativo de FXC, não de inferência por FPS.
- Teardown foi solicitado pelo limite do harness às 18:21:38Z; terminou
  Idle às 18:21:41Z, `GraphicsThreadJoined=True`, `deviceLost=False`.
  Não houve crash registrado nessa sessão. O teste curto não demonstra
  carregamento infinito: foi interrompido durante trabalho de compilação.
- O perfil epoch altera mais de um comportamento de CPU/JIT. Não atribuir
  causalidade exclusiva a um lock nem ativá-lo globalmente sem regressões.

Próximo teste: `artifacts/Run-CaptainToad-Scene-D3D12-20260929.ps1`, até
600 s, mesmo runtime e perfil epoch, D3D12, captura nativa a cada 180 presents,
sem elevar a cota de memória. Cache persistente existente é mantido; portanto
isso é um reteste **aquecido**, não uma comparação de boot frio equivalente.

## Cena-alvo alcançada no Ryubox D3D12

Sessão de 29/09/2026, 19:07:28Z a 19:13:44Z. Log detalhado
`Ryujinx_UWP_2026-09-29_19-07-28-024.log`. Mesmo runtime NativeAOT e
perfil epoch descritos acima; **não houve substituição por Vulkan ou D3D11**.
Nenhum botão A foi enviado nesta sessão: a introdução avançou por conta própria.

Evidência nativa de framebuffer:

- `artifacts/captain-toad-scene-d3d12-20260929-p00012.bmp`: abertura circular
  com o modelo 3D do Toad, capturada às 16:12:04 locais.
- `artifacts/captain-toad-scene-d3d12-20260929-p00015.bmp`: ilha de pedra,
  vegetação, Toad, Toadette e estrela da **mesma cena do controle Vulkan**,
  capturada às 16:13:18 locais, aproximadamente 5 min 50 s após iniciar.
- SHA-256 da segunda imagem:
  `D43ADE0A6149BBB80A6ADD8450D98BD7ECD4A586FC1E2C509187C27E7A836E3A`.

### O que os dados demonstram

1. O bloqueio não é uma incapacidade absoluta do D3D12 de desenhar essa cena.
   O perfil diagnóstico alcançou a introdução e produziu geometria/texturas.
2. A transição possui um gargalo real de compilação síncrona: `ProcessFrame
   5442` demorou 117203 ms; na janela correspondente houve 109011944 us
   registrados em FXC, 47 cache misses, 45 hits e 61 PSOs criados. Mesmo com
   cache parcialmente aquecido, ainda existiam muitos shaders inéditos.
3. Após compilar, o desempenho continua ruim: cerca de 8 presents/s na cena.
   Na janela de 5297 ms às 19:13:21Z houve 8504 draws, 58225 cópias de
   descritores, 1838904 us de descritores e 1449994 us de espera.
   Não somar categorias sobrepostas de gravação/descritores.
4. Memória não atingiu a cota: amostra durante a introdução com commit privado
   de 2222886912 bytes, pressão Normal, limite 5368709120 bytes. Isso não
   substitui testes de pressão/reclaim ou de várias sessões.
5. A imagem ainda difere do controle: iluminação/contraste e halo branco ao
   redor da estrela. **Alcançar a cena não significa gráficos corrigidos**.

Encerramento solicitado por arquivo de sinal após obter a captura. Log final:
`GraphicsThreadJoined=True`, `TeardownEnd; state=Idle; deviceLost=False`.
O processo terminou e não ficou um jogo oculto consumindo CPU.

### Pendências e próximos testes

- Repetir o perfil padrão pinned com cache aquecido e tempo suficiente;
  depois variar SafeJit/dispatch separadamente. A comparação anterior mudou
  vários fatores e não prova que epoch seja necessário para alcançar a cena.
- Medir shaders individuais por hash/estágio e conservar fontes dos mais
  lentos antes de alterar otimização FXC ou tradução. Não suprimir shaders.
- Comparar o mesmo render pass com o controle para iluminação/halo; inspecionar
  formatos, blend, saída de shader e pós-processamento, sem correção por título.
- Atacar custo de descritores/gravação e esperas com medição da mesma cena.
- Validar debug layer, regressões e segurança de reclaim antes de alterar
  o padrão UWP. Este resultado **não certifica Xbox, 60 FPS ou MSIX novo**.
