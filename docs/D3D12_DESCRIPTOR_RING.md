# Ring de descritores D3D12 e instrumentação

Escopo: CBV/SRV/UAV e samplers. O compilador DXIL não foi alterado nesta rodada.

## Implementação

- O caminho padrão usa posições lógicas crescentes, intervalos contíguos e
  aposentadoria pela fence da submissão que consome o intervalo. Comandos ainda
  não enviados reservam a próxima fence e também impedem reutilização.
- Cada página tem 49.152 views ou 1.536 samplers, a mesma capacidade total dos
  heaps anteriores. A capacidade é compartilhada entre submissões, sem divisão
  fixa em três fatias. Samplers respeitam o limite de 2.048 por heap visível.
- Se nenhum intervalo couber, consulta fences concluídas, recicla intervalos e,
  se necessário, cria outra página. Cada draw/dispatch reserva suas tabelas de
  um mesmo tipo em um só heap. Ao trocar heap, todas as tabelas são vinculadas
  novamente. As páginas ficam vivas no contexto, inclusive durante falhas.
- O pool tem teto de 64 MiB calculado por capacidade × incremento de descritor.
  Isso é uma estimativa de espaço de descritores, não medida de residência GPU.
  No teto, aguarda somente fences já submetidas. Se o espaço estiver ocupado
  pela lista ainda aberta, retorna erro explícito, sem sobrescrever descritores
  ou enviar uma lista que pode conter queries abertas.
- A GAL deixa de fazer flush por acumular 16.384 views ou 512 samplers. Os
  limites já validados por draw são preservados. RTV/DSV e command allocators
  mantêm suas políticas anteriores; seus flushes/esperas não desapareceram.
- As páginas são reutilizadas e mantidas até destruir o contexto, dentro do
  teto. Não foi implementado encolhimento de páginas ociosas.

## Métricas

Export aditivo `RyuboxD3D12_GetDescriptorStats`, estrutura versionada e wrapper
`D3D12NativeDevice.GetDescriptorStats()`. Contabiliza descritores consumidos,
pico por submissão, ocupação e pico do ring (inclui padding de wrap), páginas,
criações, trocas de heap, wraps, intervalos recuperados, pressão, falhas e
esperas. Tempos de espera são medidos com QueryPerformanceCounter, em µs.
O pico de ocupação é conservador: só desconta fences consultadas/concluídas.

`ringWaits`/`ringWaitUs` identificam pressão no teto do pool. `cpuFenceWaits` e
tempo total incluem readbacks e reutilização dos command allocators. Nenhum
desses tempos é uma medição de duração GPU por frame.

O CoreHost registra `d3d12-descriptors` a cada cinco segundos na thread GPU,
com deltas de consumo/esperas/trocas, picos e ocupação. A coleta não executa
flush, readback ou espera. Picos e máximos são cumulativos no contexto (que no
frontend pode incluir mais de uma sessão). O primeiro delta parte de zero.

Para A/B, `RYUBOX_D3D12_FIXED_DESCRIPTORS=1` seleciona as antigas fatias ao criar
o contexto. A GAL consulta o modo nativo para restabelecer os flushes antigos.
Ambos os modos usam o mesmo backend D3D12 e compilador. Ocupação/wrap/reclaim
do ring não se aplicam ao modo fixo; consumo, picos por submissão e tempos de
espera são medidos em ambos.

## Validação reproduzível

`eng/D3D12InteropRegression/DescriptorRingTests.cs`:

- 50.003 dispatches com UAVs distintos, ultrapassando o heap completo sem
  submissão forçada no modo ring; confere todos os slots do resultado.
- Quatro ciclos de 3.301 dispatches com CBV, SRV, UAV e samplers variáveis;
  confere cada resultado e exige reciclagem sem crescimento após aquecimento.
- 1.701 draws atravessando a capacidade de samplers com queries abertas;
  confere pixels, samples passed e primitivas geradas.

`tests/D3D12DescriptorRingTests.inl`: modelo independente dos donos dos slots,
30.000 tentativas com fences atrasadas, wrap, capacidade exata, alocação grande
demais, falha atômica e UINT64_MAX/device removal sem liberação indevida.
Executado pelo smoke nativo.

```powershell
.\eng\Build-RyujinxUwpNative.ps1 -Configuration Release `
  -OutputRoot 'E:\ports\ryujinx uwp\artifacts\descriptor-ring-native' `
  -IntermediateRoot 'E:\ports\ryujinx uwp\artifacts\descriptor-ring-obj'
dotnet build eng/D3D12InteropRegression/D3D12InteropRegression.csproj -c Release --no-restore
dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll `
  'E:\ports\ryujinx uwp\artifacts\descriptor-ring-native\Release\Ryujinx.Uwp.Native.dll' `
  'E:\ports\ryujinx uwp\artifacts\d3d12-spirvcross-validation\spirv-cross-c-shared.dll' `
  --warp --descriptors-only
```

Repetir sem `--warp` para hardware; repetir com a variável de A/B para fatias
fixas. Remover `--descriptors-only` executa a bateria completa. Medir modos em
sequência, sem outra carga GPU concorrente. Registrar adapter, debug layer e
resultado; tempo de microbenchmark não equivale a FPS de jogos.

## Resultados

Concluído e validado em 16/09/2026.

- Smoke nativo: PASS. O modelo independente executou 30.000 alocações com
  fences atrasadas, proteção de comandos não submetidos, wrap, heap cheio,
  alocação oversized, device removal e falha atômica. A bateria nativa então
  concluiu 115 submissões, 115 fences e 0 erros da debug layer.
- Regressão gerenciada em GPU física Intel 8086:5916: PASS. A bateria completa
  também aprovou os formatos de cor, depth, geometria, geometry/tessellation,
  cache, ABI e descarte de recursos.
- Ring de views: 50.003 UAVs distintos em 2 submissões, contra 5 no modo fixo;
  2 páginas, 50.003 descritores recuperados e 0 falhas de alocação.
- Ring de samplers: 13.204 samplers e 39.612 views em 8 submissões, contra 32
  no modo fixo; 3 páginas de sampler, 7 wraps e 52.816 descritores recuperados.
- Ring gráfico com query aberta: 1.701 views/samplers em 2 submissões, contra
  5 no modo fixo; samples, primitivas e pixels conferidos.
- WARP reproduziu os mesmos resultados funcionais. O A/B mostra menos
  submissões e maior capacidade por lista, mas o tempo do microbenchmark não é
  FPS de jogo e não autoriza prometer ganho universal no Xbox.

Arquivos de evidência: `artifacts/descriptor-ring-native-smoke.log`,
`artifacts/descriptor-ring-full-hardware.log`,
`artifacts/descriptor-ring-hardware.log` e `artifacts/descriptor-fixed-warp-2.log`.

## Referências

- [Microsoft: heaps visíveis a shaders](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shader-visible-descriptor-heaps)
- [Microsoft: gestão de recursos por fences](https://learn.microsoft.com/en-us/windows/win32/direct3d12/fence-based-resource-management)
- [Microsoft: tabelas de descritores](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-descriptor-tables)

Trocas de heap podem ter custo no driver; a estratégia tenta manter a página
atual e mede as trocas. O ganho em jogos e no Xbox exige comparação posterior.
