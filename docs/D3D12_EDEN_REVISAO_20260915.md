# Revisão do transplante Eden — 15/09/2026

## Conclusão e limites

O relatório recebido não correspondia integralmente ao código encontrado:
`Allocate` continuava usando apenas `CreateCommittedResource`; `MemoryChunk`
existia sem participar da alocação; `D3D12DxilCompiler` não tinha consumidor no
renderer e a chamada Mesa usava `NO_DXIL_VALIDATION`. Portanto, tradução com
retorno S_OK/container DXBC não demonstrava criação de PSO ou execução DXIL.
Também não havia base medida para as alegações de redução de 95% dos commits,
eliminação do erro 1455 ou ganho de FPS em jogos.

O usuário dispõe somente do MSIX Eden 0.29. Não foi comprovada a correspondência
entre o header copiado e a DLL que exporta também `spirv_to_dxil_pipeline`.
Não ativamos uma ABI deduzida nos jogos. `GetDxilStatus` retorna E_NOTIMPL e o
wrapper reporta indisponibilidade; o código experimental permanece preservado,
mas inacessível pela entrada protegida. Isso não é implementação DXIL concluída.
O caminho executado é D3D12 nativo com shaders DXBC, não fallback D3D11.

## Correções implementadas

- Subalocação real Tier 2: heaps de 64 MiB, recursos até 32 MiB, offsets conforme
  `GetResourceAllocationInfo`, retenção do heap no Resource, referências de
  comandos até fence, recuperação de intervalos livres e liberação de heaps
  vazios. Primeira utilização inclui barreira de aliasing. Tier 1/recursos
  maiores permanecem committed. É um alocador próprio, não D3D12MA.
- Orçamento considera heaps reservados, não somente soma de recursos vivos.
  Padrão restaurado a 1536 MiB; aumentá-lo não é correção de OOM. Upload/readback
  continuam fora dessa estatística e precisam de orçamento integrado futuro.
- Contadores diagnósticos: heaps, bytes reservados/lógicos, criações placed e
  committed, chamadas FXC de programas e criação de PSOs gráficos.
- Limites gerenciados alinhados aos 16384 views nativos; removido flush por
  simplesmente atingir 512 operações. Samplers permanecem em 512 por fatia,
  total 1536; `static_assert` impede ultrapassar o limite tradicional do heap.
  São três fatias recicladas por fence, NÃO um novo ring dinâmico/paginado.
- Cache D2P5 preserva bytecode compilado e aceita D2P4 anterior. Hull shaders
  gerados a partir de HLSL preservam recompilação especializada por patch size.
  Cache truncado é rejeitado; cache de PSOs persistente ainda não implementado.
- Blit e clear parcial agora preenchem a máscara do render target explícito.
  Antes disso, o backend selecionava o backbuffer em vez da textura de destino.
- Clear do backbuffer implícito deixou de ser descartado; slots explicitamente
  nulos/depth-only continuam sem alvo de cor.

## Evidência de testes

Bateria `eng/D3D12InteropRegression` aprovada em WARP e GPU física com debug
layer: buffers, pixels, arrays, formatos de cor/depth, máscaras, alpha,
geometry/tessellation, indiretos, counters, descarte e concorrência existentes.

Novos casos em `EdenReviewTests.cs`:

- 70.000 dispatches GAL, soma atômica exata, 6 submissões; atravessa capacidade
  de views e todas as fatias. Não é medição de FPS.
- 2.200 dispatches com sampler/textura, soma exata 2200, reutilização das fatias.
- 384 buffers descartados antes da submissão, conferência de todos os valores
  copiados. Na execução medida: 387 criações placed, zero committed pelo
  alocador de recursos default; 64 MiB reservados no pico e ao fim, incluindo
  recursos ainda vivos. Isso não contabiliza commits totais do Windows.
- Rejeição de tamanho inválido e de orçamento agregado antes da alocação;
  cleanup de contexto faulted e início de novo contexto.
- Cache executado na GPU com resultado 42, inclusive bytecode acompanhado de
  texto HLSL deliberadamente inválido. Zero chamadas FXC adicionais na recarga.
  Tempos são amostras locais, não benchmark: na rodada física observada,
  aproximadamente 2,49 ms frio e 0,61 ms recarga.
- Gate DXIL recusado explicitamente; NÃO contado como teste de execução DXIL.

A comparação A/B `RYUBOX_D3D12_COMMITTED_ONLY=1` reproduziu o erro de blit sem
subalocação. A causa foi a máscara de RT, não o alocador. O teste de apresentação
antigo esperava FlipY como uma cópia crua: foi corrigido após comparar a convenção
GAL em Vulkan/Window.cs, e ampliado para as quatro combinações X/Y com conferência
separada do upload bruto. A orientação de apresentação não foi alterada.

Logs em `artifacts/eden-review-*.log`:

- `warp`, `hardware`: bateria gerenciada completa.
- `native-build`: smoke D3D12 offscreen de 90 frames com debug layer.
- `texture-cache`, `settings`: regressões de cache/200 ciclos e parser.
- `corehost-build`: build gerenciado, zero erros; aviso CS0067 de evento não usado.
- `committed-ab`: reprodução diagnóstica anterior à correção de RT, falha esperada.
- `integrated-build`: DLL completa D3D11/D3D12 reconstruída em saída isolada.
- `d3d11-presentation`: PASS inicialização, blits legacy/V2, crop/flip,
  apresentação repetida, isolamento do frontend, resize e duas sessões.

Rodada final física: Intel HD Graphics 620, driver 27.20.100.8729, bateria
completa executada com `artifacts/eden-review-integrated/Release/Ryujinx.Uwp.Native.dll`.
SHA-256 dessa DLL:
`6F8E959BD3BABD4D5D6556512E1B9DDFAE3DCCC40D20E5245327F1025350E206`.
WARP também aprovado após a última alteração nativa. Compilação nativa isolada
emitiu C4201 no header Mesa copiado; não foi suprimido para alegar zero avisos.

Os testes de retenção atravessam submissões, mas não injetam atraso arbitrário
na fila. Ainda faltam falhas nativas de device removal/submissão induzidas,
comparação de pixels DXIL versus DXBC, teste de jogo com cache frio/quente,
profiling CPU/GPU/readback e validação UWP/NativeAOT/Xbox.

## Sessão visível Sonic Mania — 15/09/2026, 19h01 BRT

Publicação NativeAOT concluída com exit 0 em
`artifacts/sonic-eden-review-aot`; log
`artifacts/sonic-eden-review-publish-retry.log`. Há avisos de trimming/AOT,
inclusive métodos PLINQ que o compilador informa que lançarão exceção;
o sucesso da publicação não comprova esses caminhos de execução.

CoreHost atualizado e DLL nativa revisada copiados para runtime isolado em
`artifacts/sonic-eden-review/runtime`. Estado em `state`, com firmware/keys
copiados e sem substituir saves instalados. D3D12 nativo físico, Intel HD 620,
Handheld, VSync on, PTC off, SoftwarePageTable, threading GPU off.

- Processo iniciado às 19:01:00.517; CoreHostStartSuccess às 19:01:15.583.
- Primeiro callback de apresentação às 19:01:44.075: **43,6 s desde o launch**,
  ou 40,4 s desde LaunchRequested. O campo local `elapsed=31 ms` do evento
  FirstVisibleGuestPresent NÃO mede o carregamento completo.
- Captura real do logo SEGA às 19:01:46.964:
  `artifacts/sonic-eden-review/first-frame.bmp`.
- Captura da janela `visible-window-later.png` confirma avanço na animação de
  abertura, com personagem/cenário. Não comprova uma fase jogável.
- Janela de 30 amostras na abertura: 20,45 callbacks/s; janela posterior de
  60 amostras: 15,00 callbacks/s, intervalos 0–26. Não são 60 FPS, nem medição
  por timestamps GPU. Sem A/B na mesma cena, não há ganho percentual comprovado.
- Sem exceção no stderr observado; janela deixada aberta para teste manual,
  sem comandos automáticos, encerramento programado ou troca para D3D11.

**Limitação importante:** esta sessão começou com os overrides históricos do
harness (component swizzle e guest counters desativados). A captura mostrou
cores suspeitas; não foi certificado o resultado visual. Não atribuir a causa
somente a esse override sem A/B. O harness agora exige
`-LegacyIntelCompatibility` para repetir esse modo diagnóstico, imprime aviso
e por padrão mantém ambos os contratos ativos. Essa alteração passou pelo
parser PowerShell, mas não foi aplicada à sessão já aberta nem validada em
outro lançamento nesta rodada. O driver Intel antigo pode voltar a apresentar
a falha que motivou o override; não esconder essa falha com equivalência falsa.

Coleta reproduzível, sem controlar/fechar o jogo:

```powershell
.\eng\Measure-RyuboxSession.ps1 -StateDirectory 'artifacts/sonic-eden-review/state' -LastSamples 60
```

## Reprodução sintética

Na raiz do repositório, sem abrir jogos:

```powershell
.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Warp -OutputDirectory 'E:\ports\ryujinx uwp\artifacts\eden-review-validation'
dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore
dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll 'E:\ports\ryujinx uwp\artifacts\eden-review-validation\Ryujinx.D3D12.Validation.dll' 'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll' --warp
# Repetir sem --warp para hardware.
```

## Atualizacao da validacao DXIL - 16/09/2026

O MSIX Eden fornecido foi inspecionado e confirma quatro exports em
`spirv_to_dxil.dll`: `spirv_to_dxil`, `spirv_to_dxil_free`,
`spirv_to_dxil_get_version` e `spirv_to_dxil_pipeline`. O Ryubox possui apenas
o header compativel com os tres primeiros; a ABI do export adicional de
pipeline continua sem contrato verificavel.

Tambem foi corrigido o mapeamento de estagios do wrapper experimental: a ABI
Mesa usa Vertex=0, TessControl=1, TessEvaluation=2, Geometry=3, Fragment=4 e
Compute=5. Isso corrige a preparacao do codigo, mas nao ativa DXIL nos jogos.

O gate continua fechado porque a chamada atual usa `NO_DXIL_VALIDATION`, nao
configura os buffers de runtime/sysvals exigidos por shaders guest, nao integra
reflexao de bindings DXIL a root signature GAL e nao valida pipeline stages
completos. O caminho de producao permanece SPIR-V -> SPIRV-Cross -> HLSL -> DXBC
via D3D12 nativo; D3D11 continua separado. Detectar as DLLs do Eden nao e
criterio suficiente para ativar o caminho, pois isso pode causar shader invalido
ou crash.

Para considerar a adaptacao DXIL concluida ainda faltam: ABI de pipeline
documentada, validacao DXIL positiva, reflexao/normalizacao de bindings e um
teste de PSO executado no hardware.

Não houve MSIX, instalação ou desligamento nesta rodada. Não anunciar os três
pilares completos nem solução universal do erro 1455 a partir desses resultados.
