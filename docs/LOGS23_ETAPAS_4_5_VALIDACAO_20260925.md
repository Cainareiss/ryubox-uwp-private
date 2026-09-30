# Logs 23 — validação das etapas 4 e 5

Validado em 25/09/2026. Este registro separa regressões reproduzidas em
fixtures locais da confirmação nos jogos.

## Resultado

- X4576: corrigida a ordenação da assinatura de entrada pixel gerada pelo
  SPIRV-Cross. O FXC trata os valores comuns e `SV_Position` antes de
  `SV_IsFrontFace`; todos os seis ordenamentos do fixture foram normalizados e
  compilados.
- X3511: adicionado `[loop]` somente quando o limite do loop é um escalar de
  cbuffer e a coordenada de `Sample`/`SampleBias` não depende do contador. Isso
  preserva o número de iterações e a expressão de amostragem; os pixels foram
  conferidos para limites 1, 8 e 88. Limites por pixel, coordenadas que usam o
  contador e um `[unroll]` já presente ficam inalterados.
- X3694: a normalização de inicializadores zero em `groupshared` agora exige o
  builtin `SV_GroupIndex` realmente declarado e mapeado pelo SPIRV-Cross. Ela
  não elege uma lane inexistente, nem reescreve índices guest dinâmicos ou
  expressões que não são zero. O teste executou 20 dispatches com oito
  atualizações atômicas por grupo; todos os resultados esperados foram
  preservados.
- Reprodutibilidade: diagnósticos guardam SPIR-V original e HLSL exato, hashes
  SHA-256/bytes e cada tentativa em diretório imutável. O erro nativo registra
  `sourceHash`, `compileFlags` e `entryPoint`; o HRESULT do compilador continua
  sendo propagado.
- JIT/memória: leituras de admissão não ficam presas ao pico de um snapshot
  anterior. Um lease antigo não altera as contagens da sessão seguinte.
  Compilação tem preflight de scratch, trim de recuperação e recusa controlada
  se continuar sem folga.
- Telemetria registra guest, JIT, shader cache, recursos D3D12 e uploads em
  categorias distintas; os contadores são explicitamente não aditivos porque
  parte dos bytes também aparece em métricas do processo.

## Política de quota

O teto do host é o menor entre a cota reportada pelo sistema e o máximo
configurado. A opção de 6 GiB só permite esse teto quando o sistema efetivamente
reporta 6 GiB; se o Xbox reportar 5 GiB, o limite permanece 5 GiB. A configuração
de 6 GiB do guest descreve o espaço guest; cada commit do host continua sujeito
à cota do processo. Regiões JIT são reservas virtuais e não commits físicos;
suas páginas são comitadas sob demanda. O máximo de regiões permanece limitado
a oito, com preflight de commit e 128 MiB de folga conforme o ensaio comunicado.
Não foi configurado pagefile/swap.

## Testes executados

- `dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore -v:q` — PASS, zero erros; avisos NU1900 por indisponibilidade do feed privado.
- `dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll <native.dll> <spirv-cross.dll> --warp --shaders23-only` — PASS no WARP, debug layer sem erros.
- Mesmo comando sem `--warp` — PASS na GPU física, vendor `0x8086`, debug layer sem erros.
- `dotnet run --project eng/JitCacheRegression/JitCacheRegression.csproj -c Release --no-restore` — PASS, 162 verificações, incluindo 100 reinícios de sessão, limites 5/6 GiB condicionados à cota reportada e admissão de compilador após recuperação.
- `dotnet build src/Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release --no-restore -v:q` — PASS, zero erros; avisos NU1900 e um aviso existente CS0067.

## Limite da conclusão

`E:\Logs (23).zip` contém os diagnósticos X3694, X4576 e X3511, mas não os
arquivos `.hlsl`/`.spv` originais. Portanto, os fixtures comprovam as regras de
tradução e o comportamento em padrões equivalentes; ainda não comprovam que os
três shaders exatos de Brothership, Mario Kart 8 e Splatoon 2 passaram. A
correção do X3511 é intencionalmente restrita: se o HLSL real usar um limite ou
coordenada variável, ele precisa de diagnóstico próprio, sem trocar LOD,
truncar iterações ou ignorar o erro do FXC.

O teste de 128.193 funções e os 22 frames citado no relatório do Codex não veio
acompanhado de log/captura nesta pasta. Não foi iniciada uma sessão desses
jogos nesta validação. Para certificar o caso exato, preservar e enviar a pasta
`shader-failures` da sessão e o log completo de boot/memória do jogo.

## Auditoria da command list e pacote Xbox

O relato de que um readback deixa a command list fechada para o próximo draw
não se confirmou no caminho atual. O `Flush` gerenciado marca `_commands=false`;
o readback nativo abre/submete sua lista auxiliar e termina com `recording=false`.
Antes do draw seguinte, `ReserveDescriptors` chama `EnsureCommands`, que inicia
uma lista nova. Captura também deixa o estado gerenciado fechado, de modo que o
próximo draw passa pelo mesmo caminho. A regressão Logs23 repetiu readback e
dispatch e passou. Não foi necessário acrescentar uma correção especulativa.

A primeira execução integrada em WARP foi feita com guest counters desativados
pela configuração de compatibilidade do host, por isso a asserção de um sample
falhou com zero. Repetida com
`RYUJINX_UWP_D3D12_ENABLE_GUEST_COUNTERS=1`, a bateria GAL passou, incluindo
máscaras de clear, estado guest e cleanup.

MSIX Xbox gerado e assinado:
`artifacts/xbox-logs23-stage45/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.134_x64_Test/Ryujinx.Uwp.App_0.2.0.134_x64.msix`

- Identidade: `Ryujinx.Xbox.UWP`, versão `0.2.0.134`.
- Assinatura: válida, `CN=Ryujinx Xbox Dev`.
- SHA-256: `8ECB91DD427B8CAB908CF1DE48E1045549BD93ECBD68974300A2F237A026A7F4`.
- A DLL nativa UWP passou pela validação AppContainer. O empacotador conferiu
  os hashes de CoreHost, native, SPIRV-Cross e libSkiaSharp no pacote. VCLibs
  x64 está incluído em `Dependencies/x64`.
- O pacote ainda não foi instalado/testado no Xbox e nenhum jogo foi usado
  para validar frame nesta rodada. O Demon Slayer visível foi interrompido a
  pedido do usuário devido à carga do PC; não há captura real do jogo.
