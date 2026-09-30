# Ryujinx Xbox Native GS 0.1.1.50

Esta versão aplica ao port do Ryujinx os princípios úteis observados no host UWP do XBSX2, com implementação original e adequada ao contrato GAL do Switch.

## Alterações principais

- posse exclusiva do device, immediate context e swap chain pela GPU thread durante a sessão;
- bloqueio de resize, frame de teste, troca de backend e shutdown concorrentes;
- proteção `ID3D11Multithread` e fila de apresentação limitada a dois frames;
- handshake determinístico entre CoreHost e GPU thread, com timeout e erro registrado;
- correção do slot ABI de `ResolveSubresource` de 56 para 57;
- barriers GAL conservadoras por `Flush` e capabilities ajustadas ao D3D11 real;
- fallback de CPU para cópias escaladas de superfícies coloridas simples;
- `codeGeneration` para o JIT ARM e `expandedResources` para o orçamento do Xbox;
- perfil fixo D3D11, portátil e 1280x720, sem Mesa/Dozen/Vulkan no MSIX;
- frontend identificado como `NATIVE GS 0.1.1.50`.

## Validação executada

- backend D3D11: compilação Release concluída;
- host C++ UWP: compilação x64 Release concluída;
- CoreHost: publicação NativeAOT concluída;
- frontend XAML/UWP: compilação e geração do XBF concluídas;
- MSIX: assinado e inspecionado quanto ao manifesto, arquivos obrigatórios e dependências proibidas;
- script de regressão: `eng/Test-RyujinxD3D11NativeGs.ps1`.

O teste funcional definitivo de Shovel Knight ainda precisa ser executado no Xbox Series em Dev Mode, pois o PC não reproduz o runtime gráfico e o orçamento de memória do console.
