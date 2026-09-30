# Pokémon Legends Z-A: stream output — 21/09/2026

## Atualização: ranges compartilhados e diagnóstico de memória

A tentativa posterior alcançou 159 apresentações e revelou outra rejeição:
dois slots de stream output compartilhando o mesmo recurso. A validação nativa
agora permite somente intervalos disjuntos; intervalos sobrepostos e colisões
com buffers de contador continuam recusados. O teste escreve duas sequências
distintas em ranges do mesmo buffer e confere os bytes por readback.

Artefato: `artifacts/d3d12-pokemon-so-alias-fixed/Release/Ryujinx.Uwp.Native.dll`.
Regressão `--stream-output-only` aprovada em WARP e GPU física. Isso não prova
que os ranges usados pelo jogo sejam disjuntos em todas as situações.

O reteste `Ryujinx_UWP_2026-09-21_23-40-51-782.log` ainda terminou com leitura
guest inválida em `0xD0`, PC `0x8770B40`, desta vez em JobThread02. Esse mesmo
ponto apareceu em outra tentativa em JobThread01. Não atribuir automaticamente
essa falha ao shader, ao JIT ou à memória sem evidência adicional.

Foi acrescentado diagnóstico à recusa de `MapPhysicalMemory`, sem mudar seu
comportamento. Publish NativeAOT concluído com avisos de trimming/AOT.
No teste oculto `pokemon-za-memory-diagnostic`, log
`Ryujinx_UWP_2026-09-21_23-57-06-590.log`, a recusa aos 10,388 segundos foi:

- pedido adicional: `0x400000` (4 MiB), endereço `0x1597000000`;
- current = limit = `3444572160` bytes (3285 MiB);
- marcador local de pool reduzido de 2 GiB ausente.

Portanto, trata-se do limite guest normal atingido, não de evidência de falha
de commit do Windows. Pode ser uma sondagem de capacidade feita pelo jogo;
a relação causal com o acesso nulo posterior ainda não está estabelecida.
Não aumentamos limites nem retornamos sucesso para alocação recusada.

Essa sessão permaneceu ativa por mais de quatro minutos e ultrapassou 950
apresentações, sem repetir o acesso inválido. A mesma recusa de memória ocorreu
nessa execução, reforçando que ela isoladamente não explica o crash. A captura
GPU inicial mostra somente o fundo azul, não confirma a passagem pelo idioma.
`PrintWindow` da janela oculta retorna preto e não serve como evidência visual.

Telemetria por volta de 00:01 UTC: cerca de 9 callbacks/s, nenhum ring wait ou
erro de descritor; 270 waits de fence e ~1,98 s acumulados num intervalo de
~5,16 s. Esses números não são benchmark de gameplay. Memória: private commit
~6,10 GB, working set ~2,49 GB, política de pressão Emergency. Não equivalem ao
consumo físico nem ao limite guest; também não projetar FPS do Xbox a partir
dessa execução de diagnóstico no PC.

A sessão foi encerrada para repetir com capturas GPU sequenciais, sem janela
visível, em `artifacts/pokemon-za-memory-series`. Antes do relançamento, o harness
foi recompilado (zero avisos/erros) e `--stream-output-only` novamente aprovado
em WARP e GPU física, incluindo a checagem de debug errors após SO compartilhado.

## Falha reproduzida

A sessão `pokemon-za-winding-fixed` chegou à seleção de idioma e encerrou
em ConfigureStreamOutput, D3D12Program.inl:756, HRESULT 0x80070057.
Não foi uma falha de teclado; a janela preservou a última imagem.

O teste isolado `StreamOutputPackingTests` reproduziu a mesma rejeição
com dois escalares e um float2 em TEXCOORDs distintos. A validação comparava
componentes relativos à semântica com a máscara de lanes do registrador
refletido. O compilador pode compactar semânticas no mesmo registrador.

## Alterações

- Normalizar a máscara refletida para a origem da semântica ao validar SO.
  O layout enviado ao D3D12 continua relativo à semântica, sem deslocamento
  artificial dos componentes ou descarte de saídas.
- Preservar diagnóstico de semântica/index/componentes/stream e assinatura
  refletida se ainda houver divergência.
- Capturar HLSL e layout SO de programas que falham após a compilação,
  somente quando RYUBOX_D3D12_LOG_DIR está definido.
- Teste de valores reais em GPU, cache reload, semântica ausente e contagem
  inválida, além da regressão existente de SPIR-V Xfb direto/agrupado.

Referência: [D3D12_SO_DECLARATION_ENTRY](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_so_declaration_entry).

## Evidência

DLL anterior `artifacts/d3d12-constants-fixed/Release/Ryujinx.Uwp.Native.dll`:
teste novo falha com o mesmo HRESULT e linha da validação original.

DLL corrigida `artifacts/d3d12-pokemon-so-fixed/Release/Ryujinx.Uwp.Native.dll`:

- `--warp --stream-output-only`: PASS.
- GPU física, `--stream-output-only`: PASS.
- GPU física, `--constants-only`: PASS (inclui pixels e orientação).
- Build do harness: zero erros e avisos.
- Publish CoreHost: concluído com avisos preexistentes de trimming/AOT.

Comando de teste: `dotnet eng/D3D12InteropRegression/bin/Release/net10.0/D3D12InteropRegression.dll artifacts/d3d12-pokemon-so-fixed/Release/Ryujinx.Uwp.Native.dll artifacts/d3d12-spirvcross-validation/spirv-cross-c-shared.dll --stream-output-only`.

Primeira tentativa concorrente do WARP com Pokémon terminou por falta de
memória; o teste passou ao repetir isoladamente. A sessão diagnóstica de
Pokémon também encerrou por falha de alocação no JIT. Não confundir essa
falha com a validação de SO nem alegar que esta mudança resolve memória.

Reteste oculto com DLL corrigida em `artifacts/pokemon-za-so-fixed`:
primeiro processamento pesado levou 9.875 ms (não é benchmark comparável:
caches/condições diferem); apresentou 14 frames antes de encerrar às
19:53:47 UTC com `InvalidMemoryRegionException`, acesso guest 0xD0 em
JobThread01. Não chegou a reproduzir o ponto de falha anterior de SO.
Log `Ryujinx_UWP_2026-09-21_19-51-11-523.log`. Nova tentativa isolada em
`artifacts/pokemon-za-so-fixed-retest`.
Passagem da seleção de idioma e tela inicial ainda pendentes de confirmação;
o teste sintético não certifica resolução de todos os crashes do Pokémon.
Sem MSIX, sem fallback D3D11, sem alterações nas configurações do sistema.
