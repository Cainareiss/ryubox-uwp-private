# Pokémon Legends Z-A: continuação do diagnóstico guest

Atualizado em 28/09/2026. Este registro complementa
`POKEMON_ZA_GUEST_NULL_20260927.md`; não declara o crash corrigido.

## Resultados novos

- SafeJit ligado e desligado, assim como dispatch pinned e epoch, reproduziram
  a mesma leitura inválida `0xD0`. A suspeita de que as verificações extras do
  SafeJit fossem a causa não se confirmou.
- Foi adicionada uma regressão sintética que pede uma interrupção durante um
  bloco guest com GPR, SIMD e NZCV vivos. O teste passou nas oito combinações
  de SafeJit, dispatch pinned/epoch e loop nativo/gerenciado.
- `JitCacheRegression`: 226 verificações aprovadas. Isso verifica a preservação
  básica na chamada host, mas não cobre todos os interleavings de kernel do jogo.
- A configuração epoch com SafeJit desligado também terminou com o mesmo
  acesso inválido; apresentou 12 frames antes do encerramento, insuficientes
  para validar a tela inicial.

## Próxima medição

O probe existente observa a aquisição de traduções, não necessariamente cada
bloco interno. Foi adicionada instrumentação de entrada dos blocos guest
selecionados, opt-in pelo `RYUJINX_UWP_GUEST_PROBE`, e está sendo preparada uma
build diagnóstica isolada para descobrir se a inicialização de `0x0E413780`
executa e se publica o sinalizador em `0x0E413880`.

## Estado

O problema permanece aberto. O crash ocorre quando o próprio fluxo guest
escolhe `X0=0` e lê `X0+0xD0`; não foi aplicado fallback que esconda o acesso,
nem aumento de orçamento. Nenhuma captura jogável foi obtida. A próxima
alteração corretiva deve depender do traço por bloco ou de uma nova evidência
que identifique a primeira divergência guest.
