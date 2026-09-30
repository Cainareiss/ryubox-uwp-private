# Ryujinx Xbox UWP 0.1.1.45 — D3D11 Independent Heartbeat

Esta versão corrige a suspensão observada no Xbox durante o primeiro trabalho longo da GPU de Shovel Knight.

## Alterações

- O heartbeat de startup deixou a thread do loop da GPU.
- A nova thread de heartbeat usa apenas um `Present` DXGI não bloqueante.
- O heartbeat não limpa o render target e não altera o contexto imediato D3D11.
- Antes do primeiro frame real, o CoreHost cancela e aguarda o heartbeat.
- Foram adicionados logs de início/fim dos primeiros `ProcessFrame` e da primeira apresentação.
- O encerramento tolera uma chamada de driver demorada sem descartar prematuramente o token da thread.
- Nome visível atualizado para `Ryujinx Xbox D3D11`.
- Ícone, tile largo e splash screen exclusivos da versão D3D11.

## Validações

- Host nativo D3D11/D3D12: compilado.
- CoreHost .NET 10 NativeAOT x64: compilado.
- Frontend UWP x64: compilado com 0 erros.
- Exportação `RyujinxUwpGraphicsD3D11_PresentStartupHeartbeat`: presente no DLL nativo.
- MSIX: assinado e verificado com `CN=Ryujinx Xbox Dev`.
- Conteúdo obrigatório: Native Host, CoreHost e SPIRV-Cross presentes.
- Vulkan, Mesa, Dozen, DXIL e OpenGL: ausentes do MSIX D3D11-only.

## Identificação

- Package identity: `Ryujinx.Xbox.UWP`
- Version: `0.1.1.45`
- Architecture: `x64`
- SHA-256 do MSIX: `65E2ED50403C8EBF224A87DF4FA9C114E7CACA3F51A58188C96882C4B3FBB898`

No Device Portal do Xbox, envie o MSIX e, se solicitado, a dependência x64 `Microsoft.VCLibs.x64.14.00.appx`. O certificado acompanha a release.
