# Constantes desalinhadas e estado do pipeline — 21/09/2026

## Reprodução

Novo teste `ConstantBindingTests` (`--constants-only`) faz cópia GPU de dados,
usa CBVs nos offsets 0, 1, 4 e 255, com tamanhos 16 e 17, e verifica tanto o
resultado compute quanto todos os pixels de um draw verde. Alterna programas
sem reenviar as constantes entre o compute e o draw.

- DLL `artifacts/logs9-native/Release`: todos os casos passaram em WARP.
- Código nativo atual recompilado em `artifacts/d3d12-constants-baseline`:
  acesso inválido 0xC0000005 no readback após o caso de 17 bytes.

## Causa e alteração

`BindProgram` fazia snapshots CBV com `ByteCopy` depois de configurar as root
tables. `ByteCopy` troca a root signature compute e o PSO e muda o estado do
buffer de origem. Isso invalidava o estado necessário ao shader guest.

Correção: configurar root signature/tables depois das operações internas,
restaurar estado da origem e reaplicar o PSO gráfico após `BindProgram`.
Dispatch compute já reaplicava seu PSO, mas perdia a root signature/tables.

Esta regressão do código atual NÃO explica por si só a tela preta do Mario
executada com `logs9-native`, que passa os testes. Não declarar os jogos 3D
corrigidos sem captura real e teste da fase. D3D11 permanece inalterado.

## Validação

Build corrigido: `artifacts/d3d12-constants-fixed`.
Sessão diagnóstica Mario: `artifacts/mario-deluxe-black-probe`, usando a DLL
anterior com probes de bindings/alvos, sem substituir uma sessão em execução.

Build corrigido concluído; `--constants-only` aprovado em WARP e GPU física,
com zero erros da debug layer: oito combinações offset/tamanho, resultado
compute, 64 pixels por draw e troca de programas preservando constantes.
Inclui teste de front-face e backface rejection, aprovado também na DLL antiga.
A diferença textual para D3D11 não justifica inverter o culling globalmente;
essa alteração não foi realizada.

Probes do Mario confirmam alvos pretos em RGBA8, inclusive um passe sem culling.
Os primeiros trinta draws tinham CBVs resolvidos, sem MISS. Não exclui erros
de conteúdo nem os programas posteriores. O diagnóstico foi ampliado para
128 programas distintos, evitando consumir o orçamento apenas no splash.
A janela não foi fechada pelo agente. Nenhuma certificação de gameplay ou
correção geral dos jogos 3D é feita nesta rodada.

## Reteste com CoreHost atualizado

Publicação NativeAOT concluída às 11:33 locais, com avisos AOT/trim.
Runtime `artifacts/mario-deluxe-fixed-probe`, PID inicial 13144, iniciado
às 14:42 UTC com a DLL nativa corrigida. A sessão apresentou frames mas os
probes continuaram pretos. Logo, a regressão corrigida não resolveu o Mario.

Diagnóstico opt-in agora preserva HLSL e bytes dos CBVs por programa. A matriz
do primeiro draw não está zerada; não há CBV MISS nos casos examinados.

`GuestShaderReplayTests`, ativado com `RYUBOX_SHADER_REPLAY_PREFIX`, executou
os shaders reais do primeiro draw e seus CBVs capturados com geometria e textura
verde controladas: todos os pixels verdes, WARP e GPU física, debug layer limpa.
Isso valida esse par de shaders/constantes em condições controladas, não os
demais shaders nem os dados reais de vértices/texturas/estado do jogo.

Próximo diagnóstico necessário: preservar os vertex/index buffers, textura
amostrada e estado completo do mesmo draw e reproduzir a captura sem substituí-los.
Comparar conteúdo antes/depois de uploads/cópias e o mapeamento de atributos.
Não alterar culling, formatos ou shaders globalmente com base somente na tela preta.

## Reprodução com geometria capturada

`artifacts/mario-deluxe-input-probe`: primeiro draw usa quatro vértices,
stride 32, atributo R32G32B32Float e índices U16 `0,2,1,1,2,3`.
Matriz capturada válida. Textura RGBA8 inicial de 1920x1080 contém somente
zeros (isso pode ser o conteúdo inicial de uma superfície, não prova falha de upload).

Replay preservando shaders, constantes, vértices e índices, mas substituindo
a textura por verde: Clockwise (estado capturado) produz 0/64 pixels;
CounterClockwise produz 64/64. Reproduzido em WARP e GPU física.
O teste anterior usava geometria/controlador de estado nativos e não cobria
a conversão guest. Por isso passar aquele teste não excluía essa divergência.

Correção aplicada em Execute: inverter FrontCcw somente para programas com
UsesGuestSpecialization, equivalendo à conversão de origem do D3D11 para os
shaders guest. Passes internos e HLSL nativo mantêm a convenção host.
Replay com flag guest agora exige 64 pixels e rejeita a face oposta; PASS em
WARP e GPU física. Testes de constantes/pixels nativos continuam passando.
Reteste posterior: usuário confirmou a tela Now Loading de Mario e gráficos
3D corretos em Horizon Chase. Isso não certifica outros jogos ou gameplay de Mario.

## Pokémon Legends Z-A — reteste de 21/09

Runtime `artifacts/pokemon-za-winding-fixed`, CoreHost D3D12, sem probes de
readback. Captura `window-current.png` confirma a seleção de idioma.
Primeiro processamento de frame pesado levou 81.813 ms; houve pausas posteriores
de cerca de 21 segundos. Não atribuir esses tempos exclusivamente a shaders sem
perfil detalhado.

Às 19:29:03 UTC a emulação encerrou com COMException 0x80070057 em
`D3D12Program.inl:756`, `Require(found)` na validação da assinatura de saída
para stream output durante CreateProgram. O encerramento ocorreu antes dos
comandos de teclado enviados nesta rodada; portanto eles não validam navegação
nem passagem da seleção. DeviceLost=False. A janela pode permanecer exibindo
a última imagem apesar da sessão encerrada.

Revisão posterior: a comparação de componentes de stream output com máscaras
de registradores compactados foi corrigida e validada com bytes reais em WARP
e GPU física. Ver [regressão de stream output](D3D12_POKEMON_STREAM_OUTPUT_20260921.md).
O reteste do jogo encontrou também acesso inválido de memória guest; tela
inicial ainda não alcançada. Não remover a validação ou ignorar saídas.
