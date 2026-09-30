# Frontend completo D3D12 — integração em andamento

Pedido vigente: menu completo ImGui e jogos D3D12 no MSIX para teste Xbox,
sem substituição silenciosa por D3D11. D3D11 permanece uma opção separada.

## Implementação desta rodada

- `ImGuiD3D12Renderer`: backend dos draw lists ImGui sobre a ABI D3D12 nativa;
  projeção, fontes, capas, vertex/index buffers, offsets, alpha blending,
  scissor e submissões intermediárias antes de esgotar samplers. IDs de textura
  são recursos D3D12, não ponteiros SRV D3D11. Callbacks desconhecidos são
  recusados explicitamente; não são ignorados como se tivessem executado.
- `ImGuiD3D11Shell` mantém o nome histórico, mas compartilha somente a lógica
  de interface/input entre os dois backends. `InitializeD3D12` não cria device
  D3D11 nem usa D3D11On12. Carrossel, configurações e catálogo são reaproveitados.
- `D3D12FrontendHost`: inicialização HWND/SwapChainPanel/offscreen, render,
  resize, capas e ações. Lease por ID de sessão transfere o contexto ao guest;
  render/resize/shutdown do frontend são recusados durante o lease, ações de
  input ficam bloqueadas e retorno exige liberação dos botões.
- Rotas de ações selecionam o frontend D3D12 ativo; D3D11 preservado.
- Encerramento explícito dos modelos/serviços antes de destruir o contexto.
  Workers ScreenScraper recebem cancelamento e join antes de destruir estado;
  não continuar destacados após descarte dos objetos/DLL.

## Desempenho

- `Context::Read` não espera mais a submissão anterior **antes** de gravar a
  cópia: a mesma fila já ordena writes/copy. Continua esperando a fence da cópia
  antes de mapear e mantendo as fences de reutilização dos command allocators.
- Desenho não indexado ignora corretamente primitive restart: não cria buffer
  temporário de índices apenas porque o estado permaneceu habilitado.
- `Build-RyujinxUwpNative.ps1` antes não passava `/O2`, mesmo em Release. Agora
  usa `/O2 /DNDEBUG /Z7 /fp:precise`, preserva PDB e invalida objetos quando o
  script muda. [MSVC usa otimização desabilitada por padrão](https://learn.microsoft.com/en-us/cpp/build/reference/od-disable-debug?view=msvc-170).
  Isso não prova que a compilação sem otimização explique todo o FPS baixo.
- Perfil CPU Sonic: 48.246 amostras, 46.287 com pilha, zero eventos perdidos.
  Folhas: kernel 21.089, CoreHost 14.546, endereços não mapeados/JIT 8.662,
  DLL nativa 68, driver Intel D3D12 62. Sem símbolos de funções suficientes
  para atribuir uma causa única. Não equivale a tempo de espera nem tempo GPU.

## Testes / evidência

`eng/Test-D3D12Frontend.ps1`: verifica pixels de interface e capa, lease,
recusa de session ID antigo, bloqueio de input e de shutdown durante jogo,
frame gravado pelo proprietário guest, retorno, resize e segunda sessão.
Exige debug layer, não somente ausência de exceção gerenciada.

A primeira rodada WARP desenhou a interface (19.290 pixels claros e 16.478
pixels da capa vermelha), com zero erros de debug. Entretanto o processo
terminou com `0xC0000409`: **não é PASS integral**. Encerramento dos serviços foi
corrigido após esse resultado; a nova execução deve terminar com exit code 0.

`GalTests` agora verifica pixels e nenhuma criação placed extra para um draw
não indexado com restart habilitado. Reexecutar a suíte inteira na DLL nova.

## Ainda falta antes do MSIX

### Atualização da integração gerenciada — 15/09/2026

- Frontend nativo e suíte GAL completa aprovados em WARP e Intel; regressão
  D3D11 aprovada. O encerramento `0xC0000409` acima foi corrigido e as novas
  execuções terminaram com código zero.
- `UwpGraphicsHost` encaminha input, ponteiro, catálogo, capas, mensagens,
  render e DPI para a ABI D3D12. `MainPage` inicializa D3D12 e o coordenador
  passa o backend ativo ao CoreHost; nenhuma troca automática para D3D11.
- CoreHost adquire o contexto do frontend no caminho não-desktop. A GAL usa
  SafeHandle não proprietário; a thread GPU finaliza comandos e queries antes
  de entregar a limpeza à thread de encerramento. Falha no cleanup bloqueia
  reutilização, em vez de permitir acesso simultâneo ao contexto.
- `eng/D3D12FrontendRegression`: três sessões GAL com pixels, descarte em outra
  thread, memória de recursos e retorno ao menu; falha de criação de renderer
  também preserva o contexto. Primeira execução aprovada WARP e Intel.
- Build UWP gerenciado e CoreHost Release aprovados, com avisos preexistentes.
  NativeAOT publicado em `artifacts/d3d12-frontend-aot`; contém avisos de
  trimming/AOT e ainda requer teste do binário, não equivale a PASS no Xbox.
- Complementos em validação: resize/DPI adiado durante lease, overlay FPS
  desenhado em D3D12, contagem somente após Present real e teste de CoreHost
  compartilhando o contexto do menu, sem `LOCAL_WINDOW_TEST`.
- O primeiro teste CoreHost/Sonic compartilhou o contexto e encerrou com retorno
  ao menu, mas o PC não produziu apresentação em 70 s. A repetição quente
  produziu uma captura em ~15 s, porém a inspeção visual encontrou somente
  fundo escuro e o badge `FPS 0.0`. O contador da GAL no log já registrava
  apresentações depois; o detector anterior confundia clears coloridos com
  jogo. Agora exige diversidade de cores fora da área do badge. **A imagem
  real do Sonic e a execução no Xbox ainda não estão comprovadas.**

Os itens abaixo são a lista de partida; as rotas 2/3 já estão implementadas,
mas aceitação final UWP, teste de jogo e pacote ainda não estão concluídos.

1. Aprovar build otimizado, frontend WARP/Intel e regressões GAL/D3D11.
2. Ligar a ABI de frontend no `UwpGraphicsHost` e substituir as rotas que hoje
   só encaminham catálogo/input/capas para D3D11.
3. Fazer CoreHost adquirir o contexto existente com ownership emprestado,
   liberar todos os recursos GAL e devolver o lease **após** cleanup. Hoje
   CoreHost D3D12 ainda recusa execução fora do host desktop.
4. Integrar seleção de backend, início/falha/retorno/suspensão/resize/DPI na UI;
   manter apresentação exclusiva e a barreira de input já corrigida.
5. Validar implantação UWP local, duas sessões completas, input, firmware,
   cores e orientação; publicar NativeAOT, conferir DLLs/exports/assinatura.
6. Gerar MSIX só com esse caminho funcional e identificado nos logs. Xbox e
   ganho de FPS dependem do teste real no console, não de aprovação sintética.

Não foi gerado MSIX nesta etapa. A tradução Mesa SPIR-V→DXIL continua bloqueada
por ABI não comprovada; shaders HLSL/DXBC executam em D3D12 nativo.
