# Parte 1 D3D12 — revisão e correções após o relatório Gemini

11/09/2026. Estado: **parcial; não certificado como 95% ou 100%**.

Atualização de escopo em 13/09/2026: o usuário autorizou as dependências das
partes 2/3 necessárias à parte 1. Este relatório preserva a evidência da rodada
de 11/09; suas restrições de autorização não se aplicam mais a essas dependências.

Foram preservadas as implementações recebidas e corrigidos defeitos verificáveis.
Aprovar a bateria existente não demonstrava cobertura integral dos contratos GAL.
Não houve ativação no CoreHost, jogo, MSIX, instalação nem desligamento nesta rodada.

## Correções implementadas

| Área | Defeito/risco encontrado | Correção e evidência |
| --- | --- | --- |
| Janela | `SetSize(0,0)` preservava dimensões positivas; `Present` continuava trabalhando | Estado de suspensão separado das dimensões alocadas. Teste exige zero submissões durante suspensão e retomada no mesmo tamanho e em outro tamanho |
| Dispatcher GPU | Timeout não invalidava a closure; entrega duplicada também podia repetir comandos | Transição atômica pending/running/cancelled/finished; cancela ações ainda pendentes, rejeita thread errada e descarte; ações já iniciadas são aguardadas até terminar |
| Culling | FrontAndBack classificava a topologia de entrada, mesmo com GS mudando a saída | Reflexão nativa usa saída GS ou tessellator antes de resolver o descarte; conserva execução dos estágios anteriores |
| Estados | Setters de culling/depth bias podiam alterar campos antes de falhar; enums inválidos eram aceitos | Validação antes de mutação; testes conferem bias/clamp/fator em transições e preservação após valores inválidos |
| Cleanup | Callback podia reentrar em `Dispose`; novos programas podiam começar durante encerramento | Estado `disposing`, descarte na thread proprietária, rejeição de novas compilações/ações durante cleanup |
| Counters | Descarte concorrente podia zerar segmentos durante resolução | Serialização por evento de conclusão, Invalid, reserva e descarte; Flush não espera pelo dispatcher segurando o lock |

O descarte por culling não pula o draw: o teste existente verifica escritas UAV
do vertex shader. Os novos casos verificam ponto IA → triângulo GS (não desenha)
e triângulo IA → ponto GS (desenha um pixel). O segundo caso falhou com a DLL
nativa anterior e passou com a recompilação corrigida.

A classificação usa os campos `GSOutputTopology` e `HSOutputPrimitive` documentados
em [D3D12_SHADER_DESC](https://learn.microsoft.com/en-us/windows/win32/api/d3d12shader/ns-d3d12shader-d3d12_shader_desc).
Os testes novos exercitam a mudança de saída GS; não constituem cobertura de
todas as combinações de culling, tessellation e stream output.

## Validações adicionadas/reforçadas

- Dispatcher: entrega normal/duplicada, timeout seguido de entrega tardia,
  dispatcher que enfileira e lança exceção, thread errada e entrega após Dispose.
- Criação de programas em background concorrente com Dispose: todo objeto
  publicado deve terminar com ID zero e ProgramCount deve terminar em zero.
- 256 disputas entre conclusão de counter e Dispose, sem callback duplicado
  nem callback após retorno do descarte.
- Arrays de texturas/imagens/samplers com offset, sentinelas não nulas,
  ranges inválidos e conferência de que nenhum slot foi parcialmente alterado.
- 200 ciclos de textura/vista: registro gerenciado e LiveResourceBytes nativo
  retornam ao valor inicial. Isso não mede memória do processo inteiro.
- Cleanup reentrante com exceção de callback: erro preservado, device liberado.
- Falha nativa controlada por orçamento: pedido de buffer de 512 MiB enquanto
  há alocações vivas é recusado antes da alocação. Cleanup libera os objetos,
  preserva o erro e uma nova instância funciona. Não esgota RAM e não simula
  remoção física do device nem todas as falhas de submissão/readback.
- O teste de depth bias agora confere estado efetivo, não apenas chamadas de
  setters; **ainda não comprova resultado de depth/stencil por pixels**.

## Resultado e reprodução

Todos os testes abaixo passaram com a DLL integrada recompilada:

| Validação | Resultado | Evidência em artifacts/ |
| --- | --- | --- |
| Bateria GAL/SPIR-V, WARP + debug layer | PASS | `d3d12-part1-review-warp.log` |
| Bateria GAL/SPIR-V, Intel HD 620 + debug layer | PASS | `d3d12-part1-review-intel.log` |
| Testes nativos, WARP | PASS; debugErrors=0 | `d3d12-part1-review-native-warp.log` |
| Testes nativos, Intel 8086:5916 | PASS; debugErrors=0 | `d3d12-part1-review-native-intel.log` |
| Regressão apresentação D3D11 | PASS | `d3d12-part1-review-d3d11.log` |
| Build CoreHost Release | 0 erros; 15 avisos existentes | `d3d12-part1-review-corehost.log` |

Build da bateria: `dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore`.
Executar a DLL resultante com os dois caminhos abaixo, acrescentando `--warp`
para WARP; sem essa opção usa a GPU física. Ambos habilitam a debug layer.

Binários usados (SHA-256):

- `artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`:
  `3FA5274037A6042477A22AEA98A9DB3A3EA296043A2D7A8695076D6F9A050674`.
- `artifacts/d3d12-spirvcross-validation/spirv-cross-c-shared.dll`:
  `82873DD808AA7C10F3D105D11FB47BB5B9A54D244D5DB42DCB3BB1BC015DBF76`.

A DLL integrada precisa acompanhar o código gerenciado atualizado: o campo
de descarte de draw passa a transportar flags, resolvidas por reflexão nativa.
Não reutilizar a DLL anterior nem a DLL standalone antiga de validação.
O compilador SPIRV-Cross do pacote D3D11 não foi substituído.

## O que continua impedindo 100%

Estas lacunas não foram removidas do escopo de aceitação nem transformadas em
no-op. As exceções explícitas continuam indicando operações não implementadas.

| Contrato | Lacunas ainda reais | Dependência |
| --- | --- | --- |
| ITexture | Só RGBA8 2D, um mip/layer/sample e swizzle identidade; faltam vistas reinterpretadas, buffer textures e matriz de formatos | Parte 3 |
| IPipeline | Depth/stencil, MRT, MSAA, múltiplos viewports/scissors e máscaras completas | Recursos/vistas da parte 3 |
| IPipeline/samplers | Modos de rasterização e endereçamento que exigem emulação em shaders, wide lines, point sprites, clip masks, blends restantes | Parte 2 |
| IWindow | AA, filtros além de nearest/bilinear, VSync customizado, color-space passthrough | Apresentação; formatos e contrato de frequência |
| Lifecycle/counters | Falhas controladas cobertas não substituem stress de device removal, todos os pontos de falha e validação dos consumidores UWP | Testes adicionais de parte 1/plataforma |
| Programas/capabilities | Estágios/tradução geral permanecem parciais; testes de regressão não completam todos os contratos guest | Parte 2 |

Host mapping/sparse/conditional rendering recusados com fallback negociado não
são automaticamente defeitos. Cada caminho deve ser avaliado no consumidor.
Compilar CoreHost não equivale a publish NativeAOT nem teste no Xbox.

Para encerrar **100% global da parte 1**, será necessário autorizar as dependências
de formatos e shaders nas partes 2/3, além de finalizar os testes de falha/plataforma.
Enquanto a instrução for não iniciar essas partes, o status correto continua parcial.
