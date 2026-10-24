# Logs23 — etapas 2 e 3: implementação e validação local

25/09/2026. Alterações no runtime/boot e nos recursos D3D12.
Não é uma certificação de funcionamento dos jogos no Xbox.

## Etapa 2 — runtime e arquivos de boot

- O projeto UWP inclui libSkiaSharp.dll junto ao CoreHost. O preflight
  rejeita a ausência da DLL; o script compara seu SHA-256 com o arquivo
  efetivamente incluído no MSIX. A verificação ocorre depois do publish.
- A DLL Skia publicada depende de bibliotecas desktop, incluindo FONTSUB.dll.
  Incluí-la não garante que carregue no Xbox. O teclado inline verifica a
  biblioteca antes de criar objetos Skia, evitando também falhas no finalizer.
- Sem Skia utilizável, o teclado desenha uma superfície RGBA em memória:
  texto, seleção, cursor e estados dos botões. Não usa D3D11.
  É uma apresentação básica com glifos latinos; glifos ausentes recebem
  marcador visível, sem modificar o texto do applet. Não implementa novos
  serviços de entrada: o handler de texto UWP existente ainda é limitado.
- Encerramento do renderer espera suas threads terminarem antes de liberar
  superfícies, imagens, fontes e brushes.
- BootAssetCache usa temporário exclusivo, limite de 16 MiB e publicação
  completa. Reutiliza arquivos somente quando os bytes são iguais.
  Repara cache truncado; rejeita entrada vazia/grande. Se um arquivo diferente
  estiver bloqueado, informa falha e preserva o anterior, sem falso sucesso.
- O filesystem da Logo Partition e streams de imagens são descartados.

## Etapa 3 — recursos D3D12

- TextureBuffer: SetStorage com faixa/alinhamento validado, alias nativo
  compartilhando o buffer, SRV/UAV tipados, formato na ABI já existente,
  consulta de suporte do device, leitura/escrita e rebind.
  Não é uma cópia para textura 2D. Views retêm o buffer mesmo após DeleteBuffer.
  Nem todos os formatos/combinações possíveis foram exercitados; R32Uint
  é o caso comprovado de ponta a ponta nesta bateria.
- Bindings individuais, arrays, imagens e attachments retêm suas texturas
  até desvinculação. Release do cache não destrói recursos ainda vinculados.
  Cleanup do renderer libera também referências externas remanescentes.
- Uploads de buffer em blocos de 4 MiB. Os bytes guest são copiados antes
  de entrar no mutex nativo, pois uma leitura de memória protegida pode
  provocar sincronização GPU reentrante.
- Pressão do pool retorna ERROR_NOT_ENOUGH_QUOTA antes de gravar a cópia.
  A GAL faz submit, espera a fence e tenta uma vez novamente.
  Falhas reais de alocação/device não são tratadas como sucesso.
  O mecanismo de chunk/retry foi aplicado a buffers, não a todos os uploads
  de textura.
- Submissão limitada a 16.384 draws/dispatches mesmo com cache de descritores.
  A regressão de 70 mil dispatches reproduziu device removal no WARP antes
  dessa limitação; após a mudança, soma GPU correta em seis submissões.
  Isso não é medida de FPS nem garantia contra qualquer timeout.
- Clear da camada zero de textura 3D passa por view de uma única camada;
  antes limpava todas as camadas. Faixas/máscaras são validadas antes de
  modificar qualquer camada.
- Upload/TargetView agora distinguem lista fechada, orçamento e recurso
  não renderizável nos diagnósticos. Linhas de código dos logs antigos,
  isoladamente, não provam qual dessas condições causou cada crash.

## Evidências preservadas

Os caminhos abaixo são relativos à raiz E:\ports\ryujinx uwp:

| Teste | Resultado / evidência |
| --- | --- |
| Compilação nativa Release | PASS, artifacts/logs23-native-build.log |
| Compilação managed/regressões | PASS, artifacts/logs23-managed-build.log |
| Build CoreHost Release | PASS, artifacts/logs23-corehost-build.log; não é publish AOT |
| Bateria D3D12 completa WARP | PASS, artifacts/logs23-full-warp.log |
| Bateria D3D12 completa Intel | PASS, artifacts/logs23-full-intel.log |
| Upload 13 MiB / pool 4 MiB, WARP e Intel | PASS, artifacts/logs23-{warp,intel}-pressure.log |
| SRV/UAV tipado, offsets, validação atômica, vida útil de bindings | PASS nos mesmos logs de pressão |
| 50 publicações concorrentes com leitor aberto | PASS, artifacts/logs23-boot-tests.log |
| Renderização RGBA/pitch/seleção/cursor | PASS, artifacts/logs23-boot-tests.log |
| Skia ausente: 8 sessões HLE e finalizadores | PASS, artifacts/logs23-keyboard-tests.log |
| Preflight UWP com dependência presente/ausente | PASS; logs23-package-preflight.log e logs23-package-missing-dependency.log |

A bateria usa debug layer e confere bytes/pixels, não apenas retorno sem erro.
O teste de rollover continua exigindo mais de 49.152 descritores na mesma
submissão: agora são cinco views por dispatch, mantendo a cobertura sem
ultrapassar o limite de trabalho da GAL.

## Repetir

```powershell
dotnet run --project eng/BootAssetRegression -c Release
dotnet run --project eng/KeyboardFallbackRegression -c Release -p:NuGetAudit=false
dotnet build eng/D3D12InteropRegression -c Release --no-restore
$native = 'E:\ports\ryujinx uwp\artifacts\d3d12-logs23-native\Release\Ryujinx.Uwp.Native.dll'
$compiler = 'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll'
dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll $native $compiler --warp
# Repetir sem --warp para a GPU física.
$env:RYUJINX_UWP_D3D12_UPLOAD_BUDGET_MIB = '4'
dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll $native $compiler --logs23-only
Remove-Item Env:RYUJINX_UWP_D3D12_UPLOAD_BUDGET_MIB
```

## Ainda necessário antes de declarar os jogos corrigidos

Publicar NativeAOT e empacotar juntos o CoreHost novo e a DLL nativa nova
(há o novo export RetainBuffer). Não misturar com DLL de MSIX antigo.
Nenhum MSIX novo foi gerado/instalado nesta rodada.

Retestar Mario 3D World/Monster Hunter no teclado, Donkey Kong nos uploads,
Mario vs Donkey Kong na troca de texturas, Yoshi/DBZ com TextureBuffer e
Resident Evil no clear. Confirmar no Xbox, preservando logs e capturas.
Os jogos não foram iniciados nesta rodada. Os testes locais não comprovam
o fim de todas as telas pretas/crashes, nem resolvem etapas de shader/JIT
fora do escopo 2/3.

Referência do descriptor tipado:
[D3D12_BUFFER_SRV](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_buffer_srv).
