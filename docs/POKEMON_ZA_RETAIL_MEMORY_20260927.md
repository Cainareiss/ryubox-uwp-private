# Pokémon Z-A — reteste da configuração de memória (27/09/2026)

## Relação com P1

P1 já tinha instrumentação de MapPhysicalMemory, cobrança dos trechos novos,
preflight de commit e regressões de reclaim seguro do JIT. Isso não equivalia
à aceitação de Pokémon/Rivals: o próprio plano registrava esse limite.

O reteste encontrou uma configuração adicional no CoreHost: Z-A e Demon
Slayer recebiam automaticamente `MemoryConfiguration6GiB`, por ID ou nome do
arquivo. A variável de orçamento do host também selecionava esse layout.
Isso contradizia o comentário de preservar o contrato retail do Switch.

O kernel anuncia 4.916 MiB de application pool nesse layout, contra 3.285 MiB
no layout retail de 4 GiB. A correção remove a seleção expandida por jogo e
por variável do host. O marcador explícito de teste local com pool reduzido
continua separado; o log da configuração agora ocorre depois desse marcador.

Não foram removidas verificações de commit, alterado o algoritmo de aliases,
aumentada a cota do teste ou implementado swap.

## Evidência anterior

- Com layout guest de 6 GiB e cota host de 5 GiB: recusa de commit adicional
  de 4.294.967.296 bytes; uso do processo de 1.836.539.904 bytes. Sem frame.
- Uma tentativa anterior com cota host de 6 GiB não resolveu: recusa posterior
  de 536.870.912 bytes com uso de 6.138.281.984 bytes. Não é o perfil aceito.

## Build e testes desta rodada

- CoreHost NativeAOT Release recompilado com sucesso. Há avisos de auditoria
  NuGet e de trimming/AOT; sucesso de build não certifica todos esses caminhos.
- DLL: `artifacts/direct-d3d12-za-retail-20260927/Ryujinx.Uwp.CoreHost.dll`.
- SHA-256: `E7D63912B57097B99AEC0E162EADBD24839CC0EDB447D919ED1ADB52DAC14BF9`.
- Native D3D12 mantida, SHA-256:
  `EC17B1E12F0AEE76B5465395CF6B052D59C25D518ED552369AF504C41956D058`.
- `GuestCommitRegression`: aprovado (limites, overflow, inválidos, custo zero,
  diagnóstico de recusa).
- A cópia de `src/eng/JitCacheRegression` ainda esperava a política antiga
  de headroom/limite de regiões. Falhou ao ser recompilada. As asserções foram
  atualizadas para separar reserva de commit, verificar o limite exato de
  128 MiB de folga e o teto de oito regiões virtuais. Nenhuma proteção do
  allocador foi relaxada nesta rodada. Após recompilar: 159 checks aprovados.
- A cópia externa de JitCacheRegression tinha a fronteira de 128 MiB correta,
  mas uma mensagem de teste ainda dizia 448 MiB; somente essa mensagem mudou.

## Teste real

Execução oculta iniciada em `2026-09-27T05:58:32Z`, via CoreHost D3D12 direto,
sem frontend, no HD, GPU física, com cota host de 5 GiB e AutoPressA.
Captura solicitada ao renderer; nenhum gráfico foi fabricado.

O jogo passou da recusa inicial. `MapPhysicalMemory` aceitou 3.281.895.424
bytes; posteriormente o próprio guest liberou parte da memória. Aos ~114 s,
o processo usava 3.125.944.320 bytes de commit privado (aproximadamente
2,91 GiB), com pressão Normal. Ainda não havia Present nesse ponto.

Logs exclusivos desta sessão preservados em
`artifacts/direct-d3d12-za-retail-20260927/test-20260927-025832`.
O arquivo `ryujinx-session.log` contém o log completo do guest desta execução.

### Resultado final

O jogo chegou a 12 Presents aceitos pelo DXGI, em surface 1280x720, mas
encerrou após aproximadamente 9min26s. Não repetiu a recusa inicial de commit
nem apresentou `JitCacheFullException`. Houve reclaim de código durante a
sessão; isso não substitui a validação de funcionamento completo.

O novo encerramento é `InvalidMemoryRegionException`, propagado por
`MemoryManager.ReadGuest` / `NativeInterface.ReadGuestChecked`, na thread
`JobThread02` (ID guest 43), por acesso virtual a `0xD0`:

- PC reportado: `0x08770B40` (`main_app.nss:0x26BB40`);
- LR: `0x0AF99AC4` (`main_app.nss:0x2A94AC4`);
- X0 e X19: zero;
- `deviceLost=False` no encerramento.

Isso é compatível com acesso através de ponteiro nulo no guest, mas não
identifica por si só a origem do valor (carregamento de recursos, tradução,
memória, sincronização etc.). Não é justificativa para retornar zero em
leituras inválidas, suprimir a exceção ou desligar proteções de memória/JIT.

A primeira captura solicitada foi descartada pelo filtro por conteúdo
uniforme (`secondaryColorBin=0`). Nenhum PNG útil foi salvo. Os 12 Presents
comprovam atividade de apresentação, não a tela inicial correta.
Portanto: regressão de configuração corrigida, aceite de jogo/P1 integral
ainda pendente. Não foi gerado MSIX nem feita validação no Xbox nesta rodada.
