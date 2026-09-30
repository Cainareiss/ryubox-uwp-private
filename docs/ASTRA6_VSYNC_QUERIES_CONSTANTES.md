# Astra6 — VSync e itens 1/2 da auditoria de desempenho

Data: 2026-09-09. Escopo: apresentação/VSync, queries e constant buffers. Não altera orientação, crop, SetMatrixTransform, input, JIT ou política de frame pacing por título.

## VSync

- `D3D11Window.ChangeVSyncMode` propaga o intervalo ao host: Switch = 1, Unbounded/Custom = 0. O modo Custom continua com a cadência guest; não recebe um segundo limitador de refresh do monitor.
- CoreHost aplica a configuração ao criar o renderer e registra `VSync`, `SyncInterval` e `PresentFlags`.
- O callback com intervalo é usado pelo caminho direto, compatível e fallback de `PresentBackBuffer`.
- Novo export nativo `RyujinxUwpGraphicsD3D11_PresentRendererFrameWithSyncInterval` recebe 0/1. O export antigo sem argumentos foi preservado, com intervalo 1, para hosts antigos.
- Não foram habilitadas flags de tearing nem alterado o Present do frontend/D3D12.
- O próximo pacote precisa conter o CoreHost novo **e** a DLL nativa nova. Não copiar somente o CoreHost para um pacote antigo: ele não terá o novo export.

## Item 1 — queries

- Oclusão usa resultado de 8 bytes; stream-output usa 16. `GetData` escreve diretamente no span, sem array/pin/cópia intermediários por tentativa.
- `UpdateCounters` faz polling sem laço de espera para resultado pendente. `GetData` pode submeter comandos (flags 0), permitindo progresso mesmo sem Present; não se usa DONOTFLUSH indiscriminadamente.
- `Flush` continua síncrono quando o guest realmente exige o resultado, com backoff, timeout e cancelamento na destruição do renderer. Quando há renderer threaded, o interrupt executa a consulta no proprietário do contexto.
- Erro ou timeout não vira sucesso com valor zero. O timeout começa na primeira consulta, não no End anterior a uma possível compilação longa.
- Query começa antes dos draws e termina no Report guest. `ResetCounter` cria uma nova época de acumulação, sem apagar reports anteriores ainda pendentes.
- Fila contém apenas reports pendentes. Query COM é liberada ao obter o resultado; callbacks são disparados após retirar o item da fila, permitindo reentrada sem perder outro report.
- Descartar um report não perde sua contribuição aos resultados cumulativos seguintes. Reset e destruição liberam queries ativas.
- Limite desta validação: contagem **não zero** foi verificada para SamplesPassed. PrimitivesGenerated mantém o mapeamento preexistente de stream-output; sua equivalência completa com o guest em todas as combinações de geometry/tessellation não foi certificada nesta rodada. Não interpretar estes testes como implementação integral de todos os contadores possíveis.

## Item 2 — constant buffers

- Fatias físicas continuam com 65.536 bytes, respeitando os 4.096 vetores declarados pelos shaders.
- Escritas e cópias marcam somente fatias sobrepostas como dirty; alterações GPU invalidam todas as fatias do recurso.
- Materialização ocorre no bind e antes do uso por draw/dispatch. Escrita em uniform já ligado não depende de um novo SetUniformBuffers.
- Uso de buffer DYNAMIC + Map/WRITE_DISCARD: cópia direta do shadow e preenchimento explícito do restante com zero, sem os dois arrays de upload de 64 KiB anteriores. Não usa NO_OVERWRITE ou offsets opcionais de D3D11.1.
- Apenas fatias consumidas são atualizadas. Mudança de identidade causada por eviction força rebind; fatias limpas não são reenviadas.
- Mantido o limite de oito fatias em cache por recurso. Os contadores internos `ConstantUploadCount`, `ConstantUploadBytes`, `GuestWriteBytes` e `ConstantCacheHits` permitem inspeção pelos testes; não são uma medição global de FPS.

## Validação local

`eng/D3D11MaterialRegression` executa em D3D11 WARP real, sem ROM, keys, janela do jogo ou frontend:

- 71 verificações aprovadas, incluindo as anteriores de viewport, orientação, cópias, swizzles e materiais.
- 512 queries concluídas deixam a fila vazia.
- Oclusão em draws de 8x8: resultados 64, 128 acumulado e 16 após reset/divisor 4.
- Callback reentrante não perde eventos; report descartado preserva a acumulação seguinte.
- Primeiro draw permanece vermelho depois do DISCARD seguinte; segundo draw passa a verde sem rebind, com leitura real do último vetor do cbuffer zerado.
- Compute também observa a atualização sem rebind.
- 101 escritas consecutivas resultam em um upload na fatia consumida.
- 1.000 atualizações consumidas alocaram 224.000 bytes gerenciados no teste local (inclui overhead do wrapper), sem arrays intermediários de 64 KiB. Não significa redução equivalente de tráfego GPU nem ganho de FPS medido.
- Fatias continuam corretas após cópia GPU, escrita parcial, invalidação e eviction.

Native Release compilado. Smoke nativo aprovado com intervalos 0/1, rejeição de intervalo inválido, export legado, blits/crop/flip, isolamento do frontend, resize e duas sessões. CoreHost Release compilado com zero erros e 18 avisos nessa recompilação.

Evidências:

- `artifacts/vsync-queries-constants-regression.log`
- `artifacts/vsync-queries-constants-repeat.log`
- `artifacts/vsync-native-build.log`
- `artifacts/vsync-native-presentation-test.log`
- `artifacts/vsync-corehost-build.log`

## Estado de entrega

Código e regressões locais concluídos para as mudanças descritas. **Sem publicação NativeAOT, novo MSIX ou instalação nesta rodada.** O MSIX 0.2.0.117 existente não recebe alterações por recompilar as bibliotecas. Não houve teste de desempenho em jogos no Xbox; não há promessa de 60 FPS nem certificação do Gate 2.

Referências do contrato nativo: [Microsoft — D3D11_QUERY](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query), [Microsoft — recursos dinâmicos](https://learn.microsoft.com/en-us/windows/win32/direct3d11/how-to--use-dynamic-resources).
