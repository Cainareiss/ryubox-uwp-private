# Pokémon Let's Go — cena do professor (30/09/2026)

## Resultado confirmado

A perda dos olhos/detalhes do rosto e o centro preto do fundo já aparecem no
render target de origem, antes do `Present`. Portanto, o flip final explica a
orientação, mas não a perda desses elementos.

Foi feito um A/B na mesma cena com um CoreHost e uma DLL nativa recompilados
da mesma revisão:

- Caminho normal: proxy de cor quando RTV 1920×1080 e DSV 1280×720.
  Captura: `artifacts/pokemon-lets-go-srgb-copy-20260929-p00046.bmp`.
- `RYUBOX_D3D12_DIAG_SKIP_ATTACHMENT_PROXY=1`: o backend nativo desanexa o
  DSV incompatível para os draws afetados. A telemetria registrou
  `depthDetachedDelta > 0`. Captura:
  `artifacts/pokemon-oak-no-proxy-20260930-p00010.bmp`.

O rosto e o fundo continuaram incorretos no segundo caso. O proxy não é a
causa principal desse defeito; mantê-lo como padrão até outro teste visual.

## Bindings

Foi adicionado `RYUBOX_D3D12_BINDING_DIAG_START_DRAW` para começar o dump
opt-in após os menus. Com início no draw 40.000, foram observados 31 programas
distintos, nove slots de textura de fragmento disponíveis e nenhum `SRV MISS`
ou `CBV MISS` nesses primeiros usos. Dados em
`artifacts/pokemon-oak-binding-diag-20260930/`.

Isso **não** prova a correção das texturas: o dump atual amostra cada programa
somente na primeira ocorrência e não registra formato/dimensões/conteúdo de
cada SRV em todos os draws. O material pode mudar dentro do mesmo programa.

## Próximo teste que decide a causa

Identificar o draw que cobre um pixel do rosto e comparar, no mesmo frame,
SRV + subresource + dados da textura, CBV de material, shader de fragmento,
depth/stencil e blend com o Ryubing. Se os recursos recebidos forem idênticos,
inspecionar a tradução de shader/estado de profundidade; se houver diferença,
seguir a origem no texture cache/binding. Não aplicar correção global de gamma,
flip ou depth sem essa comparação.

O teste com DLL nativa antiga falhou antes do primeiro draw por contrato de
`BeginCommands`; foi descartado e repetido com a DLL recompilada. O build
gerenciado e o build nativo posteriores terminaram sem erros.
