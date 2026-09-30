# Horizon Chase — correção D3D12 de texturas-buffer

Data: 21/09/2026

## Reteste silencioso apos a correcao

- O primeiro crash foi reproduzido como `IOException: dispositivo
  inexistente` durante a leitura do NSP no HD externo, nao como falha do
  D3D12. O reteste foi repetido com uma copia local de 921.320.202 bytes.
- O diagnostico de logs foi corrigido para nunca usar o diretorio atual do
  processo (que no NativeAOT podia ser `System32`). O harness agora fornece
  `RYUBOX_D3D12_LOG_DIR` por sessao.
- O host local iniciou, apresentou frames e nao registrou `deviceLost` nem
  excecao D3D12. A captura real ainda mostra somente o logo AQUIRIS; tres
  frames seriados em 300/600/900 apresentacoes permaneceram iguais. Portanto,
  a tela inicial/efeitos 3D ainda nao foram validados e a correcao nao deve
  ser declarada concluida no jogo.
- A sequencia de apresentacoes ficou muito lenta, com longos intervalos sem
  novos frames. Isso precisa ser separado da correcao de binding: e um
  bloqueio de progresso/CPU ou de entrada durante a transicao do splash.

## Diagnóstico

Na tradução D3D12, `StorageBuffer` e `BufferTexture` usam recursos nativos
com dimensão `BUFFER`, mas pertencem a namespaces diferentes:

- `space 1`: storage buffers GAL, alimentados por `SetStorageBuffers`;
- `space 2`: texturas e samplers GAL, alimentados por
  `SetTextureAndSampler`.

O caminho anterior classificava apenas pela dimensão do recurso. Assim, um
SRV de textura-buffer em `space 2` era procurado no dicionário de storage
buffers. Quando não encontrado, o material acabava usando o caminho errado ou
um recurso substituto, um caso compatível com texturas 3D pretas.

## Correção

`D3D12Pipeline.Bindings` agora considera um recurso `BUFFER` como storage
buffer somente quando `space == 1`. Recursos `BUFFER` em `space 2` seguem o
caminho de textura/SRV, preservando sampler, binding e descritor refletidos.
O caminho D3D11 não foi alterado.

## Validação

- Build de `Ryujinx.Graphics.D3D12`: PASS, 0 avisos, 0 erros.
- Regressão isolada D3D12: topology, sync, HS/DS, stencil D24/D32 e depth
  D16/D32: PASS em WARP.
- A bateria completa ainda para no teste independente `EdenReviewTests` com
  `Cold program did not compile once`; isso não é atribuído à correção de
  classificação de textura-buffer e não foi mascarado.
- O host silencioso do Horizon foi iniciado com captura e diagnóstico, mas a
  sessão atual ficou bloqueada durante `CoreHost.Start` antes de produzir uma
  nova captura. Portanto, ainda não há evidência visual D3D12 nova para
  declarar o bug resolvido no jogo.

## Próximo critério

Reexecutar o Horizon com o CoreHost publicado a partir desta revisão e exigir
uma captura real da cena 3D, além de confirmar no `binding_diag` que o SRV de
buffer aparece em `space 2` sem `SRV MISS`. Até essa captura, a correção está
compilada e coberta por regressões de backend, mas não certificada no jogo.
