# Logs (5): regressão de desempenho e candidata 0.2.0.119

## Evidência recebida

Arquivo: `C:\Users\Cainã\Downloads\Logs (5).zip`.
SHA256: `F0841125C0EE10AC5B16C17D6D09F133AD1F74E341C1E53CA601FB711B704AEC`.
Extraído sem alterar o original em `artifacts/logs5-118-audit-20260909`.

O ZIP contém 19 inícios em dois lotes, às 03h e às 23h UTC de 09/09/2026.
Não tratar os dois como uma única versão: o lote das 23h possui as métricas novas
`jitColdWaitMs`, `jitCompileMs`, reclaim por lote e logging buffered da rodada 118;
o das 03h tem os OOM antigos já descritos na reavaliação da Parte 1. O log não
inclui hash/identidade do pacote para certificar a versão de cada processo.

Corrigido também o auditor: ele descartava jogos sem Title ID no nome (incluindo
Sonic) e lia somente a parte inteira do FPS em cultura com vírgula. Agora resolve
o ID no intervalo da sessão, preserva decimais e lê UTF-8 explicitamente.
`FirstFrameSeconds` é o primeiro callback registrado na amostragem de um segundo,
não uma captura comprovando imagem visível. `processed` conta trabalho GPU, não FPS.

### Lote das 23h

| Jogo | Primeiro callback (s) | Média das últimas 30 amostras de FPS | FXC compilando (s) |
| --- | ---: | ---: | ---: |
| Sonic Mania | 19,1 | 13,43 | 0,125 |
| Dead Cells | 38,2 | 0,77 | 0,26 |
| Alien Hominid HD | 7,8 | 3,37 | 0 (7 cache hits) |
| Horizon Chase Turbo | 48,8 | 0,07 | 0 (6 cache hits) |
| Brilliant Diamond | nenhum | sem frames | 0 |
| Let's Go Pikachu | 45,0 | 3,26 | 0 (13 cache hits) |
| Rivals of Aether | nenhum | sem frames | 0 |

As médias incluem pausas/zeros no fim da sessão; não são benchmarks de uma mesma
fase controlada. Valores acima de 60 isolados tampouco comprovam gameplay acima
de 60: podem incluir apresentação acumulada. Dados completos em
`artifacts/logs5-118-audit-20260909/audit-summary.json`.

Sonic termina com cerca de 25,6 MiB de JIT usados, sem evictions. No watchdog aos
20,157 s: 8.258.723 lookups, 8.337 traduções, 3.967,84 ms acumulados de compilação
JIT, 0,28 ms de espera pelo lock frio, 242,80 ms de trabalho da thread GPU,
131,89 ms de compilação de shader e zero espera de queries. A GPU apresentou
27 frames nesse instante, pelo caminho direto, sem CPU upload. VSync estava off.
Isso contraria atribuir seus 13 FPS a recompilar shaders, cap de 30 ou readback
de apresentação. Não mede sozinho toda a repartição do tempo entre CPU e HLE.

Rivals acumula 234.914.106 lookups em 132,718 s; a thread principal tem
234.859.449 retornos, mas o GPU processou apenas quatro dispatches, sem shader
ativo nem espera GPU. Há 29,768 s acumulados de compilação JIT. O padrão aponta
para trabalho/espera no lado guest/CPU, não prova um defeito único para todo jogo.
Horizon ainda registra 14.336 evictions: custo de recompilação/working set deve
ser reavaliado no console depois da mudança do dispatcher.

Os fatals de Wonder e Blasphemous pertencem ao lote das 03h: limite de JIT de
512 MiB. Não há fatal equivalente no trecho das 23h enviado; ausência de registro
não certifica estabilidade de jogos que não avançaram.

## Comparação com 0.2.0.99

O pacote extraído está em `artifacts/reference-0.2.0.99`. Seu CoreHost contém
mensagens de perfis com native baseline e com native dispatch + um worker
High-CQ para títulos específicos. Não é correto descrever toda a .99 como
High-CQ global, nem dizer que executamos novamente 50 jogos nessa versão.
SHA256 do CoreHost .99 inspecionado:
`BB6A51F2035323E0322A82A462CA8AF8A4240412A4E3B7D4D38C1A1AAE279969`.

O relatório da Parte 1 documenta uma mudança posterior particularmente relevante:
chamadas entre traduções passaram a retornar obrigatoriamente ao dispatcher para
permitir reclaim seguro por função. Antes, pilhas de retornos guest podiam reter
regiões inteiras e esgotar o cache. Na configuração 118 o Xbox usa o loop gerenciado:
cada retorno passa por wrapper de contexto/FP, lookup, pin e verificações de estado.
Os milhões de retornos dos logs tornam esse custo relevante. Os ajustes anteriores
de constantes e logging não eliminavam essa mudança estrutural.

Não removemos pins, não voltamos às pilhas guest desprotegidas da .99 e não
atribuímos toda a diferença de FPS exclusivamente a esse mecanismo sem um A/B no Xbox.

## Correção implementada

- Separada seleção do dispatcher de High-CQ/re-JIT. Xbox passa a usar **loop nativo
  com pins e código baseline**, conservando zero workers, sem High-CQ e sem atalhos
  de tabela que saltem a aquisição do pin. Modo gerenciado continua disponível
  para comparação local com `RYUJINX_UWP_MANAGED_PINNED_DISPATCH=1`.
- No loop nativo, liberação anterior + lookup/pin seguinte acontecem em uma única
  chamada gerenciada e um lock de publicação, não duas transições por função.
- O callback nativo atende interrupção pendente antes da próxima tradução. Falhas
  retornam pela fronteira nativa antes de propagar o erro; não atravessam reverse P/Invoke.
- Se a próxima entrada for a mesma tradução ainda válida, mantém seu pin: elimina
  unpin/pin e busca redundantes. `IsRetired` é verificado sob o mesmo lock usado pela
  invalidação. Código aposentado nunca utiliza o atalho, mesmo com o mesmo endereço.
- Configuração real é registrada com `dispatchPolicy=native-pinned-v2`; o watchdog
  lê a seleção real do loop, evitando chamar o modo nativo de managed baseline.
- Sem mudanças em orientação, shaders, constantes, hazard tracking, firmware, input,
  resolução, relógio guest, limite de JIT ou contador de FPS. PTC desligado pelo usuário
  continua desligado. Não foram apagados saves, keys ou caches.

## Validação local e limites

- `eng/JitCacheRegression --benchmark`: **59 checks**, ARM64 e ARM32 executados de
  fato em x64, BL/RET/BR, ponto flutuante com dois modos de arredondamento, interrupção
  de loop em execução, auto-invalidação e nova entrada no mesmo endereço, dois pins
  compartilhados, falha de tradução, concorrência e liberação do JIT.
- `eng/PtcRegression`: **15 checks**; política/cache seguros preservados.
- `eng/D3D11MaterialRegression`: **78 checks** em WARP real; os caminhos gráficos
  não foram revertidos. Total 152 checks, não 152 jogos.
- Microteste real de 300 mil chamadas ARM64: no último par aquecido, gerenciado
  281,91 ms, nativo 234,78 ms. Outras execuções variaram com o PC. O atalho de pin
  também reduziu o custo do microteste de lookup. Isso mostra redução local de
  overhead, **não promete ganho proporcional no console nem Sonic a 60 FPS**.
- Evidências: `artifacts/logs5-dispatch-before.log`, `logs5-dispatch-after.log`,
  `logs5-dispatch-validated.log`, `logs5-ptc-validation.log`, `logs5-d3d11-validation.log`.

Continuam pendentes: A/B de Sonic na mesma fase no Xbox; qualidade do código JIT
baseline; impacto dos retornos obrigatórios restantes; evictions de Horizon;
progressão de Rivals/Pokémon. Re-JIT concorrente e shaders paralelos não foram
habilitados. Esta candidata reduz um custo confirmado sem certificar que a
regressão de desempenho inteira foi resolvida.

## Pacote

MSIX 0.2.0.119 x64 gerado, assinado e verificado:

`artifacts/xbox/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.119_x64_Test/Ryujinx.Uwp.App_0.2.0.119_x64.msix`

- Tamanho do artefato final: 112.989.394 bytes.
- SHA256 MSIX: `C52A87AADDDD4A5C66B262FF04D22D41466720001D17D900687ADBED11ECAAAF`.
- SHA256 CoreHost publicado/empacotado: `3F7C00C0A0099DE29C242D506CF36DBAFE66EF3882F66A2DE72A13A9DD052C83`.
- SHA256 DLL Native: `E432C3AB1A6F67D908C6F0A5C7871B12843315AD7D3DE85D4D6F5DD1A0CD9BC1`, inalterada em relação à 118.
- Build terminou com código 0. Há avisos de trimming/AOT no publish; zero erros.
- SignTool verify /pa: aprovado, zero erros/avisos de assinatura. Sem timestamp externo.
- Manifesto, arquitetura, assinatura e igualdade das duas DLLs publicados/empacotados
  aprovados em `artifacts/performance-119-package-verification.log`.
- Build completo: `artifacts/performance-119-msix-build.log`.

Nenhum jogo/frontend aberto, instalação local ou desligamento nesta rodada.
