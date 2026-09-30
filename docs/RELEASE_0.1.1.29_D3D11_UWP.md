# Ryujinx Xbox UWP 0.1.1.29 — D3D11 nativo

Esta build corrige o bloqueio do backend D3D11 observado nos logs do Xbox.

## Correcoes principais

- SPIRV-Cross recompilado como binario UWP x64/AppContainer.
- CRT incorporado estaticamente; o binario nao importa `MSVCP140.dll`,
  `VCRUNTIME140.dll` ou `VCRUNTIME140_1.dll`.
- Carregamento no sandbox feito primeiro por `LoadPackagedLibrary`.
- Compatibilidade HLSL habilitada para os built-ins SPIR-V `PointSize` e
  `PointCoord`.
- Build D3D11-only: Mesa, Dozen, Vulkan, DXIL e DXC foram removidos do MSIX.
- Se uma configuracao antiga pedir Vulkan/Dozen, o frontend migra para D3D11.
- O processo de release agora rejeita automaticamente SPIRV-Cross desktop,
  binario sem AppContainer e DLLs Vulkan/Mesa no pacote D3D11.

## Validacao realizada

- Build Release x64: 0 erros.
- Assinatura MSIX: valida, `CN=Ryujinx Xbox Dev`.
- Manifesto: `Windows.Universal` e `Windows.Xbox`, versao `0.1.1.29`.
- Teste local empacotado: aplicativo instalado, iniciado e responsivo.
- Probe local: host D3D11, host nativo e SPIRV-Cross encontrados.
- Smoke test do compilador: SPIR-V -> HLSL -> DXBC concluido.
- Auditoria do MSIX: nenhuma DLL Mesa/Vulkan/DXIL/DXC presente.

SHA-256 do MSIX:

`BC60311AB0ED63597A3B03883ED618FECD72D8C502935E4A17C63CA1132C8199`

## Instalacao no Xbox Dev Mode

Envie o arquivo `Ryujinx.Uwp.App_0.1.1.29_x64.msix` pelo Device Portal e,
quando solicitado, instale as tres dependencias da pasta `Dependencies/x64`.
O certificado `RyujinxXboxDevCodeSign.cer` acompanha a release.

## Risco residual conhecido

`Ryujinx.Uwp.CoreHost.dll` e gerado pelo runtime NativeAOT do .NET e ainda
possui imports que ficam fora da whitelist de certificacao da Microsoft Store.
Esse host ja carregou e executou no Xbox Dev Mode nos logs anteriores; portanto,
ele nao era a causa do erro de carregamento do SPIRV-Cross. Ainda assim, essa
camada deve continuar sendo monitorada em testes no hardware real.
