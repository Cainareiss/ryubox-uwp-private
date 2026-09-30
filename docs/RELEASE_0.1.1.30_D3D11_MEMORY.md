# Ryujinx Xbox UWP 0.1.1.30 — D3D11 e correção de memória

Esta build trata a suspensão observada no Xbox durante a expansão inicial do
heap do Switch. Nos logs da versão anterior, keys, firmware, NSP, superfície e
renderer D3D11 eram inicializados, mas o processo era suspenso imediatamente
após `SetHeapSize(size: 0xCB000000)`, antes do primeiro frame da GPU.

## Correções principais

- Mantém páginas recém-commitadas como memória esparsa quando o preenchimento
  solicitado é zero. O Windows já garante que essas páginas nascem zeradas;
  por isso não é necessário tocar fisicamente cerca de 3,17 GiB de uma vez.
- Conserva o preenchimento explícito para páginas reutilizadas e para valores
  diferentes de zero.
- Detecta o orçamento de memória UWP atual e esperado no Xbox.
- Impede a inicialização do jogo quando o pacote está com orçamento inferior a
  3 GiB e mostra instruções para mudar o tipo de aplicativo para `Game`.
- Registra uso, limite atual, limite esperado e nível de pressão em
  `Logs/Frontend/memory.log`.
- Registra tempos detalhados antes e depois da expansão do heap.
- Corrige a telemetria do loop gráfico: `totalProcessed` agora conta somente
  comandos FIFO realmente processados, e não iterações ociosas.

## Configuração obrigatória no Xbox

Depois de instalar ou atualizar o pacote:

1. Abra o Xbox Dev Home.
2. Selecione o Ryujinx.
3. Abra `Show details`/`Exibir detalhes` pelo botão View/Menu.
4. Altere `Application type` de `App` para `Game`.
5. Feche e abra o Ryujinx novamente.

Aplicativos classificados como `App` recebem aproximadamente 1 GiB no Xbox;
jogos podem receber até 5 GiB. Essa classificação é configurada no console e
não pode ser imposta pelo manifesto MSIX.

## Validação realizada

- Build Release x64: concluído sem erros.
- Assinatura MSIX: válida, `CN=Ryujinx Xbox Dev`.
- Manifesto/pacote: versão `0.1.1.30`.
- Instalação local: concluída.
- Inicialização local do frontend: concluída e responsiva.
- Leitura local do orçamento de memória: concluída.
- Pacote D3D11: nenhuma DLL Mesa, Dozen, Vulkan, DXIL ou DXC incluída.
  `spirv-cross-c-shared.dll` permanece porque traduz shaders SPIR-V para HLSL.

SHA-256 do MSIX:

`D8EB430F3E2F55BBA942156231F680B78EFE48D1AA6E1822E87FE498B9D9C191`

## Limite da validação local

O firmware não está presente no diretório local de teste atual, portanto não
foi possível repetir no PC a sequência completa de boot do Shovel Knight. O
teste decisivo desta correção precisa ser feito no Xbox com o aplicativo
classificado como `Game`. Os novos logs permitem distinguir falha de orçamento,
expansão de heap, processamento FIFO e apresentação do primeiro frame.
