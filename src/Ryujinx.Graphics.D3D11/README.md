# Ryujinx.Graphics.D3D11

Este projeto contém o backend nativo Direct3D 11 para a adaptação UWP/Xbox do Ryujinx. Ele integra a fronteira GAL, wrappers COM sem bindings externos, recursos D3D11, pipeline de comandos e apresentação delegada ao host UWP/Xbox.

> **Estado da revisão:** backend experimental/alpha, com cobertura funcional estimada em aproximadamente **80% para uma primeira integração GAL**. A compilação foi validada; a execução de jogos em Windows/UWP/Xbox ainda não foi validada.

## Ownership UWP/Xbox

O host cria e destrói `ID3D11Device`, `ID3D11DeviceContext` e `IDXGISwapChain1`. O renderer recebe esses ponteiros emprestados por `D3D11HostContext` e não os libera. Buffers, texturas, views, samplers, estados e shaders criados pelo renderer são liberados pelo próprio renderer.

O host continua responsável por device creation, suspensão, retomada, resize, composição da superfície e recriação após device-lost. O backend não cria `HWND` e não assume uma swap chain desktop.

## Inicialização

```csharp
D3D11HostContext host = new(
    device: nativeDevicePointer,
    deviceContext: nativeContextPointer,
    swapChain: nativeSwapChainPointer,
    width: renderWidth,
    height: renderHeight,
    presentResultCallback: PresentFromUwpHost,
    deviceLostCallback: hResult => RecreateDeviceAndRenderer(hResult),
    resizeCallback: (width, height) => ResizeSwapChain(width, height),
    shaderCompiler: (stage, hlsl) => CompileHlslForD3D11(stage, hlsl));

IRenderer renderer = D3D11Renderer.Create(host);
renderer.Initialize(GraphicsDebugLevel.None);
```

O host deve fornecer o callback HLSL→DXBC/DXIL, ou bytecode pré-compilado. A ponte opcional `D3D11SpirvCrossCompiler` usa a API C do SPIRV-Cross para SPIR-V→HLSL. A biblioteca SPIRV-Cross e um compilador permitido pelo perfil UWP/Xbox não são distribuídos por este projeto.

## Recursos e pipeline desta revisão

O backend cria buffers estruturados e constant buffers, texturas 1D/2D/3D, arrays e cubemaps, views SRV/RTV/DSV/UAV por mip/camada, samplers, uploads completos e parciais, cópias GPU de regiões, readback staging e resolve de color MSAA. Texture-buffer pode vincular SRV/UAV de um buffer existente por `SetStorage`.

O pipeline implementa clears, targets, depth/stencil, viewport, scissor, topologias, input layout, draw normal/instanciado/indireto, dispatch, arrays de textura e imagem, fragment UAV, stream output/transform feedback, patches, alpha-to-coverage, queries de sincronização, contadores e blit escalado via helper shader HLSL.

| Área | Estado | Limitação |
|---|---|---|
| Buffers | Upload, clear parcial, cópia, readback e SRV/UAV | Não há mapeamento persistente nem sistema completo de fences/hazards. |
| Texturas | 1D/2D/3D, arrays, cubemaps, mips, views, staging e color MSAA | Depth MSAA readback/resolve, formatos sem equivalente DXGI e views tipadas de texture-buffer ainda faltam. |
| Compressão | Upload/cópia parcial quando os blocos são compatíveis e alinhados | Conversão automática de regiões não alinhadas e famílias ETC2/ASTC continuam pendentes. |
| Pipeline | Estados básicos, instancing, indirect, patches, stream output, UAV e blit | Alpha test, logical op, blend avançado, rasterizer discard, point polygon mode e clip distance exigem emulação. |
| Shaders | SPIR-V→HLSL opcional, HLSL→DXBC/DXIL por callback e cache `DXB1` | Bindings, semânticas, atomics, barriers, precisão e Feature Level precisam de testes com shaders reais. |
| Sincronização | Event queries e counters D3D11 para GAL | O polling é uma implementação inicial e deve ser validado contra device removal e carga real. |
| Apresentação | Present/HRESULT, resize, swap-chain callback e device-lost callback | O host ainda implementa a swap chain UWP/Xbox e a recriação completa do renderer. |

## Shaders e bindings

O `ShaderCache` escolhe SPIR-V para `TargetApi.Direct3D11`. Quando o programa recebe SPIR-V, o host pode usar `D3D11SpirvCrossCompiler` para gerar HLSL e depois chamar seu compilador nativo. O resultado deve respeitar os slots D3D11 usados pelo pipeline, o input layout, semânticas, arrays, imagens, atomics, barriers, profundidade e convenções de precisão.

A ponte de compilação não comprova compatibilidade automática de todos os shaders do Ryujinx. Recomenda-se validar primeiro vertex/pixel shader simples, depois arrays, UAV, compute, tessellation, geometry, atomics e MSAA.

## Device-lost e apresentação

`PresentResultCallback` devolve HRESULT ao renderer; códigos DXGI de device removed/reset/hung/out-of-memory acionam `DeviceLostCallback`. O callback deve parar o renderer, liberar o contexto antigo, recriar device/context/swap chain e construir um novo `D3D11HostContext`. O backend não tenta reutilizar ponteiros COM inválidos.

`SwapChainResizeCallback` pode recriar a swap chain e devolver o novo ponteiro. `PresentTextureCallback` recebe o recurso atual e o crop; o host pode copiar, resolver ou compor esse recurso no backbuffer.

## Validação

A revisão compilou com o SDK disponível no sandbox, `10.0.111`, durante uma seleção temporária. O `global.json` foi restaurado ao valor original do repositório: `10.0.301`, `rollForward: latestFeature`. Os projetos D3D11 e Ryujinx concluíram com zero erros; `git diff --check` também passou. A execução dos métodos COM requer Windows com device D3D11 e depois o alvo UWP/Xbox.

## Próximos passos até 100%

A ordem recomendada é validar o host UWP/Xbox com debug layer, exercitar um corpus de shaders do Ryujinx, completar depth/stencil MSAA, implementar views tipadas de texture-buffer, converter formatos sem equivalente DXGI, adicionar emulação shader para estados sem equivalente fixo e construir testes de imagem/performance em títulos reais.

Referências: [Direct3D 11 em UWP](https://learn.microsoft.com/en-us/windows/uwp/gaming/simple-port-from-direct3d-9-to-11-1-part-1--initializing-direct3d), [DirectX/XAML interop](https://learn.microsoft.com/en-us/windows/uwp/gaming/directx-and-xaml-interop), [Direct3D 11 API](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/) e [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross).
