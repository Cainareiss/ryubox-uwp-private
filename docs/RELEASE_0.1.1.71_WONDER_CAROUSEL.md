# Ryubox 0.1.1.71 — Wonder Ready / Carousel

## Marco gráfico

- Super Mario Bros. Wonder alcançou a tela inicial real pelo renderer D3D11.
- Correção da orientação de face frontal para o sistema de coordenadas do D3D11.
- Índices `UByte` são expandidos para `UInt16`, formato aceito pelo D3D11.
- SRVs são religadas depois das transições de render target para evitar hazards.
- Perfil portátil permanece forçado para reduzir o uso de memória no Xbox Series S.

Prova local: `mario-wonder-title-success.bmp`, capturada diretamente do framebuffer,
sem depender de uma janela visível. Na captura, o host apresentava aproximadamente
20–24 FPS e permaneceu abaixo do limite de 5 GB.

## Frontend

- Biblioteca real em carrossel horizontal virtualizado.
- Item selecionado centralizado, ampliado e animado.
- Busca por nome/arquivo, filtro NSP/XCI/NCA e ordenação A–Z/Z–A.
- D-pad e analógico navegam; A inicia; LB/RB saltam cinco jogos; View foca a busca;
  Menu abre configurações. B não encerra o aplicativo.
- Seleção anterior e tokens da pasta/keys/firmware continuam persistidos.
- Cache de capas limitado à seleção e dois vizinhos de cada lado, com miniaturas de
  352 px e cancelamento de carregamentos obsoletos.
- Estados explícitos para biblioteca vazia, busca sem resultado, erro e carregamento.
- Layout adaptativo para 1280x720 e 1920x1080.

## Pacote

- Identidade: `Ryujinx.Xbox.UWP`
- Versão: `0.1.1.71`
- Arquitetura: x64 UWP / Xbox Dev Mode
- Backend: D3D11-only; sem Mesa, Dozen ou Vulkan no MSIX.
- Assinatura: `CN=Ryujinx Xbox Dev`
