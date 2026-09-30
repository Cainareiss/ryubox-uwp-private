# Horizon e Pokémon: pareamento do defeito gráfico com Ryubing

## Evidência atual

- Horizon Chase tem uma captura da primeira corrida **correta no Ryubox D3D11** (`artifacts/horizon-material-race-check.png`): pista, carro e cenário visíveis. O usuário confirmou que, na mesma corrida em D3D12, as texturas/geometria se corrompem. Isso isola a investigação no caminho específico do D3D12; não prova que seja apenas uma textura.
- A captura D3D12 existente em `artifacts/horizon-d3d12-3d-capture/horizon-3d.png` é apenas o splash AQUIRIS. Não usá-la como prova de uma corrida corrigida ou quebrada.
- Em Pokémon Let's Go, o defeito do rosto/cenário está presente no render target **antes** do blit de apresentação. Desligar o proxy de attachment no teste A/B não o eliminou. O registro de primeiro uso de 31 programas não encontrou `SRV MISS`/`CBV MISS`, mas não prova que o conteúdo das views ou os buffers de material estejam corretos nos draws seguintes. Ver `docs/POKEMON_LETS_GO_PROFESSOR_GRAFICOS_20260930.md`.

## Diferenças verificadas no código

1. `TextureCache.cs` compartilhado é idêntico ao Ryubing Canary de referência (`artifacts/reference-ryubing-canary-1.3.351-20260929`). A divergência mais provável fica depois do cache: formato host, descompressão, upload, view, shader, binding ou estado do draw.
2. O D3D12 anuncia BC1–BC7 e ASTC como indisponíveis (`D3D12Renderer.cs`), conduzindo texturas comprimidas ao caminho de conversão por software da GAL. Ryubing/Vulkan pode usar BC nativo, e D3D11 anuncia parte da família BC. **Hipótese:** diferenças de conteúdo, swizzle, mips ou custo nesse caminho; confirmar os formatos das texturas efetivamente ligadas na corrida antes de alterar capabilities.
3. O D3D11 precisou corrigir a restauração de uniform buffers e o espelho após `CopyBuffer` para tornar o material de Horizon correto (`docs/HORIZON_MATERIAL_CONSTANTS_VALIDATION.md`). O D3D12 tem buffers/CBVs e cópias próprios. **Hipótese independente:** conteúdo de constante de material incorreto, mesmo quando o CBV existe. Uma contagem `CBV OK` não verifica bytes.
4. O Ryubing usa SPIR-V/Vulkan no teste de referência. O D3D12 do Ryubox passa por SPIRV-Cross/HLSL 5.1 e por suas próprias views SRV/RTV/DSV. As diferenças de conversão de shader, depth/culling e estado de render pass permanecem candidatas, sobretudo para carros atravessando o chão.

## Instrumentação desta rodada

O diagnóstico opt-in `RYUBOX_D3D12_BINDING_DIAG=1` agora escreve para cada SRV encontrado o ID do recurso, tipo, formato, dimensão, mip, camada, sample count e component mapping. `RYUBOX_D3D12_BINDING_DIAG_START_DRAW=N` junto com `RYUBOX_D3D12_BINDING_DIAG_WINDOW_DRAWS=K` registra uma janela de até 256 draws consecutivos, além do dump pesado apenas no primeiro uso de cada programa. A janela distingue materiais que mudam entre draws do mesmo shader.

Build da GAL D3D12: **passou**, 0 erros. CoreHost NativeAOT de diagnóstico: **publicado** em `artifacts/horizon-binding-diag-runtime-20260930`. Ainda **não** há nova captura de corrida feita com essa compilação; a automação de pulsos A disponível só produziu splash em testes anteriores e não é evidência suficiente para atribuir o bug a uma view específica.

## Próximo teste de decisão

Chegar à primeira corrida em D3D12 e capturar o mesmo instante do controle D3D11, com uma janela de draws nos objetos defeituosos. Comparar, por draw: (a) formato/ID/mip/camada/swizzle da SRV; (b) hash dos bytes do CBV de material antes e depois de `CopyBuffer`; (c) viewport, cull, depth e RTV/DSV. Se a textura lida do recurso já estiver incorreta, seguir conversão/upload/cópia. Se a textura estiver correta e o render target incorreto, seguir shader/CBV/depth/estado. Não habilitar BC nativo às cegas e não declarar o bug resolvido sem imagem da corrida.

## Defeitos de subresource reproduzidos e corrigidos

O pareamento com `Ryujinx.Graphics.Vulkan/TextureView.cs` e
`PersistentFlushBuffer.cs` confirmou que o overload GAL com `(layer, level)`
transfere uma única camada/fatia. O chamador compartilhado
`Texture.GetTextureDataSliceFromGpu` também depende desse contrato.

Foram encontrados quatro defeitos em `D3D12GalTexture.cs`:

- `GetData(layer, level)` ignorava `layer` depois de validá-lo.
- `GetData()` de arrays/cubemaps reservava memória para todas as camadas,
  mas a função nativa copiava somente a primeira camada de cada mip.
- `SetData(data, layer, level)` em Texture3D exigia os bytes do volume
  completo, rejeitando um upload legítimo de uma única fatia.
- `CopyTo(BufferRange, layer, level, stride)` em Texture3D percorria o
  volume inteiro, podendo sobrescrever bytes depois da fatia solicitada.

A correção usa views nativas de um mip/uma fatia para selecionar a leitura,
monta arrays completos em ordem mip/camada e limita uploads/cópias parciais
à fatia pedida. Não modifica o formato da textura nem requer nova ABI.

Reprodução automatizada em `eng/D3D12InteropRegression/TextureSliceTests.cs`:
antes, **11 de 12 verificações falhavam**. Depois, **12/12 passaram em WARP
e GPU física**, com debug layer sem erros. Foram acrescentados três casos
de views derivadas, totalizando **15/15 em WARP e GPU física**. O resultado
da GPU física está em `artifacts/texture-slices-physical-20260930.log`. A bateria pré-existente
`--texture3d-only` também passou em WARP (SRV/UAV, mips, clears e R16Float).

CoreHost NativeAOT corrigido publicado em
`artifacts/horizon-slices-fixed-runtime-20260930`. Compilação concluída com
avisos de NuGet/trim/AOT; não foi um build sem avisos.

O reteste anterior, de 240 s, produziu a captura real
`artifacts/horizon-race-trace-20260930/frame.bmp`, ainda no splash AQUIRIS,
e encerrou pelo limite do harness, sem device lost. Novo reteste de 600 s
usa o runtime corrigido e capturas seriadas em
`artifacts/horizon-slices-retest-20260930`.

Esse reteste foi interrompido pelo agente ainda no splash para alinhar o
harness ao já usado na comparação de Pokémon: `src/eng/Run-CoreHostD3D12Direct.ps1`
e `RYUJINX_UWP_FAST_EPOCH_JIT=1`, somente para diagnóstico desktop. As capturas
desse teste estão em `artifacts/horizon-slices-epoch-20260930`, com cota
mantida em 5 GiB. Não comparar FPS entre esses perfis como se a única
mudança fosse a correção de texturas.

Esses defeitos de transferência estão comprovados. A relação causal com
os carros/chão da corrida e com o rosto do Professor Carvalho ainda depende
da comparação visual nas respectivas cenas; os testes sintéticos não a
substituem.

## Validacao visual pelo usuario e pacote Xbox

Em 30/09/2026 o usuario executou o launcher
`artifacts/Start-HorizonChase-D3D12-Fullscreen-20260930.ps1` e enviou uma
captura da corrida (volta 1/3). Carro, pista e cenario aparecem renderizados
sem a corrupcao grosseira anteriormente relatada. O usuario confirmou a
correcao. Isso valida essa cena no PC; nao demonstra todas as pistas, outros
jogos ou desempenho no Xbox. A cena do Professor Carvalho ainda precisa de
reteste com este runtime.

O launcher usa o runtime `horizon-slices-fixed-runtime-20260930`, com
`RYUJINX_UWP_FAST_EPOCH_JIT=1` apenas no processo desktop. O pacote Xbox
mantem a politica de JIT de producao e a protecao de memoria; nao transportar
a flag diagnostica para o console sem validar reclamacao segura de codigo.

Preparacao do MSIX 0.2.0.135: frontend recompilado a partir da arvore atual;
CoreHost NativeAOT, native e SPIRV-Cross apontados explicitamente para o
runtime aprovado acima. A DLL native da pasta padrao e anterior, de 25/09,
e nao deve substituir a DLL testada de 30/09. O script de empacotamento
verifica hashes dos binarios internos, dependencias UWP e assinatura.

Pacote concluido: `artifacts/xbox-0.2.0.135/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.135_x64_Test/Ryujinx.Uwp.App_0.2.0.135_x64.msix`.
Build: 0 erros, 16 avisos. Assinatura Authenticode: Valid. Identidade interna
`Ryujinx.Xbox.UWP`, versao `0.2.0.135`, x64. Tamanho: 121033736 bytes.
SHA-256: `17540C6ACBAAF6BFD7D7E03545FBEEAD6891ECC6E627EFDF90F317A33CBEEDDE`.
CoreHost, native, SPIRV-Cross e SkiaSharp internos conferem com os hashes
do runtime testado. Dependencias x64 e certificado acompanham o pacote.
Ainda nao instalado nem validado no Xbox: teste de console sera feito pelo usuario.
