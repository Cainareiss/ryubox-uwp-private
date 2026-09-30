# Parte 1 — dependências autorizadas e formatos de cor

13/09/2026. Autorização recebida para implementar as dependências das partes
2/3 necessárias à parte 1; o restante dessas etapas permanece pausado.
**Este bloco não encerra 100% da GAL.**

## Entregue: matriz inicial de formatos 2D

| GAL | DXGI | Bytes por pixel |
| --- | --- | --- |
| R8Unorm | R8_UNORM (61) | 1 |
| R8G8Unorm | R8G8_UNORM (49) | 2 |
| R8G8B8A8Unorm | R8G8B8A8_UNORM (28) | 4 |
| B8G8R8A8Unorm | B8G8R8A8_UNORM (87) | 4 |
| R16Unorm | R16_UNORM (56) | 2 |
| R16G16Unorm | R16G16_UNORM (35) | 4 |
| R16G16B16A16Unorm | R16G16B16A16_UNORM (11) | 8 |
| R16Float | R16_FLOAT (54) | 2 |
| R16G16Float | R16G16_FLOAT (34) | 4 |
| R16G16B16A16Float | R16G16B16A16_FLOAT (10) | 8 |
| R32Float | R32_FLOAT (41) | 4 |
| R32G32Float | R32G32_FLOAT (16) | 8 |
| R32G32B32A32Float | R32G32B32A32_FLOAT (2) | 16 |

`D3D12TextureFormat.cs` é a tabela explícita GAL/DXGI/tamanho. A criação nativa
confere suporte do device a textura 2D, amostragem e render target. Só habilita
o flag UAV quando o device informa suporte de vista tipada e armazenamento.
Isso não certifica todos os modos de leitura/escrita UAV de todos os formatos.

Upload/readback usam o layout retornado pelo device, separando bytes úteis de
cada linha do alinhamento do buffer intermediário. Referência:
[GetCopyableFootprints](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-getcopyablefootprints).

Cópias para buffer e upload parcial usam o tamanho de pixel do formato real.
Temporários de self-blit preservam o formato original. Cópias brutas entre
formatos distintos são rejeitadas; o caminho de blit faz conversão amostrada.
O cache de PSOs agora inclui o formato do render target, que também é passado
à criação do pipeline nativo em vez de fixar sempre RGBA8.

O novo export `RyuboxD3D12_CreateColorTexture` é adicional; o export RGBA8
anterior permanece. Usar a DLL integrada recompilada com este código.
Os formatos ainda exigem uma camada, um mip, uma amostra e swizzle identidade.
Vistas idênticas compartilham ownership; reinterpretar vistas continua pendente.

## Validação

`eng/D3D12InteropRegression/ColorFormatTests.cs` exercita os 13 formatos com:

- round-trip de bytes no upload/readback e cópia direta;
- upload de região parcial;
- cópia para buffer com sentinelas no padding de cada linha;
- clear completo e máscara de canal, conferidos byte a byte;
- draw com o mesmo programa em render targets de formatos diferentes;
- amostragem/conversão para RGBA8;
- self-blit com temporário no formato original.

A bateria completa passou no WARP e na Intel HD 620 com debug layer, incluindo
regressões GAL, shaders, counters e cleanup. Evidências:

- `artifacts/d3d12-color-formats-warp.log`
- `artifacts/d3d12-color-formats-intel.log`
- `artifacts/d3d12-color-formats-d3d11.log`: regressão D3D11 aprovada.
- `artifacts/d3d12-color-formats-corehost.log`: build Release sem erros,
  com 15 avisos existentes; não é publish NativeAOT.

DLL integrada: `artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`.
SHA-256: `ECB34F81E634FD5FD70329FE3496830173F2E5669B87211F3B6E7204DF9D308C`.
SPIRV-Cross usado: `artifacts/d3d12-spirvcross-validation/spirv-cross-c-shared.dll`.
Nenhum jogo, instalação, MSIX, NativeAOT ou Xbox foi validado nesta rodada.

## Próximas dependências, já autorizadas

1. Formatos depth/stencil e vistas DSV/SRV correspondentes, testes/clear de
   profundidade e stencil e sua inclusão nas chaves de PSO.
2. Subresources e vistas com mip/layer/plane, orientação e swizzle reais.
3. Matrizes restantes de formatos (incluindo inteiros e BCn), MSAA/resolve,
   buffer textures, múltiplos render targets e múltiplos viewports/scissors.
4. Modos GAL/samplers que requerem emulação em shaders e filtros da janela.
5. Completar validações de lifecycle/falhas/plataforma indicadas no guia.

As capabilities amplas continuam conservadoras: suportar B8G8R8A8Unorm neste
bloco, por exemplo, não certifica toda a família BGRA/sRGB/vistas/UAVs.
Não há percentual global de conclusão calculado nem promessa de compatibilidade
com jogos antes de fechar e testar esses contratos.

## Entregue: depth D16/D32 inicial

13/09/2026. O segundo bloco autorizado cobre profundidade sem stencil para
contratos basicos de `ITexture`, `IPipeline` e `IRenderer`.

| GAL | Recurso DXGI | DSV | SRV/amostragem | Bytes por pixel |
| --- | --- | --- | --- | --- |
| D16Unorm | R16_TYPELESS (53) | D16_UNORM (55) | R16_UNORM (56) | 2 |
| D32Float | R32_TYPELESS (39) | D32_FLOAT (40) | R32_FLOAT (41) | 4 |

O backend nativo ganhou heap DSV por frame, criacao de texturas depth,
clear de profundidade e draw com estado depth separado do ABI antigo de draw.
O PSO agora recebe o formato DSV, o estado efetivo de profundidade e a ausencia
real de color target na chave.
Texturas depth podem ser amostradas como SRV tipada compativel e copiadas
somente como copia integral entre recursos identicos, respeitando a regra D3D12
de nao fazer copia parcial de depth/stencil.

No lado GAL, `ClearDepthStencil` limpa D16/D32 quando ha mascara de depth,
`SetDepthTest` traduz as comparacoes esperadas, e `SetRenderTargets` aceita um
depth target junto de um render target de cor compativel. Upload parcial de
depth usa fallback correto por CPU: le o recurso inteiro, mescla a regiao e
reenvia a textura completa, preservando bytes fora da regiao. Esse caminho e
deliberadamente conservador e pode ser otimizado depois.

Validacao acrescentada em `eng/D3D12InteropRegression/DepthTests.cs`:

- clear/readback de D16 e D32;
- depth write mask, teste desligado, comparacoes `Less` e `Greater`;
- as oito comparacoes nos codigos GAL nativos e no intervalo GL 0x200..0x207;
- rejeicao de fragmentos medida por pixel e por query de samples;
- copia integral, amostragem depth para RGBA8 e paginacao de DSV com 1.030 clears;
- draw depth-only real com zero RTVs, contador de samples e color target anterior intacto;
- upload parcial com preservacao exata das regioes nao tocadas;
- rejeicao explicita de stencil plane em formatos sem stencil.

Validacoes executadas com a DLL integrada:

- `dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore --nologo -v:q`: PASS, 0 avisos, 0 erros.
- `dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll artifacts/d3d12-spirvcross-validation/spirv-cross-c-shared.dll --warp`: PASS.
- O mesmo comando sem `--warp` na Intel HD 620: PASS.
- `.\eng\Test-RyuboxD3D12.ps1 -DebugLayer -Warp -NativeDll artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`: PASS, `debugErrors=0`.
- `artifacts/presentation-smoke/RyuboxNativePresentationSmoke.exe artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`: PASS.
- `dotnet build src/Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release --no-restore --nologo -v:q`: PASS, 0 erros e 15 avisos ja conhecidos.

DLL integrada validada:
`artifacts/d3d12-integrated-native/Release/Ryujinx.Uwp.Native.dll`.
SHA-256: `0ACD22E0D94F7ED217F8C5C19596597FA5FD144F12B8D61AC20C461B25AE8C05`.

Pendencias desse bloco: stencil, D24S8/D32S8, MSAA/resolve, mips/layers/planes,
readonly DSV compartilhado com SRV, UAV de textura depth quando aplicavel e
blit para destino depth. Essas lacunas continuam fora do certificado de 100%
da parte 1.
