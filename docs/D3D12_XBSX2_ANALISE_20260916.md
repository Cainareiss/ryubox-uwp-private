# Análise do D3D12 do XBSX2 v2.0.8.5

Atualizado em 16/09/2026. A revisão foi feita no checkout local
`C:\Users\Cainã\Downloads\xbsx2-v2.0.8.5-review`, no commit
`e6dad6695f2b24e02d6bd07383923c825219201d`, correspondente à release
`v2.0.8.5`. O XBSX2 é um fork do PCSX2; portanto, o código analisado é uma
referência de arquitetura D3D12 para Xbox, não um backend que possa ser
copiado para a GAL do Ryubox sem adaptação.

## Conclusão curta

Sim, há melhorias aproveitáveis, mas o ganho do XBSX2 não vem de uma única
ABI ou de um “driver secreto”. O resultado é a combinação de:

1. command lists rotacionadas por fence;
2. heaps shader-visible grandes e lineares por command list;
3. cache de grupos de sampler para não copiar descritores repetidos;
4. cache persistente de shaders e PSOs;
5. stream buffers circulares com posições GPU e fences;
6. tratamento explícito de swap chain, estados de recurso e resolução no Xbox.

O ring fence-aware do Ryubox já cobre a parte mais urgente de descritores e
passou nos testes funcionais e de pressão. O próximo ganho provável não é
aumentar cegamente o heap, mas reduzir cópias repetidas, recompilações de PSO
e criação de recursos de upload.

## O que foi encontrado no XBSX2

### Descritores e fences

`GSDevice12` mantém três conjuntos de command-list resources. Cada conjunto
tem command allocators, command lists, allocator de CBV/SRV/UAV com 32.768
slots e allocator de samplers com 2.048 slots. Ao avançar para o próximo
conjunto, ele espera a fence correspondente, reseta o allocator e só então
reutiliza os descritores. Isso é uma política linear por frame/fence, não um
ring global com páginas crescendo.

O `D3D12GroupedSamplerAllocator` indexa grupos pelo conjunto de índices dos
samplers. Se o grupo já existe, reutiliza o endereço GPU; só copia os
descritores quando encontra uma combinação nova. O cache é invalidado ou
resetado de forma controlada quando o conjunto é reciclado.

### Memória e uploads

O XBSX2 usa D3D12MA para centralizar alocações e posterga a destruição de
recursos até a fence do command list que os utilizou. Isso reduz fragmentação e
mantém a vida útil coerente com a GPU. O uso não é uniforme: há caminhos
committed para buffers/texturas grandes ou especiais, então não é correto
atribuir todo o desempenho a subalocação.

`D3D12StreamBuffer` implementa um buffer circular de upload: reserva por
alinhamento, registra posições GPU em uma fila de fences, trata wrap e espera
somente quando a região necessária ainda está em uso. Esse padrão é aplicável
ao upload de constantes e staging do Ryubox.

### Shaders e PSOs

O cache do XBSX2 persiste shaders e pipeline states. A chave inclui hash da
fonte/macros, entry point, tipo e tamanho; a chave de PSO inclui a descrição
completa do pipeline, incluindo estados raster/depth/blend e formatos dos
targets. Cache corrompido ou de versão incompatível é descartado.

O Ryubox já tem cache de shader GAL/D2P e o caminho SPIR-V/DXIL experimental,
mas ainda deve separar claramente cache de shader traduzido de cache de PSO
gráfico nativo persistente. Copiar o formato binário do XBSX2 não é necessário;
o contrato e a chave precisam ser próprios da GAL.

### Apresentação no Xbox

Na release analisada, a swap chain usa três buffers no Xbox, transições
explícitas para render target/present e reaplicação após resume. O port também
limita a resolução por modelo: Xbox One/One S em 1080p, Series S em 1440p
porque uma swap chain 4K causa crash, e Series X em até 4K. A resolução real do
HDMI é consultada e limitada por esse teto.

Esse ponto é especialmente relevante para o Ryubox: estabilidade de
apresentação e o limite do Series S devem ser tratados antes de buscar mais
FPS. A própria release do XBSX2 registra correções de VSync/apresentação,
transições de recursos no caminho unificado de memória e o crash de swap chain
4K no Series S. Consulte a [release oficial v2.0.8.5](https://github.com/XboxEmulationHub/XBSX2/releases/tag/v2.0.8.5).

## O que adaptar ao Ryubox

### Prioridade alta

- **Cache de grupos de samplers:** adaptar o conceito ao nosso ring. A chave
  deve apontar para descriptors CPU estáveis e o resultado deve continuar
  protegido pela fence da submissão. Isso pode diminuir cópias de sampler sem
  alterar shaders.
- **Cache persistente de PSOs D3D12:** adicionar uma camada após a reflexão e
  antes de `CreateGraphicsPipelineState`/`CreateComputePipelineState`, com
  chave incluindo bytecode, root signature, formatos, blend, raster,
  depth/stencil, topology e layout de vértices. Invalidar por versão e GPU.
- **Stream buffer fence-aware:** aplicar ao upload de constantes/staging para
  reduzir `CreateCommittedResource` pequenos e esperas desnecessárias.
- **Destruição adiada por fence:** unificar recursos, heaps e descritores
  temporários em uma fila de retirement por contexto, evitando liberar algo
  ainda referenciado por command list.
- **Apresentação Xbox:** implementar três buffers, revalidação no resume,
  transições PRESENT/RT explícitas e limite de resolução específico do modelo.

### Prioridade média

- Contadores de GPU por command list para distinguir tempo de execução, espera
  de fence e custo de cópia de descritores.
- Reutilização de command allocators/listas em vez de criar listas temporárias
  em caminhos de upload.
- Perfil de orçamento e pressão de memória separado por recurso, upload e
  descritor, com diagnóstico no CoreHost.

## O que não deve ser transplantado diretamente

- Root signatures, formatos, PSOs e shaders específicos do GS/PS2 do PCSX2.
- APIs de resolução, HDMI e modelo do Xbox sem isolar a camada UWP do Ryubox.
- D3D12MA inteiro como substituição imediata do allocator já existente; primeiro
  comparar contratos de ownership, alinhamento, device removal e NativeAOT.
- O allocator linear fixo do XBSX2 como substituto do ring atual. Ele funciona
  porque há três command lists e uma política de fence diferente; no Ryubox o
  ring por páginas já foi necessário para cruzar a capacidade dentro de uma
  lista com queries abertas.
- Binários, DLLs ou headers sem confirmar ABI, arquitetura, licença e origem.

O XBSX2 é um projeto GPL-3.0-or-later. Uma reimplementação baseada no
comportamento e nos contratos é mais segura do que copiar código, mas qualquer
transplante literal exigiria revisão de licença, avisos e compatibilidade com
as licenças do Ryubox.

## Estimativa de ganhos

Os ganhos abaixo são expectativas técnicas, não resultados medidos no Xbox:

| Adaptação | Ganho esperado | Risco |
| --- | --- | --- |
| Grupos de samplers | Menos cópias e menos pressão no ring em cenas repetitivas | Médio; invalidar cache na hora errada troca sampler |
| Cache persistente de PSO | Menos stutter e menor tempo de inicialização após aquecimento | Médio; chave incompleta gera imagem incorreta |
| Stream buffer por fence | Menos alocações pequenas e menos commit/fragmentação | Médio; erro de fence causa corrupção |
| Três buffers e apresentação Xbox | Frametime mais regular e menos flicker/resume/crash | Baixo-médio; depende da integração UWP |
| Teto de resolução por modelo | Evita crash no Series S e reduz custo de apresentação | Baixo |
| D3D12MA completo | Pode reduzir fragmentação/commit em cargas grandes | Alto; requer adaptação de ABI e NativeAOT |

Não há base para prometer que qualquer item isolado levará Sonic Mania a
60 FPS. O ring recém-concluído reduziu submissões no microbenchmark de views
de 5 para 2 e no de samplers de 32 para 8, mas FPS real depende de shader,
CPU, sincronização, apresentação e do jogo.

## Ordem recomendada

1. Manter o ring atual como baseline e guardar as métricas por sessão.
2. Implementar cache de grupos de samplers com teste de conteúdo e fences.
3. Implementar cache persistente de PSO com teste de cold/warm e invalidação.
4. Portar stream buffer e retirement de recursos.
5. Integrar swap chain de três buffers, resolução Series S e resume no frontend.
6. Só depois comparar jogos reais no PC e no Xbox, separando tempo de boot,
   shader stutter, frametime e FPS sustentado.

## Referências técnicas

- [XBSX2 v2.0.8.5](https://github.com/XboxEmulationHub/XBSX2/releases/tag/v2.0.8.5)
- [Repositório XBSX2](https://github.com/XboxEmulationHub/XBSX2)
- [Heaps de descritores visíveis a shaders](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shader-visible-descriptor-heaps)
- [Resource barriers e sincronização de estados D3D12](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12.md)
