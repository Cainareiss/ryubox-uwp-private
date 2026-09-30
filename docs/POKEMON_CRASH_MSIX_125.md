# Pokémon: contenção de falha de memória e MSIX 0.2.0.125

## Diagnóstico e limite da correção

Log: `Ryujinx_UWP_2026-09-22_01-27-26-846.log`, sessão visível.
JobThread02 leu o endereço guest `0xD0`, com X0/X19 nulos e PC `0x8770B40`.
A exceção escapou de um callback `UnmanagedCallersOnly`, encerrando o processo
antes que `GuestFaultHandler` pudesse registrar uma falha controlada da sessão.

Esta mudança corrige a passagem insegura da exceção, **não a origem do ponteiro
nulo do jogo**. Não certifica passagem da seleção de idioma ou compatibilidade.
Nenhum jogo foi iniciado para esta rodada, conforme pedido do usuário.

## Implementação

- Callbacks de leitura/escrita/tracking registram a primeira falha no tradutor
  e interrompem o contexto, sem lançar através de frames JIT/reverse P/Invoke.
- O emissor verifica o estado imediatamente após o callback e retorna ao
  dispatcher antes de consumir o valor de retorno ou executar instruções
  seguintes. Não se trata de devolver zero e continuar o jogo.
- O caminho de acesso inválido sem slow path também retorna explicitamente.
- O dispatcher existente propaga a falha no lado gerenciado; o tratamento de
  sessão do CoreHost continua responsável por encerrar a emulação.
- PTC interno atualizado de 7023 para 7024 para não reutilizar código gerado
  antes das verificações novas.

## Testes sem jogos

`dotnet run --project eng/JitCacheRegression/JitCacheRegression.csproj -c Release --no-restore`

152 verificações aprovadas, incluindo 80 combinações novas: leituras/escritas
de 8/16/32/64/128 bits, válidas e inválidas, despacho gerenciado/nativo,
pinned/epoch. Código ARM64 real é traduzido e executado com chamada aninhada;
na falha, o destino escalar não muda e stores posteriores não executam.
Casos válidos continuam e escrevem a sentinela esperada; JIT é liberado.

As primeiras versões da fixture tinham tabela de páginas nula e uma saída
`BR 0` inadequada para o dispatcher não pinned; foram corrigidas para PTEs
zeradas em memória válida e `RET 0`. Não foram erros de jogo nem testes ocultados.

## Conteúdo previsto do pacote

CoreHost NativeAOT reconstruído com esta proteção e as melhorias atuais da
árvore. Backend nativo: `d3d12-pokemon-so-alias-fixed`, com as correções de
constantes, orientação e stream output já exercitadas. D3D11 preservado,
sem usar fallback D3D11 para validar o Pokémon. Versão UWP: 0.2.0.125.

Status de empacotamento: concluído e assinado. Build UWP com 0 erros e
17 avisos; publish NativeAOT com avisos de trimming/AOT. Não instalado
automaticamente e nenhum jogo iniciado nesta rodada.

Pacote:
`artifacts/msix-0.2.0.125/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.125_x64_Test/Ryujinx.Uwp.App_0.2.0.125_x64.msix`.
Certificado e dependências ficam na mesma pasta de distribuição.

Verificação do conteúdo por SHA256: CoreHost, backend nativo e SPIRV-Cross
empacotados correspondem exatamente aos binários selecionados:

- CoreHost: `D0CD53A1C2FB65CF3D5DFC52CA29FF911C464DAD969BFCFAFF749D8B99DA0AFD`.
- Native: `26333E75818286DFDA5C17F7F73FC29048FCFD5A7301AB758DBE0101344C4884`.
- SPIRV-Cross: `82873DD808AA7C10F3D105D11FB47BB5B9A54D244D5DB42DCB3BB1BC015DBF76`.

Regressões físicas `--constants-only` e `--stream-output-only` aprovadas;
incluem orientação/culling e cópias de constantes com offsets não alinhados.
Evidência dos 152 testes JIT: `artifacts/jit-memory-fault-125.log`.
