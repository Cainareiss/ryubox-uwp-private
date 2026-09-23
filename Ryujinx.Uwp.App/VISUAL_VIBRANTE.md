# Visual vibrante e compacto

O frontend agora usa uma composição de TV com três áreas: cabeçalho compacto, rail lateral fixa e conteúdo único da aba ativa. HOME, PROD KEYS, CONFIG e SOBRE não dependem de rolagem principal com mouse.

## Cores

| Uso | Cor |
|---|---|
| Confirmação e estado pronto | Verde Ryujinx |
| Navegação lateral | Violeta neon |
| Áudio e entrada | Azul vibrante |
| Foco dos cartões | Rosa vibrante |
| Avisos | Dourado |
| Erros | Vermelho |

## Animações

A troca de aba usa uma entrada horizontal curta de 14 pixels com fade de 180 ms. Os botões laterais aumentam levemente ao receber foco e reduzem durante o pressionamento. Os cartões da biblioteca também recebem escala e uma moldura rosa quando focados. Todas as animações são curtas para manter resposta imediata ao D-pad.

## Navegação

O D-pad e o analógico vertical movem o foco. Na HOME, os direcionais percorrem a biblioteca. `A` confirma o controle focado; `B` retorna à HOME; `LB/RB` percorrem abas; `X` abre PROD KEYS; `Y` abre HOME; `View` abre CONFIG; `Menu` abre SOBRE.

## Layout compacto

A aba CONFIG exibe três cartões lado a lado: emulação, áudio/entrada e avançado. PROD KEYS mantém o firmware em uma faixa superior e coloca importação, status e lista de chaves no mesmo painel. SOBRE é dividido em informações e recursos, sem `ScrollViewer` principal.

## Perfil de memória do Xbox

Todos os jogos são iniciados no perfil portátil. O front-end mantém o modo acoplado desligado e o CoreHost força `EnableDockedMode=false`, reduzindo a pressão de memória no Xbox Dev Mode.
