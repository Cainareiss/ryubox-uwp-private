# Release 0.1.1.75 — Wonder Core e apresentação D3D11

## Base

A release combina o frontend funcional 0.1.1.73 com o núcleo da linha 0.1.1.71 que alcançou a tela inicial de Super Mario Bros. Wonder. O frontend não foi reconstruído a partir da variante regressiva: seu executável foi preservado e recebeu somente um patch direcionado para consumir o evento de sistema `BackRequested`.

## Correções do compositor

- `ImageCrop.FlipY=true` preserva a ordem vertical das linhas; `false` realiza a inversão exigida pela convenção de apresentação dos backends Ryujinx.
- Crops lógicos de tela inteira são normalizados quando a textura está uniformemente escalada, evitando o recorte 1280x720 sobre uma origem 1920x1080.
- O frame de destino e suas barras são preenchidos com alpha 255 para impedir vazamento visual do launcher.
- A telemetria de apresentação registra apenas frames de diagnóstico selecionados.

## Validação

- `Ryubox.FrameComposerTests`: aprovado.
- NativeAOT x64: aprovado.
- Execução visível de Sonic Mania por D3D11: imagem na orientação correta, enquadramento 16:9 e apresentação contínua.
- Conteúdo desempacotado do MSIX comparado com os binários de build: aprovado.
- Assinatura Authenticode: válida.

Artefato final:

`E:\ryunxins lançamentos\TESTES\Ryubox-Xbox-UWP-0.1.1.75-Wonder-Core-Orientation-Fix\Ryujinx.Uwp.App_0.1.1.75_x64.msix`

SHA-256:

`5C7D002F426AB206331A0247F9AF814918143120F44753F39D1EC24CB8378EDB`
