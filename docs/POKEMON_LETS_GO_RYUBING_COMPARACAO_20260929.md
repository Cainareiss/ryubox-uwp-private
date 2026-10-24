# Pokemon Let's Go Pikachu: controle original e memoria no PC

29/09/2026. Teste com o Ryubing Canary 1.3.351 original (Vulkan), mesma NSP
local, dados isolados do Ryubox. Objetivo: observar o trecho que falha no
CoreHost D3D12 e permitir entrada por teclado.

## Controle corrigido

O primeiro teste usou a configuracao padrao de Player1, JoyconPair. O jogo
solicitou Handheld/JoyconLeft/JoyconRight/Pokeball; o log repetia
`Hid Remap: No matching controllers found`. Portanto a janela podia abrir
enquanto o teclado nao controlava o jogo.

Criei um perfil isolado em
`artifacts/ryubing-original-comparison-data-20260929/profiles/keyboard/lets-go-handheld.json`
com `controller_type=Handheld`, `player_index=Handheld` e teclas Z=A, X=B,
WASD=analogico, setas=D-pad. O launcher informa esse perfil ao Ryubing.
O log da sessao `pokemon-lets-go-original-handheld-profile-20260929` confirma:
`Handheld configured with Keyboard "0"` e
`Configured Controller Handheld to Handheld`.

Captura do Ryubing original com a tela de selecao de controle:
`artifacts/pokemon-lets-go-original-handheld-20260929.png`. A opcao portatil
estava destacada. Uma captura posterior mostra um quadro branco de transicao.

## Resultado confirmado pelo usuario em 29/09

Ao executar `artifacts/Start-PokemonLetsGo-Ryubing-20260929.ps1` depois de
fechar o VS Code, o usuario apresentou uma captura do dialogo do Professor
Carvalho (Carvalho, Pikachu e Cubone, texto sobre o mundo Pokemon) e relatou
60 FPS na tela. A imagem foi enviada na conversa, nao foi gravada neste
workspace; portanto 60 FPS e uma observacao do usuario, nao uma medicao
independente. O log `pokemon-lets-go-original-handheld-20260929-164544.harness.log`
registra 207 s de execucao, `StoppedByMemoryGuard=False`, com encerramento
normal da janela pelo usuario. O controle Handheld estava configurado no log.

O mesmo NSP no CoreHost D3D12, com JIT seguro/pinado e audio, apresentou
`processed=1; presented=0` por mais de tres minutos; D3D12 nao alocou
recursos de jogo. A sessao foi encerrada controladamente para poupar memoria.
Isso localiza a divergencia antes do primeiro frame, mas ainda nao prova
se a causa e JIT, sincronizacao guest ou servico NVN.

O teste separado com `RYUJINX_UWP_FAST_EPOCH_JIT=1` atravessou essa parada e
apresentou a selecao de controle, a abertura 3D e a selecao de idioma no
CoreHost D3D12. Evidencias:
`artifacts/pokemon-lets-go-d3d12-epoch-20260929-p00004.bmp` (controle),
`-p00061.bmp` (abertura 3D) e `-p00094.bmp` (idioma). O ultimo quadro e
legivel e orientado corretamente, mas parte da abertura 3D saiu invertida.
O contador do backend durante a abertura registrou aproximadamente 18 a 29
`Present` aceitos por segundo em amostras, nao os 60 FPS observados pelo
usuario no Ryubing. A sessao acabou pelo limite de 240 s, sem capturar o
dialogo do Professor Carvalho. Um segundo teste D3D12 com tempo maior e
confirmacoes A controladas esta em andamento. Nao usar a captura do Ryubing
como prova de funcionamento D3D12.

## Limite do computador de teste

No primeiro teste, o monitor encerrou a sessao aos 106 s: processo com 5.635.944.448 bytes de
memoria privada e menos de 768 MiB de RAM fisica livre. Antes da interrupcao,
o uso de RAM ja estava perto do limite seguro. O encerramento foi solicitado
pelo harness e nao deve ser registrado como crash do jogo.

O computador tem aproximadamente 8 GiB de RAM. O teste posterior com VS Code
fechado terminou sem acionar o monitor de memoria. Nao fechar aplicativos do
usuario automaticamente; nao reduzir a margem do monitor ate causar OOM.

O launcher reproduzivel `artifacts/Start-PokemonLetsGo-Ryubing-20260929.ps1`
mantem o jogo aberto ate o usuario fechar a janela, desde que haja memoria.
O teste original demonstra o trecho do Professor Carvalho apenas no Ryubing.
Proximo passo: obter esse quadro no CoreHost D3D12 com captura independente.

## Comparacao adicional: cache e sincronizacao D3D12

O Ryubing usa cache de shaders por titulo; o CoreHost Ryubox nao ativava
`GraphicsConfig.EnableShaderCache`. Foi adicionada a opcao experimental
`shaderCache` (ligada pelo switch `-ShaderCache` no runner D3D12). Um primeiro
boot criou os arquivos guest/shared/direct3d12_native; o boot seguinte
registrou `Loading 42 shaders from the cache`. Isso comprova persistencia,
nao ganho de FPS nem paridade com o Ryubing. O DXBC nativo ja tinha cache
separado, e parte da reducao de compilacao pode vir dele.

Dois crashes sucessivos no mesmo ponto mostraram divergencia de estado da
lista de comandos D3D12: primeiro `MemoryBarrier` com lista fechada, depois
`Upload requires an open command list`. A entrada nativa de begin tornou-se
idempotente e `D3D12Renderer.EnsureCommands` a consulta sempre. O smoke
nativo passou com readback -> barrier -> flush, 90 frames e zero erro de
debug layer. No teste seguinte o jogo atravessou essas falhas e chegou ao
loading apos a abertura, mas caiu em `ReadBuffer`: um callback de memoria
guest na thread CPU tentou ler GPU enquanto a lista da thread grafica estava
aberta. Uma experiencia de encaminhar `GetBufferData` para a thread grafica
via fila do CoreHost compilou, mas **falhou em jogo**: apos o loading, a fila
nao foi atendida e `RunOnGpuThread` encerrou com `TimeoutException` apos 120 s.
Essa experiencia foi revertida no codigo-fonte; o runtime experimental em
`artifacts/pokemon-lets-go-shader-cache-runtime-20260929` ainda pode conter
o binario compilado antes da reversao e nao deve ser empacotado em MSIX.
O proximo ajuste precisa integrar o readback ao ownership/interrupt do FIFO
ou usar um caminho de readback que nao bloqueie a thread CPU a espera de uma
acao que o FIFO nao consegue executar. Apenas enfileirar no loop externo
do CoreHost demonstrou ser insuficiente.

## Reteste DX12 com readback independente (29/09)

Implementado `RyuboxD3D12_ReadSubmittedBuffer`: quando a thread CPU precisa
ler um buffer enquanto o dono GPU tem uma lista aberta, uma segunda lista
DIRECT faz a copia na mesma fila e espera a fence. Ela enxerga somente o
conteudo ja submetido; a lista principal nao e fechada nem reiniciada. No
caminho do dono GPU, `GetBufferData` continua usando flush e leitura normal.
O mapa gerenciado de buffers foi protegido durante a leitura cruzada.

O smoke nativo agora abre uma lista, grava um upload ainda nao submetido,
faz a leitura em outra thread e verifica o valor anterior. Depois submete
a lista e verifica o valor novo. Resultado: PASS, 119 submissões, zero erro
na debug layer. A compilacao do projeto D3D12 e o publish NativeAOT do
CoreHost tambem passaram; avisos de NuGet/AOT permanecem.

No CoreHost DX12 real, o mesmo NSP atravessou a selecao de controle, a
abertura e a tela de loading que antes travava. Captura independente
`artifacts/pokemon-lets-go-d3d12-background-read-20260929-p00023.bmp`
mostra o Professor Carvalho com "Hello there! Glad to meet you!"; `p00026`
mostra o dialogo seguinte. `p00028` mostra o mesmo dialogo com Carvalho,
Cubone e Pikachu da captura de referencia do usuario. Isso confirma progresso funcional ate Carvalho,
nao paridade visual: o rosto e o fundo estao escuros/incorretos, e partes
da cinematica anterior continuam invertidas. Nenhum 60 FPS foi medido no
CoreHost DX12; os 60 FPS sao observacao do usuario no Ryubing original.
O runner pediu `Stop()` ao atingir o limite configurado de 420 segundos; nao
houve exception nova no `errors.log` durante a sessao. O log marcou
`GraphicsThreadJoined=False` apos 10 segundos, portanto o teardown da thread
grafica ainda nao foi comprovado limpo. Essa sessao nao demonstra estabilidade
prolongada nem autoriza empacotar MSIX sem regressao extra.

A cinemática 3D do Ryubox permanece invertida em capturas como
`artifacts/pokemon-lets-go-d3d12-reconciled-20260929-p00012.bmp`; nao
confundir isso com o frame correto do Professor Carvalho no Ryubing. A
captura `p00019.bmp` e apenas o loading preto com icone, nao progresso ate
o dialogo. O usuario informa que DX11 tambem alcanca Carvalho; ele serve
como referencia sem ser usado como fallback nesta rota DX12.

## Rastreamento grafico: copia sRGB/UNORM (29/09)

A captura DX12 `artifacts/pokemon-lets-go-graphics-trace-20260929-p00049.bmp`
confirma dialogo do Carvalho com rosto e fundo escuros. A escolha de controle
esta correta no mesmo processo (`-p00001.bmp`), mas a cinematica aparece
invertida (`-p00022.bmp`). Ambas pedem `cropFlipY=True`, e nenhuma usou
viewport negativo. Inverter todos os presents quebraria a tela correta.
Houve milhares de draws usando proxy color/depth; a contribuicao dele aos
defeitos visuais ainda nao foi isolada.

O log mostrou copia `R8G8B8A8Srgb -> R8G8B8A8Unorm`. Um teste de bytes
reproduziu perda de cor: 128 virou 55 porque a copia passava por shader e
aplicava transferencia sRGB. `D3D12GalTexture.CopyTo` agora faz copia nativa
para os pares RGBA/BGRA sRGB/UNORM, preservando os bytes. Os quatro sentidos
passaram na GPU fisica e no WARP; os demais testes de formato tambem passaram.
Isso prova a correcao da copia, nao ainda a correcao visual completa do jogo.

Reteste NativeAOT DX12: `artifacts/pokemon-lets-go-srgb-copy-20260929-p00014.bmp`
mostra a cinematica visivelmente mais clara que
`artifacts/pokemon-lets-go-graphics-trace-20260929-p00014.bmp`, mas ela
continua invertida. `artifacts/pokemon-lets-go-srgb-copy-20260929-p00046.bmp`
mostra o dialogo do Carvalho com fundo mais claro nas bordas; o centro do
fundo e o rosto dele ainda estao escuros. Portanto a copia sRGB era uma causa
real e parcial, nao a unica causa dos defeitos. Nenhum ajuste global de flip
foi aplicado. O proximo rastreio precisa distinguir a orientacao dos render
targets intermediarios da apresentacao final e identificar o pass/material
que escurece o centro da cena.

O modo `--stencil-only` do harness agora ativa guest counters explicitamente.
O aparente erro anterior era causado por `SamplesPassed` desativado; os 256
masks de stencil passaram nos dois formatos, em GPU e WARP. O algoritmo de
stencil nao foi alterado com base naquele falso positivo.

## Captura pareada da origem e da tela (29/09, continuacao)

O teste de formato agora distingue duas operacoes: `CopyTo` de subresource
preserva os bytes nos pares RGBA/BGRA sRGB/UNORM, enquanto `CopyTo` com
`Extents2D` faz um blit que decodifica/codifica sRGB. As quatro combinacoes
passaram na GPU fisica e no WARP. Converter o blit regional em copia de bytes
seria uma mudanca incorreta de semantica.

Foi adicionado `RYUBOX_D3D12_CAPTURE_SOURCE_INTERVAL` (desligado por padrao)
e `RYUBOX_D3D12_CAPTURE_SOURCE_DIR` ao D3D12Window. Eles permitem salvar a
textura de origem de um Present, alem da captura do backbuffer feita pelo
CoreHost. Um primeiro posicionamento do readback *antes* do blit interrompia
a command list e falhou com `TargetView requires an open command list`; essa
experiencia foi corrigida movendo o readback para depois do Present. O teste
de apresentacao com 4 combinacoes de FlipX/FlipY passou em WARP e na GPU
fisica com a captura ligada, e a segunda sessao do jogo atravessou mais de
3000 presents.

Pares da segunda sessao, com a textura bruta nos primeiros 1280x720 pixels de
um recurso 1920x1080 e a captura final 1280x720:

| Present | Origem | Tela | Conclusao |
| --- | --- | --- | --- |
| 300 | `artifacts/pokemon-lets-go-rawsource-20260929/source-present-000300.bmp` | `artifacts/pokemon-lets-go-source-paired-20260929-p00001.bmp` | Menu bruto invertido; `FlipY` o apresenta corretamente. |
| 900 | `artifacts/pokemon-lets-go-rawsource-20260929/source-present-000900.bmp` | `artifacts/pokemon-lets-go-source-paired-20260929-p00003.bmp` | Cena do Pikachu bruta correta; o mesmo `FlipY` a inverte. |
| 3000 | `artifacts/pokemon-lets-go-rawsource-20260929/source-present-003000.bmp` | `artifacts/pokemon-lets-go-source-paired-20260929-p00010.bmp` | Carvalho bruto invertido e final correto, mas rosto e centro do fundo ja estao escuros na origem. |

A diferenca nao e simplesmente "2D versus 3D": a cena do Carvalho tambem
contem geometria 3D e tem a orientacao bruta oposta a da cinematica do
Pikachu. O proximo passo e rastrear o *produtor da textura apresentada* e as
copias/blits ou passes intermediarios que mudam sua orientacao, preservando
uma unica convencao de origem por render target. Corrigir o `FlipY` global
quebraria o menu e Carvalho. O rosto e o fundo exigem rastreio separado dos
shaders, SRVs e materiais usados antes do Present; a captura final nao os
escurece.

A segunda sessao terminou posteriormente com falha do processo host
`powershell.exe`, codigo Windows `0xC0000006` (in-page error), registrado no
Application Error do Windows. O `errors.log` do CoreHost nao recebeu uma nova
exception de emulacao nesse reteste. O codigo de falha do host, sozinho, nao
permite atribuir causa ao DX12 nem afirmar que o jogo e estavel por longo
periodo. Nao foi gerado MSIX a partir desta instrumentacao experimental.
