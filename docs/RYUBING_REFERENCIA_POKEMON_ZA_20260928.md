# Pokémon Z-A — referência Ryubing Canary 1.3.183

Pesquisa e comparação em 28/09/2026. O usuário identificou a Canary 1.3.183
como versão que executa bem o jogo. Isso é uma referência de compatibilidade,
não uma medição local de desempenho nem prova de correção do Ryubox.

## Fonte recuperada

O servidor canônico `git.ryujinx.app` não respondeu às tentativas desta rodada.
Foi utilizado o [espelho de código](https://git.panintegr.al/Backup/ryujinx),
sem executar binários externos nem substituir o projeto principal.

- Tag exata: `Canary-1.3.183`.
- Objeto da tag: `b18e06b6f73b7395ead4287cc8ba0b66e888d4bd`.
- Commit: `904d4a7eb09c220ab05f6718f5790a4af36523b2`, de 22/10/2025.
- Assunto: `gdb: Make waiting for a process to start more forgiving (200ms per poll 10x -> 500ms)`.
- Checkout separado: `artifacts/reference-ryubing-canary-1.3.183/`.
- Repositório de referência e histórico: `artifacts/reference-ryubing-20260928/`.
- HEAD do espelho: `e3940abe01a2d3039a35fd7aa5e21cfa8cf0d430`, de 27/07/2026.
  A data de sincronização do espelho não transforma esse commit na versão mais recente.
- Licença MIT consultada; conservar os avisos se código for adaptado.

A tag foi obtida por `ls-remote --tags` e `fetch ... tag Canary-1.3.183`.
Esses identificadores asseguram reprodutibilidade da referência recuperada;
não equivalem a uma verificação independente de assinatura do publicador.

## O que a comparação mostrou

A nossa base já deriva do Ryubing. Nove arquivos centrais do HEAD do espelho
têm exatamente o mesmo blob Git que `origin/master` local, antes das adaptações
do Ryubox:

- `ARMeilleure/Translation/Translator.cs`;
- `ARMeilleure/Instructions/InstEmitFlowHelper.cs` e `NativeInterface.cs`;
- `ARMeilleure/CodeGen/X86/Assembler.cs`;
- `Ryujinx.Cpu/Jit/MemoryManager.cs`;
- `Ryujinx.HLE/HOS/Kernel/Threading/KAddressArbiter.cs` e `KScheduler.cs`;
- `Ryujinx.HLE/HOS/Kernel/SupervisorCall/Syscall.cs`;
- `Ryujinx.HLE/HOS/Services/Fs/FileSystemProxy/IStorage.cs`.

Isso **não** significa que a árvore de trabalho modificada seja idêntica ao
Ryubing, nem que todos esses arquivos sejam iguais aos da Canary 1.3.183.
A versão informada pelo usuário é anterior a diversas mudanças já herdadas:

| Mudança upstream posterior à 1.3.183 | Commit | Situação da análise |
| --- | --- | --- |
| Máscara de validação de SetHeapSize | `5ced2bf7` | Correção já presente na base; não é um patch novo para importar. |
| Memory changes 3 | `92b61f9d` | Conjunto amplo de alterações; não foi demonstrado como correção específica do nosso crash. |
| Correção de KAddressArbiter | `3a593b60` | Arquivo da base coincide com a referência posterior. |
| Cache JIT por instância | `6fb71b08` | Posterior à versão funcional relatada; comparar com adaptações UWP. |
| mono-jit-v2 / tabela de endereços | `a82350bb` | Posterior à versão funcional relatada; não é um interpretador de fallback. |

Entre 1.3.183 e o HEAD do espelho há 134 arquivos alterados nos diretórios
CPU/ARMeilleure/kernel/Fs consultados, incluindo reorganizações e mudanças
semânticas. Reverter tudo misturaria causas. É necessário separar diferenças
upstream posteriores de diferenças locais UWP; ambas continuam candidatas.

## Diferenças de execução relevantes

No `ConfigurationState.cs` da própria 1.3.183, os padrões são:

- `MemoryManagerMode.HostMappedUnsafe`;
- `EnablePtc = true`;
- `BackendThreading.Auto`.

O teste atual do Ryubox usa o caminho de memória por software, adaptações de
NativeAOT/dispatch, controle de vida útil do JIT e cota de 5 GiB. A versão
desktop de referência usa backends Vulkan/OpenGL, não o nosso D3D12.
Os padrões encontrados não comprovam a configuração usada no teste do usuário.

A [documentação do projeto](https://docs.ryujinx.app/) também distingue os
modos de memória por software e host-mapped, e descreve o cache de traduções
CPU. Isso ajuda a explicar diferenças possíveis de velocidade, mas não prova
o motivo do ponteiro nulo. Não ligar HostMappedUnsafe/threading indiscriminadamente
no UWP, nem importar sua ausência de cota como solução para Xbox.

## O crash que precisamos comparar

No nosso teste, o fluxo guest escolhe `X0=0` e tenta ler `X0+0xD0`.
A instrução é `0x8770B4C`, na função `0x8770B40`, chamada pelo trecho
`0xAF99AA4`. O singleton `0xE413780` e o sinalizador `0xE413880` permanecem
zerados na observação do crash. A cadeia de inicialização identificada passa
por `0x8BE3BE0`; seu slot de despacho indireto é `0xC11A648`.

Essa é evidência imediata de falha no estado/fluxo guest, **não comprovação de
bug no rasterizador DX12**. A origem pode envolver execução CPU, memória,
serviços, ordem de inicialização ou dependência de outro subsistema. Ainda
não foi localizada a primeira divergência. Não ignorar o acesso inválido,
inventar o objeto ou aumentar a cota para esconder o sintoma.

Também existem relatos públicos de crashes de Z-A em outras versões do
Ryubing: [issue 136](https://github.com/Ryubing/Issues/issues/136) e
[issue 143](https://github.com/Ryubing/Issues/issues/143). Eles não demonstram
o mesmo defeito nem contradizem o funcionamento relatado na 1.3.183.

## Próximo comparativo controlado

1. Reproduzir a referência com a mesma versão legalmente obtida do jogo,
   firmware, save e sem mods; registrar configuração real e primeiro frame.
   O código de referência foi baixado, mas esse teste ainda não foi executado.
2. Comparar primeiro criação/despacho de `resident_ui`, leitura do slot
   `0xC11A648` e publicação dos dois campos. Usar offsets relativos ao módulo
   se o endereço de carregamento mudar.
3. Examinar separadamente: ABI de callbacks NativeAOT, dispatch/retorno do JIT,
   aliases/commit/zeroing da memória e respostas dos serviços de arquivos.
   Testar uma diferença por vez em harness, sem substituir todo o backend.
4. Correlacionar pressão e reclaim do JIT. O probe pesado amplifica recompilação;
   uma execução instrumentada não serve como benchmark do jogo normal.
5. Após corrigir a primeira divergência, exigir captura real da seleção de
   idioma/tela inicial, ausência de acesso inválido e regressão de memória/JIT.

## Conclusão

A referência exata agora está disponível localmente. Ela foi mais apropriada
para comparar nosso núcleo que transplantar a arquitetura inteira de Eden/Suyu.
A comparação identificou a proteção de recursão adicionada depois da 1.3.183
como uma diferença redundante no dispatcher fixado do Ryubox. O teste com fluxo
plano e crescimento estável do JIT executou a inicialização e eliminou o acesso
nulo durante 12 minutos. Como as execuções antigas também sofriam dezenas de
milhares de evictions, a contribuição individual dessas duas mudanças ainda
precisa de um A/B na mesma build. Os detalhes e limites da validação estão em
`POKEMON_ZA_TRACE_FINDINGS_20260928.md`.

Isso não certifica desempenho desktop no Xbox nem resolve a imagem ainda preta.

## Validação comparativa repetida — 28/09/2026

O runtime `direct-d3d12-za-canary183` é um CoreHost NativeAOT do Ryubox em modo
de compatibilidade, não um executável original Canary. Sua sessão anterior
processou e apresentou centenas de frames sem crash, mas a única captura
preservada (`pokemon-frame-8.png`) é preta; portanto ela não prova que a
interface do Pokémon foi desenhada. Retestes posteriores desse caminho também
voltaram a falhar em `0x8770B40` lendo `0xD0`.

Foi feito um A/B adicional com uma nova opção local `-Ptc` no runner. A nova
publicação NativeAOT confirmou `ptcEnabled=True`, mas a sessão caiu no mesmo
acesso inválido depois de apenas 8 presents. Assim, PTC isoladamente não
explica o resultado anterior nem corrige o boot. A opção é diagnóstica e não
altera o padrão do emulador.

Também tentei restaurar a aplicação desktop da tag exata para testar o guest
fora do CoreHost. O feed canônico de pacotes não estava acessível e o restore
ofereceu `Ryujinx.LibHac 0.21.0-alpha.133` no lugar da dependência exigida
`0.21.0-alpha.117`; cancelei antes de compilar/executar uma referência com
dependência divergente. Portanto ainda não há uma execução da aplicação
desktop Canary que possa confirmar visualmente o jogo.

Evidências do CoreHost: `artifacts/direct-d3d12-za-arbiter-fix-20260928/`
(`probe-20260928-190247`) e
`artifacts/direct-d3d12-za-canary183-ptc-20260928/`
(`probe-20260928-192658`). O resultado segue inconclusivo quanto à primeira
divergência guest; não é seguro sintetizar o singleton ou ignorar o acesso.
