# Rivals — validação local D3D12 em 15/09/2026

## Escopo

Execução oculta de Rivals of Aether pelo CoreHost, backend D3D12 nativo.
Sem alternar para D3D11, instalar pacote ou desligar o computador.
O nome histórico `Test-RyujinxD3D11DirectHost.ps1` não determina o backend:
o teste usa explicitamente `-Backend D3D12 -Child -Background`.

## Falha reproduzida no cache

A execução iniciada às 14:40:30 UTC registrou nove callbacks de apresentação,
mas terminou às 14:41:48 UTC com `ObjectDisposedException` em
`D3D12GalTexture.CheckOwner`, durante a cópia 2D. Isso não comprova nove
imagens visíveis ou uma partida em andamento.

`AutoDeleteCache.ApplyMemoryPressurePolicy` percorre as entradas procurando
texturas que não precisem de readback. Quando as antigas estão modificadas,
essa busca podia remover a própria textura recém-adicionada. O consumidor
recebia uma textura já liberada; o registro posterior no `TextureCache`
também podia deixar uma entrada inválida para o cleanup.

Correções:

- `Add` e `Lift` protegem sua própria entrada contra essa busca seletiva.
- A remoção limpa `CacheNode` antes de soltar a referência.
- A operação Twod mantém referências explícitas da origem e do destino até
  o fim da cópia, com liberação em `finally`. Criar o destino pode expulsar
  a origem do cache, mas não deve destruir um recurso ainda em uso.

## Evidência automatizada

`eng/TextureCacheRegression` compila o arquivo **real** `AutoDeleteCache.cs`
com dependências mínimas de teste, sem carregar uma GPU.

O caso falhava antes da proteção, com `Pressure evicted the newest texture`.
Depois passou em todos os níveis de pressão. Cobre também remoção de entradas
limpas antigas, referências de uma cópia durante eviction, falha simulada na
cópia e retorno da contabilidade de cache a zero.

```powershell
dotnet run --project eng/TextureCacheRegression/TextureCacheRegression.csproj -c Release --no-restore
```

Isso valida a política de cache, não a execução completa do jogo. Os objetos
de textura do teste simulam ownership; não substituem testes de pixels e
cleanup no device real.

## Limitações ainda abertas

- O orçamento nativo de recursos foi ampliado na rodada anterior. O teste
  local usa explicitamente 1536 MiB; isso não demonstra ausência de vazamento
  nem estabelece um orçamento adequado ao Xbox.
- O host local ainda desabilita component swizzle e guest counters para
  diagnóstico Intel. Não é evidência de cobertura integral da GAL.
- A normalização recente de viewports ainda exige validação de orientação,
  profundidade invertida e viewport vazio; não certifica fidelidade global.
- `nativePresent` do log compartilhado usa telemetria D3D11 e não deve ser
  interpretado como contador D3D12. É necessário conferir imagens capturadas
  e avanço dos frames, não apenas ausência de exceção.
- Publicação AOT Release concluída com exit code 0 (avisos de trimming/AOT
  continuam presentes). Log: `artifacts/rivals-cache-publish.log`.

## Repetição após correção do cache

Sessão iniciada às 14:57:35 UTC, processo 14928. `CoreHostStartSuccess`
às 14:57:43 UTC. Janela oculta e `audioVolume=0`, sem desativar emulação
de áudio. Captura solicitada em `artifacts/rivals-cache-fixed-frame.bmp`.

Hashes SHA-256 do runtime testado:

- CoreHost: `F1D433FB5753DE9D1DD970C06CFC6C5CFAE89DCD0A7A382E79EA4E90C4E3CCF3`
- Native: `75E3787B5DA53AEE9D234E179F4CE71CECD6B3058C2CAFD859E49A36C26BD0B7`

Resultado: às 15:03:34 UTC houve outra `ObjectDisposedException`, agora em
`TextureManager.UpdateRenderTargets -> D3D12Pipeline.SetRenderTargets`.
O total chegou a 103 unidades processadas e nove callbacks de apresentação;
a primeira captura estava completamente preta e nenhuma imagem útil foi salva.
Não houve comprovação de gameplay ou de FPS sustentado.

O cleanup terminou às 15:03:37 UTC sem repetir a exceção `RegionHandle`;
working set caiu de 1699 para 912 MiB. Uma única sessão não prova ausência
de vazamentos.

## Segunda falha de ownership: alvos de renderização

O teste passou a compilar também o **TextureManager real**. Antes da correção,
falhou em `SynchronizeMemory received disposed render target`: ao trocar um
alvo, `SignalModifying(false)` faz `Lift` do antigo e pode expulsar o novo,
que ainda não possuía referência. Além disso, `RefreshModifiedTextures`
encerrava a referência temporária de modificação embora o manager ainda
guardasse o alvo para uso futuro.

O manager agora mantém uma referência própria por slot até substituição ou
dispose, independente do estado de modificação. A referência do novo é
adquirida antes de soltar o antigo, com rollback em falha de sincronização.

Regressão PASS para cor e depth, troca sob pressão, refresh seguido de eviction,
falha de sincronização, mesmo alvo em slots diferentes e dispose repetido.
Evidência: `artifacts/rivals-cache-regression.txt`.

Também foi identificado que o JSON parcial de mute zerava defaults omitidos
no `DataContractJsonSerializer`. O harness agora fornece explicitamente os
defaults de VSync, áudio, logging, integrity e ignoreMissingServices. Portanto
a sessão 14:57 usou VSync=false (registrado no log) e não é benchmark comparável
com a anterior; a próxima execução preservará os defaults e só mutará volume.

Publicação da segunda correção em andamento:
`artifacts/rivals-target-ownership-publish.log`.

Atualização: publicação terminou com exit code 0. CoreHost SHA-256:
`F1FA5D7619FA659A53286B9DB1AD7C94E69C650190129447BA82450DFB323DF0`.
Nova sessão às 15:23:42 UTC, PID 7836; captura solicitada em
`artifacts/rivals-target-ownership-frame.bmp`. Às 15:27 ainda não havia
captura útil nem nova exceção. Isso não certifica correção completa.

## Configurações NativeAOT

O JSON explícito não resolveu a divergência: o input trace da sessão 15:23
contém `vsync=true` e `fileLogging=true`, mas o CoreHost registra VSync=false
e logging desabilitado. Portanto, omissão de defaults não explica sozinha
o comportamento da publicação AOT. O parser reflexivo é incompatível com
o caminho atual; os avisos IL2026/IL3050 já sinalizavam risco, mas não eram
uma prova isolada da causa.

`CoreHostSettings` foi separado e passou a ler cada campo via `JsonDocument`,
sem desserialização reflexiva. Campos ausentes conservam os valores padrão;
as restrições existentes do host (Handheld, resolução 1, SoftwarePageTable)
foram preservadas. O harness mantém defaults explícitos como documentação
da configuração do teste, mas a correção não depende deles.

`eng/CoreHostSettingsRegression` compila o parser real e testa mute sem
desativar áudio, true/false explícitos, strings, inteiros, ranges, defaults,
entrada malformada e preservação das políticas do host. Execução gerenciada
PASS; publicação e teste NativeAOT em andamento.

Também corrigido o teclado da janela D3D12: P/Plus usa o bit 9 da ABI e
O/Minus usa o bit 8. O mapeamento anterior estava invertido em relação a
`MapButtons`; o comando automatizado Plus do harness já usava o bit correto.
